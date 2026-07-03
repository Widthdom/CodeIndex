using System.Text.RegularExpressions;
using Regex = CodeIndex.Indexer.BoundedRegex;

namespace CodeIndex.Indexer;

public static partial class SymbolExtractor
{
    private static readonly Regex SmalltalkMethodStartForRangeRegex = new(@"^\s*[A-Za-z_]\w*(?:\s+class)?\s*>>", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex SmalltalkClassDeclarationForRangeRegex = new(@"^\s*(?:(?:[A-Za-z_]\w*)\s+subclass:|Class\s+named:|Object\s+subclass:)\s*#", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static (int EndLine, int? BodyStartLine, int? BodyEndLine) FindSmalltalkMethodRange(string[] lines, int startIndex)
    {
        int? bodyStartLine = null;
        var lastBodyLine = startIndex + 1;

        for (var i = startIndex + 1; i < lines.Length; i++)
        {
            var trimmed = lines[i].AsSpan().Trim();
            if (trimmed.IsEmpty)
                continue;

            if (IsSmalltalkRangeBoundary(trimmed))
                break;

            bodyStartLine ??= i + 1;
            lastBodyLine = i + 1;
        }

        return bodyStartLine == null
            ? (startIndex + 1, null, null)
            : (lastBodyLine, bodyStartLine, lastBodyLine);
    }

    private static bool IsSmalltalkRangeBoundary(ReadOnlySpan<char> line)
    {
        if (line.Contains(">>", StringComparison.Ordinal))
            return SmalltalkMethodStartForRangeRegex.IsMatch(line.ToString());

        if (!line.Contains("subclass:", StringComparison.Ordinal)
            && !line.Contains("named:", StringComparison.Ordinal))
        {
            return false;
        }

        return SmalltalkClassDeclarationForRangeRegex.IsMatch(line.ToString());
    }
}
