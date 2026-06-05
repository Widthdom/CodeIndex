using System.Text.RegularExpressions;

namespace CodeIndex.Diagnostics;

internal static class DiagnosticSanitizer
{
    private const int MaxDiagnosticFieldLength = 240;
    private static readonly Regex AbsolutePathPattern = new(
        @"(?:[A-Za-z]:)?[/\\][^\s'"";:,)]+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(50));

    public static string ForPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return string.Empty;

        var normalized = NormalizeSeparators(TryGetFullPath(path.Trim()));
        var cdidxIndex = normalized.IndexOf("/.cdidx/", StringComparison.OrdinalIgnoreCase);
        if (cdidxIndex >= 0)
            return Truncate(normalized[(cdidxIndex + 1)..]);

        var configIndex = normalized.IndexOf("/.config/cdidx/", StringComparison.OrdinalIgnoreCase);
        if (configIndex >= 0)
            return Truncate("<user-config>/" + normalized[(configIndex + "/.config/cdidx/".Length)..]);

        var fileName = Path.GetFileName(normalized);
        return Truncate(string.IsNullOrWhiteSpace(fileName) ? "<path>" : fileName);
    }

    public static string? ForOptionalLabel(string? value)
        => string.IsNullOrWhiteSpace(value) ? value : ForMessage(value);

    public static string ForMessage(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return string.Empty;

        var singleLine = message
            .Replace('\r', ' ')
            .Replace('\n', ' ')
            .Replace('\t', ' ');
        var withoutPaths = AbsolutePathPattern.Replace(singleLine, "<path>");
        return Truncate(Regex.Replace(withoutPaths, @"\s{2,}", " ", RegexOptions.None, TimeSpan.FromMilliseconds(50)).Trim());
    }

    private static string TryGetFullPath(string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
        {
            return path;
        }
    }

    private static string NormalizeSeparators(string value)
        => value.Replace('\\', '/');

    private static string Truncate(string value)
        => value.Length <= MaxDiagnosticFieldLength
            ? value
            : value[..MaxDiagnosticFieldLength] + "...";
}
