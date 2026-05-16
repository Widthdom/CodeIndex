using System.Formats.Tar;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace CodeIndex.Cli;

/// <summary>
/// Runs `cdidx report --output <path>`, which bundles a redacted crash-repro
/// tarball (`.tar.gz`) containing the cdidx version, OS / .NET runtime info,
/// the schema table list with row counts, and recent lifecycle log lines.
/// User-content fields (paths, query strings, args) are redacted by default.
/// `cdidx report --output <path>` を実行する。バージョン、OS / .NET ランタイム情報、
/// スキーマのテーブル一覧と行数、最近のライフサイクルログを含む匿名化済み
/// tarball (`.tar.gz`) を作る。パス・クエリ文字列・args 等のユーザコンテンツは
/// 既定で伏字化する。
/// </summary>
public static class ReportCommandRunner
{
    internal const int DefaultLogLines = 200;
    internal const string RedactedPlaceholder = "[redacted]";

    public static int Run(string[] cmdArgs, JsonSerializerOptions jsonOptions, string? appVersion = null)
    {
        var options = ParseArgs(cmdArgs);
        if (options.ShowHelp)
        {
            ConsoleUi.PrintUsage();
            return CommandExitCodes.Success;
        }

        if (options.ParseError != null)
            return WriteCommandError(
                options.Json,
                jsonOptions,
                options.ParseError,
                CommandExitCodes.UsageError,
                "Run `cdidx report --help` to see the supported command shape.",
                CommandErrorCodes.UsageError);

        if (string.IsNullOrWhiteSpace(options.OutputPath))
            return WriteCommandError(
                options.Json,
                jsonOptions,
                "report requires --output <path>",
                CommandExitCodes.UsageError,
                "Pass `--output report.tgz` (or another writable path) to choose where the redacted bundle is written.",
                CommandErrorCodes.UsageError);

        try
        {
            var resolvedVersion = appVersion ?? ConsoleUi.LoadVersion();
            var bundle = BuildBundle(options, resolvedVersion);
            WriteBundle(options.OutputPath!, bundle);

            var summary = new ReportBundleSummary(
                Path.GetFullPath(options.OutputPath!),
                resolvedVersion,
                bundle.Files.Count,
                bundle.SchemaTables.Count,
                bundle.LogLinesIncluded,
                bundle.LogIncluded,
                bundle.DbIncluded,
                bundle.DbPath);

            if (options.Json)
            {
                var jsonContext = CliJsonSerializerContextFactory.Create(jsonOptions);
                Console.WriteLine(JsonSerializer.Serialize(summary, jsonContext.ReportBundleSummary));
            }
            else
            {
                Console.WriteLine("Bug report bundle");
                Console.WriteLine($"  output       : {summary.OutputPath}");
                Console.WriteLine($"  cdidx        : v{summary.Version}");
                Console.WriteLine($"  files        : {summary.Files}");
                Console.WriteLine($"  schema rows  : {summary.SchemaTables}");
                Console.WriteLine($"  log lines    : {(summary.LogIncluded ? summary.LogLinesIncluded.ToString() : "skipped")}");
                Console.WriteLine($"  schema source: {(summary.DbIncluded ? summary.DbPath : "(no DB found)")}");
                Console.WriteLine();
                Console.WriteLine("Attach the tarball to the GitHub issue. Path lists, query strings, and");
                Console.WriteLine("`args=` log lines are redacted by default; rerun with `--include-args` to");
                Console.WriteLine("include literal command-line arguments only when you trust the recipient.");
            }
            return CommandExitCodes.Success;
        }
        catch (Exception ex)
        {
            if (JsonOutputFailure.TryHandle(ex, out var exitCode))
                return exitCode;

            return WriteCommandError(
                options.Json,
                jsonOptions,
                $"failed to build report: {ex.Message}",
                CommandExitCodes.DatabaseError,
                "Retry `cdidx report --output <path>`. If this persists, check that the output directory is writable.",
                CommandErrorCodes.DbError);
        }
    }

    internal static ReportBundle BuildBundle(ReportCommandOptions options, string version)
    {
        var bundle = new ReportBundle();
        var nowUtc = DateTimeOffset.UtcNow;

        var metadata = new ReportMetadata(
            Version: version,
            GeneratedAtUtc: nowUtc.ToString("O"),
            DotNetRuntimeVersion: Environment.Version.ToString(),
            FrameworkDescription: RuntimeInformation.FrameworkDescription,
            OsDescription: RuntimeInformation.OSDescription,
            OsArchitecture: RuntimeInformation.OSArchitecture.ToString(),
            ProcessArchitecture: RuntimeInformation.ProcessArchitecture.ToString(),
            IsLittleEndian: BitConverter.IsLittleEndian);
        var metaJson = JsonSerializer.Serialize(metadata, ReportMetadataJsonContext.Default.ReportMetadata);
        bundle.AddText("metadata.json", metaJson);

        bundle.AddText("version.txt", $"cdidx v{version}\n");
        bundle.AddText(
            "env.txt",
            string.Join('\n',
                $"cdidx-version: {version}",
                $"generated-at-utc: {nowUtc:O}",
                $"dotnet-runtime: {Environment.Version}",
                $"framework: {RuntimeInformation.FrameworkDescription}",
                $"os: {RuntimeInformation.OSDescription}",
                $"os-architecture: {RuntimeInformation.OSArchitecture}",
                $"process-architecture: {RuntimeInformation.ProcessArchitecture}",
                "") + "\n");

        var (schemaText, tables, dbPath, dbIncluded) = BuildSchemaSummary(options.DbPath);
        bundle.SchemaTables = tables;
        bundle.DbIncluded = dbIncluded;
        bundle.DbPath = dbPath;
        bundle.AddText("schema.txt", schemaText);

        bundle.LogIncluded = options.IncludeLog;
        bundle.LogLinesIncluded = 0;
        if (options.IncludeLog && options.LogLines > 0)
        {
            var logText = BuildRecentLogTail(options.LogLines, options.IncludeArgs, out var linesIncluded);
            bundle.LogLinesIncluded = linesIncluded;
            bundle.AddText("log/stderr-recent.log", logText);
        }

        bundle.AddText("README.md", BuildReadme(version, options.IncludeLog, options.IncludeArgs));
        return bundle;
    }

    internal static string BuildReadme(string version, bool includeLog, bool includeArgs)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# cdidx bug-report bundle");
        sb.AppendLine();
        sb.AppendLine($"Generated by cdidx v{version}. Attach this tarball to the GitHub issue you are filing at");
        sb.AppendLine("<https://github.com/Widthdom/CodeIndex/issues>.");
        sb.AppendLine();
        sb.AppendLine("## Contents");
        sb.AppendLine();
        sb.AppendLine("- `metadata.json` — version, OS, .NET runtime info (machine-readable).");
        sb.AppendLine("- `version.txt` — cdidx version only.");
        sb.AppendLine("- `env.txt` — human-readable OS / runtime summary.");
        sb.AppendLine("- `schema.txt` — list of SQLite tables and row counts (no user content).");
        if (includeLog)
        {
            sb.AppendLine("- `log/stderr-recent.log` — last N lines of the cdidx lifecycle log");
            sb.AppendLine(includeArgs
                ? "  (includes literal `args=` lines; rerun without `--include-args` to redact them)."
                : "  (`args=` lines are redacted; rerun with `--include-args` to keep them literal).");
        }
        else
        {
            sb.AppendLine("- (log skipped via `--no-log`)");
        }
        sb.AppendLine();
        sb.AppendLine("## Redactions");
        sb.AppendLine();
        sb.AppendLine("- Indexed source content, file paths, query strings, and `args=` lines are not included by default.");
        sb.AppendLine("- Schema reporting only emits table names and integer row counts.");
        return sb.ToString();
    }

    internal static (string Text, List<ReportSchemaTable> Tables, string? DbPath, bool DbIncluded) BuildSchemaSummary(string dbPath)
    {
        if (!File.Exists(dbPath))
        {
            var missingText = $"no SQLite index found at: {dbPath}\nRun `cdidx index <projectPath>` first if you want schema details attached.\n";
            return (missingText, new List<ReportSchemaTable>(), dbPath, false);
        }

        var tables = new List<ReportSchemaTable>();
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadOnly,
        }.ConnectionString;

        using var connection = new SqliteConnection(connectionString);
        connection.Open();

        var tableNames = new List<string>();
        using (var listCmd = connection.CreateCommand())
        {
            listCmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' ORDER BY name";
            using var reader = listCmd.ExecuteReader();
            while (reader.Read())
                tableNames.Add(reader.GetString(0));
        }

        foreach (var name in tableNames)
        {
            long rowCount;
            try
            {
                using var countCmd = connection.CreateCommand();
                countCmd.CommandText = $"SELECT COUNT(*) FROM \"{name.Replace("\"", "\"\"")}\"";
                rowCount = Convert.ToInt64(countCmd.ExecuteScalar());
            }
            catch (SqliteException)
            {
                rowCount = -1;
            }
            tables.Add(new ReportSchemaTable(name, rowCount));
        }

        var sb = new StringBuilder();
        sb.AppendLine($"database: {Path.GetFullPath(dbPath)}");
        sb.AppendLine($"tables  : {tables.Count}");
        sb.AppendLine();
        sb.AppendLine("name | row_count");
        sb.AppendLine("-----|----------");
        foreach (var t in tables)
            sb.AppendLine($"{t.Name} | {(t.RowCount < 0 ? "(unreadable)" : t.RowCount.ToString())}");

        return (sb.ToString(), tables, dbPath, true);
    }

    internal static string BuildRecentLogTail(int maxLines, bool includeArgs, out int linesIncluded)
    {
        linesIncluded = 0;
        var logDir = GlobalToolLog.ResolveLogDirectoryForReport();
        if (string.IsNullOrWhiteSpace(logDir) || !Directory.Exists(logDir))
            return $"no cdidx lifecycle log directory found (looked at: {logDir ?? "<unknown>"}).\n";

        var logFiles = new DirectoryInfo(logDir)
            .EnumerateFiles("stderr-*.log", SearchOption.TopDirectoryOnly)
            .OrderByDescending(f => f.Name, StringComparer.Ordinal)
            .ToList();
        if (logFiles.Count == 0)
            return $"no cdidx lifecycle log files found in: {logDir}\n";

        var collected = new LinkedList<string>();
        foreach (var file in logFiles)
        {
            if (collected.Count >= maxLines)
                break;
            string[] lines;
            try
            {
                lines = File.ReadAllLines(file.FullName);
            }
            catch (IOException)
            {
                continue;
            }
            for (var i = lines.Length - 1; i >= 0 && collected.Count < maxLines; i--)
                collected.AddFirst(lines[i]);
        }

        var sb = new StringBuilder();
        sb.AppendLine($"# cdidx lifecycle log (last {collected.Count} lines, newest last)");
        sb.AppendLine($"# source directory: {logDir}");
        sb.AppendLine();
        foreach (var line in collected)
        {
            sb.AppendLine(includeArgs ? line : RedactSensitiveFields(line));
        }
        linesIncluded = collected.Count;
        return sb.ToString();
    }

    internal static string RedactSensitiveFields(string line)
    {
        var redacted = RedactKeyValue(line, "args=");
        redacted = RedactKeyValue(redacted, "cwd=");
        return redacted;
    }

    private static string RedactKeyValue(string line, string key)
    {
        var idx = line.IndexOf(key, StringComparison.Ordinal);
        if (idx < 0)
            return line;
        return line[..(idx + key.Length)] + RedactedPlaceholder;
    }

    private static void WriteBundle(string outputPath, ReportBundle bundle)
    {
        var dir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        using var fileStream = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None);
        using var gz = new GZipStream(fileStream, CompressionLevel.Optimal);
        using var tar = new TarWriter(gz, TarEntryFormat.Pax, leaveOpen: true);

        foreach (var (name, bytes) in bundle.Files)
        {
            var entry = new PaxTarEntry(TarEntryType.RegularFile, name)
            {
                DataStream = new MemoryStream(bytes, writable: false),
                Mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead,
                ModificationTime = DateTimeOffset.UtcNow,
            };
            tar.WriteEntry(entry);
        }
    }

    internal static ReportCommandOptions ParseArgs(string[] args)
    {
        var options = new ReportCommandOptions
        {
            DbPath = Path.Combine(".cdidx", "codeindex.db"),
            LogLines = DefaultLogLines,
            IncludeLog = true,
            IncludeArgs = false,
        };

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--db" when i + 1 < args.Length:
                    options.DbPath = args[++i];
                    break;
                case "--db":
                    options.ParseError = "--db requires a value";
                    break;
                case "--output" or "-o" when i + 1 < args.Length:
                    options.OutputPath = args[++i];
                    break;
                case "--output" or "-o":
                    options.ParseError = "--output requires a value";
                    break;
                case "--json":
                    options.Json = true;
                    break;
                case "--no-log":
                    options.IncludeLog = false;
                    break;
                case "--include-args":
                    options.IncludeArgs = true;
                    break;
                case "--log-lines" when i + 1 < args.Length:
                    if (!int.TryParse(args[++i], out var parsedLines) || parsedLines < 0)
                        options.ParseError = $"--log-lines requires a non-negative integer, got '{args[i]}'";
                    else
                        options.LogLines = parsedLines;
                    break;
                case "--log-lines":
                    options.ParseError = "--log-lines requires a value";
                    break;
                case "--help" or "-h":
                    options.ShowHelp = true;
                    return options;
                default:
                    if (args[i].StartsWith('-'))
                        options.ParseError = $"report does not support option: '{args[i]}'";
                    else
                        options.ParseError = $"report does not accept positional arguments: '{args[i]}'";
                    break;
            }

            if (options.ParseError != null)
                break;
        }

        return options;
    }

    private static int WriteCommandError(bool json, JsonSerializerOptions jsonOptions, string message, int exitCode, string? hint = null, string? errorCode = null)
    {
        if (json)
            Console.WriteLine(JsonSerializer.Serialize(
                new CommandErrorJsonResult("error", message, hint, errorCode),
                CliJsonSerializerContextFactory.Create(jsonOptions).CommandErrorJsonResult));
        else
        {
            var prefix = errorCode is null ? "Error" : $"Error [{errorCode}]";
            Console.Error.WriteLine($"{prefix}: {message}");
            if (hint != null)
                Console.Error.WriteLine($"Hint: {hint}");
        }
        return exitCode;
    }
}

internal sealed class ReportCommandOptions
{
    public string DbPath { get; set; } = string.Empty;
    public string? OutputPath { get; set; }
    public bool Json { get; set; }
    public bool ShowHelp { get; set; }
    public bool IncludeLog { get; set; } = true;
    public bool IncludeArgs { get; set; }
    public int LogLines { get; set; } = ReportCommandRunner.DefaultLogLines;
    public string? ParseError { get; set; }
}

internal sealed class ReportBundle
{
    public List<(string Name, byte[] Bytes)> Files { get; } = new();
    public List<ReportSchemaTable> SchemaTables { get; set; } = new();
    public bool LogIncluded { get; set; }
    public bool DbIncluded { get; set; }
    public string? DbPath { get; set; }
    public int LogLinesIncluded { get; set; }

    public void AddText(string name, string content) =>
        Files.Add((name, Encoding.UTF8.GetBytes(content)));
}

internal sealed record ReportSchemaTable(string Name, long RowCount);

internal sealed record ReportMetadata(
    string Version,
    string GeneratedAtUtc,
    string DotNetRuntimeVersion,
    string FrameworkDescription,
    string OsDescription,
    string OsArchitecture,
    string ProcessArchitecture,
    bool IsLittleEndian);

[System.Text.Json.Serialization.JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = System.Text.Json.Serialization.JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
[System.Text.Json.Serialization.JsonSerializable(typeof(ReportMetadata))]
internal partial class ReportMetadataJsonContext : System.Text.Json.Serialization.JsonSerializerContext;
