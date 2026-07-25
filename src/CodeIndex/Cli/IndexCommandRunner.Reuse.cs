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
    private static string? GetStatReusableLanguage(
        string absolutePath,
        FileIndexer.LanguageDetectionResult detection)
    {
        if (string.Equals(Path.GetExtension(absolutePath), ".h", StringComparison.OrdinalIgnoreCase))
            return null;

        return detection.Status == FileIndexer.FileProbeStatus.Supported
            ? detection.Language
            : null;
    }

    private static long? TryGetUnchangedFileIdFromChecksum(
        DbWriter writer,
        string absolutePath,
        string relativePath,
        string? language,
        long? maxBytes)
    {
        if (language == null)
            return null;

        try
        {
            var info = new FileInfo(absolutePath);
            if (!info.Exists)
                return null;
            if (!FileIndexer.TryComputeChecksum(absolutePath, maxBytes ?? FileIndexer.DefaultMaxFileSizeBytes, out var checksum))
                return null;

            return writer.GetUnchangedFileId(
                relativePath,
                info.LastWriteTimeUtc,
                checksum,
                size: info.Length,
                language: language);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }
}
