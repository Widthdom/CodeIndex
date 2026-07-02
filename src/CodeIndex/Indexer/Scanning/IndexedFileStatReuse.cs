using CodeIndex.Database;

namespace CodeIndex.Indexer;

internal readonly record struct IndexedFileStatReuseResult(long FileId, long Size);

internal static class IndexedFileStatReuse
{
    internal static Action<string>? LookupForTesting { get; set; }

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
            if (!info.Exists)
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
}
