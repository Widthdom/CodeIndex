using System.Text;

namespace CodeIndex.Indexer;

internal static class RustSymbolNameNormalizer
{
    internal static string Normalize(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return name;

        var trimmed = name.Trim();
        if (trimmed.Length == 0)
            return trimmed;

        if (!trimmed.Contains("::", StringComparison.Ordinal))
            return trimmed.StartsWith("r#", StringComparison.Ordinal)
                ? trimmed[2..]
                : trimmed;

        var builder = new StringBuilder(trimmed.Length);
        var segmentStart = 0;
        while (segmentStart <= trimmed.Length)
        {
            var separator = trimmed.IndexOf("::", segmentStart, StringComparison.Ordinal);
            var segmentEnd = separator >= 0 ? separator : trimmed.Length;
            AppendRustPathSegment(builder, trimmed, segmentStart, segmentEnd - segmentStart, trim: true);
            if (separator < 0)
                break;

            builder.Append("::");
            segmentStart = separator + 2;
        }

        return builder.ToString();
    }

    private static void AppendRustPathSegment(StringBuilder builder, string value, int start, int length, bool trim)
    {
        if (trim)
        {
            while (length > 0 && char.IsWhiteSpace(value[start]))
            {
                start++;
                length--;
            }

            while (length > 0 && char.IsWhiteSpace(value[start + length - 1]))
                length--;
        }

        if (length >= 2
            && value[start] == 'r'
            && value[start + 1] == '#')
        {
            start += 2;
            length -= 2;
        }

        builder.Append(value, start, length);
    }
}
