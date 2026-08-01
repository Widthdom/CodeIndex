using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CodeIndex.Diagnostics;
using Microsoft.Data.Sqlite;

namespace CodeIndex.Cli;

/// <summary>
/// Persists one bounded, redacted top-level failure independently of lifecycle logging so
/// a provenance-matching `cdidx report` invocation can describe the failure that recommended the report.
/// lifecycle log の有効・無効とは独立して、上限付き・匿名化済みの直近 top-level failure を
/// 1 件だけ保存し、provenance が一致する `cdidx report` で同じ失敗を説明できるようにする。
/// </summary>
internal static class LastFailureEventStore
{
    internal const string FileName = "last-failure.json";
    internal const int SchemaVersion = 3;
    internal const int MaxEventBytes = 32 * 1024;
    internal const int MaxDiagnosticsChars = 8 * 1024;
    internal const int MaxDiagnosticLines = 32;
    internal static readonly TimeSpan MaxReportCorrelationAge = TimeSpan.FromHours(24);
    internal static readonly TimeSpan MaxReportFutureSkew = TimeSpan.FromMinutes(5);
    private const int MaxFieldChars = 256;
    private const int OpaqueIdentityBytes = 16;
    private const int RunIdBytes = 16;

    internal static bool TryPersist(
        IReadOnlyList<string> args,
        string appVersion,
        int exitCode,
        Exception exception,
        DateTimeOffset occurredAtUtc,
        string? runId = null,
        string? dbPathForTesting = null,
        string? workspacePathForTesting = null)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(exception);

        try
        {
            var resolvedPaths = ResolveFailureProvenancePaths(args);
            var provenance = CreateReportProvenance(
                dbPathForTesting ?? resolvedPaths.DbPath,
                appVersion,
                occurredAtUtc,
                runId ?? CreateRunId(),
                workspacePathForTesting ?? resolvedPaths.WorkspacePath);
            var exceptionCategory = SanitizeField(DiagnosticRedactor.ClassifyException(exception));
            var exceptionType = SanitizeField(exception.GetType().FullName ?? exception.GetType().Name);
            var failure = new LastFailureEvent(
                SchemaVersion,
                occurredAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
                SanitizeField(appVersion),
                ResolveBinaryPath(),
                DiagnosticSanitizer.ForPath(Environment.ProcessPath),
                ResolveCommandCategory(args),
                exitCode,
                exceptionCategory,
                exceptionType,
                exceptionCategory,
                BuildPersistedDiagnostics(exception, exceptionCategory, exceptionType),
                PathsRedacted: true,
                LiteralArgumentsIncluded: false,
                WorkspaceId: provenance.WorkspaceId,
                DatabaseId: provenance.DatabaseId,
                RunId: provenance.RunId);

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

    internal static bool TryBuildReportPayload(
        ReportProvenance reportProvenance,
        out string payload,
        out ReportLastFailureEvidence evidence)
    {
        payload = string.Empty;
        evidence = ReportLastFailureEvidence.Unavailable("not_found");
        try
        {
            var path = Path.Combine(GlobalToolLog.ResolveLogDirectoryForReport(), FileName);
            if (!File.Exists(path))
                return false;
            if (FileSystemBoundary.IsSymlinkOrReparsePoint(new FileInfo(path)))
            {
                evidence = ReportLastFailureEvidence.Unavailable("unsafe_file");
                return false;
            }

            var json = DataDirectorySecurity.ReadTextWithinLimit(
                path,
                MaxEventBytes,
                FileShare.ReadWrite | FileShare.Delete);
            if (string.IsNullOrWhiteSpace(json))
            {
                evidence = ReportLastFailureEvidence.Unavailable("invalid_or_empty");
                return false;
            }

            var failure = JsonSerializer.Deserialize(json, LastFailureEventJsonContext.Default.LastFailureEvent);
            if (!TryNormalize(failure, out var normalized, out var validationReason))
            {
                evidence = failure is null
                    ? ReportLastFailureEvidence.Unavailable(validationReason)
                    : BuildEvidence("excluded", validationReason, failure);
                return false;
            }

            var correlationReason = GetCorrelationFailureReason(normalized, reportProvenance);
            if (correlationReason is not null)
            {
                evidence = BuildEvidence("excluded", correlationReason, normalized);
                return false;
            }

            payload = JsonSerializer.Serialize(normalized, LastFailureEventJsonContext.Default.LastFailureEvent);
            if (Encoding.UTF8.GetByteCount(payload) > MaxEventBytes)
            {
                payload = string.Empty;
                evidence = BuildEvidence("excluded", "event_too_large", normalized);
                return false;
            }

            evidence = BuildEvidence("included", "matched", normalized);
            return true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Corrupt or unavailable diagnostic state must not prevent report creation.
            // 壊れた、または読み取れない診断 state で report 作成を妨げない。
            payload = string.Empty;
            evidence = ReportLastFailureEvidence.Unavailable("invalid_or_unreadable");
            return false;
        }
    }

    private static bool TryNormalize(
        LastFailureEvent? failure,
        out LastFailureEvent normalized,
        out string validationReason)
    {
        normalized = null!;
        validationReason = "invalid";
        if (failure is null)
            return false;
        if (failure.SchemaVersion != SchemaVersion)
        {
            validationReason = failure.SchemaVersion < SchemaVersion
                ? "missing_provenance"
                : "unsupported_schema";
            return false;
        }
        if (string.IsNullOrWhiteSpace(failure.WorkspaceId)
            || string.IsNullOrWhiteSpace(failure.DatabaseId)
            || string.IsNullOrWhiteSpace(failure.RunId))
        {
            validationReason = "missing_provenance";
            return false;
        }
        if (!IsOpaqueIdentity(failure.WorkspaceId, "ws_")
            || !IsOpaqueIdentity(failure.DatabaseId, "db_")
            || !IsOpaqueIdentity(failure.RunId, "run_"))
        {
            validationReason = "invalid_provenance";
            return false;
        }

        if (!failure.PathsRedacted
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
        validationReason = "valid";
        return true;
    }

    internal static string CreateRunId()
        => "run_" + HexEncoding.ToLowerHexString(RandomNumberGenerator.GetBytes(RunIdBytes));

    internal static ReportProvenance CreateReportProvenance(
        string dbPath,
        string appVersion,
        DateTimeOffset timestampUtc,
        string runId,
        string? workspacePath = null)
    {
        var normalizedDbPath = Path.GetFullPath(DbPathResolver.NormalizeDbPath(dbPath));
        var normalizedWorkspacePath = Path.GetFullPath(
            workspacePath ?? ResolveWorkspacePath(normalizedDbPath));
        return new ReportProvenance(
            WorkspaceId: ComputeOpaquePathIdentity("workspace", "ws_", normalizedWorkspacePath),
            DatabaseId: ComputeOpaquePathIdentity("database", "db_", normalizedDbPath),
            BinaryVersion: SanitizeField(appVersion),
            TimestampUtc: timestampUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            RunId: IsOpaqueIdentity(runId, "run_") ? runId : CreateRunId());
    }

    private static string? GetCorrelationFailureReason(
        LastFailureEvent failure,
        ReportProvenance reportProvenance)
    {
        if (!string.Equals(failure.WorkspaceId, reportProvenance.WorkspaceId, StringComparison.Ordinal))
            return "workspace_mismatch";
        if (!string.Equals(failure.DatabaseId, reportProvenance.DatabaseId, StringComparison.Ordinal))
            return "database_mismatch";
        if (!string.Equals(failure.BinaryVersion, reportProvenance.BinaryVersion, StringComparison.Ordinal))
            return "binary_version_mismatch";
        if (!DateTimeOffset.TryParseExact(
                failure.OccurredAtUtc,
                "O",
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var occurredAtUtc)
            || !DateTimeOffset.TryParseExact(
                reportProvenance.TimestampUtc,
                "O",
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var reportTimestampUtc))
        {
            return "invalid_timestamp";
        }

        var age = reportTimestampUtc.ToUniversalTime() - occurredAtUtc.ToUniversalTime();
        if (age < -MaxReportFutureSkew)
            return "future_timestamp";
        if (age > MaxReportCorrelationAge)
            return "stale";
        return null;
    }

    private static ReportLastFailureEvidence BuildEvidence(
        string disposition,
        string reason,
        LastFailureEvent failure)
    {
        var occurredAtUtc = DateTimeOffset.TryParseExact(
            failure.OccurredAtUtc,
            "O",
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind,
            out var parsedTimestamp)
            ? parsedTimestamp.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)
            : null;
        return new ReportLastFailureEvidence(
            disposition,
            reason,
            occurredAtUtc,
            string.IsNullOrWhiteSpace(failure.BinaryVersion) ? null : SanitizeField(failure.BinaryVersion),
            IsOpaqueIdentity(failure.WorkspaceId, "ws_") ? failure.WorkspaceId : null,
            IsOpaqueIdentity(failure.DatabaseId, "db_") ? failure.DatabaseId : null,
            IsOpaqueIdentity(failure.RunId, "run_") ? failure.RunId : null);
    }

    private static string ComputeOpaquePathIdentity(string domain, string prefix, string path)
    {
        // Emit only a bounded opaque fingerprint; raw local paths never enter the event or bundle.
        // Case-insensitive filesystems fold path casing before hashing so equivalent spellings
        // correlate, while case-sensitive filesystems retain the exact path identity.
        // 上限付きの不透明 fingerprint だけを出力し、ローカル path 自体は event / bundle に入れない。
        // case-insensitive FS では同じ path の大小違いを hash 前に統一し、case-sensitive FS
        // では正確な path identity を維持する。
        var normalizedPath = PathCasing.NormalizeBoundaryPath(path);
        var identityPath = PathCasing.IsIgnoreCase(normalizedPath)
            ? normalizedPath.ToUpperInvariant()
            : normalizedPath;
        var input = Encoding.UTF8.GetBytes(domain + "\0" + identityPath);
        var digest = SHA256.HashData(input);
        return prefix + HexEncoding.ToLowerHexString(digest, 0, OpaqueIdentityBytes);
    }

    private static bool IsOpaqueIdentity(string? value, string prefix)
    {
        if (value is null || value.Length != prefix.Length + OpaqueIdentityBytes * 2
            || !value.StartsWith(prefix, StringComparison.Ordinal))
        {
            return false;
        }

        foreach (var c in value.AsSpan(prefix.Length))
        {
            if (c is not (>= '0' and <= '9') and not (>= 'a' and <= 'f'))
                return false;
        }
        return true;
    }

    internal static string ResolveFailureDbPath(IReadOnlyList<string> args)
        => ResolveFailureProvenancePaths(args).DbPath;

    internal static (string DbPath, string? WorkspacePath) ResolveFailureProvenancePaths(
        IReadOnlyList<string> args)
    {
        var workspacePath = Environment.CurrentDirectory;
        if (string.Equals(ResolveCommandCategory(args), "index", StringComparison.Ordinal))
        {
            var indexArgs = args.Count > 0 && string.Equals(args[0], "index", StringComparison.Ordinal)
                ? args.Skip(1).ToArray()
                : args.ToArray();
            var options = IndexCommandRunner.ParseArgs(indexArgs);
            var projectPath = options.ProjectPath ?? workspacePath;
            var dbPath = DbPathResolver.ResolveForIndex(
                projectPath,
                options.DbPath,
                options.DataDir).DbPath;
            return (dbPath, projectPath);
        }

        var queryArgs = args.Count > 0 ? args.Skip(1).ToArray() : [];
        var queryDbPath = QueryCommandRunner.ParseArgs(
            queryArgs,
            jsonDefault: false,
            validateDefaultLimit: false,
            validateDefaultSnippetLines: false,
            validateDefaultMaxLineWidth: false).DbPath;
        return (queryDbPath, null);
    }

    private static string ResolveWorkspacePath(string normalizedDbPath)
    {
        try
        {
            var indexedProjectRoot = DbPathResolver.ResolveProjectRootForQuery(
                normalizedDbPath,
                dbPathExplicit: true);
            if (!string.IsNullOrWhiteSpace(indexedProjectRoot))
                return indexedProjectRoot;
        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            // Provenance falls back to path shape/current workspace when DB metadata is unavailable.
            // DB metadata を読めない場合は path 形状 / current workspace へ fallback する。
        }

        var dbDirectory = Path.GetDirectoryName(normalizedDbPath);
        if (dbDirectory is not null
            && string.Equals(Path.GetFileName(dbDirectory), ".cdidx", StringComparison.OrdinalIgnoreCase))
        {
            return Path.GetDirectoryName(dbDirectory) ?? Environment.CurrentDirectory;
        }

        return Environment.CurrentDirectory;
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
        var assemblyName = typeof(ProgramRunner).Assembly.GetName().Name;
        var assemblyPath = string.IsNullOrWhiteSpace(assemblyName)
            ? null
            : Path.Combine(AppContext.BaseDirectory, assemblyName + ".dll");
        return DiagnosticSanitizer.ForPath(
            !string.IsNullOrWhiteSpace(assemblyPath) && File.Exists(assemblyPath)
                ? assemblyPath
                : Environment.ProcessPath);
    }

    private static string SanitizeField(string? value)
        => DiagnosticSanitizer.ForMessage(value, MaxFieldChars);

    private static string BuildPersistedDiagnostics(
        Exception exception,
        string exceptionCategory,
        string exceptionType)
    {
        var diagnostics = SanitizeDiagnostics(
            GlobalToolLog.FormatExceptionChain(exception, includeStacks: true));
        if (TryNormalizeStoredDiagnostics(
                diagnostics,
                exceptionCategory,
                exceptionType,
                out var normalized))
        {
            return normalized;
        }

        // A platform or runtime may emit a stack frame that cannot survive the bounded canonical
        // representation. Preserve the exception chain without stacks instead of persisting an
        // event that report would immediately reject as invalid.
        // platform / runtime 固有の stack frame が上限付き canonical 表現に収まらない場合は、
        // report が直後に invalid として拒否する event ではなく stack なしの例外 chain を保存する。
        var chainOnlyDiagnostics = SanitizeDiagnostics(
            GlobalToolLog.FormatExceptionChain(exception, includeStacks: false));
        if (TryNormalizeStoredDiagnostics(
                chainOnlyDiagnostics,
                exceptionCategory,
                exceptionType,
                out normalized))
        {
            return normalized;
        }

        return $"exception[0] type={exceptionType} message=\"{exceptionCategory}\"";
    }

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
    bool LiteralArgumentsIncluded,
    string? WorkspaceId,
    string? DatabaseId,
    string? RunId);

[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(LastFailureEvent))]
internal partial class LastFailureEventJsonContext : JsonSerializerContext;
