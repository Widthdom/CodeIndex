using CodeIndex.Database;
using CodeIndex.Indexer;
using CodeIndex.Models;

namespace CodeIndex.Cli;

public static partial class IndexCommandRunner
{
    private sealed class SkippedUpdateFileHandlingContext
    {
        internal required DbWriter Writer { get; init; }
        internal required FileIndexer Indexer { get; init; }
        internal required IndexCommandOptions Options { get; init; }
        internal required string AbsolutePath { get; init; }
        internal required string RelativePath { get; init; }
        internal required string IndexPath { get; init; }
        internal string? KnownLanguage { get; init; }
        internal required bool ProjectRootWritten { get; init; }
        internal required int TargetIndex { get; init; }
        internal required ReadableFileByteTracker ReadableFileBytes { get; init; }
        internal required bool HasCSharpWorkspaceSnapshot { get; init; }
        internal required CSharpStaticInterfacePrepass.FileStatSnapshot CSharpWorkspaceSnapshot { get; init; }
        internal Dictionary<string, CSharpStaticInterfacePrepass.FileStatSnapshot>? CSharpWorkspaceSnapshots { get; init; }
        internal required List<CliJsonMessage> WarningList { get; init; }
        internal required IndexProgressReporter UpdateProgress { get; init; }
        internal required CancellationToken CancellationToken { get; init; }
        internal required Action DemoteReadinessOnce { get; init; }
        internal required Action<string> SetCurrentUpdatePhase { get; init; }
        internal required Action<string, string, string> RecordCSharpWorkspaceDrift { get; init; }
        internal required Action<string, string, Exception> RecordUpdateFileFailure { get; init; }
        internal required Func<string, string?, bool, int> PurgeStaleUpdateCleanupPaths { get; init; }
        internal required Action RequireTypeScriptAugmentationRefresh { get; init; }
        internal required Action WriteProjectRootOnce { get; init; }
        internal required Action<string?> RecordDynamicGraphFileRefresh { get; init; }
    }

    private sealed record SkippedUpdateFileHandlingResult(
        int Updated,
        int Skipped,
        int Warnings,
        bool MutualRecursionRefreshNeeded);

    private static SkippedUpdateFileHandlingResult HandleSkippedUpdateFile(
        SkippedUpdateFileHandlingContext context,
        Exception exception)
    {
        var descriptor = DescribeSkippedUpdateFile(exception);
        if (context.HasCSharpWorkspaceSnapshot
            && !CSharpStaticInterfacePrepass.TryValidateLoadedFileStatSnapshot(
                context.AbsolutePath,
                context.IndexPath,
                context.RelativePath,
                context.CSharpWorkspaceSnapshot.Size,
                context.CSharpWorkspaceSnapshot.ModifiedUtc,
                context.CSharpWorkspaceSnapshots!,
                out _,
                context.CancellationToken))
        {
            context.RecordCSharpWorkspaceDrift(
                context.RelativePath,
                descriptor.PreflightDriftMessage,
                "reading");
            return new SkippedUpdateFileHandlingResult(0, 1, 0, false);
        }

        var warnings = 0;
        if (descriptor.PrintWarning)
        {
            warnings++;
            var sanitizedMessage =
                CommandErrorWriter.FormatSanitizedExceptionMessage(exception);
            context.WarningList.Add(
                new CliJsonMessage(context.RelativePath, sanitizedMessage));
            if (!context.Options.Json && !context.Options.Quiet)
            {
                context.UpdateProgress.Pause();
                ConsoleUi.PrintWarning(sanitizedMessage);
                context.UpdateProgress.Resume();
            }
        }

        context.DemoteReadinessOnce();
        context.SetCurrentUpdatePhase("writing");
        try
        {
            var persistence = PersistSkippedUpdateFile(
                new SkippedUpdateFilePersistenceContext
                {
                    Writer = context.Writer,
                    Indexer = context.Indexer,
                    Options = context.Options,
                    AbsolutePath = context.AbsolutePath,
                    RelativePath = context.RelativePath,
                    KnownLanguage = context.KnownLanguage,
                    ProjectRootWritten = context.ProjectRootWritten,
                    TransactionName = descriptor.TransactionName,
                    WorkspaceChangedMessage = descriptor.WorkspaceChangedMessage,
                    Issue = descriptor.Issue,
                    TargetIndex = context.TargetIndex,
                    ReadableFileBytes = context.ReadableFileBytes,
                    CancellationToken = context.CancellationToken,
                    ValidateSkippedRecord = skippedRecord =>
                        !context.HasCSharpWorkspaceSnapshot
                        || (skippedRecord.Lang == "csharp"
                            && CSharpStaticInterfacePrepass.TryValidateLoadedFileStatSnapshot(
                                context.AbsolutePath,
                                context.IndexPath,
                                context.RelativePath,
                                skippedRecord.Size,
                                skippedRecord.Modified,
                                context.CSharpWorkspaceSnapshots!,
                                out _,
                                context.CancellationToken)),
                    PurgeStaleUpdateCleanupPaths =
                        context.PurgeStaleUpdateCleanupPaths,
                    RequireTypeScriptAugmentationRefresh =
                        context.RequireTypeScriptAugmentationRefresh,
                    WriteProjectRootOnce = context.WriteProjectRootOnce,
                    RecordDynamicGraphFileRefresh =
                        context.RecordDynamicGraphFileRefresh,
                });
            return new SkippedUpdateFileHandlingResult(
                1,
                0,
                warnings,
                persistence.MutualRecursionRefreshNeeded);
        }
        catch (CSharpWorkspaceChangedException workspaceChanged)
        {
            context.RecordCSharpWorkspaceDrift(
                context.RelativePath,
                workspaceChanged.Message,
                "reading");
            return new SkippedUpdateFileHandlingResult(0, 1, warnings, false);
        }
        catch (Exception writeException)
        {
            if (writeException is IndexExtractionStalledException
                or IndexInterruptedException
                or OperationCanceledException)
            {
                throw;
            }

            context.RecordUpdateFileFailure(
                context.RelativePath,
                "writing",
                writeException);
            return new SkippedUpdateFileHandlingResult(0, 0, warnings, false);
        }
    }

    private static SkippedUpdateFileDescriptor DescribeSkippedUpdateFile(
        Exception exception)
    {
        if (exception is FileIndexer.BinaryFileSkippedException binaryFile)
        {
            return new SkippedUpdateFileDescriptor(
                "update skipped binary",
                "The C# file changed to binary content after contract preflight.",
                "The C# file changed while recording its binary skip state.",
                BuildNullByteIssue(binaryFile),
                PrintWarning: true);
        }

        var fileTooLarge =
            (FileIndexer.FileTooLargeSkippedException)exception;
        return new SkippedUpdateFileDescriptor(
            "update skipped oversized file",
            "The C# file changed size or timestamp after contract preflight.",
            "The C# file changed while recording its oversized skip state.",
            new FileIssue
            {
                Path = fileTooLarge.RelativePath,
                Kind = "file_too_large",
                Line = 0,
                Message = fileTooLarge.Message,
            },
            PrintWarning: false);
    }

    private sealed record SkippedUpdateFileDescriptor(
        string TransactionName,
        string PreflightDriftMessage,
        string WorkspaceChangedMessage,
        FileIssue Issue,
        bool PrintWarning);
}
