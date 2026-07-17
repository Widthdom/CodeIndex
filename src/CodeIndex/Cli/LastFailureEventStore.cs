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
    internal const int SchemaVersion = 1;
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
                DiagnosticRedactor.FormatExceptionMessage(exception, maxChars: 512, redactPaths: true),
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
            || !DateTimeOffset.TryParseExact(
                failure.OccurredAtUtc,
                "O",
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var occurredAtUtc)
            || string.IsNullOrWhiteSpace(failure.BinaryVersion)
            || string.IsNullOrWhiteSpace(failure.CommandCategory)
            || string.IsNullOrWhiteSpace(failure.ExceptionCategory)
            || string.IsNullOrWhiteSpace(failure.ExceptionType))
        {
            return false;
        }

        normalized = failure with
        {
            OccurredAtUtc = occurredAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            BinaryVersion = SanitizeField(failure.BinaryVersion),
            BinaryPath = DiagnosticSanitizer.ForPath(failure.BinaryPath),
            ProcessPath = DiagnosticSanitizer.ForPath(failure.ProcessPath),
            CommandCategory = SanitizeField(failure.CommandCategory),
            ExceptionCategory = SanitizeField(failure.ExceptionCategory),
            ExceptionType = SanitizeField(failure.ExceptionType),
            ExceptionMessage = DiagnosticSanitizer.ForMessage(failure.ExceptionMessage, maxLength: 512),
            Diagnostics = SanitizeDiagnostics(failure.Diagnostics),
            PathsRedacted = true,
            LiteralArgumentsIncluded = false,
        };
        return true;
    }

    private static string ResolveCommandCategory(IReadOnlyList<string> args)
        => args.Count == 0 || string.IsNullOrWhiteSpace(args[0])
            ? "unknown"
            : SanitizeField(args[0]);

    private static string ResolveBinaryPath()
    {
        var assemblyPath = typeof(ProgramRunner).Assembly.Location;
        return DiagnosticSanitizer.ForPath(
            string.IsNullOrWhiteSpace(assemblyPath) ? Environment.ProcessPath : assemblyPath);
    }

    private static string SanitizeField(string? value)
        => DiagnosticSanitizer.ForMessage(value, MaxFieldChars);

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
