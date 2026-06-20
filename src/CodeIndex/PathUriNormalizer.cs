namespace CodeIndex;

/// <summary>
/// Shared URI path decoding guard for file-like paths. It rejects malformed percent escapes
/// and encoded path-boundary characters before any caller normalizes the decoded path.
/// file-like path 用の URI path decode guard。decode 済み path を正規化する前に、不正な
/// percent escape と encoded path boundary 文字を拒否する。
/// </summary>
internal static class PathUriNormalizer
{
    internal static bool TryDecodeRelativeUriPath(string encodedPath, bool allowBackslash, out string decodedPath)
    {
        decodedPath = string.Empty;
        if (string.IsNullOrWhiteSpace(encodedPath)
            || ContainsInvalidPercentEscape(encodedPath)
            || ContainsEncodedPathBoundary(encodedPath))
        {
            return false;
        }

        var decoded = Uri.UnescapeDataString(encodedPath);
        if (!allowBackslash && decoded.Contains('\\', StringComparison.Ordinal))
            return false;

        var normalized = decoded.Replace('\\', '/');
        if (normalized.Length == 0
            || Path.IsPathRooted(normalized)
            || HasWindowsDrivePrefix(normalized)
            || normalized.Split('/').Any(segment => segment.Length == 0 || segment is "." or ".."))
        {
            return false;
        }

        decodedPath = normalized;
        return true;
    }

    internal static bool TryNormalizeFileUriPath(string fileUri, out string normalizedPath, out string? error)
    {
        normalizedPath = fileUri;
        error = null;
        if (!fileUri.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
            return true;

        var pathText = StripQuery(fileUri);
        var pathPayload = pathText["file:".Length..];
        if (ContainsInvalidPercentEscape(pathText))
        {
            error = "Invalid percent escape in file URI.";
            return false;
        }
        if (ContainsEncodedPathBoundary(pathPayload))
        {
            error = "Encoded path separators or traversal markers are not allowed in file URI paths.";
            return false;
        }

        try
        {
            if (!pathText.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
            {
                var relativePath = Uri.UnescapeDataString(pathText["file:".Length..]);
                if (string.IsNullOrWhiteSpace(relativePath))
                    return true;
                normalizedPath = Path.GetFullPath(relativePath);
                return true;
            }

            var uri = new Uri(pathText);
            if (!uri.IsFile)
                return true;

            var localPath = uri.LocalPath;
            if (string.IsNullOrWhiteSpace(localPath))
                return true;

            normalizedPath = Path.IsPathRooted(localPath)
                ? localPath
                : Path.GetFullPath(localPath);
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or UriFormatException)
        {
            error = ex.Message;
            return false;
        }
    }

    internal static bool ContainsInvalidPercentEscape(string text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != '%')
                continue;

            if (i + 2 >= text.Length || !IsHexDigit(text[i + 1]) || !IsHexDigit(text[i + 2]))
                return true;
            i += 2;
        }

        return false;
    }

    internal static bool HasWindowsDrivePrefix(string path)
        => path.Length >= 2
            && path[1] == ':'
            && ((path[0] >= 'A' && path[0] <= 'Z') || (path[0] >= 'a' && path[0] <= 'z'));

    private static string StripQuery(string uri)
    {
        var query = uri.IndexOf('?');
        return query >= 0 ? uri[..query] : uri;
    }

    private static bool ContainsEncodedPathBoundary(string text)
    {
        foreach (var segment in text.Split('/', '\\'))
        {
            if (segment.Length == 0)
                continue;

            var decodedSegment = Uri.UnescapeDataString(segment);
            if (decodedSegment is "." or "..")
                return true;
        }

        for (var i = 0; i + 2 < text.Length; i++)
        {
            if (text[i] != '%')
                continue;

            var decoded = DecodeAsciiHex(text[i + 1], text[i + 2]);
            if (decoded is '/' or '\\')
                return true;
            i += 2;
        }

        return false;
    }

    private static char DecodeAsciiHex(char high, char low)
        => (char)((HexValue(high) << 4) | HexValue(low));

    private static int HexValue(char ch)
        => ch switch
        {
            >= '0' and <= '9' => ch - '0',
            >= 'A' and <= 'F' => ch - 'A' + 10,
            >= 'a' and <= 'f' => ch - 'a' + 10,
            _ => 0,
        };

    private static bool IsHexDigit(char ch)
        => (ch >= '0' && ch <= '9') ||
           (ch >= 'A' && ch <= 'F') ||
           (ch >= 'a' && ch <= 'f');
}
