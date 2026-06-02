using CodeIndex.Database;
using CodeIndex.Indexer;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace CodeIndex.Cli;

/// <summary>
/// User-visible color output policy. Drives ANSI escape emission.
/// 色出力ポリシー。ANSI エスケープ発行を制御する。
/// </summary>
public enum ColorMode
{
    /// <summary>Honor env vars (NO_COLOR / CLICOLOR_FORCE / CLICOLOR) and fall back to TTY auto-detect.</summary>
    Auto = 0,
    /// <summary>Always emit ANSI escapes, even when stdout is redirected.</summary>
    Always = 1,
    /// <summary>Never emit ANSI escapes, even on a TTY.</summary>
    Never = 2,
}

/// <summary>
/// ANSI color palette to use when color output is enabled. <see cref="Basic"/>
/// stays within the 8 standard SGR colors (30–37) and avoids the bright-black
/// dim escape (<c>\x1b[90m</c>), which is unreadable on many SSH/CI terminals.
/// <see cref="Color256"/> uses 256-color codes (`\x1b[38;5;Nm`) for higher
/// contrast on capable terminals. <see cref="Truecolor"/> uses 24-bit RGB
/// (`\x1b[38;2;R;G;Bm`) for terminals that advertise truecolor via
/// <c>COLORTERM=truecolor|24bit</c>.
/// 色出力で使用する ANSI パレット。Basic は標準8色のみで `\x1b[90m`（dim）を
/// 避け、SSH / CI 端末でも可読性を確保する。Color256 / Truecolor はそれぞれ
/// 256色 / 24ビットRGB を用い、対応端末で高コントラストを実現する。
/// </summary>
public enum ColorPalette
{
    /// <summary>Standard 8-color ANSI palette (30–37); avoids dim (`\x1b[90m`).</summary>
    Basic = 0,
    /// <summary>256-color ANSI palette (`\x1b[38;5;Nm`).</summary>
    Color256 = 1,
    /// <summary>24-bit RGB / truecolor palette (`\x1b[38;2;R;G;Bm`).</summary>
    Truecolor = 2,
}

public enum DurationOutputFormat
{
    Auto = 0,
    Seconds = 1,
    Hms = 2,
}

public enum CompletionNotificationMode
{
    Auto = 0,
    None = 1,
    Bell = 2,
    Osc9 = 3,
}

/// <summary>
/// Console UI helpers: spinner, progress bar, banner, and easter egg messages.
/// コンソールUIヘルパー: スピナー、プログレスバー、バナー、イースターエッグメッセージ。
/// </summary>
public static class ConsoleUi
{
    public const string DisableProgressEnvironmentVariable = "CDIDX_DISABLE_PROGRESS";
    public const string PrefersReducedMotionEnvironmentVariable = "PREFERS_REDUCED_MOTION";
    public const int SummaryLabelWidth = 9;

    private static readonly (string Command, string Usage)[] CommandUsageLines =
    [
        ("index", "cdidx index <projectPath> [--db <path>] [--rebuild] [--optimize] [--verbose] [--dry-run] [--force] [--quiet] [--json] [--memory-trace] [--duration-format <auto|seconds|hms>] [--notify <auto|bell|osc9|desktop|none>] [--max-file-bytes <bytes>] [--max-symbols-per-file <n>] [--follow-symlinks <none|internal|all>] [--include-symbol-kind <kind>[,<kind>]] [--exclude-symbol-kind <kind>[,<kind>]] [--watch [--debounce <ms>]]"),
        ("hooks", "cdidx hooks <install|uninstall|status> [--project <path>] [--force] [--json]"),
        ("backfill-fold", "cdidx backfill-fold [--db <path>] [--dry-run] [--no-checkpoint] [--json]"),
        ("optimize", "cdidx optimize [--db <path>] [--json]"),
        ("vacuum", "cdidx vacuum [--db <path>] [--json]"),
        ("index-commits", "cdidx index <projectPath> --commits <commit-ref> [commit-ref ...] [--db <path>] [--verbose] [--dry-run] [--json] [--memory-trace] [--duration-format <auto|seconds|hms>] [--max-file-bytes <bytes>] [--include-symbol-kind <kind>[,<kind>]] [--exclude-symbol-kind <kind>[,<kind>]]"),
        ("index-changed-between", "cdidx index <projectPath> --changed-between <old-ref> <new-ref> [--db <path>] [--verbose] [--dry-run] [--json] [--memory-trace] [--duration-format <auto|seconds|hms>] [--max-file-bytes <bytes>] [--include-symbol-kind <kind>[,<kind>]] [--exclude-symbol-kind <kind>[,<kind>]]"),
        ("index-files", "cdidx index <projectPath> --files <path> [path ...] [--db <path>] [--verbose] [--dry-run] [--json] [--memory-trace] [--duration-format <auto|seconds|hms>] [--max-file-bytes <bytes>] [--include-symbol-kind <kind>[,<kind>]] [--exclude-symbol-kind <kind>[,<kind>]]"),
        ("search", "cdidx search <query>|--query <query>|-- <query> [--db <path>] [--json[=ndjson|array]] [--format <text|json|count|compact|csv|tsv|lsp|qf|sarif>] [--verbose] [--limit <n>|--top <n>] [--lang <lang>] [--path <glob>] [--exclude-path <glob>] [--exclude-tests] [--snippet-lines <n>] [--snippet-focus <leftmost|quality|proximity>] [--max-line-width <n>] [--fts] [--exact|--exact-substring] [--prefix] [--count] [--since <datetime>] [--no-dedup] [--no-visibility-rank] [--require-before <query>] [--require-after <query>] [--reject-before <query>] [--reject-after <query>] [--guard-window <n>]"),
        ("definition", "cdidx definition <query>|--query <query>|-- <query> [--db <path>] [--json] [--format <text|json|count|compact|csv|tsv|lsp|qf|sarif>] [--verbose] [--limit <n>|--top <n>] [--lang <lang>] [--kind <kind>] [--visibility <v[,v]>] [--exclude-visibility <v[,v]>] [--path <glob>] [--exclude-path <glob>] [--exclude-tests] [--body] [--exact|--exact-name] [--count] [--since <datetime>]"),
        ("goto", "cdidx goto <query>|--query <query>|-- <query> [--db <path>] [--json] [--limit <n>|--top <n>] [--lang <lang>] [--kind <kind>] [--path <glob>] [--exclude-path <glob>] [--exclude-tests] [--exact|--exact-name] [--all]"),
        ("references", "cdidx references <query>|--query <query>|-- <query> [--db <path>] [--json] [--format <text|json|count|compact|csv|tsv|lsp|qf|sarif>] [--verbose] [--limit <n>|--top <n>] [--lang <lang>] [--kind <kind>] [--path <glob>] [--exclude-path <glob>] [--exclude-tests] [--body] [--snippet-lines <n>] [--max-line-width <n>] [--exact|--exact-name] [--count]"),
        ("callers", "cdidx callers <query>|--query <query>|-- <query> [--db <path>] [--json] [--format <text|json|count|compact|csv|tsv|lsp|qf|sarif>] [--verbose] [--limit <n>|--top <n>] [--lang <lang>] [--kind <kind>] [--rank-by <weighted|count|kind>] [--raw-kinds] [--path <glob>] [--exclude-path <glob>] [--exclude-tests] [--body] [--snippet-lines <n>] [--max-line-width <n>] [--exact|--exact-name] [--count]"),
        ("callees", "cdidx callees <query>|--query <query>|-- <query> [--db <path>] [--json] [--format <text|json|count|compact|csv|tsv|lsp|qf|sarif>] [--verbose] [--limit <n>|--top <n>] [--lang <lang>] [--kind <kind>] [--rank-by <weighted|count|kind>] [--raw-kinds] [--path <glob>] [--exclude-path <glob>] [--exclude-tests] [--body] [--snippet-lines <n>] [--max-line-width <n>] [--exact|--exact-name] [--count]"),
        ("symbols", "cdidx symbols [query|--query <query>|-- <query>] [--name <name>] [--db <path>] [--json] [--verbose] [--limit <n>|--top <n>] [--lang <lang>] [--kind <kind>] [--visibility <v[,v]>] [--exclude-visibility <v[,v]>] [--path <glob>] [--exclude-path <glob>] [--exclude-tests] [--exact|--exact-name] [--count] [--since <datetime>]"),
        ("files", "cdidx files [query|--query <query>|-- <query>] [--db <path>] [--json] [--verbose] [--limit <n>|--top <n>] [--lang <lang>] [--path <glob>] [--exclude-path <glob>] [--exclude-tests] [--count] [--since <datetime>] [--bytes]"),
        ("find", "cdidx find <query> --path <glob> [--db <path>] [--json] [--format <text|json|count|compact|csv|tsv|lsp|qf|sarif>] [--verbose] [--limit <n>|--top <n>] [--lang <lang>] [--exclude-path <glob>] [--exclude-tests] [--before <n>] [--after <n>] [--snippet-lines <n>] [--focus-line <line>] [--focus-column <n>] [--max-line-width <n>] [--exact] [--regex] [--count]"),
        ("excerpt", "cdidx excerpt <path> --start <line> [--end <line>] [--before <n>] [--after <n>] [--max-line-width <n>] [--focus-line <line>] [--focus-column <n>] [--focus-length <n>] [--db <path>] [--json] [--verbose]"),
        ("map", "cdidx map [--db <path>] [--json] [--verbose] [--limit <n>|--top <n>] [--lang <lang>] [--path <glob>] [--exclude-path <glob>] [--exclude-tests] [--bytes] [--sections <tree,languages,hotspots,metrics>] [--depth <n>] [--min-entrypoint-confidence <0.0..1.0>]"),
        ("inspect", "cdidx inspect <query>|--query <query>|-- <query> [--db <path>] [--json] [--verbose] [--limit <n>|--top <n>] [--lang <lang>] [--path <glob>] [--exclude-path <glob>] [--exclude-tests] [--body] [--max-line-width <n>] [--exact|--exact-name]"),
        ("outline", "cdidx outline <path> [--db <path>] [--json] [--verbose]"),
        ("status", "cdidx status [--db <path>] [--json] [--verbose] [--check[=workspace,fold,graph,issues,hotspot,csharp,sql,newer]] [--stale-after <duration>] [--explain <field>] [--log-path] [--config] [--check-updates]"),
        ("workspace", "cdidx workspace <list|status|use|current> [name] [--json]"),
        ("config", "cdidx config show [--json]"),
        ("validate-config", "cdidx validate-config"),
        ("doctor", "cdidx doctor"),
        ("db", "cdidx db --integrity-check|schema|prune [--dry-run|--apply] [--db <path>] [--json] | cdidx db checkpoint [name] [--db <path>] [--json] | cdidx db checkpoints --list [--db <path>] [--json] | cdidx db restore <name> [--db <path>] [--json]"),
        ("diff", "cdidx diff <db1> <db2> [--json] [--summary-only] [--detailed] [--limit <n>]"),
        ("report", "cdidx report --output <path> [--db <path>] [--json] [--log-lines <n<=2000>] [--no-log] [--include-args]"),
        ("validate", "cdidx validate [--db <path>] [--json] [--format <text|json|count|compact|csv|tsv|lsp|qf|sarif>] [--verbose] [--kind <kind>] [--path <glob>]"),
        ("impact", "cdidx impact <query>|--query <query>|-- <query> [--db <path>] [--json] [--verbose] [--limit <n>|--top <n>] [--lang <lang>] [--path <glob>] [--exclude-path <glob>] [--exclude-tests] [--body] [--snippet-lines <n>] [--max-line-width <n>] [--max-hops <n>] [--count] [--with-paths]"),
        ("deps", "cdidx deps [--db <path>] [--json] [--format <dot|graphml|json-graph|edgelist>] [--verbose] [--limit <n>|--top <n>] [--lang <lang>] [--path <glob>] [--exclude-path <glob>] [--exclude-tests] [--reverse] [--cycles]"),
        ("unused", "cdidx unused [--db <path>] [--json] [--verbose] [--limit <n>|--top <n>] [--kind <kind>] [--visibility <v[,v]>] [--exclude-visibility <v[,v]>] [--lang <lang>] [--path <glob>] [--exclude-path <glob>] [--exclude-tests] [--count]"),
        ("hotspots", "cdidx hotspots [--db <path>] [--json] [--verbose] [--limit <n>|--top <n>] [--kind <kind>] [--visibility <v[,v]>] [--exclude-visibility <v[,v]>] [--lang <lang>] [--path <glob>] [--exclude-path <glob>] [--exclude-tests] [--count] [--group-by <symbol|file|statement>] [--group-by-name]"),
        ("suggestions", "cdidx suggestions <list|show|export> [id] [--db <path>] [--json] [--status <all|submitted|unsubmitted>] [--language <lang>] [--category <category>] [--since <datetime>] [--agent <name>] [--format <json|markdown|issue-drafts>] [--open-issues <path>]"),
        ("export", "cdidx export <archive> [--db <path>] [--json]"),
        ("export", "cdidx export ctags [--output <path>] [--db <path>]"),
        ("import", "cdidx import <archive> [--db <path>] [--prune-paths] [--json]"),
        ("languages", "cdidx languages [--json]"),
        ("batch", "cdidx batch [--db <path>]  # reads JSON string arrays from stdin, one query command per line; max 1,048,576 chars/line and 256 arguments"),
        ("mcp", "cdidx mcp [--db <path>] [--transport stdio|http] [--http-listen <host:port>] [--audit-log <path>] [--audit-log-include-values] [--audit-log-max-bytes <n>] [--suggestion-dedup-threshold <0..1>]"),
        ("lsp", "cdidx lsp [--db <path>]"),
        ("completions", "cdidx completions <shell>"),
        ("--completions", "cdidx --completions <shell>"),
        ("upgrade", "cdidx upgrade [--check-only]"),
        ("license", "cdidx license"),
    ];

    private static readonly (string Command, string Note)[] CommandUsageNotes =
    [
        ("mcp", "--json is not supported; MCP requests and responses are JSON-RPC over the selected transport."),
        ("completions", "--json is not supported; output is a shell script for the selected shell."),
    ];

    public static string FormatSummaryLine(string label, object? value, int labelWidth = SummaryLabelWidth, string indent = "")
        => $"{indent}{label.PadRight(labelWidth)}: {value}";

    private const int SpinnerFrameDelayMs = 100;
    private const int SpinnerStopDelayMs = 20;
    private const int ConsoleLineMargin = 1;
    private static readonly object TerminalLock = new();
    private static TextWriter? _synchronizedOut;
    private static TextWriter? _synchronizedError;
    private static readonly string[] ByteUnits = ["bytes", "KiB", "MiB", "GiB", "TiB", "PiB"];
    private static readonly AsyncLocal<int> JsonOutputDepth = new();

    private static readonly string[] DefaultBrailleSpinnerFrames =
    [
        "⠋", "⠙", "⠹", "⠸", "⠼",
        "⠴", "⠦", "⠧", "⠇", "⠏",
    ];
    private static readonly string[] AsciiSpinnerFrames = ["|", "/", "-", "\\"];

    internal static string Counted(int count, string singular, string? plural = null, string? format = null)
    {
        var formatted = format == null
            ? count.ToString(CultureInfo.InvariantCulture)
            : count.ToString(format, CultureInfo.InvariantCulture);
        return $"{formatted} {(count == 1 ? singular : plural ?? singular + "s")}";
    }

    internal static string FormatNumber(long value, string format = "N0")
        => value.ToString(format, CultureInfo.InvariantCulture);

    internal static string FormatNumber(int value, string format = "N0")
        => value.ToString(format, CultureInfo.InvariantCulture);

    internal static string FoundSummary(int count, string singular, string? plural = null)
    {
        plural ??= singular + "s";
        return count == 0
            ? $"No {plural} found."
            : $"Found {Counted(count, singular, plural)}.";
    }

    internal static void EnsureConsoleWritersSynchronized()
    {
        lock (TerminalLock)
        {
            var output = Console.Out;
            if (!ReferenceEquals(output, _synchronizedOut))
            {
                _synchronizedOut = TextWriter.Synchronized(output);
                Console.SetOut(_synchronizedOut);
            }

            var error = Console.Error;
            if (!ReferenceEquals(error, _synchronizedError))
            {
                _synchronizedError = TextWriter.Synchronized(error);
                Console.SetError(_synchronizedError);
            }
        }
    }

    internal static void TryWriteErrorLine(string? value = null)
    {
        try
        {
            if (value == null)
                Console.Error.WriteLine();
            else
                Console.Error.WriteLine(value);
        }
        catch (ObjectDisposedException)
        {
        }
        catch (IOException)
        {
        }
    }

    internal static IDisposable SuppressAnsiForJsonOutput(bool enabled)
    {
        if (!enabled)
            return NoopDisposable.Instance;

        JsonOutputDepth.Value++;
        return new JsonOutputScope();
    }

    // --- Spinner / スピナー ---

    public static string FormatDuration(TimeSpan duration, DurationOutputFormat format = DurationOutputFormat.Auto)
    {
        if (duration < TimeSpan.Zero)
            duration = TimeSpan.Zero;

        return format switch
        {
            DurationOutputFormat.Seconds => FormatDurationAsSeconds(duration),
            DurationOutputFormat.Hms => FormatDurationAsHms(duration),
            _ => FormatDurationAuto(duration),
        };
    }

    private static string FormatDurationAuto(TimeSpan duration)
    {
        if (duration < TimeSpan.FromSeconds(1))
            return string.Create(CultureInfo.InvariantCulture, $"{Math.Floor(duration.TotalMilliseconds):0}ms");

        if (duration < TimeSpan.FromMinutes(1))
            return string.Create(CultureInfo.InvariantCulture, $"{duration.TotalSeconds:0.0}s");

        var totalSeconds = (long)Math.Floor(duration.TotalSeconds);
        if (duration < TimeSpan.FromHours(1))
        {
            var minutes = totalSeconds / 60;
            var seconds = totalSeconds % 60;
            return string.Create(CultureInfo.InvariantCulture, $"{minutes}m {seconds}s");
        }

        var hours = totalSeconds / 3600;
        var remainder = totalSeconds % 3600;
        var remMinutes = remainder / 60;
        var remSeconds = remainder % 60;
        return string.Create(CultureInfo.InvariantCulture, $"{hours}h {remMinutes}m {remSeconds}s");
    }

    private static string FormatDurationAsSeconds(TimeSpan duration)
        => string.Create(CultureInfo.InvariantCulture, $"{duration.TotalSeconds:0.0}s");

    private static string FormatDurationAsHms(TimeSpan duration)
    {
        var totalSeconds = (long)Math.Floor(duration.TotalSeconds);
        var hours = totalSeconds / 3600;
        var remainder = totalSeconds % 3600;
        var minutes = remainder / 60;
        var seconds = remainder % 60;
        return string.Create(CultureInfo.InvariantCulture, $"{hours:00}:{minutes:00}:{seconds:00}");
    }

    /// <summary>
    /// Start spinner on a background thread, returns CancellationTokenSource to stop it.
    /// バックグラウンドスレッドでスピナーを開始。停止用のCancellationTokenSourceを返す。
    /// </summary>
    public static CancellationTokenSource? StartSpinner(string message, string[] frames)
    {
        EnsureConsoleWritersSynchronized();

        // Braille frames are single-char; themed frames are longer strings containing the display text
        // ブレイルフレームは1文字、テーマフレームは表示テキストを含む長い文字列
        bool isThemed = frames.Length > 0 && frames[0].Length > 2;

        if (!ShouldUseInteractiveConsole() || !ShouldUseProgressAnimation())
        {
            Console.WriteLine(message);
            return null;
        }

        var cts = new CancellationTokenSource();
        var ct = cts.Token;
        _ = Task.Run(async () =>
        {
            int i = 0;
            while (!ct.IsCancellationRequested)
            {
                var frame = frames[i % frames.Length];
                var line = isThemed ? $"\r{frame}" : $"\r{frame} {message}";
                lock (TerminalLock)
                {
                    Console.Write(line);
                    Console.Out.Flush();
                }
                i++;
                try { await Task.Delay(SpinnerFrameDelayMs, ct).ConfigureAwait(false); } catch (OperationCanceledException) { break; }
            }
        }, ct);
        return cts;
    }

    /// <summary>
    /// Stop spinner and clear the line.
    /// スピナーを停止して行をクリア。
    /// </summary>
    public static void StopSpinner(CancellationTokenSource? cts)
    {
        if (cts == null) return;
        cts.Cancel();
        // Small delay to let the spinner task exit / スピナータスク終了のための短い待機
        Thread.Sleep(SpinnerStopDelayMs);
        if (ShouldUseInteractiveConsole())
        {
            lock (TerminalLock)
            {
                Console.Write($"\r{new string(' ', GetWindowWidth() - ConsoleLineMargin)}\r");
                Console.Out.Flush();
            }
        }
        cts.Dispose();
    }

    internal static void SetProgressAnimationEnabled(bool? enabled)
        => _progressAnimationEnabledOverride = enabled;

    internal static bool ShouldUseProgressAnimation()
    {
        if (_progressAnimationEnabledOverride.HasValue)
            return _progressAnimationEnabledOverride.Value;

        if (IsTruthyEnvironmentVariable(DisableProgressEnvironmentVariable))
            return false;

        var reducedMotion = Environment.GetEnvironmentVariable(PrefersReducedMotionEnvironmentVariable);
        return string.IsNullOrWhiteSpace(reducedMotion) || !IsTruthyEnvironmentValue(reducedMotion);
    }

    /// <summary>
    /// Get spinner frames based on easter egg flag.
    /// イースターエッグフラグに基づくスピナーフレームを取得。
    /// </summary>
    public static string[] GetSpinnerFrames(string? easterEgg)
    {
        if (!ShouldUseUnicodeGlyphs())
            return AsciiSpinnerFrames;

        return easterEgg switch
        {
            "--sushi" =>
            [
                "\U0001f363 Slicing       ", "\U0001f363 Slicing.      ", "\U0001f363 Slicing..     ", "\U0001f363 Slicing...    ",
            "\U0001f363 Shaping       ", "\U0001f363 Shaping.      ", "\U0001f363 Shaping..     ", "\U0001f363 Shaping...    ",
            "\U0001f363 Pressing      ", "\U0001f363 Pressing.     ", "\U0001f363 Pressing..    ", "\U0001f363 Pressing...   ",
            "\U0001f363 Itadakimasu!  ",
        ],
            "--coffee" =>
            [
                "\u2615 Grinding      ", "\u2615 Grinding.     ", "\u2615 Grinding..    ", "\u2615 Grinding...   ",
            "\u2615 Heating       ", "\u2615 Heating.      ", "\u2615 Heating..     ", "\u2615 Heating...    ",
            "\u2615 Brewing       ", "\u2615 Brewing.      ", "\u2615 Brewing..     ", "\u2615 Brewing...    ",
        ],
            "--ramen" =>
            [
                "\U0001f35c Boiling       ", "\U0001f35c Boiling.      ", "\U0001f35c Boiling..     ", "\U0001f35c Boiling...    ",
            "\U0001f35c Steaming      ", "\U0001f35c Steaming.     ", "\U0001f35c Steaming..    ", "\U0001f35c Steaming...   ",
            "\U0001f35c Slurping      ", "\U0001f35c Slurping.     ", "\U0001f35c Slurping..    ", "\U0001f35c Slurping...   ",
            "\U0001f35c Itadakimasu!  ",
        ],
            "--wine" =>
            [
                "\U0001f377 Crushing      ", "\U0001f377 Crushing.     ", "\U0001f377 Crushing..    ", "\U0001f377 Crushing...   ",
            "\U0001f377 Aging         ", "\U0001f377 Aging.        ", "\U0001f377 Aging..       ", "\U0001f377 Aging...      ",
            "\U0001f377 Pouring       ", "\U0001f377 Pouring.      ", "\U0001f377 Pouring..     ", "\U0001f377 Pouring...    ",
            "\U0001f377 Sant\u00e9!        ",
        ],
            "--beer" =>
            [
                "\U0001f37a Tapping       ", "\U0001f37a Tapping.      ", "\U0001f37a Tapping..     ", "\U0001f37a Tapping...    ",
            "\U0001f37a Pouring       ", "\U0001f37a Pouring.      ", "\U0001f37a Pouring..     ", "\U0001f37a Pouring...    ",
            "\U0001f37a Foaming       ", "\U0001f37a Foaming.      ", "\U0001f37a Foaming..     ", "\U0001f37a Foaming...    ",
            "\U0001f37a Cheers!       ",
        ],
            "--matcha" =>
            [
                "\U0001f375 Sifting       ", "\U0001f375 Sifting.      ", "\U0001f375 Sifting..     ", "\U0001f375 Sifting...    ",
            "\U0001f375 Pouring       ", "\U0001f375 Pouring.      ", "\U0001f375 Pouring..     ", "\U0001f375 Pouring...    ",
            "\U0001f375 Whisking      ", "\U0001f375 Whisking.     ", "\U0001f375 Whisking..    ", "\U0001f375 Whisking...   ",
            "\U0001f375 Douzo!        ",
        ],
            "--whisky" =>
            [
                "\U0001f943 Mashing       ", "\U0001f943 Mashing.      ", "\U0001f943 Mashing..     ", "\U0001f943 Mashing...    ",
            "\U0001f943 Distilling    ", "\U0001f943 Distilling.   ", "\U0001f943 Distilling..  ", "\U0001f943 Distilling... ",
            "\U0001f943 Aging         ", "\U0001f943 Aging.        ", "\U0001f943 Aging..       ", "\U0001f943 Aging...      ",
            "\U0001f943 Slainte!      ",
        ],
            // Default: Braille spinner / デフォルト: ブレイルスピナー
            _ => DefaultBrailleSpinnerFrames,
        };
    }

    // --- Progress bar / プログレスバー ---

    // Active spinner frames for progress bar (themed or default braille)
    // プログレスバー用アクティブスピナーフレーム（テーマ付きまたはデフォルトブレイル）
    private static string[] _progressSpinnerFrames = DefaultBrailleSpinnerFrames;
    // Track last progress line length for clearing / クリア用に最後のプログレス行の長さを記録
    private static int _lastProgressLineLength;
    private static bool _asciiOutputForced;
    private static bool? _progressAnimationEnabledOverride;
    private static bool _widthDetectionFailed;
    private static bool _widthDetectionTraceWritten;
    private static bool _traceWidthDetectionFailures;

    /// <summary>
    /// Set progress bar spinner theme (reuses GetSpinnerFrames).
    /// プログレスバーのスピナーテーマを設定（GetSpinnerFramesを再利用）。
    /// </summary>
    public static void SetProgressTheme(string? easterEgg)
    {
        _progressSpinnerFrames = GetSpinnerFrames(easterEgg);
    }

    /// <summary>
    /// Print inline progress bar with spinner.
    /// スピナー付きインライン進捗バーを表示。
    /// </summary>
    public static void PrintProgress(int current, int total)
    {
        if (total <= 0)
            return;

        var output = Console.Out;
        var redirected = !ShouldUseInteractiveConsole();

        // Update every 50 files or at completion / 50ファイルごと、または完了時に更新
        if (current % 50 != 0 && current != total)
            return;

        var line = FormatProgressLine(
            current,
            total,
            redirected ? 80 : GetWindowWidth(),
            ShouldUseUnicodeGlyphs(),
            ShouldUseProgressAnimation());

        if (!redirected)
        {
            lock (TerminalLock)
            {
                output.Write($"\r{line}");
                output.Flush();
                _lastProgressLineLength = line.Length;
                if (current == total)
                {
                    output.WriteLine();
                    _lastProgressLineLength = 0;
                }
            }
        }
        else
        {
            // Fallback for redirected output / リダイレクト時はフォールバック
            output.WriteLine(line.TrimStart());
        }
    }

    internal static string FormatProgressLine(
        int current,
        int total,
        int windowWidth,
        bool useUnicodeGlyphs,
        bool useProgressAnimation = true)
    {
        const int barWidth = 32;
        var pct = (double)current / total;
        var percentAndCounts = string.Create(
            CultureInfo.InvariantCulture,
            $"{pct * 100,5:F1}%  [{current:N0}/{total:N0}]");

        if (useUnicodeGlyphs && windowWidth < 40)
            return percentAndCounts;

        int filled = (int)Math.Round(pct * barWidth);
        if (filled > barWidth) filled = barWidth;
        if (filled < 0) filled = 0;

        var spinner = useProgressAnimation ? ResolveProgressSpinner(current, total, useUnicodeGlyphs) : " ";
        var bar = useUnicodeGlyphs
            ? new string('\u2588', filled) + new string('\u2591', barWidth - filled)
            : $"[{new string('#', filled)}{new string('-', barWidth - filled)}]";
        return $"{spinner} {bar} {percentAndCounts}";
    }

    private static string ResolveProgressSpinner(int current, int total, bool useUnicodeGlyphs)
    {
        if (current == total)
            return " ";

        return useUnicodeGlyphs
            ? _progressSpinnerFrames[(current / 50) % _progressSpinnerFrames.Length]
            : "-";
    }

    /// <summary>
    /// Clear the current progress bar line so other output can be printed cleanly.
    /// 他の出力を正しく表示するために現在のプログレスバー行をクリア。
    /// </summary>
    public static void ClearProgressLine()
    {
        lock (TerminalLock)
        {
            ClearProgressLineCore();
        }
    }

    private static void ClearProgressLineCore()
    {
        if (ShouldUseInteractiveConsole() && _lastProgressLineLength > 0)
        {
            Console.Write($"\r{new string(' ', _lastProgressLineLength)}\r");
            Console.Out.Flush();
            _lastProgressLineLength = 0;
        }
    }

    /// <summary>
    /// Print a warning message, clearing the progress bar line first if needed.
    /// 必要に応じてプログレスバー行をクリアしてから警告メッセージを表示。
    /// </summary>
    public static void PrintWarning(string message)
    {
        lock (TerminalLock)
        {
            ClearProgressLineCore();
            Console.Error.WriteLine($"  [WARN] {message}");
            Console.Error.Flush();
            Console.Out.Flush();
        }
    }

    // --- Banner / バナー ---

    /// <summary>
    /// Print ASCII-art banner.
    /// ASCIIアートバナーを表示。
    /// </summary>
    public static void PrintBanner()
    {
        const string banner = """

             ██████╗ ██████╗ ██████╗ ███████╗██╗███╗   ██╗██████╗ ███████╗██╗  ██╗
            ██╔════╝██╔═══██╗██╔══██╗██╔════╝██║████╗  ██║██╔══██╗██╔════╝╚██╗██╔╝
            ██║     ██║   ██║██║  ██║█████╗  ██║██╔██╗ ██║██║  ██║█████╗   ╚███╔╝
            ██║     ██║   ██║██║  ██║██╔══╝  ██║██║╚██╗██║██║  ██║██╔══╝   ██╔██╗
            ╚██████╗╚██████╔╝██████╔╝███████╗██║██║ ╚████║██████╔╝███████╗██╔╝ ██╗
             ╚═════╝ ╚═════╝ ╚═════╝ ╚══════╝╚═╝╚═╝  ╚═══╝╚═════╝ ╚══════╝╚═╝  ╚═╝
            """;
        Console.WriteLine(banner);
    }

    public static void PrintIndexCompleteSummary(
        string projectRoot,
        string resolvedDbPath,
        bool incremental,
        int filesScanned,
        IReadOnlyDictionary<string, int> languageCounts)
    {
        Console.WriteLine(incremental ? "Next steps (incremental):" : "Next steps:");
        Console.WriteLine("  - Search code: cdidx search \"authenticate\" --path src/");
        Console.WriteLine("  - Find a definition: cdidx definition SymbolName");
        Console.WriteLine($"  - Start MCP: cdidx mcp --db {QuoteForDisplay(resolvedDbPath)}");
        Console.WriteLine($"  - Database: {resolvedDbPath}");
        Console.WriteLine("  - Exclude paths with .gitignore or .cdidxignore, then rerun cdidx index .");
        Console.WriteLine($"  - Scanned {Counted(filesScanned, "file", format: "N0")} under {projectRoot}");
        if (languageCounts.Count > 0)
        {
            var summary = string.Join(
                ", ",
                languageCounts
                    .OrderByDescending(static pair => pair.Value)
                    .ThenBy(static pair => pair.Key, StringComparer.Ordinal)
                    .Take(6)
                    .Select(static pair => $"{pair.Key} {pair.Value.ToString("N0", CultureInfo.InvariantCulture)}"));
            Console.WriteLine($"  - Languages: {summary}");
        }
        Console.WriteLine();
    }

    public static void EmitCompletionNotification(CompletionNotificationMode mode, string message)
    {
        var resolved = mode == CompletionNotificationMode.Auto
            ? ShouldUseInteractiveConsole() ? CompletionNotificationMode.Bell : CompletionNotificationMode.None
            : mode;
        if (resolved == CompletionNotificationMode.None)
            return;

        var safeMessage = message.Replace('\r', ' ').Replace('\n', ' ');
        if (resolved == CompletionNotificationMode.Osc9)
            Console.Error.Write($"\u001b]9;{safeMessage}\a");
        else
            Console.Error.Write('\a');
        Console.Error.Flush();
    }

    private static string QuoteForDisplay(string value)
        => value.IndexOfAny([' ', '\t', '"']) < 0
            ? value
            : $"\"{value.Replace("\"", "\\\"", StringComparison.Ordinal)}\"";

    // --- Easter eggs / イースターエッグ ---

    /// <summary>
    /// Print easter egg message (standalone mode). Renders the catalog entry for
    /// <paramref name="flag"/> in the language chosen by <see cref="UiLanguageResolver"/>
    /// (<c>CDIDX_LANG</c> env > <see cref="System.Globalization.CultureInfo.CurrentUICulture"/>
    /// > English fallback). Unknown flags print two blank lines for legacy compatibility.
    /// Pass <paramref name="languageOverride"/> to bypass env/culture resolution (used by
    /// tests so they do not mutate the live process environment).
    /// イースターエッグメッセージを表示（単体実行時）。<see cref="UiLanguageResolver"/>
    /// が選んだ言語（<c>CDIDX_LANG</c> 環境変数 &gt; カルチャ &gt; 英語）でカタログ
    /// エントリを描画する。未知フラグは従来互換で空行を2つ出力。
    /// <paramref name="languageOverride"/> を指定すると環境変数/カルチャ判定をスキップする
    /// （テストがプロセス環境を書き換えずに済むようにするためのフック）。
    /// </summary>
    public static void PrintEasterEggMessage(string flag, UiLanguage? languageOverride = null)
    {
        var pair = flag switch
        {
            "--sushi" => UiMessages.EasterEggSushi,
            "--coffee" => UiMessages.EasterEggCoffee,
            "--ramen" => UiMessages.EasterEggRamen,
            "--wine" => UiMessages.EasterEggWine,
            "--beer" => UiMessages.EasterEggBeer,
            "--matcha" => UiMessages.EasterEggMatcha,
            "--whisky" => UiMessages.EasterEggWhisky,
            _ => null,
        };
        if (pair is null)
        {
            Console.WriteLine();
            Console.WriteLine();
            return;
        }

        var lang = languageOverride ?? UiLanguageResolver.Resolve();
        foreach (var line in UiMessages.Render(pair, lang))
            Console.WriteLine(line);
    }

    // --- Version loading / バージョン読み込み ---

    /// <summary>
    /// Load version from version.json.
    /// version.jsonからバージョンを読み込み。
    /// </summary>
    public static string LoadVersion()
    {
        var exeDir = AppContext.BaseDirectory;
        var path = Path.Combine(exeDir, "version.json");
        if (!File.Exists(LongPath.EnsureWindowsPrefix(path)))
        {
            // Fallback: look relative to current directory / カレントディレクトリからの相対パスでフォールバック
            path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "version.json");
        }
        var ioPath = LongPath.EnsureWindowsPrefix(path);
        if (File.Exists(ioPath))
        {
            var json = File.ReadAllText(ioPath);
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("version", out var ver))
                return ver.GetString() ?? "0.0.0";
        }
        return "0.0.0";
    }

    /// <summary>
    /// Format byte counts for human-facing CLI output using binary units.
    /// 人間向けCLI出力用にバイト数を2進単位で整形する。
    /// </summary>
    public static string FormatBytes(long bytes)
    {
        if (bytes < 0)
            return string.Create(CultureInfo.InvariantCulture, $"{bytes:N0} bytes");
        if (bytes < 1024)
            return string.Create(CultureInfo.InvariantCulture, $"{bytes:N0} bytes");

        var value = (double)bytes;
        var unitIndex = 0;
        while (value >= 1024 && unitIndex < ByteUnits.Length - 1)
        {
            value /= 1024;
            unitIndex++;
        }

        return string.Create(CultureInfo.InvariantCulture, $"{value:N1} {ByteUnits[unitIndex]}");
    }

    /// <summary>
    /// Build metadata stamped into the assembly at compile time, used by
    /// `--version` so dev builds and tagged releases are distinguishable in
    /// bug reports (#1550). Any field can be "unknown" when the build host
    /// lacks git (e.g. a tarball-only checkout).
    /// `--version` がバグ報告で dev ビルドとタグ済みリリースを区別できる
    /// よう、ビルド時にアセンブリへ刻んだメタデータ (#1550)。git の無い
    /// ビルドホストでは各フィールドが "unknown" になりうる。
    /// </summary>
    public sealed record BuildMetadata(string Version, string Commit, string BuildDate, string Dirty);

    /// <summary>
    /// Load the full build metadata: semver from version.json plus commit/build
    /// date/dirty flag stamped into the assembly via <c>AssemblyMetadataAttribute</c>.
    /// version.json の semver と、AssemblyMetadataAttribute で刻まれた
    /// commit / build date / dirty フラグを合わせて読み込む。
    /// </summary>
    public static BuildMetadata LoadBuildMetadata()
    {
        var assembly = typeof(ConsoleUi).Assembly;
        return new BuildMetadata(
            Version: LoadVersion(),
            Commit: ReadAssemblyMetadata(assembly, "CdidxCommit"),
            BuildDate: ReadAssemblyMetadata(assembly, "CdidxBuildDate"),
            Dirty: ReadAssemblyMetadata(assembly, "CdidxBuildDirty"));
    }

    private static string ReadAssemblyMetadata(Assembly assembly, string key)
    {
        foreach (var attr in assembly.GetCustomAttributes<AssemblyMetadataAttribute>())
        {
            if (string.Equals(attr.Key, key, StringComparison.Ordinal))
                return string.IsNullOrWhiteSpace(attr.Value) ? "unknown" : attr.Value!;
        }
        return "unknown";
    }

    // --- Usage / 使い方 ---

    /// <summary>
    /// Print usage information.
    /// 使い方を表示する。
    /// </summary>
    public static void PrintUsage(bool showBanner = true)
        => PrintUsageBrief(showBanner);

    public static void PrintUsageBrief(bool showBanner = true)
    {
        if (showBanner)
        {
            PrintBanner();
        }

        Console.WriteLine("Usage:");
        Console.WriteLine("  cdidx <projectPath>");
        Console.WriteLine("  cdidx <command> [options]");
        Console.WriteLine("  cdidx --help-all");
        Console.WriteLine("  cdidx --help-flags");
        Console.WriteLine();
        Console.WriteLine("Commands:");
        Console.WriteLine("  index <projectPath>        Build or update the index for a project");
        Console.WriteLine("  search <query>             Full-text search across indexed chunks");
        Console.WriteLine("  definition <query>         Resolve symbol definitions with extracted ranges");
        Console.WriteLine("  references <query>         Find indexed references for a symbol (--kind uses reference kind)");
        Console.WriteLine("  callers <query>            Find callers of a symbol (--kind uses reference kind)");
        Console.WriteLine("  callees <query>            Find callees used by a caller (--kind uses reference kind)");
        Console.WriteLine("  symbols [query]            Search symbols (functions, classes, imports)");
        Console.WriteLine("  map                        Show a repo-level overview for AI orientation");
        Console.WriteLine("  inspect <query>            Bundle definition, graph, and nearby symbol context");
        Console.WriteLine("  status                     Show database statistics, freshness, config, and logs");
        Console.WriteLine("  doctor                     Print a redacted environment summary for bug reports");
        Console.WriteLine("  validate                   Report encoding issues (U+FFFD origin/severity, BOM, null bytes, mixed line endings, UTF-16 BOM, likely non-UTF8)");
        Console.WriteLine("  impact <query>             Show transitive callers; type queries may return heuristic file-level dependency hints");
        Console.WriteLine("  deps                       Show file-level dependency edges from the reference graph");
        Console.WriteLine("  unused                     Find symbols defined but never referenced (dead code)");
        Console.WriteLine("  hotspots                   Find high-impact symbols; duplicate-name families may fall back conservatively");
        Console.WriteLine("  export                     Export ctags or a portable CodeIndex archive");
        Console.WriteLine("  import                     Import a portable CodeIndex archive");
        Console.WriteLine("  batch                      Run newline-delimited JSON query commands with one DB connection");
        Console.WriteLine("  mcp                        Start MCP server (for AI tools: Claude, Cursor, etc.)");
        Console.WriteLine("  lsp                        Start LSP server over stdio (for LSP-native editors)");
        Console.WriteLine("  completions <shell>        Generate shell completions for bash, zsh, fish, or PowerShell");
        Console.WriteLine();
        Console.WriteLine("Run `cdidx --help-all` for every command and option, `cdidx --help-flags` for shared flags, or `cdidx <command> --help` for one command.");
        Console.WriteLine();
        Console.WriteLine("Examples:");
        Console.WriteLine("  cdidx ./myproject");
        Console.WriteLine("  cdidx search \"authenticate\"");
        Console.WriteLine("  cdidx inspect Run --body --exclude-tests");
    }

    public static void PrintUsageFull(bool showBanner = true)
    {
        if (showBanner)
        {
            PrintBanner();
        }

        var helpWidth = ShouldUseInteractiveConsole() ? Math.Min(GetWindowWidth(), 120) : 0;
        void WriteHelpLine(string line = "")
        {
            if (helpWidth <= 0)
            {
                Console.WriteLine(line);
                return;
            }

            foreach (var wrapped in WrapHelpLine(line, helpWidth))
                Console.WriteLine(wrapped);
        }

        Console.WriteLine("Usage:");
        Console.WriteLine("  cdidx <projectPath>");
        foreach (var (_, usage) in CommandUsageLines)
            WriteHelpLine($"  {usage}");
        Console.WriteLine();
        PrintCommandSummary();
        Console.WriteLine();
        PrintFlagReference(WriteHelpLine);
        Console.WriteLine();
        PrintExamples();
    }

    public static void PrintFlagUsage(bool showBanner = true)
    {
        if (showBanner)
        {
            PrintBanner();
        }

        var helpWidth = ShouldUseInteractiveConsole() ? Math.Min(GetWindowWidth(), 120) : 0;
        void WriteHelpLine(string line = "")
        {
            if (helpWidth <= 0)
            {
                Console.WriteLine(line);
                return;
            }

            foreach (var wrapped in WrapHelpLine(line, helpWidth))
                Console.WriteLine(wrapped);
        }

        Console.WriteLine("Usage:");
        Console.WriteLine("  cdidx --help-flags");
        Console.WriteLine();
        PrintFlagReference(WriteHelpLine);
        Console.WriteLine();
        Console.WriteLine("Run `cdidx --help-all` to show commands and examples.");
    }

    private static void PrintCommandSummary()
    {
        Console.WriteLine("Commands:");
        Console.WriteLine("  index <projectPath>        Build or update the index for a project");
        Console.WriteLine("  backfill-fold              Upgrade folded-name columns in an existing index DB");
        Console.WriteLine("  optimize                   Optimize FTS5 segments in an existing index DB");
        Console.WriteLine("  vacuum                     Reclaim free SQLite pages from an existing index DB");
        Console.WriteLine("  search <query>             Full-text search across indexed chunks");
        Console.WriteLine("  definition <query>         Resolve symbol definitions with extracted ranges");
        Console.WriteLine("  goto <query>               Return one best LSP Location for a definition");
        Console.WriteLine("  references <query>         Find indexed references for a symbol (--kind uses reference kind)");
        Console.WriteLine("  callers <query>            Find callers of a symbol (--kind uses reference kind)");
        Console.WriteLine("  callees <query>            Find callees used by a caller (--kind uses reference kind)");
        Console.WriteLine("  symbols [query]            Search symbols (functions, classes, imports)");
        Console.WriteLine("  files [query]              List indexed files");
        Console.WriteLine("  find <query>               Find literal substring matches inside known indexed files");
        Console.WriteLine("  excerpt <path>             Reconstruct a line-range excerpt from indexed chunks");
        Console.WriteLine("  map                        Show a repo-level overview for AI orientation");
        Console.WriteLine("  inspect <query>            Bundle definition, graph, and nearby symbol context");
        Console.WriteLine("  outline <path>             Show a file outline ordered by line, start column, kind, and name");
        Console.WriteLine("  status                     Show database statistics; add --check for freshness, --config for effective config, --explain <field> for readiness, or --log-path for logs");
        Console.WriteLine("  workspace                  List manifest members and manage the active workspace");
        Console.WriteLine("  config show                Show resolved workspace config and precedence");
        Console.WriteLine("  upgrade                    Check for and install the latest release via install.sh");
        Console.WriteLine("  validate-config            Validate .cdidx/config.json or .cdidxrc.json");
        Console.WriteLine("  doctor                     Print a redacted environment summary for bug reports");
        Console.WriteLine("  db --integrity-check       Run SQLite `PRAGMA integrity_check` and report findings");
        Console.WriteLine("  db schema                  Dump SQLite schema entries and PRAGMA user_version");
        Console.WriteLine("  db prune --dry-run|--apply Count or delete orphaned DB rows");
        Console.WriteLine("  diff <db1> <db2>           Compare two index databases; exit 0 identical, 1 drift, 2 schema mismatch, 3 unreadable");
        Console.WriteLine("  report --output <path>     Build a redacted crash-repro tarball (.tgz) for bug reports");
        Console.WriteLine("  validate                   Report encoding issues (U+FFFD origin/severity, BOM, null bytes, mixed line endings, UTF-16 BOM, likely non-UTF8)");
        Console.WriteLine("  impact <query>             Show transitive callers; type queries may return heuristic file-level dependency hints");
        Console.WriteLine("  deps                       Show file-level dependency edges from the reference graph");
        Console.WriteLine("  unused                     Find symbols defined but never referenced (dead code)");
        Console.WriteLine("  hotspots                   Find high-impact symbols; duplicate-name families may fall back conservatively");
        Console.WriteLine("  suggestions                List, inspect, and export local suggestion history");
        Console.WriteLine("  export                     Export ctags or a portable CodeIndex archive");
        Console.WriteLine("  import                     Import a portable CodeIndex archive");
        Console.WriteLine("  languages                  List supported languages and their capabilities");
        Console.WriteLine("  batch                      Run newline-delimited JSON query commands with one DB connection");
        Console.WriteLine("  mcp                        Start MCP server (for AI tools: Claude, Cursor, etc.)");
        Console.WriteLine("  lsp                        Start LSP server over stdio (for LSP-native editors)");
        Console.WriteLine("  completions <shell>        Generate shell completions for bash, zsh, fish, or PowerShell");
        Console.WriteLine("  license                    Show licensing, trademark, and commercial-use summary");
    }

    private static void PrintFlagReference(Action<string> WriteHelpLine)
    {
        Console.WriteLine();
        Console.WriteLine("Index and update options:");
        Console.WriteLine("  --db <path>                Database file path (default for index: <projectPath>/.cdidx/codeindex.db)");
        WriteHelpLine("  .cdidxignore               Optional project-local ignore file; loaded after .gitignore in each directory");
        Console.WriteLine("  --rebuild                  Delete existing DB and rebuild from scratch");
        Console.WriteLine("  --verbose                  Show per-file status ([OK  ]/[SKIP]/[DEL ]/[ERR ])");
        Console.WriteLine("  --dry-run                  Scan files without writing to the database");
        Console.WriteLine("  --force                    Bypass the per-database index lock; only use when no other cdidx index is active");
        Console.WriteLine("  --json                     Output results as JSON (for AI/machine use)");
        Console.WriteLine("  --memory-trace             Include phase memory samples in index JSON output");
        Console.WriteLine("  --quiet, -q, --silent      Suppress informational stderr output; errors still print (also honors CDIDX_QUIET=1)");
        Console.WriteLine("  --duration-format <format> Index elapsed time format: `auto` (default), `seconds`, or `hms`; JSON keeps raw elapsed_ms");
        WriteHelpLine("  --notify <mode>           Long index completion signal: auto, bell, osc9, desktop, or none (also honors CDIDX_NOTIFY; quiet/json suppress it)");
        WriteHelpLine("  --max-file-bytes <bytes>  Index only files up to this size (default: 4MiB; also honors CDIDX_MAX_FILE_BYTES; accepts K/M/G suffixes)");
        WriteHelpLine("  --max-symbols-per-file <n> Skip file content, symbols, and references when one file emits too many symbols (default: 5000)");
        WriteHelpLine("  --parallelism <n>         Full-scan extraction workers (default: CPU count capped at 16; also honors CDIDX_INDEX_PARALLELISM)");
        WriteHelpLine("  --follow-symlinks <mode>  Directory symlink policy: none (default), internal, or all");
        WriteHelpLine("  --include-symbol-kind <kind>[,<kind>]  Keep only matching symbol kinds during indexing");
        WriteHelpLine("  --exclude-symbol-kind <kind>[,<kind>]  Drop matching symbol kinds during indexing");
        Console.WriteLine("  --commits <commit-ref> [commit-ref ...]");
        Console.WriteLine("                              Update only files changed in the specified git commits (preferred after commits)");
        Console.WriteLine("  --changed-between <old-ref> <new-ref>");
        Console.WriteLine("                              Update only files changed between two git refs (useful after branch switches)");
        Console.WriteLine("  --files <path> [path ...]  Update only the specified files; old rename/delete paths are not purged unless also listed");
        WriteHelpLine("  --watch                    After the initial scan, stay running and reindex on file changes (FileSystemWatcher / inotify / FSEvents); rejects --commits / --changed-between / --files / --dry-run");
        Console.WriteLine("  --debounce <ms>            Watch only: coalesce bursts of file events into one update after <ms> of quiet (default: 500)");
        Console.WriteLine("  --optimize                 index only: optimize the existing FTS5 table for this project's DB without scanning files");
        WriteHelpLine("  --color <when>             Color output: `auto` (default), `always`, or `never`; flag wins over `CLICOLOR_FORCE` / `NO_COLOR` / `CLICOLOR` env vars, which win over TTY auto-detect");
        WriteHelpLine("  --palette <name>           ANSI palette: `basic` (8-color, default fallback), `256`, or `truecolor`; flag wins over `CDIDX_COLOR_PALETTE` env var, which wins over `COLORTERM` / `TERM` auto-detect");
        WriteHelpLine("  --ascii                    Use ASCII spinner/progress glyphs instead of Unicode glyphs (also honors CDIDX_ASCII=1, NO_UNICODE, TERM=dumb, accessibility env hints, and non-UTF-8 locales)");
        WriteHelpLine("  --no-progress              Disable animated progress/spinner output (also honors CDIDX_DISABLE_PROGRESS=1 and PREFERS_REDUCED_MOTION)");
        Console.WriteLine("  --metrics <path>           Append one JSONL record per CLI command / MCP tool call to <path> (also honors CDIDX_METRICS=<path>)");
        Console.WriteLine("  --help, -h                 Show this help message");
        Console.WriteLine("  --version, -V              Show version information");
        Console.WriteLine("  --license                  Show licensing, trademark, and commercial-use summary");
        Console.WriteLine("  --completions <shell>      Generate shell completions (bash, zsh, fish, powershell)");
        Console.WriteLine();
        Console.WriteLine("Update workflows:");
        Console.WriteLine("  Use --commits with a project path after normal commits; git diff sees rename/delete paths too.");
        Console.WriteLine("  Use --changed-between <old-ref> <new-ref> after switching branches to refresh only changed files.");
        Console.WriteLine("  Use --files only for known in-place edits or new files; old rename/delete paths stay indexed unless also listed.");
        Console.WriteLine("  Incremental writes optimize FTS5 opportunistically after a small maintenance threshold; run `cdidx optimize` for manual maintenance.");
        Console.WriteLine();
        Console.WriteLine("Query options:");
        Console.WriteLine("  --db <path>                Database file path (default: .cdidx/codeindex.db in current directory)");
        WriteHelpLine("  --json                     Output as JSON (search streams ndjson by default; use search --json=array for one array)");
        WriteHelpLine("  --verbose                  Query commands: emit debug diagnostics to stderr; with --json, append an _debug JSON object");
        WriteHelpLine("  --quiet, -q, --silent      Query commands: suppress informational stderr output, including zero-result hints and summaries; errors still print. Overrides --verbose stderr text.");
        WriteHelpLine("  --profile                  Read commands: append SQL timing, row-count, and EXPLAIN QUERY PLAN JSON after the normal result");
        WriteHelpLine("  --slow-query-ms <n>        Read commands: log profiled SQL statements that take at least <n> ms (use 0 to log every statement)");
        Console.WriteLine("  --limit <n>, --top <n>     Max results to return (default: 20)");
        Console.WriteLine("  --lang <lang>              Filter by language (aliases: bat, cmd, cshtml, razor, ts, tsx, cts, mts)");
        Console.WriteLine("  --path <glob>              Restrict matches to glob-style path patterns (* and ?)");
        WriteHelpLine($"  --query <query>            Pass a query literal, useful when the query starts with '-' (`search` max {QueryLimits.MaxQueryLength} chars)");
        Console.WriteLine("  --exclude-path <glob>      Exclude glob-style path patterns (* and ?) (repeatable)");
        Console.WriteLine("  --exclude-tests            Exclude likely test files");
        Console.WriteLine("  --include-generated        Include generated files in query results");
        Console.WriteLine("  --snippet-lines <n>        search/find snippet length (1-20, default: search 8; find 1)");
        Console.WriteLine("  --snippet-focus <mode>     search only: long-line focus mode (leftmost|quality|proximity, default: quality)");
        WriteHelpLine($"  --max-line-width <n>       search/references/callers/callees/find/excerpt/impact/inspect only: clamp very long single-line snippet/context/excerpt payloads (`0` disables clamping; default: {LineWidthFormatter.DefaultMaxLineWidth})");
        Console.WriteLine("  --focus-line <line>        find/excerpt: focus a specific line");
        Console.WriteLine("  --focus-column <n>         find/excerpt: focus a specific 1-based column");
        Console.WriteLine("  --focus-length <n>         excerpt: width of the focused span (default: 1, requires --focus-column)");
        WriteHelpLine($"  --fts                      Use raw FTS5 query syntax for search (content:term, NEAR(a b, 5), OR, NOT, groups, prefix*, \"phrase\"; search query max {QueryLimits.MaxQueryLength} chars; raw FTS parser max {DbReader.MaxRawFtsQueryLength} chars, {DbReader.MaxRawFtsBooleanOperators} boolean ops, {DbReader.MaxRawFtsNearOperators} NEAR ops; trailing * is a prefix shorthand in literal-safe mode)");
        Console.WriteLine("  --exact                    Backward-compatible shorthand.");
        Console.WriteLine("                              Prefer --exact-substring for search,");
        Console.WriteLine("                              --exact for find,");
        Console.WriteLine("                              and --exact-name for symbol/graph lookups.");
        Console.WriteLine("                              Combining exact-match flags is rejected.");
        Console.WriteLine("  --exact-substring          Search only: case-sensitive exact substring");
        Console.WriteLine("                              (no FTS5)");
        Console.WriteLine("  --exact-name               Exact name match for symbols, definition,");
        Console.WriteLine("                              references, callers, callees, and inspect.");
        Console.WriteLine("                              Uses NFKC + Unicode CaseFold when ready.");
        Console.WriteLine("                              Legacy/stale-fold DBs fall back to ASCII NOCASE;");
        Console.WriteLine("                              run `cdidx backfill-fold` or check fold_ready.");
        WriteHelpLine("  --kind <kind>              definition/symbols/hotspots/unused: symbol kind; references: reference kind (call/instantiate/subscribe/attribute/annotation); callers/callees: call-graph kinds only (call/instantiate/subscribe — metadata kinds rejected, use references instead); validate: issue kind");
        Console.WriteLine("  --visibility <v[,v]>       Filter symbols/definitions/unused/hotspots by visibility: public, protected, internal, private");
        WriteHelpLine("  --exclude-visibility <v[,v]> Exclude symbols/definitions/unused/hotspots by visibility");
        WriteHelpLine("  --count                    Count only; search/definition/references/callers/callees/symbols/files/find/unused ignore --limit, impact/hotspots still use visible page counts");
        Console.WriteLine("  --since <datetime>         Filter to files modified since this timestamp (ISO 8601)");
        Console.WriteLine("  --no-dedup                 search only: return every raw overlapping chunk hit (debug/density)");
        WriteHelpLine($"  --require-before/--require-after <query>  search only: keep primary matches only when the guard query appears within --guard-window lines before/after the match (default {DbReader.DefaultSearchGuardWindow}, max {DbReader.MaxSearchGuardWindow})");
        WriteHelpLine("  --reject-before/--reject-after <query>    search only: drop primary matches when the guard query appears within the same before/after window; useful for finding API calls missing nearby checks");
        Console.WriteLine("  --bytes                    Show raw byte counts in human output for files/map instead of binary units; JSON always keeps raw integer bytes");
        Console.WriteLine("  --min-entrypoint-confidence <n>  map only: omit entrypoint candidates below this 0.0..1.0 confidence");
        WriteHelpLine("  --max-hops <n>             Max BFS hops for impact analysis, inclusive (default: 5; --max-hops 2 returns callers at hop 1 and 2; --max-hops 0 resolves the symbol without traversing callers)");
        Console.WriteLine("  --depth <n>                Deprecated alias for --max-hops");
        Console.WriteLine("  --reverse                  Reverse direction for deps (show dependents)");
        WriteHelpLine("  --group-by-name            hotspots: collapse rows sharing (name, kind) across files; JSON count is the number of name/kind groups, not reference, file, or definition-site count");
        WriteHelpLine("  --with-paths               impact: also emit `paths` per caller — the shortest call chains [root, ..., caller] (diamond graphs surface every converging route, capped per row)");
        WriteHelpLine("  unused reflection note     C# nameof/typeof and direct reflection member-name literals such as GetMethod(\"Foo\") are indexed; dynamically constructed reflection names may need manual review");
        WriteHelpLine("  Note: if a query itself starts with '-', pass it with --query <query> or -- <query>; for option values that start with '--', use --opt=<value>.");
    }

    private static void PrintExamples()
    {
        Console.WriteLine("Examples:");
        Console.WriteLine("  cdidx ./myproject                             Index a project");
        Console.WriteLine("  cdidx backfill-fold                           Upgrade folded-name columns in an existing DB");
        Console.WriteLine("  cdidx optimize                                Optimize FTS5 segments in an existing DB");
        Console.WriteLine("  cdidx vacuum --json                           Reclaim DB free pages and report page metrics");
        Console.WriteLine("  cdidx index ./myproject --commits abc123      Update DB from one commit");
        Console.WriteLine("  cdidx index ./myproject --commits abc123 def456");
        Console.WriteLine("                                              Update DB from multiple commits");
        Console.WriteLine("  cdidx index ./myproject --changed-between main feature");
        Console.WriteLine("                                              Update DB from files changed between two refs");
        Console.WriteLine("  cdidx index ./myproject --files src/app.cs    Update specific files");
        Console.WriteLine("  cdidx index ./myproject --watch               Run an initial scan, then keep the index live as files change (Ctrl+C to stop)");
        Console.WriteLine("  cdidx export ctags --output tags              Export editor tags for Vim, Emacs, and Sublime");
        Console.WriteLine("  cdidx export codeindex.cdidx.zip              Export a portable CodeIndex archive");
        Console.WriteLine("  cdidx import codeindex.cdidx.zip              Import a portable CodeIndex archive");
        Console.WriteLine("  cdidx search \"authenticate\"                    Full-text search");
        Console.WriteLine("  cdidx search \"auth*\"                          Prefix shorthand in literal-safe mode");
        Console.WriteLine("  cdidx search --query --path --path README.md   Search for a literal option token");
        Console.WriteLine("  cdidx search \"Run();\" --exact-substring        Case-sensitive exact substring search");
        Console.WriteLine("  cdidx search \"File.ReadAllText\" --exact-substring --reject-before \"Length\" --guard-window 8");
        Console.WriteLine("                                              Find calls without a nearby preceding size guard");
        Console.WriteLine("  cdidx search authenticate --json=array         Emit search results as one JSON array");
        Console.WriteLine("  cdidx search authenticate --profile            Append SQL profile JSON for slow-query debugging");
        Console.WriteLine("  cdidx search authenticate --verbose            Emit query debug diagnostics on stderr");
        Console.WriteLine("  cdidx definition ResolveGitCommonDir --body   Show a symbol definition and body");
        Console.WriteLine("  cdidx references ResolveGitCommonDir          Find indexed references");
        Console.WriteLine("  cdidx references DbContext --kind instantiate Filter constructor sites by reference kind");
        Console.WriteLine("  cdidx references e --path dist/app.js --max-line-width 120");
        Console.WriteLine("                                              Clamp a minified single-line context window");
        Console.WriteLine("  cdidx excerpt src/app.js --start 120 --focus-column 88 --max-line-width 120");
        Console.WriteLine("                                              Keep the requested token visible inside a long line");
        Console.WriteLine("  cdidx callers ResolveGitCommonDir             Find callers");
        Console.WriteLine("  cdidx callees AddToGitExclude                 Find callees used by a caller");
        Console.WriteLine("  cdidx symbols Run --exact-name                Exact symbol-name match");
        Console.WriteLine("  cdidx symbols UserService --kind class        Find class definitions");
        Console.WriteLine("  cdidx find guard --path src/Auth.cs --after 2 Find literal matches inside a known file");
        Console.WriteLine("  cdidx find --path README.md -- --path         Search a literal that starts with '-'");
        Console.WriteLine("  cdidx excerpt src/app.cs --start 10 --end 20  Reconstruct a file excerpt");
        Console.WriteLine("  cdidx map --path src/ --exclude-tests          Show a repo map for source code");
        Console.WriteLine("  cdidx inspect Run --body --exclude-tests       Inspect one symbol with bundled context");
        Console.WriteLine("  cdidx outline src/app.cs --json                Symbol outline of a single file");
        Console.WriteLine("  cdidx deps --path src/ --exclude-tests          Show file-level dependency edges");
        Console.WriteLine("  cdidx deps --reverse --path src/app.cs          Show what depends on a file");
        Console.WriteLine("  cdidx unused --lang csharp --exclude-tests      Find potentially unused symbols");
        Console.WriteLine("  cdidx hotspots --lang csharp --exclude-tests    Find high-impact symbols with conservative duplicate fallback");
        Console.WriteLine("  cdidx hotspots --group-by=file --json           Compare hotspot volume by target file");
        Console.WriteLine("  cdidx hotspots --group-by-name --exclude-tests  Collapse same-name hotspots across files");
        Console.WriteLine("  cdidx impact Run --max-hops 0 --exclude-tests  Resolve a symbol without traversing callers");
        Console.WriteLine("  cdidx impact FolderDiffService --json           Type query may return heuristic file-level dependency hints");
        Console.WriteLine("  cdidx files --lang python                      List Python files");
        Console.WriteLine("  cdidx files --since 2024-01-01                 Files modified since a date");
        Console.WriteLine("  cdidx status --json                            DB stats as JSON");
        Console.WriteLine("  cdidx status --config                          Effective configuration as JSON");
        Console.WriteLine("  cdidx validate-config                          Validate checked-in config");
        Console.WriteLine("  cdidx languages                                Show supported languages");
        Console.WriteLine("  cdidx --completions zsh > ~/.zfunc/_cdidx      Generate a zsh completion script");
        Console.WriteLine("  cdidx license                                  Show licensing and commercial-use terms");
    }

    internal static IReadOnlyList<string> WrapHelpLine(string line, int maxWidth)
    {
        if (maxWidth <= 0 || line.Length <= maxWidth)
            return [line];

        var continuationIndent = GetHelpContinuationIndent(line);
        return WrapLineByWords(line, maxWidth, continuationIndent);
    }

    private static string GetHelpContinuationIndent(string line)
    {
        var leading = 0;
        while (leading < line.Length && line[leading] == ' ')
            leading++;

        for (var i = leading + 1; i < line.Length - 1; i++)
        {
            if (line[i] == ' ' && line[i + 1] == ' ')
            {
                while (i < line.Length && line[i] == ' ')
                    i++;
                if (i < line.Length)
                    return new string(' ', i);
                break;
            }
        }

        return new string(' ', Math.Min(leading + 2, 8));
    }

    private static IReadOnlyList<string> WrapLineByWords(string line, int maxWidth, string continuationIndent)
    {
        maxWidth = Math.Max(1, maxWidth);
        if (continuationIndent.Length >= maxWidth)
            continuationIndent = new string(' ', Math.Max(0, Math.Min(2, maxWidth - 1)));

        var lines = new List<string>();
        var current = line;
        while (current.Length > maxWidth)
        {
            var breakAt = current.LastIndexOf(' ', Math.Min(maxWidth, current.Length - 1));
            if (breakAt <= 0 || current[..breakAt].Trim().Length == 0)
                breakAt = maxWidth;

            lines.Add(current[..breakAt].TrimEnd());
            var nextStart = breakAt < current.Length && current[breakAt] == ' ' ? breakAt + 1 : breakAt;
            current = continuationIndent + current[nextStart..].TrimStart();
        }

        lines.Add(current);
        return lines;
    }

    public static void PrintLicenseSummary()
    {
        Console.WriteLine("cdidx / CodeIndex license");
        Console.WriteLine();
        Console.WriteLine("License: Functional Source License, Version 1.1, ALv2 Future License (FSL-1.1-ALv2)");
        Console.WriteLine("Copyright: Copyright 2026 Widthdom.");
        Console.WriteLine("Summary: use, modification, and distribution are allowed for non-competing purposes, including internal, commercial, AI, IDE, MCP, CI, and scripting integrations.");
        Console.WriteLine("Competing commercial products or services require a separate written agreement with Widthdom.");
        Console.WriteLine("Names and trademarks: CodeIndex and cdidx are not licensed for derivative product, package, service, or endorsement branding.");
        Console.WriteLine();
        Console.WriteLine("See LICENSE, LICENSES/FSL-1.1-ALv2.txt, LICENSES/Apache-2.0.txt, COMMERCIAL_LICENSE.md, INTEGRATION_POLICY.md, and TRADEMARKS.md for the controlling terms.");
    }

    public static string? GetUsageLine(string command)
    {
        foreach (var (name, usage) in CommandUsageLines)
        {
            if (string.Equals(name, command, StringComparison.Ordinal))
                return usage;
        }

        return null;
    }

    public static bool PrintCommandUsage(string command)
    {
        var usages = GetCommandUsageLines(command);
        if (usages.Count == 0)
            return false;

        Console.WriteLine("Usage:");
        foreach (var usage in usages)
            Console.WriteLine($"  {usage}");
        var notes = GetCommandUsageNotes(command);
        if (notes.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("Notes:");
            foreach (var note in notes)
                Console.WriteLine($"  {note}");
        }
        Console.WriteLine();
        Console.WriteLine("Run `cdidx --help` to show all commands and shared options.");
        return true;
    }

    private static IReadOnlyList<string> GetCommandUsageLines(string command)
    {
        var usages = new List<string>();
        foreach (var (name, usage) in CommandUsageLines)
        {
            if (string.Equals(name, command, StringComparison.Ordinal)
                || string.Equals(command, "index", StringComparison.Ordinal) && name.StartsWith("index-", StringComparison.Ordinal))
            {
                usages.Add(usage);
            }
        }

        return usages;
    }

    private static IReadOnlyList<string> GetCommandUsageNotes(string command)
    {
        var notes = new List<string>();
        foreach (var (name, note) in CommandUsageNotes)
        {
            if (string.Equals(name, command, StringComparison.Ordinal))
                notes.Add(note);
        }

        return notes;
    }

    // --- Did-you-mean / もしかして ---

    /// <summary>
    /// Find the closest matching command name using Damerau-Levenshtein distance.
    /// Short commands use a stricter threshold to avoid unrelated suggestions.
    /// Damerau-Levenshtein距離で最も近いコマンド名を返す。短いコマンドは無関係な推薦を避けるため閾値を厳しくする。
    /// </summary>
    public static string? FindClosestCommand(string input) =>
        FindClosestMatch(input, Commands);

    /// <summary>
    /// Find the closest match for <paramref name="input"/> from <paramref name="candidates"/>
    /// using Damerau-Levenshtein distance with the same length-aware threshold the
    /// command suggester uses (#1582). Comparison is case-insensitive. Returns the original
    /// (cased) candidate string, or <c>null</c> when no candidate is within the threshold.
    /// 任意の候補集合に対して Damerau-Levenshtein 距離で最も近い候補を返す (#1582)。
    /// 短い入力には厳しめの距離閾値を適用し、無関係な推薦を避ける。比較は case-insensitive。
    /// </summary>
    public static string? FindClosestMatch(string? input, IEnumerable<string> candidates)
    {
        if (string.IsNullOrWhiteSpace(input))
            return null;

        var normalized = input.ToLowerInvariant();
        string? best = null;
        var bestDist = int.MaxValue;
        foreach (var candidate in candidates)
        {
            if (string.IsNullOrEmpty(candidate))
                continue;
            var candidateNormalized = candidate.ToLowerInvariant();
            if (string.Equals(normalized, candidateNormalized, StringComparison.Ordinal))
                return candidate;
            var dist = DamerauLevenshteinDistance(normalized, candidateNormalized);
            if (dist > GetSuggestionDistanceThreshold(normalized.Length, candidateNormalized.Length))
                continue;
            if (dist < bestDist)
            {
                bestDist = dist;
                best = candidate;
            }
        }
        return best;
    }

    /// <summary>
    /// Return up to <paramref name="maxResults"/> closest candidates for <paramref name="input"/>,
    /// ordered by Damerau-Levenshtein distance. Useful for structured suggestions in MCP
    /// error payloads (#1582). Returns an empty list when no candidate is within the threshold.
    /// Damerau-Levenshtein 距離で近い候補を最大 <paramref name="maxResults"/> 件まで返す。
    /// MCP の structured error payload で `similar_values` を返す用途を想定する (#1582)。
    /// </summary>
    public static IReadOnlyList<string> FindClosestMatches(string? input, IEnumerable<string> candidates, int maxResults = 3)
    {
        if (string.IsNullOrWhiteSpace(input) || maxResults <= 0)
            return Array.Empty<string>();

        var normalized = input.ToLowerInvariant();
        var matches = new List<(string Candidate, int Distance)>();
        foreach (var candidate in candidates)
        {
            if (string.IsNullOrEmpty(candidate))
                continue;
            var candidateNormalized = candidate.ToLowerInvariant();
            if (string.Equals(normalized, candidateNormalized, StringComparison.Ordinal))
                continue;
            var dist = DamerauLevenshteinDistance(normalized, candidateNormalized);
            if (dist > GetSuggestionDistanceThreshold(normalized.Length, candidateNormalized.Length))
                continue;
            matches.Add((candidate, dist));
        }
        return matches
            .OrderBy(m => m.Distance)
            .ThenBy(m => m.Candidate, StringComparer.Ordinal)
            .Select(m => m.Candidate)
            .Take(maxResults)
            .ToList();
    }

    private static int GetSuggestionDistanceThreshold(int inputLength, int commandLength)
    {
        var shorter = Math.Min(inputLength, commandLength);
        return shorter switch
        {
            <= 4 => 1,
            <= 10 => 2,
            _ => 3,
        };
    }

    private static int DamerauLevenshteinDistance(string s, string t)
    {
        var n = s.Length;
        var m = t.Length;
        var d = new int[n + 1, m + 1];
        for (var i = 0; i <= n; i++) d[i, 0] = i;
        for (var j = 0; j <= m; j++) d[0, j] = j;
        for (var i = 1; i <= n; i++)
        {
            for (var j = 1; j <= m; j++)
            {
                var cost = s[i - 1] == t[j - 1] ? 0 : 1;
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + cost);
                if (i > 1 && j > 1 && s[i - 1] == t[j - 2] && s[i - 2] == t[j - 1])
                    d[i, j] = Math.Min(d[i, j], d[i - 2, j - 2] + 1);
            }
        }
        return d[n, m];
    }

    // --- Shell Completions / シェル補完 ---

    private static readonly string[] Commands =
    [
        "index", "backfill-fold", "optimize", "search", "definition", "goto", "references", "callers", "callees",
        "symbols", "files", "find", "excerpt", "map", "inspect", "outline", "status", "validate-config",
        "validate", "deps", "impact", "unused", "hotspots", "languages", "batch", "mcp", "completions", "db", "vacuum", "report", "license", "upgrade",
    ];

    /// <summary>
    /// Print shell completion script for the specified shell.
    /// 指定シェル向けの補完スクリプトを出力する。
    /// </summary>
    /// <summary>
    /// Print shell completion script. Returns false for unknown shells.
    /// シェル補完スクリプトを出力。不明なシェルの場合はfalseを返す。
    /// </summary>
    public static bool PrintCompletions(string shell)
    {
        try
        {
            Console.WriteLine(GetCompletionScript(shell));
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            Console.Error.WriteLine($"Unknown shell: {shell}. Supported: bash, zsh, fish, powershell");
            return false;
        }
    }

    internal static string GetCompletionScript(string shell) =>
        shell.ToLowerInvariant() switch
        {
            "bash" => GetBashCompletions(),
            "zsh" => GetZshCompletions(),
            "fish" => GetFishCompletions(),
            "powershell" or "pwsh" => GetPowerShellCompletions(),
            _ => throw new ArgumentOutOfRangeException(nameof(shell), shell, "Unknown shell"),
        };

    /// <summary>
    /// Get sorted unique language names from FileIndexer for completion values.
    /// 補完値用にFileIndexerからソート済みのユニークな言語名を取得する。
    /// </summary>
    private static string GetCompletionLangs() =>
        string.Join(" ", FileIndexer.GetLanguageExtensions().Values
            .Append("bat")
            .Append("cmd")
            .Concat(QueryCommandRunner.GetCompletionLanguageAliases())
            .Distinct()
            .OrderBy(l => l));

    private static string GetCompletionKinds() =>
        string.Join(" ", new[]
        {
            "accessor", "annotation", "associatedtype", "attribute", "augmentation",
            "call", "class", "class_hook", "consumes_hook", "constant", "constructor",
            "delegate", "enum", "event", "field", "friend", "function", "heading",
            "hook", "impl", "import", "instantiate", "interface", "label", "lambda",
            "method", "module", "namespace", "object", "operator", "procedure",
            "property", "razor_event_binding", "record", "reference", "specialization",
            "struct", "subscribe", "test.method", "trait", "type", "type_reference",
            "typealias", "union", "unsubscribe", "variable",
        }.OrderBy(k => k, StringComparer.Ordinal));

    // Commands that get their own per-command completion branch (bash/zsh). Order matters: the
    // `else` generic branch is the catch-all, and `search` must remain the last `elif` so the
    // tests `PrintCompletions_BashAndZshScopeMaxLineWidthToSearchBranch` can isolate it.
    // bash / zsh の専用ブランチを持つコマンド。順序は意図的で、`search` が最終 elif、`else` が
    // generic catch-all となるよう揃える。テストもこの並びを前提にしている。
    private static readonly string[] EnumeratedCompletionCommands =
    [
        "find", "excerpt", "references", "inspect", "hotspots", "status", "validate-config", "db", "report", "search",
    ];

    // Generic-branch representative set: union of completion flags from these commands populates
    // the bash/zsh `else` branch. Excludes find/excerpt/etc. which have their own branches, and
    // intentionally omits commands whose flags would surface in their own branches.
    // generic ブランチを構成する代表コマンド集合。専用ブランチを持つコマンドは除外。
    private static readonly string[] GenericBranchRepresentativeCommands =
    [
        "definition", "callers", "callees", "symbols", "files", "map", "impact", "deps", "unused",
    ];

    private static string GetBashCompletions()
    {
        var cmds = string.Join(" ", Commands);
        var langs = GetCompletionLangs();
        var kinds = GetCompletionKinds();
        var version = LoadVersion();
        var sb = new StringBuilder();
        sb.Append($"# cdidx bash completions generated for version {version}\n");
        sb.Append("# Regenerate this script after upgrading cdidx.\n");
        sb.Append("_cdidx() {\n");
        sb.Append("    local cur prev commands\n");
        sb.Append("    local cmd\n");
        sb.Append("    cur=\"${COMP_WORDS[COMP_CWORD]}\"\n");
        sb.Append("    prev=\"${COMP_WORDS[COMP_CWORD-1]}\"\n");
        sb.Append("    cmd=\"${COMP_WORDS[1]}\"\n");
        sb.Append($"    commands=\"{cmds}\"\n");
        sb.Append("\n");
        sb.Append("    if [ $COMP_CWORD -eq 1 ]; then\n");
        sb.Append("        COMPREPLY=($(compgen -W \"$commands --help --version --license\" -- \"$cur\"))\n");
        sb.Append("        return\n");
        sb.Append("    fi\n");
        sb.Append("\n");
        sb.Append("    case \"$prev\" in\n");
        sb.Append("        --db|--path|--exclude-path|--output|-o) COMPREPLY=($(compgen -f -- \"$cur\")) ;;\n");
        sb.Append($"        --lang) COMPREPLY=($(compgen -W \"{langs}\" -- \"$cur\")) ;;\n");
        sb.Append($"        --kind) COMPREPLY=($(compgen -W \"{kinds}\" -- \"$cur\")) ;;\n");
        sb.Append("        *)\n");
        for (var i = 0; i < EnumeratedCompletionCommands.Length; i++)
        {
            var command = EnumeratedCompletionCommands[i];
            var keyword = i == 0 ? "if" : "elif";
            sb.Append($"            {keyword} [ \"$cmd\" = \"{command}\" ]; then\n");
            sb.Append($"                COMPREPLY=($(compgen -W \"{BuildBashFlagList(command)}\" -- \"$cur\"))\n");
        }
        sb.Append("            else\n");
        sb.Append($"                COMPREPLY=($(compgen -W \"{BuildBashGenericFlagList()}\" -- \"$cur\"))\n");
        sb.Append("            fi\n");
        sb.Append("            ;;\n");
        sb.Append("    esac\n");
        sb.Append("}\n");
        sb.Append("complete -F _cdidx cdidx");
        return sb.ToString();
    }

    private static string BuildBashFlagList(string command)
    {
        // Per-command branch: schema flags + universal --help. `find` additionally surfaces
        // `--` as the end-of-options marker so users can pass literal queries starting with `-`.
        // schema のフラグに `--help` を加え、`find` のみ `--` end-of-options マーカーも露出させる。
        var tokens = new List<string>();
        foreach (var flag in CliFlagSchema.GetCompletionFlagsForCommand(command))
        {
            tokens.Add(flag.Name);
            if (flag.ShortName is not null)
                tokens.Add(flag.ShortName);
        }
        tokens.Add("--help");
        if (command == "find")
            tokens.Add("--");
        return string.Join(" ", tokens);
    }

    private static string BuildBashGenericFlagList()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var tokens = new List<string>();
        foreach (var command in GenericBranchRepresentativeCommands)
        {
            foreach (var flag in CliFlagSchema.GetCompletionFlagsForCommand(command))
            {
                // Skip flags that are scoped to the enumerated per-command branches; the generic
                // branch is the catch-all for "everything else".
                if (IsEnumeratedBranchScopedFlag(flag.Name))
                    continue;
                if (seen.Add(flag.Name))
                {
                    tokens.Add(flag.Name);
                    if (flag.ShortName is not null)
                        tokens.Add(flag.ShortName);
                }
            }
        }
        tokens.Add("--help");
        return string.Join(" ", tokens);
    }

    private static bool IsEnumeratedBranchScopedFlag(string flagName) =>
        flagName is "--max-line-width" or "--snippet-lines" or "--snippet-focus" or "--fts" or "--no-dedup" or "--no-visibility-rank"
            or "--prefix" or "--exact-substring" or "--integrity-check" or "--check" or "--stale-after"
            or "--start" or "--end" or "--focus-line" or "--focus-column" or "--focus-length"
            or "--before" or "--after" or "--group-by-name";

    private static string GetZshCompletions()
    {
        var cmds = string.Join(" ", Commands.Select(c => $"'{c}:{c} command'"));
        var langs = GetCompletionLangs();
        var kinds = GetCompletionKinds();
        var version = LoadVersion();
        var sb = new StringBuilder();
        sb.Append("#compdef cdidx\n");
        sb.Append($"# cdidx zsh completions generated for version {version}\n");
        sb.Append("# Regenerate this script after upgrading cdidx.\n");
        sb.Append("_cdidx() {\n");
        sb.Append("    local -a commands\n");
        sb.Append("    commands=(\n");
        sb.Append($"        {cmds}\n");
        sb.Append("    )\n");
        sb.Append("\n");
        sb.Append("    _arguments -C \\\n");
        sb.Append("        '1:command:->cmds' \\\n");
        sb.Append("        '*::arg:->args'\n");
        sb.Append("\n");
        sb.Append("    case $state in\n");
        sb.Append("        cmds) _describe 'command' commands ;;\n");
        sb.Append("        args)\n");
        sb.Append("            local subcmd\n");
        sb.Append("            subcmd=$words[2]\n");
        for (var i = 0; i < EnumeratedCompletionCommands.Length; i++)
        {
            var command = EnumeratedCompletionCommands[i];
            var keyword = i == 0 ? "if" : "elif";
            sb.Append($"            {keyword} [[ $subcmd == {command} ]]; then\n");
            AppendZshArguments(sb, BuildZshArgsForCommand(command, langs, kinds));
        }
        sb.Append("            else\n");
        AppendZshArguments(sb, BuildZshGenericArgs(langs, kinds));
        sb.Append("            fi\n");
        sb.Append("            ;;\n");
        sb.Append("    esac\n");
        sb.Append("}\n");
        sb.Append("_cdidx");
        return sb.ToString();
    }

    private static List<string> BuildZshArgsForCommand(string command, string langs, string kinds)
    {
        var args = new List<string>();
        foreach (var flag in CliFlagSchema.GetCompletionFlagsForCommand(command))
            args.AddRange(FormatZshArguments(flag, langs, kinds));
        // Append a trailing positional placeholder so zsh suggests path/query completion after
        // the flags — but only for commands that actually accept a positional argument. `status`,
        // `db`, `hotspots`, etc. would reject anything typed there, so emitting no placeholder
        // matches the original hand-written script's behavior.
        // 末尾 positional は path / query を受け付けるコマンドにのみ付ける。
        var positional = command switch
        {
            "excerpt" => "'*:path'",
            "find" or "search" or "references" or "inspect" => "'*:query'",
            _ => null,
        };
        if (positional is not null)
            args.Add(positional);
        return args;
    }

    private static List<string> BuildZshGenericArgs(string langs, string kinds)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var args = new List<string>();
        foreach (var command in GenericBranchRepresentativeCommands)
        {
            foreach (var flag in CliFlagSchema.GetCompletionFlagsForCommand(command))
            {
                if (IsEnumeratedBranchScopedFlag(flag.Name))
                    continue;
                if (seen.Add(flag.Name))
                    args.AddRange(FormatZshArguments(flag, langs, kinds));
            }
        }
        args.Add("'*:query'");
        return args;
    }

    private static IEnumerable<string> FormatZshArguments(CliFlag flag, string langs, string kinds)
    {
        yield return FormatZshArgument(flag.Name, flag, langs, kinds);
        if (flag.ShortName is not null)
            yield return FormatZshArgument(flag.ShortName, flag, langs, kinds);
    }

    private static string FormatZshArgument(string name, CliFlag flag, string langs, string kinds)
    {
        var desc = flag.Description.Replace("'", "''");
        if (!flag.IsValueBearing)
            return $"'{name}[{desc}]'";

        var valueSpec = flag.ValuePlaceholder switch
        {
            "<path>" => "file:_files",
            "<glob>" => "pattern",
            "<n>" => "number",
            "<line>" => "number",
            "<id>" => "id",
            "<datetime>" => "datetime",
            "<lang>" => $"language:({langs})",
            "<kind>" => $"kind:({kinds})",
            "<query>" => "query",
            "<name>" => "name",
            "<host:port>" => "address",
            "<stdio|http>" => "transport:(stdio http)",
            _ => "value",
        };
        return $"'{name}[{desc}]:{valueSpec}'";
    }

    private static void AppendZshArguments(StringBuilder sb, IReadOnlyList<string> args)
    {
        sb.Append("                _arguments");
        for (var i = 0; i < args.Count; i++)
        {
            sb.Append(" \\\n                    ");
            sb.Append(args[i]);
        }
        sb.Append('\n');
    }

    private static string GetFishCompletions()
    {
        var langs = GetCompletionLangs();
        var kinds = GetCompletionKinds();
        var lines = new List<string>
        {
            $"# cdidx fish completions generated for version {LoadVersion()}",
            "# Regenerate this script after upgrading cdidx.",
        };
        foreach (var cmd in Commands)
            lines.Add($"complete -c cdidx -n '__fish_use_subcommand' -a '{cmd}' -d '{cmd} command'");
        lines.Add("complete -c cdidx -n '__fish_use_subcommand' -l help -d 'Show help'");
        lines.Add("complete -c cdidx -n '__fish_use_subcommand' -l version -d 'Show version'");
        lines.Add("complete -c cdidx -n '__fish_use_subcommand' -l license -d 'Show license summary'");

        // Emit one `complete` line per schema flag, joining the applicable command list into the
        // fish `__fish_seen_subcommand_from` predicate. Hotspots' `--group-by-name` description is
        // shortened to match the legacy "Collapse same-name rows across files" tooltip that the
        // existing test pins (the schema description is fuller and still appears in zsh).
        // schema 1 行 = fish の 1 行 (`complete -c cdidx -n '__fish_seen_subcommand_from <cmds>' -l <name> ...`)
        // という対応で生成する。`--group-by-name` のみ既存テストが期待する短い tooltip を維持。
        foreach (var flag in CliFlagSchema.All)
        {
            var commands = string.Join(' ', flag.Commands.OrderBy(c => Array.IndexOf(Commands, c)));
            var name = flag.Name.TrimStart('-');
            // Token order is `-l name (-r)? (-a 'values')? -d 'description'` — matches the
            // pre-refactor hand-written script so the ConsoleUiTests fish-extractor regex
            // (`'  -l <flag>`) keeps working for value-bearing flags too.
            // トークン順は旧スクリプトと同じ `-l name (-r)? (-a) -d` を維持する。
            // ConsoleUiTests の fish 抽出正規表現が -l の直前に値マーカーを期待していないため。
            var requiresArg = flag.IsValueBearing ? " -r" : "";
            var shortName = flag.ShortName is null ? "" : $" -s {flag.ShortName.TrimStart('-')}";
            var description = name switch
            {
                "group-by-name" => "Collapse same-name rows across files",
                _ => flag.Description,
            };
            var argSpec = flag.ValuePlaceholder switch
            {
                "<lang>" => $" -a '{langs}'",
                "<kind>" => $" -a '{kinds}'",
                "<stdio|http>" => " -a 'stdio http'",
                _ => "",
            };
            description = description.Replace("'", "\\'");
            lines.Add($"complete -c cdidx -n '__fish_seen_subcommand_from {commands}' -l {name}{shortName}{requiresArg}{argSpec} -d '{description}'");
        }
        return string.Join(Environment.NewLine, lines);
    }

    private static string GetPowerShellCompletions()
    {
        var cmds = FormatPowerShellArray(Commands);
        var langs = FormatPowerShellArray(GetCompletionLangs().Split(' ', StringSplitOptions.RemoveEmptyEntries));
        var kinds = FormatPowerShellArray(GetCompletionKinds().Split(' ', StringSplitOptions.RemoveEmptyEntries));
        var sb = new StringBuilder();
        sb.AppendLine($"# cdidx PowerShell completions generated for version {LoadVersion()}");
        sb.AppendLine("# Regenerate this script after upgrading cdidx.");
        sb.AppendLine("Register-ArgumentCompleter -Native -CommandName cdidx -ScriptBlock {");
        sb.AppendLine("    param($wordToComplete, $commandAst, $cursorPosition)");
        sb.AppendLine($"    $commands = @({cmds})");
        sb.AppendLine($"    $langs = @({langs})");
        sb.AppendLine($"    $kinds = @({kinds})");
        sb.AppendLine("    $elements = @($commandAst.CommandElements)");
        sb.AppendLine("    $tokens = @($elements | ForEach-Object { $_.Extent.Text })");
        sb.AppendLine("    $lastElement = if ($elements.Count -ge 1) { $elements[$elements.Count - 1] } else { $null }");
        sb.AppendLine("    $afterLastToken = $lastElement -and $cursorPosition -gt $lastElement.Extent.EndOffset");
        sb.AppendLine("    $subcmd = $tokens | Where-Object { $_ -ne 'cdidx' -and -not $_.StartsWith('-') } | Select-Object -First 1");
        sb.AppendLine("    $prev = if ([string]::IsNullOrEmpty($wordToComplete) -and $tokens.Count -ge 1) { $tokens[$tokens.Count - 1] } elseif ($tokens.Count -ge 2) { $tokens[$tokens.Count - 2] } else { '' }");
        sb.AppendLine("    function New-CdidxCompletion($value, $kind = 'ParameterValue') {");
        sb.AppendLine("        [System.Management.Automation.CompletionResult]::new($value, $value, $kind, $value)");
        sb.AppendLine("    }");
        sb.AppendLine("    switch ($prev) {");
        sb.AppendLine("        { $_ -in @('--db', '--path', '--exclude-path', '--output', '-o') } {");
        sb.AppendLine("            Get-ChildItem -Name \"$wordToComplete*\" -ErrorAction SilentlyContinue | ForEach-Object { New-CdidxCompletion $_ 'ProviderItem' }");
        sb.AppendLine("            return");
        sb.AppendLine("        }");
        sb.AppendLine("        '--lang' { $langs | Where-Object { $_.StartsWith($wordToComplete, [System.StringComparison]::OrdinalIgnoreCase) } | ForEach-Object { New-CdidxCompletion $_ }; return }");
        sb.AppendLine("        '--kind' { $kinds | Where-Object { $_.StartsWith($wordToComplete, [System.StringComparison]::OrdinalIgnoreCase) } | ForEach-Object { New-CdidxCompletion $_ }; return }");
        sb.AppendLine("    }");
        sb.AppendLine("    if (-not $subcmd -or ($tokens.Count -le 2 -and -not ([string]::IsNullOrEmpty($wordToComplete)) -and -not $afterLastToken)) {");
        sb.AppendLine("        $commands + @('--help', '--version', '--license') | Where-Object { $_.StartsWith($wordToComplete, [System.StringComparison]::OrdinalIgnoreCase) } | ForEach-Object { New-CdidxCompletion $_ 'ParameterName' }");
        sb.AppendLine("        return");
        sb.AppendLine("    }");
        sb.AppendLine("    switch ($subcmd) {");
        foreach (var command in EnumeratedCompletionCommands)
            sb.AppendLine($"        '{EscapePowerShellSingleQuoted(command)}' {{ $flags = @({FormatPowerShellArray(BuildPowerShellFlagList(command))}) }}");
        sb.AppendLine($"        default {{ $flags = @({FormatPowerShellArray(BuildPowerShellGenericFlagList())}) }}");
        sb.AppendLine("    }");
        sb.AppendLine("    $flags | Where-Object { $_.StartsWith($wordToComplete, [System.StringComparison]::OrdinalIgnoreCase) } | ForEach-Object { New-CdidxCompletion $_ 'ParameterName' }");
        sb.Append("}");
        return sb.ToString();
    }

    private static List<string> BuildPowerShellFlagList(string command)
    {
        var tokens = new List<string>();
        foreach (var flag in CliFlagSchema.GetCompletionFlagsForCommand(command))
        {
            tokens.Add(flag.Name);
            if (flag.ShortName is not null)
                tokens.Add(flag.ShortName);
        }
        tokens.Add("--help");
        if (command == "find")
            tokens.Add("--");
        return tokens;
    }

    private static List<string> BuildPowerShellGenericFlagList()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var tokens = new List<string>();
        foreach (var command in GenericBranchRepresentativeCommands)
        {
            foreach (var flag in CliFlagSchema.GetCompletionFlagsForCommand(command))
            {
                if (IsEnumeratedBranchScopedFlag(flag.Name))
                    continue;
                if (seen.Add(flag.Name))
                {
                    tokens.Add(flag.Name);
                    if (flag.ShortName is not null)
                        tokens.Add(flag.ShortName);
                }
            }
        }
        tokens.Add("--help");
        return tokens;
    }

    private static string FormatPowerShellArray(IEnumerable<string> values) =>
        string.Join(", ", values.Select(value => $"'{EscapePowerShellSingleQuoted(value)}'"));

    private static string EscapePowerShellSingleQuoted(string value) =>
        value.Replace("'", "''", StringComparison.Ordinal);

    // --- Helpers / ヘルパー ---

    private static ColorMode _colorMode = ColorMode.Auto;
    private static ColorPalette? _explicitPalette;
    private static bool? _windowsVirtualTerminalProcessingEnabled;
    private static Func<bool>? _windowsVirtualTerminalProcessingDetectorForTests;
    private const int StdOutputHandle = -11;
    private const uint EnableVirtualTerminalProcessing = 0x0004;

    /// <summary>
    /// Set the active color-output mode. <see cref="ColorMode.Always"/> and
    /// <see cref="ColorMode.Never"/> short-circuit env / TTY checks in
    /// <see cref="ShouldUseColor"/>; <see cref="ColorMode.Auto"/> defers to
    /// the existing CLICOLOR_FORCE / NO_COLOR / CLICOLOR / TTY chain.
    /// 色出力モードを設定する。Always / Never は環境変数と TTY 判定を上書きする。
    /// </summary>
    public static void SetColorMode(ColorMode mode) => _colorMode = mode;

    internal static ColorMode GetColorMode() => _colorMode;

    /// <summary>
    /// Override the active ANSI palette. <c>null</c> restores auto-detection
    /// via <c>COLORTERM</c> / <c>TERM</c> / <c>CDIDX_COLOR_PALETTE</c>.
    /// </summary>
    public static void SetColorPalette(ColorPalette? palette) => _explicitPalette = palette;

    internal static ColorPalette? GetExplicitColorPalette() => _explicitPalette;

    /// <summary>
    /// Parse a user-supplied `--palette` value. Accepts `basic`, `256`,
    /// `color256`, `truecolor`, and `24bit` (case-insensitive). Returns false
    /// on any other value.
    /// `--palette` 値を解析する。`basic` / `256` / `truecolor` などを許可する。
    /// </summary>
    public static bool TryParseColorPalette(string? value, out ColorPalette palette)
    {
        switch (value?.Trim().ToLowerInvariant())
        {
            case "basic":
            case "8":
            case "16":
            case "ansi":
                palette = ColorPalette.Basic;
                return true;
            case "256":
            case "color256":
            case "8bit":
                palette = ColorPalette.Color256;
                return true;
            case "truecolor":
            case "24bit":
            case "rgb":
                palette = ColorPalette.Truecolor;
                return true;
            default:
                palette = ColorPalette.Basic;
                return false;
        }
    }

    /// <summary>
    /// Resolve the palette to use. Honors the explicit override set via
    /// <see cref="SetColorPalette"/> first, then falls back to the
    /// <c>CDIDX_COLOR_PALETTE</c> environment variable, then to capability
    /// detection from <c>COLORTERM</c> / <c>TERM</c>.
    /// </summary>
    public static ColorPalette ResolveColorPalette()
    {
        if (_explicitPalette is { } explicitPalette)
            return explicitPalette;

        var envPalette = Environment.GetEnvironmentVariable("CDIDX_COLOR_PALETTE");
        if (!string.IsNullOrWhiteSpace(envPalette) && TryParseColorPalette(envPalette, out var parsed))
            return parsed;

        return DetectColorPalette();
    }

    /// <summary>
    /// Detect the terminal palette from the <c>COLORTERM</c> and <c>TERM</c>
    /// environment variables. <c>COLORTERM=truecolor</c> / <c>COLORTERM=24bit</c>
    /// → <see cref="ColorPalette.Truecolor"/>. <c>TERM</c> containing
    /// <c>256color</c> (e.g. <c>xterm-256color</c>, <c>screen-256color</c>) →
    /// <see cref="ColorPalette.Color256"/>. Otherwise <see cref="ColorPalette.Basic"/>.
    /// </summary>
    internal static ColorPalette DetectColorPalette()
    {
        var colorTerm = Environment.GetEnvironmentVariable("COLORTERM");
        if (!string.IsNullOrEmpty(colorTerm))
        {
            var ct = colorTerm.Trim().ToLowerInvariant();
            if (ct == "truecolor" || ct == "24bit")
                return ColorPalette.Truecolor;
        }

        var term = Environment.GetEnvironmentVariable("TERM");
        if (!string.IsNullOrEmpty(term))
        {
            var t = term.ToLowerInvariant();
            if (t.Contains("256color", StringComparison.Ordinal))
                return ColorPalette.Color256;
            if (t.Contains("truecolor", StringComparison.Ordinal) || t.Contains("direct", StringComparison.Ordinal))
                return ColorPalette.Truecolor;
        }

        return ColorPalette.Basic;
    }

    /// <summary>
    /// Parse a user-supplied `--color` value. Accepts `auto`, `always`, and
    /// `never` (case-insensitive). Returns false on any other value.
    /// `--color` 値を解析する。`auto` / `always` / `never` のみ許可。
    /// </summary>
    public static bool TryParseColorMode(string? value, out ColorMode mode)
    {
        switch (value?.Trim().ToLowerInvariant())
        {
            case "auto":
                mode = ColorMode.Auto;
                return true;
            case "always":
                mode = ColorMode.Always;
                return true;
            case "never":
                mode = ColorMode.Never;
                return true;
            default:
                mode = ColorMode.Auto;
                return false;
        }
    }

    /// <summary>
    /// Colorize a symbol kind name with ANSI escape codes for terminal output.
    /// Honors the active <see cref="ColorMode"/>; in <see cref="ColorMode.Auto"/>
    /// falls back to <see cref="ShouldUseColor"/>'s env + TTY policy.
    /// シンボル種別名を ANSI エスケープコードで色付けする。<see cref="ColorMode"/> を尊重し、
    /// auto では環境変数と TTY 自動判定にフォールバックする。
    /// </summary>
    public static string ColorizeKind(string kind, int padWidth = 0)
    {
        var padded = padWidth > 0 ? kind.PadRight(padWidth) : kind;
        if (JsonOutputDepth.Value <= 0 && ShouldUseColor())
        {
            var color = GetKindColorCode(kind, ResolveColorPalette());
            if (color.Length > 0)
                return $"{color}{padded}\x1b[0m";
        }
        return padded;
    }

    // Per-palette SGR introducer for a given symbol kind. Basic stays within
    // the 8 standard ANSI colors (30–37) and intentionally avoids
    // `\x1b[90m` (bright-black / dim), which is unreadable on many minimal
    // SSH / CI terminals; namespace / import fall back to plain white (37).
    // 各パレットでのシンボル種別ごとの SGR コード。Basic は標準8色のみで
    // dim (`\x1b[90m`) を避け、SSH/CI 端末でも可読性を確保する。
    internal static string GetKindColorCode(string kind, ColorPalette palette) => palette switch
    {
        ColorPalette.Truecolor => kind switch
        {
            "class" => "\x1b[38;2;102;217;239m",     // bright cyan
            "struct" => "\x1b[38;2;102;217;239m",
            "interface" => "\x1b[38;2;102;160;255m",  // bright blue
            "enum" => "\x1b[38;2;215;110;215m",       // bright magenta
            "function" => "\x1b[38;2;255;215;75m",    // gold yellow
            "property" => "\x1b[38;2;160;230;100m",   // bright green
            "event" => "\x1b[38;2;255;100;100m",      // bright red
            "delegate" => "\x1b[38;2;215;110;215m",
            "namespace" => "\x1b[38;2;180;180;180m",  // light gray (readable on dark + light bg)
            "import" => "\x1b[38;2;180;180;180m",
            _ => "",
        },
        ColorPalette.Color256 => kind switch
        {
            "class" => "\x1b[38;5;81m",     // cyan
            "struct" => "\x1b[38;5;81m",
            "interface" => "\x1b[38;5;75m",  // blue
            "enum" => "\x1b[38;5;213m",      // magenta
            "function" => "\x1b[38;5;221m",  // gold
            "property" => "\x1b[38;5;120m",  // green
            "event" => "\x1b[38;5;203m",     // salmon red
            "delegate" => "\x1b[38;5;213m",
            "namespace" => "\x1b[38;5;245m", // medium gray (not as dim as 90m)
            "import" => "\x1b[38;5;245m",
            _ => "",
        },
        _ => kind switch
        {
            "class" => "\x1b[36m",      // cyan / シアン
            "struct" => "\x1b[36m",     // cyan / シアン
            "interface" => "\x1b[34m",  // blue / 青
            "enum" => "\x1b[35m",       // magenta / マゼンタ
            "function" => "\x1b[33m",   // yellow / 黄
            "property" => "\x1b[32m",   // green / 緑
            "event" => "\x1b[31m",      // red / 赤
            "delegate" => "\x1b[35m",   // magenta / マゼンタ
            "namespace" => "\x1b[37m",  // white (instead of dim 90m) / 白（dim 回避）
            "import" => "\x1b[37m",     // white (instead of dim 90m) / 白（dim 回避）
            _ => "",
        },
    };

    internal static bool ShouldUseInteractiveConsole()
        => ShouldUseInteractiveConsole(
            Console.IsOutputRedirected,
            Console.Out.Encoding,
            Console.Out is StringWriter,
            HasTerminalEnvironmentHint(),
            IsTerminalEnvironmentDisabled(),
            OperatingSystem.IsWindows());

    internal static bool ShouldUseInteractiveConsole(
        bool isOutputRedirected,
        Encoding outputEncoding,
        bool isTextWriterCapture,
        bool hasTerminalEnvironmentHint,
        bool isTerminalEnvironmentDisabled,
        bool isWindows)
    {
        if (isOutputRedirected)
            return false;

        if (isTerminalEnvironmentDisabled)
            return false;

        // StringWriter-based test capture leaves the process console attached, so
        // Console.IsOutputRedirected stays false even though interactive terminal
        // behavior would be unsafe. Detect it directly instead of inferring from
        // encoding, because real terminals may expose UTF-8 or UTF-16 independently
        // of ConPTY/ANSI support.
        if (isTextWriterCapture)
            return false;

        return isWindows || hasTerminalEnvironmentHint;
    }

    internal static bool ShouldUseAnsiOutput()
        => ShouldUseAnsiOutput(
            Console.IsOutputRedirected,
            Console.Out.Encoding,
            Console.Out is StringWriter,
            HasTerminalEnvironmentHint(),
            IsTerminalEnvironmentDisabled(),
            OperatingSystem.IsWindows(),
            GetWindowsVirtualTerminalProcessingEnabled());

    internal static bool ShouldUseAnsiOutput(
        bool isOutputRedirected,
        Encoding outputEncoding,
        bool isTextWriterCapture,
        bool hasTerminalEnvironmentHint,
        bool isTerminalEnvironmentDisabled,
        bool isWindows,
        bool windowsVirtualTerminalProcessingEnabled)
    {
        if (!ShouldUseInteractiveConsole(isOutputRedirected, outputEncoding, isTextWriterCapture, hasTerminalEnvironmentHint, isTerminalEnvironmentDisabled, isWindows))
            return false;

        if (!isWindows)
            return true;

        return windowsVirtualTerminalProcessingEnabled || hasTerminalEnvironmentHint;
    }

    /// <summary>
    /// Decide whether ANSI color escapes should be emitted. Precedence (highest first):
    ///   1. Explicit <see cref="ColorMode"/> from `--color` flag (Always/Never short-circuit).
    ///   2. CLICOLOR_FORCE (any non-empty value other than "0") — force color on.
    ///   3. NO_COLOR (any non-empty value) — color off.
    ///   4. CLICOLOR=0 — color off.
    ///   5. Otherwise fall back to <see cref="ShouldUseInteractiveConsole"/>.
    /// ANSI 色エスケープを出力するかを判定する。`--color` フラグ > 環境変数 > TTY 判定。
    /// </summary>
    public static bool ShouldUseColor()
    {
        if (_colorMode == ColorMode.Always)
            return true;
        if (_colorMode == ColorMode.Never)
            return false;
        if (IsForceColorRequested())
            return true;
        if (IsNoColorRequested())
            return false;
        return ShouldUseAnsiOutput();
    }

    private static bool HasTerminalEnvironmentHint()
    {
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WT_SESSION")))
            return true;
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WT_PROFILE_ID")))
            return true;
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("TERM_PROGRAM")))
            return true;

        var term = Environment.GetEnvironmentVariable("TERM");
        return !string.IsNullOrWhiteSpace(term)
            && !term.Equals("dumb", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsTerminalEnvironmentDisabled()
        => IsDumbTerminal() || IsCiEnvironment();

    private static bool IsCiEnvironment()
    {
        var ci = Environment.GetEnvironmentVariable("CI");
        return !string.IsNullOrEmpty(ci)
            && !ci.Equals("0", StringComparison.OrdinalIgnoreCase)
            && !ci.Equals("false", StringComparison.OrdinalIgnoreCase)
            && !ci.Equals("no", StringComparison.OrdinalIgnoreCase)
            && !ci.Equals("off", StringComparison.OrdinalIgnoreCase);
    }

    private static bool GetWindowsVirtualTerminalProcessingEnabled()
    {
        if (!OperatingSystem.IsWindows())
            return false;

        if (_windowsVirtualTerminalProcessingEnabled is { } cached)
            return cached;

        var detected = (_windowsVirtualTerminalProcessingDetectorForTests ?? DetectWindowsVirtualTerminalProcessing)();
        _windowsVirtualTerminalProcessingEnabled = detected;
        return detected;
    }

    private static bool DetectWindowsVirtualTerminalProcessing()
    {
        var handle = GetStdHandle(StdOutputHandle);
        if (handle == IntPtr.Zero || handle == new IntPtr(-1))
            return false;

        return GetConsoleMode(handle, out var mode)
            && (mode & EnableVirtualTerminalProcessing) != 0;
    }

    internal static void SetWindowsVirtualTerminalProcessingDetectorForTests(Func<bool>? detector)
    {
        _windowsVirtualTerminalProcessingDetectorForTests = detector;
        _windowsVirtualTerminalProcessingEnabled = null;
    }

    internal static void ResetTerminalCapabilityCacheForTests()
    {
        _windowsVirtualTerminalProcessingEnabled = null;
        _windowsVirtualTerminalProcessingDetectorForTests = null;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int nStdHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetConsoleMode(IntPtr hConsoleHandle, out uint lpMode);

    private static bool IsForceColorRequested()
    {
        var force = Environment.GetEnvironmentVariable("CLICOLOR_FORCE");
        return !string.IsNullOrEmpty(force) && force != "0";
    }

    private static bool IsNoColorRequested()
    {
        var noColor = Environment.GetEnvironmentVariable("NO_COLOR");
        if (!string.IsNullOrEmpty(noColor))
            return true;

        var cliColor = Environment.GetEnvironmentVariable("CLICOLOR");
        return cliColor == "0";
    }

    internal static bool ShouldUseUnicodeGlyphs()
    {
        if (IsAsciiOutputRequested())
            return false;

        if (IsDumbTerminal())
            return false;

        var locale = FirstNonEmptyEnvironmentVariable("LC_ALL", "LC_CTYPE", "LANG");
        if (locale != null && !IsUnicodeLocale(locale))
            return false;

        return Console.OutputEncoding.CodePage == Encoding.UTF8.CodePage
            || Console.OutputEncoding.CodePage == Encoding.Unicode.CodePage;
    }

    private static bool IsAsciiOutputRequested()
    {
        if (_asciiOutputForced)
            return true;

        var ascii = Environment.GetEnvironmentVariable("CDIDX_ASCII");
        if (!string.IsNullOrEmpty(ascii) && ascii != "0")
            return true;

        var noUnicode = Environment.GetEnvironmentVariable("NO_UNICODE");
        if (!string.IsNullOrEmpty(noUnicode) && noUnicode != "0")
            return true;

        var atBridgeType = Environment.GetEnvironmentVariable("AT_BRIDGE_TYPE");
        if (!string.IsNullOrEmpty(atBridgeType))
            return true;

        var accessibilityEnabled = Environment.GetEnvironmentVariable("ACCESSIBILITY_ENABLED");
        if (!string.IsNullOrEmpty(accessibilityEnabled) && accessibilityEnabled != "0")
            return true;

        return IsPosixLocale(Environment.GetEnvironmentVariable("LC_ALL"))
            || IsPosixLocale(Environment.GetEnvironmentVariable("LC_CTYPE"))
            || IsPosixLocale(Environment.GetEnvironmentVariable("LANG"));
    }

    private static bool IsTruthyEnvironmentVariable(string name)
        => IsTruthyEnvironmentValue(Environment.GetEnvironmentVariable(name));

    private static bool IsTruthyEnvironmentValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        return value.Trim() is not ("0" or "false" or "False" or "FALSE" or "no" or "No" or "NO");
    }

    private static bool IsDumbTerminal()
        => string.Equals(Environment.GetEnvironmentVariable("TERM"), "dumb", StringComparison.OrdinalIgnoreCase);

    private static bool IsPosixLocale(string? locale)
        => locale != null
            && (locale.Equals("C", StringComparison.OrdinalIgnoreCase)
                || locale.Equals("POSIX", StringComparison.OrdinalIgnoreCase));

    private static bool IsUnicodeLocale(string locale)
        => locale.Contains(".UTF-8", StringComparison.OrdinalIgnoreCase)
            || locale.Contains(".UTF8", StringComparison.OrdinalIgnoreCase);

    private static string? FirstNonEmptyEnvironmentVariable(params string[] names)
    {
        foreach (var name in names)
        {
            var value = Environment.GetEnvironmentVariable(name);
            if (!string.IsNullOrEmpty(value))
                return value;
        }

        return null;
    }

    internal static void SetAsciiOutput(bool enabled) => _asciiOutputForced = enabled;

    internal static bool IsAsciiOutputForced() => _asciiOutputForced;

    internal static bool WidthDetectionFailed => _widthDetectionFailed;

    internal static void SetWidthDetectionTracing(bool enabled) => _traceWidthDetectionFailures = enabled;

    /// <summary>
    /// Get console window width safely (some environments throw IOException).
    /// コンソール幅を安全に取得する（一部環境ではIOExceptionが発生する）。
    /// </summary>
    internal static int GetWindowWidth()
    {
        if (TryGetColumnsEnvironmentWidth(out var columnsWidth))
            return columnsWidth;

        try
        {
            var w = Console.WindowWidth;
            if (w > 0)
                return w;
        }
        catch (IOException ex)
        {
            return GetFallbackWindowWidth(ex);
        }
        catch (NotSupportedException ex)
        {
            return GetFallbackWindowWidth(ex);
        }

        return GetFallbackWindowWidth(null);
    }

    private static int GetFallbackWindowWidth(Exception? exception)
    {
        _widthDetectionFailed = true;
        if (_traceWidthDetectionFailures && !_widthDetectionTraceWritten)
        {
            var suffix = exception == null ? string.Empty : $" ({exception.GetType().Name}: {exception.Message})";
            Console.Error.WriteLine($"cdidx: console width detection failed; using COLUMNS or 80 columns{suffix}");
            _widthDetectionTraceWritten = true;
        }

        return TryGetColumnsEnvironmentWidth(out var columnsWidth) ? columnsWidth : 80;
    }

    private static bool TryGetColumnsEnvironmentWidth(out int width)
    {
        var columns = Environment.GetEnvironmentVariable("COLUMNS");
        if (int.TryParse(columns, NumberStyles.Integer, CultureInfo.InvariantCulture, out width) && width > 0)
            return true;

        width = 0;
        return false;
    }

    private sealed class JsonOutputScope : IDisposable
    {
        public void Dispose()
        {
            if (JsonOutputDepth.Value > 0)
                JsonOutputDepth.Value--;
        }
    }

    private sealed class NoopDisposable : IDisposable
    {
        public static readonly NoopDisposable Instance = new();
        public void Dispose()
        {
        }
    }
}
