namespace CodeIndex;

internal static class FileUriPolicy
{
    internal const string AbsoluteFileUriRequiredMessage = "textDocument.uri must be an absolute file URI.";

    internal static string PathToFileUri(string path, string? baseDirectory = null)
    {
        var fullPath = Path.IsPathRooted(path)
            ? Path.GetFullPath(path)
            : Path.GetFullPath(path, baseDirectory ?? Environment.CurrentDirectory);
        return new Uri(fullPath).AbsoluteUri;
    }

    internal static string AbsoluteFileUriToPath(string uriText, string errorMessage = AbsoluteFileUriRequiredMessage)
    {
        if (TryGetAbsoluteFileUriPath(uriText, out var localPath, out var error))
            return localPath;

        throw new ArgumentException(error ?? errorMessage);
    }

    internal static bool TryGetAbsoluteFileUriPath(string uriText, out string localPath, out string? error)
    {
        localPath = string.Empty;
        error = null;
        if (!uriText.StartsWith("file://", StringComparison.OrdinalIgnoreCase) ||
            !Uri.TryCreate(uriText, UriKind.Absolute, out var parsed) ||
            !parsed.IsFile)
        {
            error = AbsoluteFileUriRequiredMessage;
            return false;
        }

        if (!PathUriNormalizer.TryNormalizeFileUriPath(uriText, out localPath, out error))
        {
            error ??= AbsoluteFileUriRequiredMessage;
            return false;
        }

        return true;
    }

    internal static bool TryNormalizeFileUriPath(string fileUri, out string normalizedPath, out string? error)
        => PathUriNormalizer.TryNormalizeFileUriPath(fileUri, out normalizedPath, out error);
}
