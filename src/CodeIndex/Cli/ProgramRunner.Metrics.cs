using System.Diagnostics;
using System.Globalization;
using System.ComponentModel;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using CodeIndex.Database;
using CodeIndex.Diagnostics;
using CodeIndex.Indexer;
using CodeIndex.Indexer.Extensibility;
using CodeIndex.Indexer.Hooks;
using CodeIndex.Lsp;
using CodeIndex.Mcp;
using CodeIndex.Models;
using Microsoft.Data.Sqlite;

namespace CodeIndex.Cli;

internal static partial class ProgramRunner
{
    internal static bool TryConsumeMetricsFlag(ref string[] args, out string? path, out string error)
    {
        path = null;
        error = string.Empty;
        if (args.Length == 0)
            return true;

        var kept = new List<string>(args.Length);
        string? requested = null;
        var passthrough = false;
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (passthrough)
            {
                kept.Add(arg);
                continue;
            }
            if (arg == "--")
            {
                passthrough = true;
                kept.Add(arg);
                continue;
            }
            if (ShouldPreserveQueryCommandToken(args, i))
            {
                kept.Add(arg);
                continue;
            }

            string? rawValue = null;
            if (arg == "--metrics")
            {
                if (i + 1 >= args.Length)
                {
                    error = "Error: --metrics requires a path value (use `--metrics <path>` or `--metrics=<path>`).";
                    return false;
                }
                rawValue = args[++i];
            }
            else if (arg.StartsWith("--metrics=", StringComparison.Ordinal))
            {
                rawValue = arg.Substring("--metrics=".Length);
            }
            else
            {
                kept.Add(arg);
                continue;
            }

            if (string.IsNullOrWhiteSpace(rawValue))
            {
                error = "Error: --metrics requires a non-empty path value.";
                return false;
            }
            requested = rawValue;
        }

        path = requested;
        args = kept.ToArray();
        return true;
    }

    internal static bool TryConsumeQueryTraceFlag(ref string[] args, out string traceMode, out string error)
        => TryConsumeQueryTraceFlag(commandName: null, ref args, out traceMode, out error);

    internal static bool TryConsumeQueryTraceFlag(string? commandName, ref string[] args, out string traceMode, out string error)
    {
        traceMode = "none";
        error = string.Empty;
        if (args.Length == 0)
            return true;

        var kept = new List<string>(args.Length);
        var passthrough = false;
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (passthrough)
            {
                kept.Add(arg);
                continue;
            }
            if (arg == "--")
            {
                passthrough = true;
                kept.Add(arg);
                continue;
            }
            if (commandName is not null && ShouldPreserveQueryCommandToken(commandName, args, i))
            {
                kept.Add(arg);
                continue;
            }

            string? rawValue = null;
            if (arg == "--trace")
            {
                if (i + 1 >= args.Length)
                {
                    error = "Error: --trace requires a value (use `--trace stderr`, `--trace file`, `--trace none`, or `--trace=<mode>`).";
                    return false;
                }
                rawValue = args[++i];
            }
            else if (arg.StartsWith("--trace=", StringComparison.Ordinal))
            {
                rawValue = arg.Substring("--trace=".Length);
            }
            else
            {
                kept.Add(arg);
                continue;
            }

            if (string.IsNullOrWhiteSpace(rawValue))
            {
                error = "Error: --trace requires a non-empty value.";
                return false;
            }
            if (rawValue is not ("none" or "stderr" or "file"))
            {
                error = $"Error: --trace must be one of `none`, `stderr`, or `file`, got `{ConsoleUi.FormatBoundedValue(rawValue)}`.";
                return false;
            }
            traceMode = rawValue;
        }

        args = kept.ToArray();
        return true;
    }

    internal static void EmitQueryTrace(string mode, string commandName, string[] subArgs, DateTimeOffset startTimestamp, Stopwatch stopwatch, int exitCode, int? resultCount)
    {
        if (mode == "none")
            return;

        try
        {
            var elapsedMs = stopwatch.Elapsed.TotalMilliseconds;
            var payload = BuildQueryTraceJson(commandName, subArgs, startTimestamp, elapsedMs, exitCode, resultCount);
            if (mode == "stderr")
            {
                CommandErrorWriter.WriteStderr(payload);
                return;
            }

            var selection = GlobalToolLog.ResolveLogDirectorySelectionForRepositoryWrite();
            var directory = selection.Path;
            var boundary = selection.Boundary;
            if (boundary is null)
                Directory.CreateDirectory(directory);
            else
                boundary.CreateSensitiveDestinationDirectory();
            PrivateLogFile.HardenExisting(directory, "query-trace-*.jsonl", boundary: boundary);
            var path = ResolveQueryTracePath(directory);
            var encoded = Encoding.UTF8.GetBytes(payload + Environment.NewLine);
            using (var stream = PrivateLogFile.OpenAppend(path, FileShare.ReadWrite, boundary))
            {
                stream.Write(encoded, 0, encoded.Length);
                stream.Flush();
            }
            PrivateLogFile.TrySetPrivatePermissions(path, boundary: boundary);
            PrivateLogFile.PruneOldFiles(
                directory,
                "query-trace-*.jsonl",
                RetainedQueryTraceFileCount,
                boundary: boundary);
        }
        catch
        {
            // Best-effort only: trace output must never change query command behavior.
        }
    }

    private static string ResolveQueryTracePath(string directory)
    {
        var date = TimeProvider.GetUtcNow().UtcDateTime.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        return Path.Combine(directory, $"query-trace-{date}.jsonl");
    }

    private static string BuildQueryTraceJson(string commandName, string[] subArgs, DateTimeOffset timestamp, double elapsedMs, int exitCode, int? resultCount)
    {
        var payload = new JsonObject
        {
            ["timestamp"] = timestamp.ToString("O", CultureInfo.InvariantCulture),
            ["tool"] = commandName,
            ["source"] = "cli_query",
            ["parameters"] = BuildQueryTraceParameters(subArgs),
            ["elapsed_ms"] = Math.Round(elapsedMs, 3),
            ["result_count"] = resultCount,
            ["exit_code"] = exitCode,
        };
        if (exitCode != CommandExitCodes.Success)
            payload["error"] = "command_failed";
        return payload.ToJsonString(CreateDefaultJsonOptions());
    }

    private static JsonObject BuildQueryTraceParameters(string[] args)
    {
        var parameters = new JsonObject
        {
            ["json"] = false,
            ["count"] = false,
        };
        var paths = new List<string>();
        var excludePaths = new List<string>();
        var passthrough = false;
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (passthrough)
                continue;
            if (arg == "--")
            {
                passthrough = true;
                continue;
            }

            string? inlineValue = null;
            var optionName = arg;
            var equals = arg.IndexOf('=');
            if (equals > 0)
            {
                optionName = arg[..equals];
                inlineValue = arg[(equals + 1)..];
            }

            string? value = inlineValue;
            if (value == null && optionName is "--lang" or "--limit" or "--top" or "--path" or "--exclude-path")
            {
                if (i + 1 < args.Length)
                    value = args[++i];
            }

            switch (optionName)
            {
                case "--json":
                    parameters["json"] = true;
                    if (!string.IsNullOrWhiteSpace(value))
                        AddQueryTraceString(parameters, "json_format", value);
                    break;
                case "--count":
                    parameters["count"] = true;
                    break;
                case "--lang" when !string.IsNullOrWhiteSpace(value):
                    AddQueryTraceString(parameters, "lang", value);
                    break;
                case "--limit" when !string.IsNullOrWhiteSpace(value):
                case "--top" when !string.IsNullOrWhiteSpace(value):
                    AddQueryTraceString(parameters, "limit", value);
                    break;
                case "--path" when !string.IsNullOrWhiteSpace(value):
                    paths.Add(value);
                    break;
                case "--exclude-path" when !string.IsNullOrWhiteSpace(value):
                    excludePaths.Add(value);
                    break;
            }
        }
        AddQueryTraceArray(parameters, "path", paths);
        AddQueryTraceArray(parameters, "exclude_path", excludePaths);
        return parameters;
    }

    private static void AddQueryTraceString(JsonObject parameters, string name, string value)
    {
        var bounded = ConsoleUi.BoundDisplayText(value, QueryTraceValueMaxChars);
        parameters[name] = bounded.Text;
        if (bounded.Truncated)
        {
            parameters[$"{name}_truncated"] = true;
            parameters[$"{name}_original_length"] = bounded.OriginalLength;
        }
    }

    private static void AddQueryTraceArray(JsonObject parameters, string name, List<string> values)
    {
        if (values.Count == 0)
            return;

        var array = new JsonArray();
        var valueTruncated = false;
        foreach (var value in values.Take(QueryTraceArrayMaxItems))
        {
            var bounded = ConsoleUi.BoundDisplayText(value, QueryTraceValueMaxChars);
            valueTruncated |= bounded.Truncated;
            array.Add(JsonValue.Create(bounded.Text));
        }

        parameters[name] = array;
        if (values.Count > QueryTraceArrayMaxItems)
        {
            parameters[$"{name}_truncated"] = true;
            parameters[$"{name}_original_count"] = values.Count;
        }

        if (valueTruncated)
            parameters[$"{name}_value_truncated"] = true;
    }

    private sealed class QueryTraceOutputCapture : TextWriter
    {
        private readonly TextWriter _inner;
        private readonly IDisposable _ownership;
        private readonly bool _countNumericOutput;
        private readonly bool _countJsonLines;
        private bool _disposed;

        private QueryTraceOutputCapture(
            TextWriter inner,
            IDisposable ownership,
            bool countNumericOutput,
            bool countJsonLines)
        {
            _inner = inner;
            _ownership = ownership;
            _countNumericOutput = countNumericOutput;
            _countJsonLines = countJsonLines;
        }

        public override Encoding Encoding => _inner.Encoding;
        public int? ResultCount { get; private set; }

        public static QueryTraceOutputCapture? TryStart(string traceMode, string[] args)
        {
            if (traceMode == "none")
                return null;

            var ownership = ConsoleStreamOwnership.Enter();
            try
            {
                var capture = new QueryTraceOutputCapture(
                    Console.Out,
                    ownership,
                    HasFlag(args, "--count"),
                    HasFlag(args, "--json") && !HasInlineValue(args, "--json", "array"));
                Console.SetOut(capture);
                return capture;
            }
            catch
            {
                ownership.Dispose();
                throw;
            }
        }

        public override void Write(char value) => _inner.Write(value);
        public override void Write(string? value) => _inner.Write(value);

        public override void WriteLine(string? value)
        {
            _inner.WriteLine(value);
            ObserveLine(value);
        }

        public override void WriteLine()
        {
            _inner.WriteLine();
            ObserveLine(string.Empty);
        }

        protected override void Dispose(bool disposing)
        {
            if (!_disposed && disposing)
            {
                try
                {
                    ConsoleStreamOwnership.RestoreOut(_inner);
                    _disposed = true;
                }
                finally
                {
                    _ownership.Dispose();
                }
            }
            base.Dispose(disposing);
        }

        private void ObserveLine(string? value)
        {
            if (value == null)
                return;

            var trimmed = value.Trim();
            if (_countNumericOutput && int.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var count) && count >= 0)
            {
                ResultCount = count;
                return;
            }

            if (_countJsonLines && trimmed.StartsWith('{'))
                ResultCount = (ResultCount ?? 0) + 1;
        }

        private static bool HasFlag(string[] args, string name)
        {
            var passthrough = false;
            foreach (var arg in args)
            {
                if (passthrough)
                    continue;
                if (arg == "--")
                {
                    passthrough = true;
                    continue;
                }
                if (arg == name || arg.StartsWith(name + "=", StringComparison.Ordinal))
                    return true;
            }
            return false;
        }

        private static bool HasInlineValue(string[] args, string name, string value)
        {
            var expected = name + "=" + value;
            var passthrough = false;
            foreach (var arg in args)
            {
                if (passthrough)
                    continue;
                if (arg == "--")
                {
                    passthrough = true;
                    continue;
                }
                if (arg == expected)
                    return true;
            }
            return false;
        }
    }

    internal static void EmitCommandMetric(string tool, string[] args, DateTimeOffset startTimestamp, Stopwatch stopwatch, int exitCode, string? error = null)
    {
        if (!MetricsSink.IsActive)
            return;

        stopwatch.Stop();
        MetricsSink.Record(new MetricsEvent(
            Timestamp: startTimestamp,
            Tool: tool,
            Source: "cli",
            ElapsedMs: stopwatch.Elapsed.TotalMilliseconds,
            ExitCode: exitCode,
            Language: TryParseLanguageFromArgs(args),
            Error: error));
    }

    internal static string? TryParseLanguageFromArgs(string[] args)
    {
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg == "--")
                return null;
            if (arg == "--lang" && i + 1 < args.Length)
                return args[i + 1];
            if (arg.StartsWith("--lang=", StringComparison.Ordinal))
                return arg.Substring("--lang=".Length);
        }
        return null;
    }

    internal static JsonSerializerOptions CreateDefaultJsonOptions() => new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
        TypeInfoResolver = CliJsonSerializerContext.Default,
    };
}
