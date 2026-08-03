using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Text.Json;
using CodeIndex.Database;
using CodeIndex.Diagnostics;
using CodeIndex.Indexer;
using CodeIndex.Indexer.Hooks;
using CodeIndex.Models;
using Microsoft.Data.Sqlite;

namespace CodeIndex.Cli;

public static partial class IndexCommandRunner
{
    private readonly record struct FullScanFileTarget(
        string FilePath,
        string RelativePath,
        string DisplayRelativePath,
        string IndexPath,
        string? Language,
        bool GeneratedExtractionSuppressed)
    {
        public static FullScanFileTarget CreateFromPath(string projectRoot, string path)
        {
            var filePath = Path.IsPathRooted(path)
                ? path
                : Path.Combine(projectRoot, FileIndexer.NormalizeRelativePathForCurrentPlatform(path));
            return Create(projectRoot, filePath);
        }

        public static FullScanFileTarget Create(string projectRoot, string filePath, string? language = null)
        {
            var relativePath = FileIndexer.GetRelativePathFromProjectRoot(projectRoot, filePath);
            return new FullScanFileTarget(
                filePath,
                relativePath,
                FileIndexer.NormalizePathSeparators(relativePath),
                FileIndexer.NormalizeIndexPath(relativePath),
                language,
                GeneratedExtractionSuppressed: false);
        }
    }

    private readonly record struct UpdateFileTarget(
        string FilePath,
        string RelativePath,
        string DisplayRelativePath,
        string IndexPath)
    {
        public static UpdateFileTarget Create(string projectRoot, string path)
        {
            var isRooted = Path.IsPathRooted(path);
            var filePath = isRooted
                ? path
                : Path.Combine(projectRoot, path.Replace('/', Path.DirectorySeparatorChar));
            var relativePath = isRooted
                ? FileIndexer.GetRelativePathFromProjectRoot(projectRoot, path)
                : path;
            return new UpdateFileTarget(
                filePath,
                relativePath,
                FileIndexer.NormalizePathSeparators(relativePath),
                FileIndexer.NormalizeIndexPath(relativePath));
        }
    }

    private sealed record FullScanFileWorkItem(
        int FileIndex,
        string FilePath,
        string RelativePath,
        FileRecord? Record,
        string? Content,
        bool? HasOversizeLine,
        int? ConflictMarkerLine,
        string? Warning,
        IReadOnlyList<ChunkRecord>? Chunks,
        IReadOnlyList<SymbolRecord>? Symbols,
        IReadOnlyList<ReferenceRecord>? References,
        IReadOnlyList<FileIssue>? Issues,
        FileIssue? GeneratedSuppressionIssue,
        bool GeneratedSuppressionChecked,
        string? FailurePhase,
        Exception? Exception)
    {
        public static FullScanFileWorkItem Success(
            int fileIndex,
            string filePath,
            string relativePath,
            FileRecord record,
            string? content,
            bool hasOversizeLine,
            int conflictMarkerLine,
            string? warning,
            IReadOnlyList<ChunkRecord>? chunks,
            IReadOnlyList<SymbolRecord>? symbols,
            IReadOnlyList<ReferenceRecord>? references,
            IReadOnlyList<FileIssue>? issues,
            FileIssue? generatedSuppressionIssue,
            bool generatedSuppressionChecked)
        {
            return new FullScanFileWorkItem(
                fileIndex,
                filePath,
                relativePath,
                record,
                content,
                hasOversizeLine,
                conflictMarkerLine,
                warning,
                chunks,
                symbols,
                references,
                issues,
                generatedSuppressionIssue,
                generatedSuppressionChecked,
                null,
                null);
        }

        public static FullScanFileWorkItem Precomputed(
            int fileIndex,
            string filePath,
            string relativePath,
            FileRecord record,
            string? warning,
            IReadOnlyList<ChunkRecord> chunks,
            IReadOnlyList<SymbolRecord> symbols,
            IReadOnlyList<ReferenceRecord> references,
            IReadOnlyList<FileIssue> issues,
            FileIssue? generatedSuppressionIssue = null,
            bool generatedSuppressionChecked = false,
            string? content = null,
            bool? hasOversizeLine = null,
            int? conflictMarkerLine = null)
        {
            return new FullScanFileWorkItem(
                fileIndex,
                filePath,
                relativePath,
                record,
                content,
                hasOversizeLine,
                conflictMarkerLine,
                warning,
                chunks,
                symbols,
                references,
                issues,
                generatedSuppressionIssue,
                generatedSuppressionChecked,
                null,
                null);
        }

        public static FullScanFileWorkItem Failure(int fileIndex, string filePath, string relativePath, string phase, Exception exception)
            => new(fileIndex, filePath, relativePath, null, null, null, null, null, null, null, null, null, null, false, phase, exception);

        public static FullScanFileWorkItem Skipped(int fileIndex, string filePath, string relativePath, string warning)
            => new(fileIndex, filePath, relativePath, null, null, null, null, warning, null, null, null, null, null, false, null, null);
    }

    private sealed class CSharpWorkspaceSnapshotDriftException(string path)
        : IOException("A C# source changed after workspace preflight; rerun indexing to refresh the complete C# graph.")
    {
        public string Path { get; } = path;
    }

    private sealed record FoldOnlyRemediation(
        string DegradedReason,
        string RecommendedAction,
        string AlternativeAction);

    private sealed class IndexInterruptedException : OperationCanceledException
    {
        public IndexInterruptedException(int filesProcessed, int? filesTotal, string? actualMode = null)
            : base("Indexing was interrupted.")
        {
            FilesProcessed = filesProcessed;
            FilesTotal = filesTotal;
            ActualMode = actualMode;
        }

        public int FilesProcessed { get; }
        public int? FilesTotal { get; }
        public string? ActualMode { get; }
    }

    private sealed class IndexExtractionStalledException : Exception
    {
        public IndexExtractionStalledException(int filesProcessed, int? filesTotal, TimeSpan timeout, string? activePath, string? workerError = null)
            : base("Index extraction stalled.")
        {
            FilesProcessed = filesProcessed;
            FilesTotal = filesTotal;
            Timeout = timeout;
            ActivePath = activePath;
            WorkerError = workerError;
        }

        public int FilesProcessed { get; }
        public int? FilesTotal { get; }
        public TimeSpan Timeout { get; }
        public string? ActivePath { get; }
        public string? WorkerError { get; }
    }

    private sealed class CancelKeyPressRegistration(ConsoleCancelEventHandler handler) : IDisposable
    {
        public void Dispose()
        {
            Console.CancelKeyPress -= handler;
        }
    }

    private sealed class NullDisposable : IDisposable
    {
        public static readonly NullDisposable Instance = new();

        public void Dispose()
        {
        }
    }
}
