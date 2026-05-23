using System.Security.Cryptography;
using System.Text;
using System.Diagnostics;
using CodeIndex.Cli;
using Microsoft.Data.Sqlite;

namespace CodeIndex.Database;

/// <summary>
/// Opt-in reader debug helper: when CDIDX_DEBUG=1, tracks the last SQL,
/// bound parameters, and last-read row so that reader-level exceptions
/// (e.g. "The data is NULL at ordinal N") can be attributed to a concrete
/// query and row. Text values are redacted by default (length + salted short
/// SHA256 prefix, with path values reduced to shape only). Setting
/// CDIDX_DEBUG=unsafe alone is no longer enough to dump raw
/// text content — the process must also be started with the explicit
/// `--debug-unsafe` CLI flag (set via <see cref="EnableUnsafeForProcess"/>).
/// Without that per-invocation flag the helper downgrades to redacted mode and
/// emits a one-shot stderr warning, so a stale `CDIDX_DEBUG=unsafe` left in a
/// shell profile or CI environment cannot quietly leak indexed source content
/// (#1530).
/// CDIDX_DEBUG=1 のときだけ、直近の SQL・バインドパラメータ・直近の行を追跡し、
/// reader 例外（例: "The data is NULL at ordinal N"）を具体的なクエリと行に結び付ける。
/// 既定ではテキスト値はハッシュ化（長さと SHA256 先頭）される。CDIDX_DEBUG=unsafe を環境変数だけで
/// 指定しても生テキストは出ない。プロセス起動時に `--debug-unsafe` を明示する必要があり、
/// 指定しなかった場合は redacted にフォールバックして stderr に一度だけ警告を出す。
/// シェルプロファイルや CI に残った `CDIDX_DEBUG=unsafe` で索引済みソースが漏れるのを防ぐ（#1530）。
/// </summary>
public static class DbDebug
{
    private const int MaxNumericChars = 64;
    private static readonly byte[] s_hashSalt = RandomNumberGenerator.GetBytes(16);
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<SqliteDataReader, ActiveProfile> s_activeProfiles = new();

    [ThreadStatic]
    private static string? _lastSql;
    [ThreadStatic]
    private static List<(string Name, string Value)>? _lastParams;
    [ThreadStatic]
    private static List<(string Name, string Value)>? _lastRow;
    [ThreadStatic]
    private static bool _hasContext;
    [ThreadStatic]
    private static List<QueryProfileEntry>? _profileEntries;
    [ThreadStatic]
    private static long? _slowQueryThresholdMs;

    // Process-wide gate that must be flipped by an explicit CLI flag before
    // CDIDX_DEBUG=unsafe is honored. Defaults to false so an env var alone
    // never triggers raw-text mode (#1530).
    // CDIDX_DEBUG=unsafe を有効化するためにプロセス側で必須となるゲート。
    // 既定値は false で、環境変数だけでは生テキストモードに入れない（#1530）。
    private static int _allowUnsafeProcess;
    private static int _unsafeDowngradeWarned;
    private static int _invalidDebugValueWarned;

    public static bool IsEnabled => ResolveMode() != DebugMode.Off;

    /// <summary>
    /// Allow CDIDX_DEBUG=unsafe to actually produce raw text dumps in this
    /// process. The CLI calls this when `--debug-unsafe` is passed at startup.
    /// CDIDX_DEBUG=unsafe による生テキスト出力をこのプロセス内で許可する。
    /// CLI は起動時に `--debug-unsafe` を受け取った場合のみ呼ぶ。
    /// </summary>
    public static void EnableUnsafeForProcess()
    {
        Interlocked.Exchange(ref _allowUnsafeProcess, 1);
    }

    internal static bool IsUnsafeAllowedForProcess() =>
        Interlocked.CompareExchange(ref _allowUnsafeProcess, 0, 0) == 1;

    internal static void ResetForTesting()
    {
        Interlocked.Exchange(ref _allowUnsafeProcess, 0);
        Interlocked.Exchange(ref _unsafeDowngradeWarned, 0);
        Interlocked.Exchange(ref _invalidDebugValueWarned, 0);
        EndProfile();
    }

    public static bool IsProfileEnabled => _profileEntries != null;

    public static void BeginProfile(long? slowQueryThresholdMs = null)
    {
        _profileEntries = new List<QueryProfileEntry>();
        _slowQueryThresholdMs = slowQueryThresholdMs;
    }

    public static List<QueryProfileEntry> EndProfile()
    {
        var entries = _profileEntries ?? new List<QueryProfileEntry>();
        _profileEntries = null;
        _slowQueryThresholdMs = null;
        return entries;
    }

    private enum DebugMode { Off, Redacted, Unsafe }

    private static DebugMode ResolveMode()
    {
        var raw = Environment.GetEnvironmentVariable("CDIDX_DEBUG");
        if (string.IsNullOrWhiteSpace(raw))
            return DebugMode.Off;
        var value = raw.Trim();
        if (value.Equals("unsafe", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("full", StringComparison.OrdinalIgnoreCase))
        {
            if (IsUnsafeAllowedForProcess())
                return DebugMode.Unsafe;
            WarnUnsafeDowngradedOnce();
            return DebugMode.Redacted;
        }

        if (TryParseDebugBool(value, out var enabled))
            return enabled ? DebugMode.Redacted : DebugMode.Off;

        WarnInvalidDebugValueOnce(value);
        return DebugMode.Off;
    }

    private static bool TryParseDebugBool(string raw, out bool value)
    {
        value = false;
        switch (raw.ToLowerInvariant())
        {
            case "1":
            case "true":
            case "yes":
            case "on":
                value = true;
                return true;
            case "0":
            case "false":
            case "no":
            case "off":
                value = false;
                return true;
            default:
                return false;
        }
    }

    private static void WarnInvalidDebugValueOnce(string value)
    {
        if (Interlocked.Exchange(ref _invalidDebugValueWarned, 1) != 0)
            return;
        Console.Error.WriteLine(
            $"[cdidx] CDIDX_DEBUG value '{value}' is not recognized. Expected one of: 1, 0, true, false, yes, no, on, off, unsafe, full. Falling back to off.");
    }

    private static void WarnUnsafeDowngradedOnce()
    {
        if (Interlocked.Exchange(ref _unsafeDowngradeWarned, 1) != 0)
            return;
        Console.Error.WriteLine(
            "[cdidx] CDIDX_DEBUG=unsafe was ignored: pass --debug-unsafe on the command line to enable raw text dumps. Falling back to redacted mode for this process.");
    }

    /// <summary>
    /// Clear tracked SQL/params/row for the current thread. Must be called at
    /// the start of each request/command so a later unrelated exception does
    /// not dump stale state from a previous query.
    /// スレッド単位で追跡中の SQL/パラメータ/行をリセットする。リクエスト開始時に必ず呼び、
    /// 別リクエストで発生した無関係な例外に過去の状態を流用しないこと。
    /// </summary>
    public static void ResetContext()
    {
        _lastSql = null;
        _lastParams = null;
        _lastRow = null;
        _hasContext = false;
    }

    internal static void TrackCommand(SqliteCommand cmd)
    {
        if (!IsEnabled)
            return;
        var mode = ResolveMode();
        _lastSql = cmd.CommandText;
        var ps = new List<(string, string)>(cmd.Parameters.Count);
        foreach (SqliteParameter p in cmd.Parameters)
            ps.Add((p.ParameterName, FormatValue(p.Value, mode, p.ParameterName)));
        _lastParams = ps;
        _lastRow = null;
        _hasContext = true;
    }

    internal static SqliteDataReader ExecuteReader(SqliteCommand cmd)
    {
        if (!IsProfileEnabled)
            return cmd.ExecuteReader();

        var entry = new QueryProfileEntry(cmd.CommandText, CaptureQueryPlan(cmd));
        _profileEntries!.Add(entry);

        var sw = Stopwatch.StartNew();
        var reader = cmd.ExecuteReader();
        sw.Stop();

        entry.AddElapsed(sw.Elapsed);
        entry.MarkCompletedIfSlow(_slowQueryThresholdMs);
        s_activeProfiles.Add(reader, new ActiveProfile(entry));
        return reader;
    }

    internal static void TrackReadElapsed(SqliteDataReader reader, TimeSpan elapsed, bool rowRead)
    {
        if (!s_activeProfiles.TryGetValue(reader, out var active))
            return;

        active.Entry.AddElapsed(elapsed);
        if (rowRead)
            active.Entry.IncrementRows();
        active.Entry.MarkCompletedIfSlow(_slowQueryThresholdMs);
    }

    private static List<QueryPlanRow> CaptureQueryPlan(SqliteCommand source)
    {
        var rows = new List<QueryPlanRow>();
        try
        {
            using var explain = source.Connection!.CreateCommand();
            explain.Transaction = source.Transaction;
            explain.CommandText = "EXPLAIN QUERY PLAN " + source.CommandText;
            foreach (SqliteParameter parameter in source.Parameters)
                explain.Parameters.AddWithValue(parameter.ParameterName, parameter.Value ?? DBNull.Value);

            using var reader = explain.ExecuteReader();
            while (reader.Read())
            {
                rows.Add(new QueryPlanRow(
                    reader.GetInt32(0),
                    reader.GetInt32(1),
                    reader.GetInt32(2),
                    reader.GetString(3)));
            }
        }
        catch (Exception ex)
        {
            rows.Add(new QueryPlanRow(-1, -1, -1, "EXPLAIN QUERY PLAN failed: " + ex.Message));
        }

        return rows;
    }

    internal static void SnapshotRow(SqliteDataReader reader)
    {
        if (!IsEnabled)
            return;
        var mode = ResolveMode();
        var row = new List<(string, string)>(reader.FieldCount);
        for (int i = 0; i < reader.FieldCount; i++)
        {
            var name = reader.GetName(i);
            try
            {
                var value = reader.IsDBNull(i) ? "<NULL>" : FormatValue(reader.GetValue(i), mode, name);
                row.Add((name, value));
            }
            catch (Exception ex)
            {
                row.Add((name, $"<error: {ex.GetType().Name}: {ex.Message}>"));
            }
        }
        _lastRow = row;
        _hasContext = true;
    }

    /// <summary>
    /// When debug is enabled and the current thread has tracked a reader
    /// context since the last ResetContext(), append SQL/params/row info to
    /// stderr. No-op otherwise — never dumps stale state from a previous
    /// request.
    /// デバッグ有効で、直近の ResetContext() 以降にこのスレッドで reader コンテキストを
    /// 追跡していた場合のみ stderr に追記する。それ以外は何もせず、過去リクエストの状態を流出させない。
    /// </summary>
    public static void DumpToStderr(Exception ex)
    {
        if (!IsEnabled || !_hasContext)
            return;
        var mode = ResolveMode();
        var sb = new StringBuilder();
        sb.AppendLine("--- CDIDX_DEBUG ---");
        sb.AppendLine($"Mode: {(mode == DebugMode.Unsafe ? "unsafe (raw content)" : "redacted (salted text hashes; path shape only)")}");
        sb.AppendLine("Exception chain:");
        sb.AppendLine(GlobalToolLog.FormatExceptionChain(ex, includeStacks: mode == DebugMode.Unsafe));
        if (_lastSql != null)
        {
            sb.AppendLine("Last SQL:");
            foreach (var line in _lastSql.Split('\n'))
                sb.AppendLine($"  {line.TrimEnd()}");
        }
        if (_lastParams is { Count: > 0 })
        {
            sb.AppendLine("Parameters:");
            foreach (var (name, value) in _lastParams)
                sb.AppendLine($"  {name} = {value}");
        }
        if (_lastRow is { Count: > 0 })
        {
            sb.AppendLine("Last row read:");
            foreach (var (name, value) in _lastRow)
                sb.AppendLine($"  [{name}] = {value}");
        }
        if (mode != DebugMode.Unsafe && ex.StackTrace != null)
        {
            sb.AppendLine("Stack:");
            sb.AppendLine(ex.StackTrace);
        }
        sb.AppendLine("--- END CDIDX_DEBUG ---");
        Console.Error.Write(sb.ToString());
    }

    private static string FormatValue(object? value, DebugMode mode, string? valueName = null)
    {
        if (value is null || value is DBNull)
            return "<NULL>";
        return value switch
        {
            string str => FormatString(str, mode, valueName),
            byte[] bytes => $"<byte[{bytes.Length}]>",
            _ => TruncateNumeric(value.ToString() ?? "<NULL>"),
        };
    }

    private static string FormatString(string s, DebugMode mode, string? valueName)
    {
        if (mode == DebugMode.Unsafe)
        {
            var shown = s.Length <= 200 ? s : s.Substring(0, 200) + $"…<+{s.Length - 200}>";
            return "\"" + shown + "\"";
        }
        if (LooksLikePathName(valueName))
            return $"<path segments={CountPathSegments(s)}>";

        // Redacted mode: emit length + per-process salted short SHA256 prefix,
        // preserving within-run correlation without stable cross-log fingerprints.
        return $"<str len={s.Length} sha256={ShortHash(s)}>";
    }

    private static string ShortHash(string s)
    {
        var valueBytes = Encoding.UTF8.GetBytes(s);
        var input = new byte[s_hashSalt.Length + valueBytes.Length];
        Buffer.BlockCopy(s_hashSalt, 0, input, 0, s_hashSalt.Length);
        Buffer.BlockCopy(valueBytes, 0, input, s_hashSalt.Length, valueBytes.Length);
        var bytes = SHA256.HashData(input);
        var sb = new StringBuilder(16);
        for (int i = 0; i < 8; i++)
            sb.Append(bytes[i].ToString("x2"));
        return sb.ToString();
    }

    private static bool LooksLikePathName(string? valueName) =>
        !string.IsNullOrWhiteSpace(valueName)
        && valueName.Contains("path", StringComparison.OrdinalIgnoreCase);

    private static int CountPathSegments(string path)
    {
        var normalized = path.Replace('\\', '/');
        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length;
    }

    private static string TruncateNumeric(string s)
    {
        if (s.Length <= MaxNumericChars)
            return s;
        return s.Substring(0, MaxNumericChars) + $"…<+{s.Length - MaxNumericChars} chars>";
    }
}

/// <summary>
/// Extensions that route SQLite reads through DbDebug tracking when enabled.
/// Zero overhead when CDIDX_DEBUG is unset.
/// CDIDX_DEBUG 有効時のみ DbDebug を経由して SQLite 読み取りを追跡する拡張。
/// </summary>
internal static class DbDebugExtensions
{
    public static SqliteDataReader ExecuteTrackedReader(this SqliteCommand cmd)
    {
        DbDebug.TrackCommand(cmd);
        return DbDebug.ExecuteReader(cmd);
    }

    public static bool TrackedRead(this SqliteDataReader reader)
    {
        var sw = Stopwatch.StartNew();
        var ok = reader.Read();
        sw.Stop();
        DbDebug.TrackReadElapsed(reader, sw.Elapsed, ok);
        if (ok)
            DbDebug.SnapshotRow(reader);
        return ok;
    }
}

public sealed class QueryProfileEntry
{
    private long _elapsedTicks;
    private int _rowsRead;
    private bool _slowLogged;

    internal QueryProfileEntry(string sql, List<QueryPlanRow> queryPlan)
    {
        Sql = sql;
        QueryPlan = queryPlan;
    }

    public string Sql { get; }
    public List<QueryPlanRow> QueryPlan { get; }
    public double ElapsedMs => TimeSpan.FromTicks(Interlocked.Read(ref _elapsedTicks)).TotalMilliseconds;
    public int RowsScanned => Volatile.Read(ref _rowsRead);

    internal void AddElapsed(TimeSpan elapsed) => Interlocked.Add(ref _elapsedTicks, elapsed.Ticks);

    internal void IncrementRows() => Interlocked.Increment(ref _rowsRead);

    internal void MarkCompletedIfSlow(long? slowQueryThresholdMs)
    {
        if (!slowQueryThresholdMs.HasValue || _slowLogged)
            return;

        var elapsedMs = ElapsedMs;
        if (elapsedMs < slowQueryThresholdMs.Value)
            return;

        _slowLogged = true;
        try
        {
            CodeIndex.Cli.GlobalToolLog.Info($"slow_query elapsed_ms={elapsedMs:0.###} rows_scanned={RowsScanned} sql={Sql.Replace("\n", " ", StringComparison.Ordinal)}");
        }
        catch
        {
            // Best-effort only / ベストエフォートのみ
        }
    }
}

public sealed record QueryPlanRow(int Id, int Parent, int NotUsed, string Detail);

internal sealed record ActiveProfile(QueryProfileEntry Entry);
