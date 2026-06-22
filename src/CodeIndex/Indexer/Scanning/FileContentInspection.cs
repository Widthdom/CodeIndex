namespace CodeIndex.Indexer;

internal readonly record struct FileContentInspection(
    bool IsGitLfsPointer,
    bool IsUtf16,
    bool Utf16BigEndian,
    bool HasUtf16Bom)
{
    public static FileContentInspection GitLfsPointer()
        => new(IsGitLfsPointer: true, IsUtf16: false, Utf16BigEndian: false, HasUtf16Bom: false);

    public static FileContentInspection Inspect(byte[] rawBytes)
    {
        if (FileContentLoader.IsGitLfsPointer(rawBytes))
            return GitLfsPointer();

        var isUtf16 = FileContentLoader.TryDetectUtf16Encoding(
            rawBytes,
            allowHeuristic: true,
            out var utf16BigEndian,
            out var hasUtf16Bom);
        return new FileContentInspection(
            IsGitLfsPointer: false,
            IsUtf16: isUtf16,
            Utf16BigEndian: utf16BigEndian,
            HasUtf16Bom: hasUtf16Bom);
    }
}
