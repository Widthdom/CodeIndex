using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace CodeIndex.Cli;

/// <summary>
/// Runs `db` subcommands that operate directly on the SQLite file (integrity check, etc.).
/// SQLite ファイル本体に対する `db` サブコマンド（整合性チェックなど）を実行する。
/// </summary>
public static class DbCommandRunner
{
    public static int RunIntegrityCheck(string[] cmdArgs, JsonSerializerOptions jsonOptions)
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
                "Run `cdidx db --integrity-check --help` to see the supported command shape.");

        if (!options.IntegrityCheck)
            return WriteCommandError(
                options.Json,
                jsonOptions,
                "db requires a mode flag",
                CommandExitCodes.UsageError,
                "Pass `--integrity-check` to run `PRAGMA integrity_check` on the database.");

        var dbPath = options.DbPath;
        var isUri = dbPath.StartsWith("file:", StringComparison.OrdinalIgnoreCase);
        if (!isUri && !File.Exists(dbPath))
            return WriteCommandError(
                options.Json,
                jsonOptions,
                $"database not found: {dbPath}",
                CommandExitCodes.NotFound,
                "Point `--db` at an existing `codeindex.db`, or run `cdidx index <projectPath>` first to create one.");

        try
        {
            var issues = RunIntegrityCheckPragma(dbPath);
            var ok = issues.Count == 1 && string.Equals(issues[0], "ok", StringComparison.Ordinal);
            var jsonContext = CliJsonSerializerContextFactory.Create(jsonOptions);

            if (options.Json)
            {
                Console.WriteLine(JsonSerializer.Serialize(
                    new DbIntegrityCheckJsonResult(
                        Path.GetFullPath(isUri ? dbPath : dbPath),
                        ok,
                        ok ? new List<string>() : issues),
                    jsonContext.DbIntegrityCheckJsonResult));
            }
            else
            {
                Console.WriteLine("Integrity check");
                Console.WriteLine($"  database: {Path.GetFullPath(isUri ? dbPath : dbPath)}");
                Console.WriteLine($"  result  : {(ok ? "ok" : "corrupted")}");
                if (!ok)
                {
                    Console.WriteLine($"  issues  : {issues.Count} row(s)");
                    foreach (var line in issues)
                        Console.WriteLine($"    - {line}");
                    Console.WriteLine();
                    Console.Error.WriteLine("Error: SQLite reported integrity_check failures.");
                    Console.Error.WriteLine("Hint: rebuild with `cdidx index <projectPath> --rebuild` to discard the corrupted DB and start fresh.");
                }
            }

            return ok ? CommandExitCodes.Success : CommandExitCodes.DatabaseError;
        }
        catch (Exception ex)
        {
            if (JsonOutputFailure.TryHandle(ex, out var exitCode))
                return exitCode;

            return WriteCommandError(
                options.Json,
                jsonOptions,
                $"failed to run integrity check: {ex.Message}",
                CommandExitCodes.DatabaseError,
                "Retry `cdidx db --integrity-check`. If this persists, the DB may be unreadable; rebuild with `cdidx index <projectPath> --rebuild`.");
        }
    }

    // PRAGMA integrity_check returns a single row `"ok"` when the file passes every consistency
    // probe, otherwise it returns up to N rows of corruption findings. The pragma itself only
    // reads the database, so a read-only connection is sufficient and avoids the WAL-mode
    // pragma side effects of the normal DbContext open path.
    // PRAGMA integrity_check は問題が無ければ 1 行の `"ok"` を、破損があれば最大 N 行の検出結果を返す。
    // 読み取りのみのため read-only 接続で十分で、DbContext の WAL モード設定副作用を避けられる。
    private static List<string> RunIntegrityCheckPragma(string dbPath)
    {
        var connectionString = dbPath.StartsWith("file:", StringComparison.OrdinalIgnoreCase)
            ? $"Data Source={dbPath}"
            : new SqliteConnectionStringBuilder
            {
                DataSource = dbPath,
                Mode = SqliteOpenMode.ReadOnly,
            }.ConnectionString;

        using var connection = new SqliteConnection(connectionString);
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "PRAGMA integrity_check";
        using var reader = cmd.ExecuteReader();
        var rows = new List<string>();
        while (reader.Read())
        {
            var raw = reader.IsDBNull(0) ? string.Empty : reader.GetString(0);
            rows.Add(raw);
        }
        return rows.Count > 0 ? rows : new List<string> { "ok" };
    }

    internal static DbCommandOptions ParseArgs(string[] args)
    {
        var dbPath = Path.Combine(".cdidx", "codeindex.db");
        var json = false;
        var integrityCheck = false;
        string? parseError = null;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--db" when i + 1 < args.Length:
                    dbPath = args[++i];
                    break;
                case "--db":
                    parseError = "--db requires a value";
                    break;
                case "--json":
                    json = true;
                    break;
                case "--integrity-check":
                    integrityCheck = true;
                    break;
                case "--help" or "-h":
                    return new DbCommandOptions { ShowHelp = true, DbPath = dbPath, Json = json };
                default:
                    if (args[i].StartsWith('-'))
                        parseError = $"db does not support option: '{args[i]}'";
                    else
                        parseError = $"db does not accept positional arguments: '{args[i]}'";
                    break;
            }

            if (parseError != null)
                break;
        }

        return new DbCommandOptions
        {
            DbPath = dbPath,
            Json = json,
            IntegrityCheck = integrityCheck,
            ParseError = parseError,
        };
    }

    private static int WriteCommandError(bool json, JsonSerializerOptions jsonOptions, string message, int exitCode, string? hint = null)
    {
        if (json)
            Console.WriteLine(JsonSerializer.Serialize(
                new CommandErrorJsonResult("error", message, hint),
                CliJsonSerializerContextFactory.Create(jsonOptions).CommandErrorJsonResult));
        else
        {
            Console.Error.WriteLine($"Error: {message}");
            if (hint != null)
                Console.Error.WriteLine($"Hint: {hint}");
        }
        return exitCode;
    }
}

internal sealed class DbCommandOptions
{
    public string DbPath { get; init; } = string.Empty;
    public bool Json { get; init; }
    public bool ShowHelp { get; init; }
    public bool IntegrityCheck { get; init; }
    public string? ParseError { get; init; }
}
