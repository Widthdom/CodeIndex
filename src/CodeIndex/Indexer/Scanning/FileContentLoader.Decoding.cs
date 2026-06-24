using System.Text;

namespace CodeIndex.Indexer;

internal sealed partial class FileContentLoader
{
    private static readonly UTF8Encoding StrictUtf8Encoding = new(false, throwOnInvalidBytes: true);
    private static readonly UTF8Encoding LenientUtf8Encoding = new(false, throwOnInvalidBytes: false);
    private static readonly UnicodeEncoding Utf16LeBomEncoding = new(bigEndian: false, byteOrderMark: true, throwOnInvalidBytes: false);
    private static readonly UnicodeEncoding Utf16LeNoBomEncoding = new(bigEndian: false, byteOrderMark: false, throwOnInvalidBytes: false);
    private static readonly UnicodeEncoding Utf16BeBomEncoding = new(bigEndian: true, byteOrderMark: true, throwOnInvalidBytes: false);
    private static readonly UnicodeEncoding Utf16BeNoBomEncoding = new(bigEndian: true, byteOrderMark: false, throwOnInvalidBytes: false);

    private (string Content, string? Warning, FileContentInspection Inspection) DecodeIndexableContent(byte[] bytes, string relativePath)
    {
        var isUtf16Encoded = TryDetectUtf16Encoding(bytes, allowHeuristic: true, out var utf16BigEndian, out var hasUtf16Bom);
        var inspection = new FileContentInspection(
            IsGitLfsPointer: false,
            IsUtf16: isUtf16Encoded,
            Utf16BigEndian: utf16BigEndian,
            HasUtf16Bom: hasUtf16Bom);

        if (!isUtf16Encoded && TryFindNullByte(bytes, out var nullByteOffset))
            throw new FileIndexer.BinaryFileSkippedException(
                relativePath,
                nullByteOffset,
                $"{relativePath}: binary file skipped because it contains NULL byte at byte offset {nullByteOffset}");

        if (isUtf16Encoded)
        {
            var content = GetUtf16Encoding(utf16BigEndian, hasUtf16Bom).GetString(bytes);
            var warning = hasUtf16Bom
                ? null
                : $"{relativePath}: decoded as {(utf16BigEndian ? "UTF-16BE" : "UTF-16LE")} without BOM by NUL-byte heuristic";
            return (content, warning, inspection);
        }

        try
        {
            return (StrictUtf8Encoding.GetString(bytes), null, inspection);
        }
        catch (DecoderFallbackException)
        {
            var content = LenientUtf8Encoding.GetString(bytes);
            return (content, $"{relativePath}: contains invalid UTF-8 bytes (replaced with U+FFFD)", inspection);
        }
    }

    private static UnicodeEncoding GetUtf16Encoding(bool bigEndian, bool hasBom)
    {
        return bigEndian
            ? hasBom ? Utf16BeBomEncoding : Utf16BeNoBomEncoding
            : hasBom ? Utf16LeBomEncoding : Utf16LeNoBomEncoding;
    }

    internal static bool ContainsIndexBlockingNullByte(byte[] rawBytes)
    {
        return TryFindIndexBlockingNullByte(rawBytes, out _);
    }

    internal static bool TryFindIndexBlockingNullByte(byte[] rawBytes, out int offset)
    {
        offset = -1;
        if (TryDetectUtf16Encoding(rawBytes, allowHeuristic: true, out _, out _))
            return false;

        return TryFindNullByte(rawBytes, out offset);
    }

    private static bool TryFindNullByte(byte[] rawBytes, out int offset)
    {
        offset = Array.IndexOf(rawBytes, (byte)0);
        return offset >= 0;
    }

    internal static bool TryDetectUtf16Encoding(
        byte[] rawBytes,
        bool allowHeuristic,
        out bool bigEndian,
        out bool hasBom)
    {
        bigEndian = false;
        hasBom = false;

        if (rawBytes.Length >= 2 && rawBytes[0] == 0xFE && rawBytes[1] == 0xFF)
        {
            bigEndian = true;
            hasBom = true;
            return true;
        }

        if (rawBytes.Length >= 2 && rawBytes[0] == 0xFF && rawBytes[1] == 0xFE
            && !(rawBytes.Length >= 4 && rawBytes[2] == 0x00 && rawBytes[3] == 0x00))
        {
            hasBom = true;
            return true;
        }

        if (!allowHeuristic || rawBytes.Length < 4)
            return false;

        var sampleLength = Math.Min(rawBytes.Length, 4096);
        sampleLength -= sampleLength % 2;
        var pairs = sampleLength / 2;
        if (pairs == 0)
            return false;

        var evenNulls = 0;
        var oddNulls = 0;
        var oddTextBytes = 0;
        var evenTextBytes = 0;
        for (var i = 0; i < sampleLength; i += 2)
        {
            if (rawBytes[i] == 0)
                evenNulls++;
            if (rawBytes[i + 1] == 0)
                oddNulls++;
            if (IsLikelyTextByte(rawBytes[i + 1]))
                oddTextBytes++;
            if (IsLikelyTextByte(rawBytes[i]))
                evenTextBytes++;
        }

        const double NullParityThreshold = 0.30;
        const double OppositeNullThreshold = 0.01;
        const double TextByteThreshold = 0.80;
        var beScore = (double)evenNulls / pairs;
        var leScore = (double)oddNulls / pairs;
        var beOppositeScore = (double)oddNulls / pairs;
        var leOppositeScore = (double)evenNulls / pairs;

        if (beScore >= NullParityThreshold
            && beOppositeScore <= OppositeNullThreshold
            && (double)oddTextBytes / pairs >= TextByteThreshold)
        {
            bigEndian = true;
            return true;
        }

        if (leScore >= NullParityThreshold
            && leOppositeScore <= OppositeNullThreshold
            && (double)evenTextBytes / pairs >= TextByteThreshold)
        {
            bigEndian = false;
            return true;
        }

        return false;
    }

    private static bool IsLikelyTextByte(byte value)
        => value is 0x09 or 0x0A or 0x0D || value >= 0x20;
}
