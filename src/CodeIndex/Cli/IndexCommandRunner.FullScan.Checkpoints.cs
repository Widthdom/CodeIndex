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
    internal const int MaxScanCheckpointBytes = 1024 * 1024;
    internal const int MaxScanCheckpointJsonDepth = 16;
    internal const int MaxScanCheckpointDirectories = 4096;
    internal const int MaxScanCheckpointDirectoryLength = 4096;

    internal static IReadOnlySet<string> LoadScanCheckpoint(string path, string? currentHead) =>
        LoadScanCheckpointDetailed(path, currentHead).Directories;

    internal static ScanCheckpointLoadResult LoadScanCheckpointDetailed(string path, string? currentHead)
    {
        try
        {
            if (!File.Exists(path))
                return EmptyScanCheckpointLoadResult();
            if (string.IsNullOrWhiteSpace(currentHead))
                return IgnoredScanCheckpoint(path, "current Git HEAD is unavailable");

            var text = DataDirectorySecurity.ReadTextWithinLimit(path, MaxScanCheckpointBytes, FileShare.ReadWrite);
            if (text is null)
                return IgnoredScanCheckpoint(path, $"file exceeds the scan checkpoint size limit of {MaxScanCheckpointBytes:N0} bytes");

            var checkpoint = BoundedJson.Deserialize<ScanCheckpoint>(
                text,
                MaxScanCheckpointBytes,
                new JsonSerializerOptions { MaxDepth = MaxScanCheckpointJsonDepth });
            if (checkpoint is null)
                return IgnoredScanCheckpoint(path, "JSON root is null or not a scan checkpoint object");
            if (checkpoint.Version != ScanCheckpointVersion)
                return IgnoredScanCheckpoint(path, FormatScanCheckpointVersionMismatch(checkpoint.Version));
            if (!string.Equals(checkpoint.GitHead, currentHead, StringComparison.Ordinal))
                return IgnoredScanCheckpoint(path, "checkpoint GitHead does not match current HEAD; checkpoint is stale");
            if (!TryBuildScanCheckpointDirectories(checkpoint.Directories, out var directories, out var directoryFailureReason))
                return IgnoredScanCheckpoint(path, directoryFailureReason);

            return new ScanCheckpointLoadResult(directories, WarningMessage: null);
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException)
        {
            return IgnoredScanCheckpoint(
                path,
                $"malformed checkpoint JSON, exceeded the JSON byte limit, or depth exceeds {MaxScanCheckpointJsonDepth:N0} ({CommandErrorWriter.FormatSanitizedException(ex)})");
        }
        catch (IOException ex)
        {
            return IgnoredScanCheckpoint(path, $"read failed ({CommandErrorWriter.FormatSanitizedException(ex)})");
        }
        catch (UnauthorizedAccessException ex)
        {
            return IgnoredScanCheckpoint(path, $"read failed ({CommandErrorWriter.FormatSanitizedException(ex)})");
        }
    }

    private static string FormatScanCheckpointVersionMismatch(int version) =>
        version > ScanCheckpointVersion
            ? $"future checkpoint version {version:N0} exceeds supported version {ScanCheckpointVersion:N0}"
            : $"unsupported checkpoint version {version:N0}; supported version is {ScanCheckpointVersion:N0}";

    private static ScanCheckpointLoadResult EmptyScanCheckpointLoadResult() =>
        new(EmptyScanCheckpointDirectories(), WarningMessage: null);

    private static ScanCheckpointLoadResult IgnoredScanCheckpoint(string path, string reason) =>
        new(
            EmptyScanCheckpointDirectories(),
            $"scan checkpoint ignored for {ConsoleUi.FormatBoundedValue(path)}: {reason}; continuing with a full scan.");

    private static bool TryBuildScanCheckpointDirectories(
        IReadOnlyList<string>? rawDirectories,
        out IReadOnlySet<string> directories,
        out string failureReason)
    {
        directories = EmptyScanCheckpointDirectories();
        failureReason = string.Empty;
        if (rawDirectories is not { Count: > 0 })
        {
            failureReason = "Directories must be a non-empty JSON array";
            return false;
        }
        if (rawDirectories.Count > MaxScanCheckpointDirectories)
        {
            failureReason =
                $"Directories contains {rawDirectories.Count:N0} entries, exceeding the limit of {MaxScanCheckpointDirectories:N0}";
            return false;
        }

        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (var directory in rawDirectories)
        {
            if (directory is null)
            {
                failureReason = "Directories contains a null entry";
                return false;
            }
            if (directory.Length == 0)
                continue;
            if (directory.Length > MaxScanCheckpointDirectoryLength)
            {
                failureReason =
                    $"Directories contains an entry longer than {MaxScanCheckpointDirectoryLength:N0} characters";
                return false;
            }

            result.Add(directory);
        }

        if (result.Count == 0)
        {
            failureReason = "Directories contains only empty entries";
            return false;
        }

        directories = result;
        return true;
    }

    private static HashSet<string> EmptyScanCheckpointDirectories() => new(StringComparer.Ordinal);

    private static void DeleteScanCheckpoint(
        string path,
        List<CliJsonMessage> warningList,
        bool json,
        bool quiet)
    {
        try
        {
            if (File.Exists(path))
            {
                if (DeleteScanCheckpointForTesting != null)
                    DeleteScanCheckpointForTesting(path);
                else
                    File.Delete(path);
            }
        }
        catch (Exception ex) when (IsScanCheckpointPersistenceException(ex))
        {
            RecordScanCheckpointPersistenceWarning(path, "delete", ex, warningList, json, quiet);
        }
    }

    private static bool IsScanCheckpointPersistenceException(Exception ex)
        => ex is IOException
            or UnauthorizedAccessException
            or ArgumentException
            or NotSupportedException
            or PathTooLongException;

    private static void RecordScanCheckpointPersistenceWarning(
        string path,
        string operation,
        Exception ex,
        List<CliJsonMessage> warningList,
        bool json,
        bool quiet)
    {
        var message =
            $"scan checkpoint {operation} failed for {ConsoleUi.FormatBoundedValue(path)} " +
            $"({CommandErrorWriter.FormatSanitizedException(ex)}); continuing without failing the scan.";
        warningList.Add(new CliJsonMessage("<scan_checkpoint>", message));
        if (!json && !quiet)
            ConsoleUi.PrintWarning(message);
    }
}
