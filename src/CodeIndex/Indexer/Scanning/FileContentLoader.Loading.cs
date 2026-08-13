namespace CodeIndex.Indexer;

internal sealed partial class FileContentLoader
{
    private static LoadedFileContent BuildGitLfsPointerContent(
        RawFileSnapshot rawFile)
    {
        return new LoadedFileContent(
            string.Empty,
            rawFile.Bytes,
            rawFile.SizeBytes,
            rawFile.ModifiedUtc,
            NormalizedContentFacts.Empty,
            ComputeChecksum(rawFile.Bytes),
            null,
            FileContentInspection.GitLfsPointer());
    }

    private static LoadedFileContent BuildLoadedFileContent(
        RawFileSnapshot rawFile,
        DecodedFileContent decoded,
        NormalizedIndexableContent normalized)
    {
        var checksum = CanReuseRawBytesForNormalizedChecksum(
            decoded.Content,
            decoded.Warning,
            decoded.Inspection,
            normalized)
                ? ComputeRawChecksum(rawFile.Bytes)
                : ComputeChecksumFromNormalizedContent(normalized.Content);

        return new LoadedFileContent(
            normalized.Content,
            rawFile.Bytes,
            rawFile.SizeBytes,
            rawFile.ModifiedUtc,
            normalized.Facts,
            checksum,
            decoded.Warning,
            decoded.Inspection);
    }
}
