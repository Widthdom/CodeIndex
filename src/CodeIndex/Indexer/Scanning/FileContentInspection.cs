namespace CodeIndex.Indexer;

internal readonly record struct FileContentInspection(
    bool IsGitLfsPointer,
    bool IsUtf16,
    bool Utf16BigEndian,
    bool HasUtf16Bom,
    RawByteContentInspection RawByteContent)
{
    public static FileContentInspection GitLfsPointer()
        => new(
            IsGitLfsPointer: true,
            IsUtf16: false,
            Utf16BigEndian: false,
            HasUtf16Bom: false,
            RawByteContent: RawByteContentInspection.Empty);

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
            HasUtf16Bom: hasUtf16Bom,
            RawByteContent: isUtf16
                ? RawByteContentInspection.Empty
                : RawByteContentInspection.Inspect(rawBytes));
    }
}

internal readonly record struct RawByteContentInspection(
    bool HasUtf8Bom,
    bool HasNullByte,
    int NullByteOffset,
    bool HasCrlf,
    bool HasLfOnly,
    bool HasCrOnly)
{
    public static RawByteContentInspection Empty { get; } = new(
        HasUtf8Bom: false,
        HasNullByte: false,
        NullByteOffset: -1,
        HasCrlf: false,
        HasLfOnly: false,
        HasCrOnly: false);

    public static RawByteContentInspection Inspect(byte[] rawBytes)
    {
        var hasUtf8Bom = rawBytes.Length >= 3
            && rawBytes[0] == 0xEF
            && rawBytes[1] == 0xBB
            && rawBytes[2] == 0xBF;
        var rawSpan = rawBytes.AsSpan();
        if (rawSpan.IndexOfAny((byte)0, (byte)0x0D) < 0)
        {
            return new RawByteContentInspection(
                HasUtf8Bom: hasUtf8Bom,
                HasNullByte: false,
                NullByteOffset: -1,
                HasCrlf: false,
                HasLfOnly: rawSpan.IndexOf((byte)0x0A) >= 0,
                HasCrOnly: false);
        }

        var hasCrlf = false;
        var hasLfOnly = false;
        var hasCrOnly = false;
        var nullByteOffset = -1;

        // Line-ending classification checks raw bytes before LF normalization so bare CR
        // and three-way mixes are not flattened by the content normalization pass.
        for (var i = 0; i < rawBytes.Length; i++)
        {
            var value = rawBytes[i];
            if (value == 0 && nullByteOffset < 0)
                nullByteOffset = i;

            if (value == 0x0D)
            {
                if (i + 1 < rawBytes.Length && rawBytes[i + 1] == 0x0A)
                {
                    hasCrlf = true;
                    i++;
                }
                else
                {
                    hasCrOnly = true;
                }
            }
            else if (value == 0x0A)
            {
                hasLfOnly = true;
            }
        }

        return new RawByteContentInspection(
            HasUtf8Bom: hasUtf8Bom,
            HasNullByte: nullByteOffset >= 0,
            NullByteOffset: nullByteOffset,
            HasCrlf: hasCrlf,
            HasLfOnly: hasLfOnly,
            HasCrOnly: hasCrOnly);
    }
}
