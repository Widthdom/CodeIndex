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
    private sealed record FullScanDiscoveryResult(
        FileIndexer.ScanFilesResult ScanResult,
        IReadOnlyList<string> Files,
        List<CliJsonMessage> ErrorList,
        List<CliJsonMessage> WarningList,
        string ScanCheckpointPath,
        FileIndexer.ScanInputSnapshot? InputSnapshot);

    private static FullScanDiscoveryResult DiscoverFullScanFiles(
        FileIndexer indexer,
        string projectRoot,
        IndexCommandOptions options,
        string[] spinnerFrames,
        int? initialFileCapacity,
        CancellationToken cancellationToken)
    {
        var actualMode = options.Rebuild ? "rebuild" : "incremental";
        CancellationTokenSource? spinnerCts = null;
        if (!options.Json && !options.Quiet)
            spinnerCts = ConsoleUi.StartSpinner("Scanning...", spinnerFrames);

        void ThrowIfDiscoveryCancelled()
        {
            if (!cancellationToken.IsCancellationRequested)
                return;

            ConsoleUi.StopSpinner(spinnerCts);
            throw new IndexInterruptedException(0, null, actualMode);
        }

        var scanCheckpointPath = Path.Combine(projectRoot, ".cdidx", ScanCheckpointFileName);
        WriteFullScanJsonLiveness(options, "scanning files...");
        var scanHeartbeat = StartFullScanJsonPhaseHeartbeat(options, "scanning files");
        FileIndexer.ScanFilesResult scanResult;
        FileIndexer.ScanInputSnapshot? inputSnapshot = null;
        try
        {
            ThrowIfDiscoveryCancelled();
            var scanWithSnapshots = indexer.ScanFilesDetailedWithDirectoryListingSnapshots(
                new HashSet<string>(StringComparer.Ordinal),
                continueOnError: true,
                initialFileCapacity: initialFileCapacity,
                cancellationToken: cancellationToken);
            scanResult = scanWithSnapshots.ScanResult;
            inputSnapshot = scanWithSnapshots.InputSnapshot;
            ThrowIfDiscoveryCancelled();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw new IndexInterruptedException(0, null, actualMode);
        }
        finally
        {
            StopFullScanJsonPhaseHeartbeat(scanHeartbeat);
        }
        var files = scanResult.Files;
        ConsoleUi.StopSpinner(spinnerCts);
        WriteFullScanJsonLiveness(options, $"found {ConsoleUi.Counted(files.Count, "file", format: "N0")}; preparing database...");
        var errorList = new List<CliJsonMessage>();
        var warningList = new List<CliJsonMessage>();
        foreach (var error in scanResult.Errors)
        {
            var message = new CliJsonMessage(error.Path, error.Message);
            if (error.IsFatal)
                errorList.Add(message);
            else
                warningList.Add(message);
        }
        if (!options.Json && !options.Quiet)
        {
            CommandOutputWriter.WriteLine($"  Found {ConsoleUi.Counted(files.Count, "file", format: "N0")}");
            foreach (var error in scanResult.Errors)
                ConsoleUi.PrintWarning($"{error.Path}: {error.Message}");
            CommandOutputWriter.WriteLine();
        }

        return new FullScanDiscoveryResult(
            scanResult,
            files,
            errorList,
            warningList,
            scanCheckpointPath,
            inputSnapshot);
    }

    private static void WriteFullScanJsonLiveness(IndexCommandOptions options, string message)
    {
        if (!options.Json || options.Quiet)
            return;

        ConsoleUi.TryWriteErrorLine($"cdidx: {message}");
    }

    private static (CancellationTokenSource Cts, Task Task)? StartFullScanJsonPhaseHeartbeat(
        IndexCommandOptions options,
        string phase,
        Func<string?>? detailProvider = null)
    {
        return StartObservedJsonPhaseHeartbeat(
            options.Json && !options.Quiet,
            "cdidx-index",
            phase,
            ConsoleUi.TryWriteErrorLine,
            detailProvider);
    }

    private static void StopFullScanJsonPhaseHeartbeat((CancellationTokenSource Cts, Task Task)? heartbeat)
        => StopObservedJsonPhaseHeartbeat(heartbeat);
}
