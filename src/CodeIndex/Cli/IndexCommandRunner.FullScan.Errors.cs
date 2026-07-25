using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Runtime.InteropServices;
using CodeIndex.Database;
using CodeIndex.Diagnostics;
using CodeIndex.Indexer;
using CodeIndex.Indexer.Extensibility;
using CodeIndex.Indexer.Hooks;
using CodeIndex.Models;
using Microsoft.Data.Sqlite;

namespace CodeIndex.Cli;

public static partial class IndexCommandRunner
{
    internal static string FormatPerFileErrorLine(string label, string path, Exception ex) =>
        FormatPerFileErrorLine(label, path, ex, FormatIndexFileException(ex));

    internal static string FormatPerFileErrorLine(string label, string path, Exception ex, string message) =>
        $"  [{label}] {CollapseLineBreaks(path)}: {CollapseLineBreaks(message)}";

    internal static void LogIndexFileFailure(string eventName, string path, Exception ex) =>
        LogIndexFileFailure(eventName, path, phase: null, ex);

    internal static void LogIndexFileFailure(string eventName, string path, string? phase, Exception ex)
    {
        var phaseSuffix = string.IsNullOrWhiteSpace(phase) ? string.Empty : $" phase={CollapseLineBreaks(phase)}";
        var detail = CollapseLineBreaks(FormatIndexFileException(ex));
        GlobalToolLog.Error($"{eventName} path={CollapseLineBreaks(path)}{phaseSuffix} detail={detail}", ex);
    }

    [DoesNotReturn]
    internal static void RethrowPreservingStackTrace(Exception ex) =>
        ExceptionDispatchInfo.Capture(ex).Throw();

    internal static string FormatIndexFileException(Exception ex) =>
        ex switch
        {
            RegexMatchTimeoutException timeoutException => RuntimeSafety.FormatRegexTimeout(timeoutException),
            IndexExtractionStalledException stalledException => FormatExtractionStalledMessage(stalledException),
            SymbolExtractionWorkerFailureException workerException =>
                $"Symbol extraction worker failed. Worker diagnostic: {CollapseLineBreaks(workerException.WorkerError)}",
            _ => CommandErrorWriter.FormatSanitizedException(ex),
        };

    internal static StatusIndexFileError BuildIndexFileError(string path, string? phase, Exception ex)
    {
        var stablePhase = string.IsNullOrWhiteSpace(phase) ? "unknown" : phase;
        var category = ex switch
        {
            RegexMatchTimeoutException => "regex_timeout",
            IndexExtractionStalledException => "extraction_stalled",
            SqliteException => "persistence_error",
            IOException or UnauthorizedAccessException when stablePhase is "reading" or "csharp_prepass" => "file_read_error",
            _ when stablePhase == "csharp_workspace_validation" => "extraction_error",
            _ when stablePhase is "chunking" or "symbols" or "references" or "validating" => "extraction_error",
            _ when stablePhase == "committing" => "persistence_error",
            _ => "index_file_error",
        };
        var (line, column) = ex is JsonException jsonException
            ? (jsonException.LineNumber + 1, jsonException.BytePositionInLine + 1)
            : ((long?)null, (long?)null);
        return new StatusIndexFileError
        {
            File = FileIndexer.NormalizePathSeparators(path),
            Category = category,
            Phase = stablePhase,
            Detail = ex is SymbolExtractionWorkerFailureException
                ? DiagnosticRedactor.BoundDiagnosticText(FormatIndexFileException(ex), maxChars: 512)
                : CommandErrorWriter.FormatSanitizedExceptionDetail(ex),
            Line = line,
            Column = column,
        };
    }

    private static string FormatExtractionStalledMessage(IndexExtractionStalledException ex)
    {
        var pathSuffix = string.IsNullOrWhiteSpace(ex.ActivePath) ? string.Empty : $" Last active phase: {ex.ActivePath}.";
        return $"Index extraction made no progress for {ConsoleUi.FormatDuration(ex.Timeout)}.{pathSuffix}{FormatWorkerDiagnosticSuffix(ex.WorkerError)}";
    }

    private static string FormatWorkerDiagnosticSuffix(string? workerError)
        => string.IsNullOrWhiteSpace(workerError)
            ? string.Empty
            : $" Worker diagnostic: {CollapseLineBreaks(workerError)}.";

    private static FileIssue BuildSymbolCountExceededIssue(string path, int symbolCount, int maxSymbolsPerFile) =>
        new()
        {
            Path = path,
            Kind = "symbol_count_exceeded",
            Line = 0,
            Message = $"Symbol extraction produced {symbolCount:N0} symbols, exceeding the --max-symbols-per-file limit of {maxSymbolsPerFile:N0}; file content, symbols, and references were not indexed. Exclude the generated/pathological file or raise --max-symbols-per-file if this is expected.",
        };

    private static FileIssue BuildReferenceCountExceededIssue(string path, int referenceCount, int maxReferencesPerFile) =>
        new()
        {
            Path = path,
            Kind = "reference_count_exceeded",
            Line = 0,
            Message = $"Reference extraction produced {referenceCount:N0} references, exceeding the --max-references-per-file limit of {maxReferencesPerFile:N0}; references were not indexed for this file. Exclude the generated/pathological file or raise --max-references-per-file if this is expected.",
        };

    internal static IReadOnlyList<FileIssue> AppendReferenceExtractionDiagnosticIssues(
        IReadOnlyList<FileIssue> issues,
        string path,
        IReadOnlyList<ReferenceExtractionDiagnostic> diagnostics)
    {
        foreach (var diagnostic in diagnostics)
        {
            issues = AppendIssue(issues, new FileIssue
            {
                Path = path,
                Kind = diagnostic.Kind,
                Line = 0,
                Message = diagnostic.Message,
                Severity = FileIssue.SeverityWarning,
            });
        }

        return issues;
    }

    internal static FileIssue BuildNullByteIssue(FileIndexer.BinaryFileSkippedException ex) =>
        new()
        {
            Path = ex.RelativePath,
            Kind = "null_byte",
            Line = 0,
            Message = CommandErrorWriter.FormatSanitizedExceptionMessage(ex),
        };

    internal static FileIssue? BuildRegexTimeoutIssue(string path, BoundedRegex.RegexTimeoutCaptureScope capture) =>
        BuildRegexTimeoutIssue(
            path,
            capture.Language,
            capture.PatternFamily,
            capture.TimeoutCount,
            capture.Diagnostics,
            capture.DiagnosticsTruncated);

    internal static FileIssue? BuildRegexTimeoutIssue(
        string path,
        string? language,
        string patternFamily,
        int timeoutCount,
        IReadOnlyList<BoundedRegex.RegexTimeoutDiagnostic> diagnostics,
        bool diagnosticsTruncated)
    {
        if (timeoutCount <= 0)
            return null;

        var normalizedLanguage = string.IsNullOrWhiteSpace(language) ? "unknown" : language;
        var samples = diagnostics.Count == 0
            ? "none"
            : string.Join(", ", diagnostics.Select(static diagnostic =>
                $"{diagnostic.Operation}:{diagnostic.PatternHash} len={diagnostic.PatternLength} timeout={diagnostic.TimeoutMs:0.###}ms"));
        var truncationSuffix = diagnosticsTruncated ? "; additional timeout diagnostics omitted" : string.Empty;
        return new FileIssue
        {
            Path = path,
            Kind = "regex_timeout",
            Line = 0,
            Message = $"Regex timeout fallback occurred during {patternFamily} for language {normalizedLanguage} ({timeoutCount:N0} timeout(s); samples {samples}{truncationSuffix}); extraction used a safe no-match fallback and may be incomplete for this file.",
        };
    }

    private static bool ExistingFileBlocksReuse(
        DbWriter writer,
        long fileId,
        int maxSymbolsPerFile,
        int maxReferencesPerFile,
        FileIssue? generatedSuppressionIssue) =>
        ExistingFileBlocksReuse(
            writer,
            fileId,
            maxSymbolsPerFile,
            maxReferencesPerFile,
            generatedSuppressionIssue != null);

    private static bool ExistingFileBlocksReuse(
        DbWriter writer,
        long fileId,
        int maxSymbolsPerFile,
        int maxReferencesPerFile,
        bool generatedExtractionSuppressed) =>
        writer.HasReusableFileBlockingIssueForFile(
            fileId,
            maxSymbolsPerFile,
            maxReferencesPerFile,
            generatedExtractionSuppressed);

    internal static IReadOnlyList<FileIssue> AppendIssue(IReadOnlyList<FileIssue> issues, FileIssue issue)
    {
        if (issues.Count == 0)
            return [issue];

        var combined = issues.ToList();
        combined.Add(issue);
        return combined;
    }

    internal static IReadOnlyList<FileIssue> AppendIssueIfMissing(IReadOnlyList<FileIssue> issues, FileIssue issue)
    {
        for (var i = 0; i < issues.Count; i++)
        {
            if (string.Equals(issues[i].Kind, issue.Kind, StringComparison.Ordinal))
                return issues;
        }

        return AppendIssue(issues, issue);
    }

    internal static string FormatIndexPhasePath(string path, string phase) =>
        $"{path} ({phase})";
}
