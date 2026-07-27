using CodeIndex.Database;

namespace CodeIndex.Indexer;

internal readonly record struct IndexedFileStatReuseResult(long FileId, long Size);

internal static class IndexedFileStatReuse
{
    private static readonly AsyncLocal<Action<string>?> ScopedLookupForTesting = new();
    internal static Action<string>? LookupForTesting
    {
        get => ScopedLookupForTesting.Value;
        set => ScopedLookupForTesting.Value = value;
    }

    internal static IndexedFileStatReuseResult? TryGetUnchangedFile(
        DbWriter writer,
        string absolutePath,
        string relativePath,
        string? language,
        bool allowReuse)
    {
        if (!allowReuse || language == null)
            return null;

        LookupForTesting?.Invoke(relativePath);
        try
        {
            var info = new FileInfo(absolutePath);
            if (!info.Exists)
                return null;

            var fileId = writer.GetUnchangedFileIdByStat(
                relativePath,
                info.LastWriteTimeUtc,
                info.Length,
                language: language);
            return fileId.HasValue
                ? new IndexedFileStatReuseResult(fileId.Value, info.Length)
                : null;
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

    internal static IndexedFileStatReuseResult? TryGetReusableUnchangedFile(
        DbWriter writer,
        string absolutePath,
        string relativePath,
        string? language,
        long maxFileSizeBytes,
        int maxSymbolsPerFile,
        int maxReferencesPerFile,
        bool? generatedExtractionSuppressed,
        bool allowReuse)
    {
        if (!allowReuse || language == null)
            return null;

        LookupForTesting?.Invoke(relativePath);
        try
        {
            var info = new FileInfo(absolutePath);
            if (!info.Exists || info.Length > maxFileSizeBytes)
                return null;

            var fileId = writer.GetReusableUnchangedFileIdByStat(
                relativePath,
                info.LastWriteTimeUtc,
                info.Length,
                language,
                maxSymbolsPerFile,
                maxReferencesPerFile,
                generatedExtractionSuppressed,
                allowReuse: true);
            return fileId.HasValue
                ? new IndexedFileStatReuseResult(fileId.Value, info.Length)
                : null;
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

    internal static IndexedFileStatReuseResult? TryGetReusableUnchangedFile(
        ReusableIndexedFileStatsSnapshot reusableFiles,
        string absolutePath,
        string relativePath,
        string? language,
        bool? generatedExtractionSuppressed)
    {
        LookupForTesting?.Invoke(relativePath);
        if (language == null
            || !reusableFiles.TryGetValue(relativePath, out var indexed)
            || !string.Equals(indexed.Language, language, StringComparison.Ordinal)
            || (generatedExtractionSuppressed.HasValue
                && indexed.GeneratedExtractionSuppressed != generatedExtractionSuppressed.Value))
        {
            return null;
        }

        try
        {
            var info = new FileInfo(absolutePath);
            if (!info.Exists
                || info.Length > reusableFiles.MaxFileSizeBytes
                || info.Length != indexed.Size
                || info.LastWriteTimeUtc != indexed.ModifiedUtc)
            {
                return null;
            }

            return new IndexedFileStatReuseResult(indexed.FileId, indexed.Size);
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
