using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using CodeIndex.Database;
using CodeIndex.Diagnostics;
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
internal static partial class JsonEnvelopeWrapper
{
    internal const string EnvelopeFlag = "--json-envelope";
    internal const int MaxCapturedOutputChars = 10 * 1024 * 1024;
    internal const int MaxRawJsonItemChars = 1024 * 1024;
    internal const int MaxRawJsonItems = 10_000;
    internal const int MaxRawJsonNodes = 16_384;
    internal const int MaxRawJsonItemDepth = 32;

    private static readonly HashSet<string> WrappableCommands = new(StringComparer.Ordinal)
    {
        "search", "definition", "references", "callers", "callees",
        "symbols", "files", "find", "excerpt", "map", "inspect",
        "outline", "status", "validate", "languages", "impact",
        "deps", "unused", "hotspots",
    };

    internal static string CanonicalizeCommandName(string command)
        => command switch
        {
            "refs" => "references",
            "stats" => "status",
            _ => command,
        };

    internal static bool ShouldWrap(string command, string[] args)
    {
        command = CanonicalizeCommandName(command);
        return WrappableCommands.Contains(command)
               && (HasEnvelopeFlag(args) || ShouldAutoWrapBoundedResponse(command, args));
    }

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
        command = CanonicalizeCommandName(command);
        if (IsBoundedResponseRequest(command, args))
            return RunBoundedResponse(command, args, appVersion, jsonOptions, runInner);

        if (HasArgument(args, "--max-json-bytes"))
        {
            CommandErrorWriter.WriteStderr("Error [E010_USAGE_ERROR]: --json-envelope cannot be combined with --max-json-bytes because envelope serialization changes the final stdout byte count.");
            CommandErrorWriter.WriteStderr("Hint: use streaming --json=ndjson with --max-json-bytes, or remove the byte cap when a single JSON envelope is required.");
            return CommandExitCodes.UsageError;
        }

        var innerArgs = PrepareInnerArgs(args);
        var queryNormalized = ExtractQueryArg(args);
        var (resolvedDbPath, dbPathExplicit) = ResolveQueryDbPath(args);

        using var captured = new BoundedStringWriter(MaxCapturedOutputChars);
        var stopwatch = Stopwatch.StartNew();
        int exitCode;
        JsonEnvelopeCaptureLimitExceededException? captureLimitExceeded = null;
        try
        {
            using var outputScope = ScopedConsoleOutput.Redirect(captured);
            exitCode = runInner(innerArgs);
        }
        catch (JsonEnvelopeCaptureLimitExceededException ex)
        {
            captureLimitExceeded = ex;
            exitCode = CommandExitCodes.InvalidArgument;
        }
        finally
        {
            stopwatch.Stop();
        }

        if (captureLimitExceeded is not null)
        {
            var message = $"--json-envelope captured output exceeded {captureLimitExceeded.MaxChars} characters.";
            var hint = "Reduce the result set with --limit/--top or run the command with --json for streaming NDJSON output.";
            CommandErrorWriter.WriteStderr($"Error [{CommandErrorCodes.UsageError}]: {message}");
            CommandErrorWriter.WriteStderr($"Hint: {hint}");
            var envelopeError = new JsonObject
            {
                ["message"] = message,
                ["hint"] = hint,
                ["error_code"] = CommandErrorCodes.UsageError,
            };
            var overflowEnvelope = BuildEnvelope(
                command,
                queryNormalized,
                resolvedDbPath,
                dbPathExplicit,
                appVersion,
                stopwatch.Elapsed.TotalMilliseconds,
                new JsonArray(),
                exitCode,
                envelopeError);

            Console.WriteLine(overflowEnvelope.ToJsonString(jsonOptions));
            return exitCode;
        }

        var raw = captured.ToString();
        JsonArray results;
        JsonObject? parseError = null;
        JsonObject? streamTerminal = null;
        JsonArray? streamControlRecords = null;
        try
        {
            results = ParseRawJsonItems(command, raw, out streamTerminal, out streamControlRecords);
        }
        catch (JsonEnvelopeRawJsonItemLimitExceededException ex)
        {
            exitCode = CommandExitCodes.InvalidArgument;
            var message = $"--json-envelope raw JSON item line exceeded {ex.MaxChars} characters.";
            var hint = "Run the command with --json for streaming NDJSON output or reduce the raw item size.";
            CommandErrorWriter.WriteStderr($"Error [{CommandErrorCodes.UsageError}]: {message}");
            CommandErrorWriter.WriteStderr($"Hint: {hint}");
            parseError = new JsonObject
            {
                ["message"] = message,
                ["hint"] = hint,
                ["error_code"] = CommandErrorCodes.UsageError,
                ["max_chars"] = ex.MaxChars,
            };
            results = [];
        }
        catch (JsonEnvelopeRawJsonBudgetExceededException ex)
        {
            exitCode = CommandExitCodes.InvalidArgument;
            var message = $"--json-envelope raw JSON {ex.BudgetName} exceeded {ex.MaxValue}.";
            var hint = "Run the command with --json for streaming NDJSON output or reduce the result set with --limit/--top.";
            CommandErrorWriter.WriteStderr($"Error [{CommandErrorCodes.UsageError}]: {message}");
            CommandErrorWriter.WriteStderr($"Hint: {hint}");
            parseError = new JsonObject
            {
                ["message"] = message,
                ["hint"] = hint,
                ["error_code"] = CommandErrorCodes.UsageError,
                [ex.JsonPropertyName] = ex.MaxValue,
            };
            results = [];
        }
        var envelope = BuildEnvelope(
            command,
            queryNormalized,
            resolvedDbPath,
            dbPathExplicit,
            appVersion,
            stopwatch.Elapsed.TotalMilliseconds,
            results,
            exitCode,
            parseError,
            streamTerminal,
            streamControlRecords);

        Console.WriteLine(envelope.ToJsonString(jsonOptions));
        return exitCode;
    }

    private static bool HasArgument(string[] args, string option)
        => args.Any(arg => string.Equals(arg, option, StringComparison.Ordinal)
                           || arg.StartsWith(option + "=", StringComparison.Ordinal));

    private static JsonObject BuildEnvelope(
        string command,
        string? queryNormalized,
        string dbPath,
        bool dbPathExplicit,
        string appVersion,
        double elapsedMs,
        JsonArray results,
        int exitCode,
        JsonObject? error = null,
        JsonObject? streamTerminal = null,
        JsonArray? streamControlRecords = null)
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
        if (error is not null)
            metadata["error"] = error;
        if (streamTerminal is not null)
            metadata["stream_terminal"] = streamTerminal.DeepClone();
        if (streamControlRecords is { Count: > 0 })
            metadata["stream_control_records"] = streamControlRecords.DeepClone();

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
            var normalizedPath = DbPathResolver.NormalizeDbPath(resolvedPath);
            return DbPathResolver.TryReadIndexedHeadSha(normalizedPath)
                ?? DbPathResolver.TryReadIndexedHeadCommit(normalizedPath);
        }
        catch
        {
            return null;
        }
    }

    private static JsonArray ParseRawJsonItems(
        string command,
        string raw,
        out JsonObject? streamTerminal,
        out JsonArray streamControlRecords)
    {
        var array = new JsonArray();
        streamTerminal = null;
        streamControlRecords = [];
        var rawJsonNodeCount = 0;
        if (string.IsNullOrEmpty(raw))
            return array;

        var trimmed = raw.Trim();
        if (trimmed.Length <= MaxRawJsonItemChars)
        {
            try
            {
                var document = JsonFrameParser.ParseNode(trimmed, MaxRawJsonItemDepth);
                if (document is not null)
                {
                    rawJsonNodeCount = AddRawJsonNodeCount(rawJsonNodeCount, CountJsonNodes(document));
                    if (IsJsonStreamTerminal(document))
                    {
                        streamTerminal = (JsonObject)document.DeepClone();
                        if (!IsTerminalResultRecord(command, document))
                            return array;
                    }
                    else if (IsJsonStreamControlRecord(document))
                    {
                        streamControlRecords.Add(document);
                        return array;
                    }

                    array.Add(document);
                    return array;
                }
            }
            catch (JsonException)
            {
                // Multiple NDJSON frames are parsed line by line below.
            }
        }

        foreach (var line in EnumerateRawLines(raw))
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;
            if (line.Length > MaxRawJsonItemChars)
                throw new JsonEnvelopeRawJsonItemLimitExceededException(MaxRawJsonItemChars);

            JsonNode? node;
            try
            {
                node = JsonFrameParser.ParseNode(line, MaxRawJsonItemDepth);
            }
            catch (JsonException)
            {
                EnsureRawJsonItemBudget(array.Count);
                rawJsonNodeCount = AddRawJsonNodeCount(rawJsonNodeCount, 1);
                array.Add(line);
                continue;
            }

            if (node is null)
                continue;
            if (IsJsonStreamTerminal(node))
            {
                streamTerminal = (JsonObject)node.DeepClone();
                if (!IsTerminalResultRecord(command, node))
                    continue;
            }
            else if (IsJsonStreamControlRecord(node))
            {
                EnsureRawJsonItemBudget(array.Count + streamControlRecords.Count);
                rawJsonNodeCount = AddRawJsonNodeCount(rawJsonNodeCount, CountJsonNodes(node));
                streamControlRecords.Add(node);
                continue;
            }

            EnsureRawJsonItemBudget(array.Count + streamControlRecords.Count);
            rawJsonNodeCount = AddRawJsonNodeCount(rawJsonNodeCount, CountJsonNodes(node));
            array.Add(node);
        }

        return array;
    }

    private static void EnsureRawJsonItemBudget(int currentItemCount)
    {
        if (currentItemCount >= MaxRawJsonItems)
        {
            throw new JsonEnvelopeRawJsonBudgetExceededException(
                "item count",
                "max_items",
                MaxRawJsonItems);
        }
    }

    private static int AddRawJsonNodeCount(int currentNodeCount, int nodesToAdd)
    {
        if (nodesToAdd < 0 || currentNodeCount > MaxRawJsonNodes - nodesToAdd)
        {
            throw new JsonEnvelopeRawJsonBudgetExceededException(
                "node count",
                "max_nodes",
                MaxRawJsonNodes);
        }

        return currentNodeCount + nodesToAdd;
    }

    private static int CountJsonNodes(JsonNode node)
    {
        var count = 0;
        Count(node);
        return count;

        void Count(JsonNode? current)
        {
            if (current is null)
                return;
            count = AddRawJsonNodeCount(count, 1);
            switch (current)
            {
                case JsonArray array:
                    foreach (var child in array)
                        Count(child);
                    break;
                case JsonObject obj:
                    foreach (var property in obj)
                        Count(property.Value);
                    break;
            }
        }
    }

    private static IEnumerable<string> EnumerateRawLines(string raw)
    {
        var start = 0;
        while (start <= raw.Length)
        {
            var newline = raw.IndexOf('\n', start);
            if (newline < 0)
            {
                yield return raw[start..].TrimEnd('\r');
                yield break;
            }

            yield return raw[start..newline].TrimEnd('\r');
            start = newline + 1;
        }
    }

    private static bool IsJsonStreamTerminal(JsonNode node)
    {
        if (node is not JsonObject obj)
            return false;
        if (obj.TryGetPropertyValue("terminal_record", out var terminalNode)
            && terminalNode is JsonValue terminalValue
            && terminalValue.TryGetValue<bool>(out var terminal)
            && terminal)
            return true;
        return obj.TryGetPropertyValue("done", out var doneNode)
            && doneNode is JsonValue doneValue
            && doneValue.TryGetValue<bool>(out var done)
            && done
            && obj.TryGetPropertyValue("interrupted", out _)
            && obj.TryGetPropertyValue("count", out _);
    }

    private static bool IsJsonStreamControlRecord(JsonNode node)
    {
        if (node is not JsonObject obj)
            return false;
        if (obj.ContainsKey("profile") || obj.ContainsKey("_debug"))
            return true;
        if (!obj.TryGetPropertyValue("count", out var countNode)
            || countNode is not JsonValue countValue
            || !countValue.TryGetValue<int>(out var count)
            || count != 0)
            return false;

        return HasEmptyArray(obj, "results") || HasEmptyArray(obj, "files");
    }

    private static bool HasEmptyArray(JsonObject obj, string propertyName)
        => obj.TryGetPropertyValue(propertyName, out var node)
           && node is JsonArray { Count: 0 };

    private static bool IsTerminalResultRecord(string command, JsonNode node)
        => string.Equals(command, "find", StringComparison.Ordinal)
           && node is JsonObject obj
           && obj.TryGetPropertyValue("count", out _);

    // Mirrors the value-taking options in QueryCommandRunner.ParseArgs so we can locate the
    // first positional (= query) without being fooled by `--db <path>`-style values.
    // QueryCommandRunner.ParseArgs と同じ value-taking option を認識し、`--db <path>` の値を
    // positional 引数（= query）と取り違えないようにする。
    private static readonly HashSet<string> ValueConsumingOptions = new(StringComparer.Ordinal)
    {
        "--db", "--data-dir", "--limit", "--top", "--lang", "--kind", "--since",
        "--start", "--end", "--before", "--after", "--name",
        "--snippet-lines", "--snippet-focus", "--path", "--exclude-path", "--max-hops", "--depth",
        "--focus-line", "--focus-column", "--focus-length",
        "--max-line-width", "--fields", "--cursor", "--max-json-bytes", "--format",
        "--sections", "--rank-by", "--visibility", "--exclude-visibility", "--group-by",
        "--stale-after", "--explain", "--context", "--line-scan-limit", "--min-entrypoint-confidence",
        "--project", "--solution",
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
        => TryExtractOptionValue(args, "--db", out dbPath);

    private static bool TryExtractDataDir(string[] args, out string? dataDir)
        => TryExtractOptionValue(args, "--data-dir", out dataDir);

    private static bool TryExtractOptionValue(string[] args, string option, out string? value)
    {
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (string.Equals(arg, option, StringComparison.Ordinal) && i + 1 < args.Length)
            {
                value = args[i + 1];
                return true;
            }
            if (arg.StartsWith(option + "=", StringComparison.Ordinal))
            {
                value = arg[(option.Length + 1)..];
                return true;
            }
        }
        value = null;
        return false;
    }

    private static (string DbPath, bool DbPathExplicit) ResolveQueryDbPath(string[] args)
    {
        var dbPathExplicit = TryExtractDbPath(args, out var explicitDbPath);
        TryExtractDataDir(args, out var explicitDataDir);
        var resolution = DbPathResolver.ResolveForQuery(
            Environment.CurrentDirectory,
            explicitDbPath,
            explicitDataDir);
        return (resolution.DbPath, dbPathExplicit);
    }

    private sealed class BoundedStringWriter(int maxChars) : StringWriter
    {
        private int _writtenChars;

        public override void Write(char value)
        {
            EnsureCapacity(1);
            base.Write(value);
        }

        public override void Write(string? value)
        {
            if (value is null)
                return;
            EnsureCapacity(value.Length);
            base.Write(value);
        }

        public override void Write(char[]? buffer, int index, int count)
        {
            if (buffer is null)
                return;
            EnsureCapacity(count);
            base.Write(buffer, index, count);
        }

        public override void Write(ReadOnlySpan<char> buffer)
        {
            EnsureCapacity(buffer.Length);
            base.Write(buffer);
        }

        private void EnsureCapacity(int charCount)
        {
            if (charCount < 0 || _writtenChars > maxChars - charCount)
                throw new JsonEnvelopeCaptureLimitExceededException(maxChars);
            _writtenChars += charCount;
        }
    }

    private sealed class JsonEnvelopeCaptureLimitExceededException(int maxChars) : Exception
    {
        public int MaxChars { get; } = maxChars;
    }

    private sealed class JsonEnvelopeRawJsonItemLimitExceededException(int maxChars) : Exception
    {
        public int MaxChars { get; } = maxChars;
    }

    private sealed class JsonEnvelopeRawJsonBudgetExceededException(
        string budgetName,
        string jsonPropertyName,
        int maxValue) : Exception
    {
        public string BudgetName { get; } = budgetName;
        public string JsonPropertyName { get; } = jsonPropertyName;
        public int MaxValue { get; } = maxValue;
    }
}
