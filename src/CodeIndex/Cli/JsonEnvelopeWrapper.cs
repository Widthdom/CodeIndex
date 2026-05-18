using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using CodeIndex.Database;
using CodeIndex.Indexer;

namespace CodeIndex.Cli;

/// <summary>
/// Wraps NDJSON-style query command output into a single
/// <c>{ "metadata": {...}, "results": [...] }</c> envelope when the caller
/// passes <c>--json-envelope</c>. Issue #1527.
/// 呼び出し側が <c>--json-envelope</c> を指定したとき、各クエリ系コマンドが
/// 1 行ずつ出力する NDJSON を <c>{ "metadata": {...}, "results": [...] }</c>
/// 単一エンベロープに包んで返す。Issue #1527。
/// </summary>
internal static class JsonEnvelopeWrapper
{
    internal const string EnvelopeFlag = "--json-envelope";

    private static readonly HashSet<string> WrappableCommands = new(StringComparer.Ordinal)
    {
        "search", "definition", "references", "callers", "callees",
        "symbols", "files", "find", "excerpt", "map", "inspect",
        "outline", "status", "validate", "languages", "impact",
        "deps", "unused", "hotspots",
    };

    internal static bool ShouldWrap(string command, string[] args)
        => WrappableCommands.Contains(command) && HasEnvelopeFlag(args);

    internal static bool HasEnvelopeFlag(string[] args)
    {
        for (var i = 0; i < args.Length; i++)
        {
            if (string.Equals(args[i], EnvelopeFlag, StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Strip <c>--json-envelope</c> from the args and ensure <c>--json</c> is present;
    /// the inner command runner only knows about <c>--json</c>.
    /// 内側のコマンドランナーは <c>--json</c> しか知らないため、
    /// <c>--json-envelope</c> を取り除き、<c>--json</c> を付与する。
    /// </summary>
    internal static string[] PrepareInnerArgs(string[] args)
    {
        var stripped = new List<string>(args.Length);
        var sawJson = false;
        foreach (var arg in args)
        {
            if (string.Equals(arg, EnvelopeFlag, StringComparison.Ordinal))
                continue;
            if (string.Equals(arg, "--json", StringComparison.Ordinal))
                sawJson = true;
            stripped.Add(arg);
        }

        if (!sawJson)
            stripped.Add("--json");
        return [.. stripped];
    }

    internal static int RunWrapped(
        string command,
        string[] args,
        string appVersion,
        JsonSerializerOptions jsonOptions,
        Func<string[], int> runInner)
    {
        var innerArgs = PrepareInnerArgs(args);
        var queryNormalized = ExtractQueryArg(args);
        var dbPathExplicit = TryExtractDbPath(args, out var explicitDbPath);
        var resolvedDbPath = string.IsNullOrWhiteSpace(explicitDbPath)
            ? Path.Combine(".cdidx", "codeindex.db")
            : explicitDbPath!;

        var originalOut = Console.Out;
        using var captured = new StringWriter();
        var stopwatch = Stopwatch.StartNew();
        int exitCode;
        try
        {
            Console.SetOut(captured);
            exitCode = runInner(innerArgs);
        }
        finally
        {
            stopwatch.Stop();
            Console.SetOut(originalOut);
        }

        var raw = captured.ToString();
        var results = ParseRawJsonItems(raw);
        var envelope = BuildEnvelope(
            command,
            queryNormalized,
            resolvedDbPath,
            dbPathExplicit,
            appVersion,
            stopwatch.Elapsed.TotalMilliseconds,
            results,
            exitCode);

        Console.WriteLine(envelope.ToJsonString(jsonOptions));
        return exitCode;
    }

    private static JsonObject BuildEnvelope(
        string command,
        string? queryNormalized,
        string dbPath,
        bool dbPathExplicit,
        string appVersion,
        double elapsedMs,
        JsonArray results,
        int exitCode)
    {
        var metadata = new JsonObject
        {
            ["api_version"] = JsonOutputContract.ApiVersion,
            ["command"] = command,
            ["cdidx_version"] = appVersion,
            ["elapsed_ms"] = Math.Round(elapsedMs, 3),
            ["db_path"] = dbPath,
            ["result_count"] = results.Count,
            ["exit_code"] = exitCode,
        };

        if (!string.IsNullOrEmpty(queryNormalized))
            metadata["query_normalized"] = queryNormalized;

        var indexedHead = SafeReadIndexedHead(dbPath, dbPathExplicit);
        if (!string.IsNullOrEmpty(indexedHead))
            metadata["indexed_at_head_sha"] = indexedHead;

        return new JsonObject
        {
            ["metadata"] = metadata,
            ["results"] = results,
        };
    }

    private static string? SafeReadIndexedHead(string dbPath, bool dbPathExplicit)
    {
        try
        {
            var resolvedPath = dbPath;
            if (!dbPathExplicit && !File.Exists(LongPath.EnsureWindowsPrefix(resolvedPath)))
                return null;
            return DbPathResolver.TryReadIndexedHeadCommit(DbPathResolver.NormalizeDbPath(resolvedPath));
        }
        catch
        {
            return null;
        }
    }

    private static JsonArray ParseRawJsonItems(string raw)
    {
        var array = new JsonArray();
        if (string.IsNullOrEmpty(raw))
            return array;

        foreach (var rawLine in raw.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (string.IsNullOrWhiteSpace(line))
                continue;

            JsonNode? node;
            try
            {
                node = JsonNode.Parse(line);
            }
            catch (JsonException)
            {
                array.Add(line);
                continue;
            }

            if (node is null)
                continue;

            array.Add(node);
        }

        return array;
    }

    // Mirrors the value-taking options in QueryCommandRunner.ParseArgs so we can locate the
    // first positional (= query) without being fooled by `--db <path>`-style values.
    // QueryCommandRunner.ParseArgs と同じ value-taking option を認識し、`--db <path>` の値を
    // positional 引数（= query）と取り違えないようにする。
    private static readonly HashSet<string> ValueConsumingOptions = new(StringComparer.Ordinal)
    {
        "--db", "--limit", "--top", "--lang", "--kind", "--since",
        "--start", "--end", "--before", "--after", "--name",
        "--snippet-lines", "--snippet-focus", "--path", "--exclude-path", "--depth",
        "--focus-line", "--focus-column", "--focus-length",
        "--max-line-width",
    };

    private static string? ExtractQueryArg(string[] args)
    {
        string? firstPositional = null;
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (string.Equals(arg, "--query", StringComparison.Ordinal) && i + 1 < args.Length)
                return args[i + 1];
            if (arg.StartsWith("--query=", StringComparison.Ordinal))
                return arg["--query=".Length..];
            if (string.Equals(arg, "--", StringComparison.Ordinal) && i + 1 < args.Length)
                return args[i + 1];
            if (ValueConsumingOptions.Contains(arg) && i + 1 < args.Length)
            {
                i++;
                continue;
            }
            if (firstPositional is null && !arg.StartsWith('-'))
                firstPositional = arg;
        }
        return firstPositional;
    }

    private static bool TryExtractDbPath(string[] args, out string? dbPath)
    {
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (string.Equals(arg, "--db", StringComparison.Ordinal) && i + 1 < args.Length)
            {
                dbPath = args[i + 1];
                return true;
            }
            if (arg.StartsWith("--db=", StringComparison.Ordinal))
            {
                dbPath = arg["--db=".Length..];
                return true;
            }
        }
        dbPath = null;
        return false;
    }
}
