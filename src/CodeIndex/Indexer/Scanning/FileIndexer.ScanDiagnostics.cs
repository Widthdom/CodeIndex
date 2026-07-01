using System.Globalization;
using System.Text;

namespace CodeIndex.Indexer;

public partial class FileIndexer
{
    private string FormatPathForScanIssue(string absolutePath)
    {
        var displayPath = absolutePath;
        try
        {
            displayPath = Path.GetRelativePath(_projectRoot, absolutePath);
        }
        catch (ArgumentException)
        {
        }

        return EscapeControlCharacters(NormalizePathSeparators(displayPath));
    }

    private static string EscapeControlCharacters(string value)
    {
        var firstControl = -1;
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] < ' ')
            {
                firstControl = i;
                break;
            }
        }

        if (firstControl < 0)
            return value;

        var builder = new StringBuilder(value.Length + 8);
        if (firstControl > 0)
            builder.Append(value, 0, firstControl);
        for (var i = firstControl; i < value.Length; i++)
        {
            var c = value[i];
            if (c < ' ')
                builder.Append(CultureInfo.InvariantCulture, $"\\u{(int)c:X4}");
            else
                builder.Append(c);
        }

        return builder.ToString();
    }
}
