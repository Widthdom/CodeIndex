using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CodeIndex.Diagnostics;

namespace CodeIndex.Cli;

/// <summary>
/// Persists one bounded, redacted top-level failure independently of lifecycle logging so
/// the next `cdidx report` invocation can describe the failure that recommended the report.
/// lifecycle log の有効・無効とは独立して、上限付き・匿名化済みの直近 top-level failure を
/// 1 件だけ保存し、案内後の `cdidx report` で同じ失敗を説明できるようにする。
/// </summary>
internal static class LastFailureEventStore
{
    internal const string FileName = "last-failure.json";
    internal const int SchemaVersion = 2;
    internal const int MaxEventBytes = 32 * 1024;
    internal const int MaxDiagnosticsChars = 8 * 1024;
    internal const int MaxDiagnosticLines = 32;
    private const int MaxFieldChars = 256;

    internal static bool TryPersist(
        IReadOnlyList<string> args,
        string appVersion,
        int exitCode,
        Exception exception,
        DateTimeOffset occurredAtUtc)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(exception);

        try
        {
            var failure = new LastFailureEvent(
                SchemaVersion,
                occurredAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
                SanitizeField(appVersion),
                ResolveBinaryPath(),
                DiagnosticSanitizer.ForPath(Environment.ProcessPath),
                ResolveCommandCategory(args),
                exitCode,
                SanitizeField(DiagnosticRedactor.ClassifyException(exception)),
                SanitizeField(exception.GetType().FullName ?? exception.GetType().Name),
                SanitizeField(DiagnosticRedactor.ClassifyException(exception)),
                SanitizeDiagnostics(GlobalToolLog.FormatExceptionChain(exception, includeStacks: true)),
                PathsRedacted: true,
                LiteralArgumentsIncluded: false);

            var json = JsonSerializer.Serialize(failure, LastFailureEventJsonContext.Default.LastFailureEvent);
            if (Encoding.UTF8.GetByteCount(json) > MaxEventBytes)
                return false;

            var logDirectory = GlobalToolLog.ResolveLogDirectoryForReport();
            DataDirectorySecurity.CreateSensitiveDirectory(logDirectory);
            DataDirectorySecurity.WritePrivateText(Path.Combine(logDirectory, FileName), json + "\n");
            return true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Failure capture is best-effort and must never replace the original exception.
            // failure capture はベストエフォートであり、元の例外を上書きしてはならない。
            return false;
        }
    }

    internal static bool TryBuildReportPayload(out string payload)
    {
        payload = string.Empty;
        try
        {
            var path = Path.Combine(GlobalToolLog.ResolveLogDirectoryForReport(), FileName);
            if (!File.Exists(path))
                return false;
            if (FileSystemBoundary.IsSymlinkOrReparsePoint(new FileInfo(path)))
                return false;

            var json = DataDirectorySecurity.ReadTextWithinLimit(
                path,
                MaxEventBytes,
                FileShare.ReadWrite | FileShare.Delete);
            if (string.IsNullOrWhiteSpace(json))
                return false;

            var failure = JsonSerializer.Deserialize(json, LastFailureEventJsonContext.Default.LastFailureEvent);
            if (!TryNormalize(failure, out var normalized))
                return false;

            payload = JsonSerializer.Serialize(normalized, LastFailureEventJsonContext.Default.LastFailureEvent);
            return Encoding.UTF8.GetByteCount(payload) <= MaxEventBytes;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Corrupt or unavailable diagnostic state must not prevent report creation.
            // 壊れた、または読み取れない診断 state で report 作成を妨げない。
            payload = string.Empty;
            return false;
        }
    }

    private static bool TryNormalize(LastFailureEvent? failure, out LastFailureEvent normalized)
    {
        normalized = null!;
        if (failure is null
            || failure.SchemaVersion != SchemaVersion
            || !failure.PathsRedacted
            || failure.LiteralArgumentsIncluded
            || !DateTimeOffset.TryParseExact(
                failure.OccurredAtUtc,
                "O",
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var occurredAtUtc)
            || string.IsNullOrWhiteSpace(failure.BinaryVersion)
            || string.IsNullOrWhiteSpace(failure.CommandCategory)
            || string.IsNullOrWhiteSpace(failure.ExceptionCategory)
            || string.IsNullOrWhiteSpace(failure.ExceptionType)
            || !TryNormalizeStoredCommandCategory(failure.CommandCategory, out var commandCategory)
            || !IsStableExceptionCategory(failure.ExceptionCategory)
            || !string.Equals(failure.ExceptionMessage, failure.ExceptionCategory, StringComparison.Ordinal)
            || !string.Equals(failure.ExceptionType, SanitizeField(failure.ExceptionType), StringComparison.Ordinal)
            || !TryNormalizeStoredDiagnostics(
                failure.Diagnostics,
                failure.ExceptionCategory,
                failure.ExceptionType,
                out var diagnostics))
        {
            return false;
        }

        normalized = failure with
        {
            OccurredAtUtc = occurredAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            BinaryVersion = SanitizeField(failure.BinaryVersion),
            BinaryPath = DiagnosticSanitizer.ForPath(failure.BinaryPath),
            ProcessPath = DiagnosticSanitizer.ForPath(failure.ProcessPath),
            CommandCategory = commandCategory,
            ExceptionMessage = failure.ExceptionCategory,
            Diagnostics = diagnostics,
            PathsRedacted = true,
            LiteralArgumentsIncluded = false,
        };
        return true;
    }

    private static string ResolveCommandCategory(IReadOnlyList<string> args)
    {
        if (args.Count == 0 || string.IsNullOrWhiteSpace(args[0]))
            return "unknown";

        var firstArg = args[0];
        if (CliCommandCatalog.TryResolvePublicCommand(firstArg, out var command))
            return command;

        return ProgramRunner.IsProjectPathArg(firstArg) ? "index" : "unknown";
    }

    private static string ResolveBinaryPath()
    {
        var assemblyPath = typeof(ProgramRunner).Assembly.Location;
        return DiagnosticSanitizer.ForPath(
            string.IsNullOrWhiteSpace(assemblyPath) ? Environment.ProcessPath : assemblyPath);
    }

    private static string SanitizeField(string? value)
        => DiagnosticSanitizer.ForMessage(value, MaxFieldChars);

    private static bool TryNormalizeStoredCommandCategory(string category, out string normalized)
    {
        normalized = string.Empty;
        if (string.Equals(category, "unknown", StringComparison.Ordinal))
        {
            normalized = category;
            return true;
        }

        if (!CliCommandCatalog.TryResolvePublicCommand(category, out var command)
            || !string.Equals(command, category, StringComparison.Ordinal))
        {
            return false;
        }

        normalized = command;
        return true;
    }

    private static bool IsStableExceptionCategory(string category) => category is
        "access_denied"
        or "path_too_long"
        or "directory_not_found"
        or "file_not_found"
        or "io_error"
        or "decoder_error"
        or "operation_canceled"
        or "timeout"
        or "sqlite_error"
        or "argument_error"
        or "invalid_operation"
        or "not_supported"
        or "exception_message_redacted";

    private static bool TryNormalizeStoredDiagnostics(
        string? diagnostics,
        string expectedCategory,
        string expectedType,
        out string normalized)
    {
        normalized = string.Empty;
        var sanitized = SanitizeDiagnostics(diagnostics);
        if (!string.Equals(diagnostics, sanitized, StringComparison.Ordinal))
            return false;

        var lines = sanitized.Split('\n');
        if (lines.Length == 0
            || !IsStructuredExceptionHeader(lines[0], requireRoot: true, expectedCategory, expectedType))
        {
            return false;
        }

        for (var index = 1; index < lines.Length; index++)
        {
            var line = lines[index];
            if (IsTerminalDiagnosticMarker(line))
            {
                if (index != lines.Length - 1)
                    return false;
                continue;
            }

            if (index == lines.Length - 1
                && line.EndsWith(GlobalToolLog.ExceptionChainTruncationMarker, StringComparison.Ordinal))
            {
                line = line[..^GlobalToolLog.ExceptionChainTruncationMarker.Length];
                if (line.Length == 0)
                    continue;
            }

            if (!IsStructuredExceptionHeader(line, requireRoot: false, expectedCategory: null, expectedType: null)
                && !IsStructuredStackLine(line)
                && !IsStructuredAggregateIndex(line))
            {
                return false;
            }
        }

        normalized = sanitized;
        return true;
    }

    private static bool IsStructuredExceptionHeader(
        string line,
        bool requireRoot,
        string? expectedCategory,
        string? expectedType)
    {
        var leadingSpaces = CountLeadingSpaces(line);
        var content = line[leadingSpaces..];
        var bracketStart = content.IndexOf('[', StringComparison.Ordinal);
        var bracketEnd = content.IndexOf("] type=", StringComparison.Ordinal);
        if (bracketStart <= 0 || bracketEnd <= bracketStart + 1)
            return false;

        if (!int.TryParse(
                content.AsSpan(bracketStart + 1, bracketEnd - bracketStart - 1),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var depth)
            || depth < 0
            || leadingSpaces != depth * 2
            || requireRoot != (depth == 0)
            || !string.Equals(
                content[..bracketStart],
                depth == 0 ? "exception" : "inner_exception",
                StringComparison.Ordinal))
        {
            return false;
        }

        const string messageMarker = " message=\"";
        var typeStart = bracketEnd + "] type=".Length;
        var messageStart = content.IndexOf(messageMarker, typeStart, StringComparison.Ordinal);
        if (messageStart <= typeStart || !content.EndsWith('"'))
            return false;

        var type = content[typeStart..messageStart];
        var category = content[(messageStart + messageMarker.Length)..^1];
        return string.Equals(type, SanitizeField(type), StringComparison.Ordinal)
            && IsStableExceptionCategory(category)
            && (!requireRoot
                || (string.Equals(category, expectedCategory, StringComparison.Ordinal)
                    && string.Equals(type, expectedType, StringComparison.Ordinal)));
    }

    private static bool IsStructuredStackLine(string line)
    {
        var leadingSpaces = CountLeadingSpaces(line);
        if (leadingSpaces < 2 || leadingSpaces % 2 != 0)
            return false;

        var content = line[leadingSpaces..];
        if (!content.StartsWith("stack: ", StringComparison.Ordinal))
            return false;

        var frame = content["stack: ".Length..].TrimStart();
        if (frame is "--- End of stack trace from previous location ---"
            or "--- End of inner exception stack trace ---")
        {
            return true;
        }

        if (!frame.StartsWith("at ", StringComparison.Ordinal))
            return false;

        var openParenthesis = frame.IndexOf('(', "at ".Length);
        var closeParenthesis = openParenthesis < 0 ? -1 : frame.IndexOf(')', openParenthesis + 1);
        if (openParenthesis <= "at ".Length || closeParenthesis <= openParenthesis)
            return false;

        foreach (var character in frame.AsSpan("at ".Length, openParenthesis - "at ".Length))
        {
            if (char.IsWhiteSpace(character) || character is '=' or '"' or '\'' or '\\')
                return false;
        }

        return true;
    }

    private static bool IsStructuredAggregateIndex(string line)
    {
        var leadingSpaces = CountLeadingSpaces(line);
        if (leadingSpaces < 2 || leadingSpaces % 2 != 0)
            return false;

        const string prefix = "aggregate_inner_index=";
        var content = line[leadingSpaces..];
        return content.StartsWith(prefix, StringComparison.Ordinal)
            && int.TryParse(
                content.AsSpan(prefix.Length),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var index)
            && index >= 0;
    }

    private static bool IsTerminalDiagnosticMarker(string line) => line is
        "[diagnostic lines truncated]"
        or "[diagnostics truncated]"
        or GlobalToolLog.ExceptionChainTruncationMarker;

    private static int CountLeadingSpaces(string value)
    {
        var count = 0;
        while (count < value.Length && value[count] == ' ')
            count++;
        return count;
    }

    private static string SanitizeDiagnostics(string? diagnostics)
    {
        if (string.IsNullOrWhiteSpace(diagnostics))
            return "unavailable";

        var lines = diagnostics
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n');
        var output = new StringBuilder(Math.Min(diagnostics.Length, MaxDiagnosticsChars));
        var count = Math.Min(lines.Length, MaxDiagnosticLines);
        for (var index = 0; index < count; index++)
        {
            if (index > 0)
                output.Append('\n');
            output.Append(DiagnosticRedactor.FormatExceptionStackLine(lines[index], maxChars: 512));
            if (output.Length >= MaxDiagnosticsChars)
                break;
        }

        if (lines.Length > MaxDiagnosticLines)
            output.Append("\n[diagnostic lines truncated]");
        if (output.Length > MaxDiagnosticsChars)
            return output.ToString(0, MaxDiagnosticsChars - 24) + "\n[diagnostics truncated]";
        return output.ToString();
    }
}

internal sealed record LastFailureEvent(
    int SchemaVersion,
    string OccurredAtUtc,
    string BinaryVersion,
    string BinaryPath,
    string ProcessPath,
    string CommandCategory,
    int ExitCode,
    string ExceptionCategory,
    string ExceptionType,
    string ExceptionMessage,
    string Diagnostics,
    bool PathsRedacted,
    bool LiteralArgumentsIncluded);

[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(LastFailureEvent))]
internal partial class LastFailureEventJsonContext : JsonSerializerContext;
