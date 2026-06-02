using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Regex = CodeIndex.Indexer.BoundedRegex;
using CodeIndex.Models;

namespace CodeIndex.Indexer;

public static partial class SymbolExtractor
{
    private const string PerlIdentifierPattern = @"[\p{L}_][\p{L}\p{Nd}_]*";
    private const string PerlQualifiedIdentifierPattern = PerlIdentifierPattern + @"(?:::" + PerlIdentifierPattern + @")*";
    private static readonly Regex PerlHashConstantStartRegex = new(
        @"^\s*use\s+constant\s+\{",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex PerlHashConstantKeyRegex = new(
        @"(?:^|,)\s*(?:""(?<quoted>(?:\\x\{[0-9A-Fa-f]+\}|\\x[0-9A-Fa-f]{2}|\\.|[^""])*)""|'(?<quoted>(?:\\x\{[0-9A-Fa-f]+\}|\\x[0-9A-Fa-f]{2}|\\.|[^'])*)'|(?<bare>[\p{L}_][\p{L}\p{Nd}_]*))\s*=>",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static void ExtractPerlHashConstantSymbols(long fileId, string[] lines, List<SymbolRecord> symbols)
    {
        for (var i = 0; i < lines.Length; i++)
        {
            if (!TryCollectPerlHashConstantBody(lines, i, out var body, out var lineSegments, out var endLineIndex, out var signature))
                continue;

            var seenConstantNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (Match keyMatch in PerlHashConstantKeyRegex.Matches(body))
            {
                var nameGroup = keyMatch.Groups["bare"].Success
                    ? keyMatch.Groups["bare"]
                    : keyMatch.Groups["quoted"];
                if (!nameGroup.Success)
                    continue;
                var name = NormalizePerlConstantName(nameGroup.Value);
                if (name.Length == 0 || !seenConstantNames.Add(name))
                    continue;

                var (lineIndex, column) = ResolvePerlHashConstantBodyPosition(lineSegments, nameGroup.Index);
                AddSymbolRecord(
                    symbols,
                    cssSeenSymbols: null,
                    lineIndex + 1,
                    new SymbolRecord
                    {
                        FileId = fileId,
                        Kind = "function",
                        Name = name,
                        Line = lineIndex + 1,
                        StartLine = lineIndex + 1,
                        EndLine = lineIndex + 1,
                        StartColumn = column,
                        Signature = signature,
                    });
            }

            i = endLineIndex;
        }
    }

    private static string NormalizePerlConstantName(string name)
        => DecodePerlQuotedConstantEscapes(name.Trim()).Normalize(NormalizationForm.FormC);

    private static string DecodePerlQuotedConstantEscapes(string value)
    {
        if (value.IndexOf('\\') < 0)
            return value;

        var builder = new StringBuilder(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] != '\\' || i + 1 >= value.Length)
            {
                builder.Append(value[i]);
                continue;
            }

            var marker = value[i + 1];
            if (marker == 'x'
                && i + 3 < value.Length
                && value[i + 2] == '{'
                && TryFindPerlBracedHexEscapeEnd(value, i + 3, out var escapeEnd)
                && TryParseHexScalar(value.AsSpan(i + 3, escapeEnd - (i + 3)), out var scalar)
                && scalar <= 0x10FFFF)
            {
                builder.Append(char.ConvertFromUtf32(scalar));
                i = escapeEnd;
                continue;
            }

            if (marker == 'x' && i + 3 < value.Length && TryParseHexScalar(value.AsSpan(i + 2, 2), out var hexByte))
            {
                builder.Append((char)hexByte);
                i += 3;
                continue;
            }

            builder.Append(marker);
            i++;
        }

        return builder.ToString();
    }

    private static bool TryParseHexScalar(ReadOnlySpan<char> value, out int scalar)
        => int.TryParse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out scalar);

    private static bool TryFindPerlBracedHexEscapeEnd(string value, int start, out int end)
    {
        for (var i = start; i < value.Length; i++)
        {
            if (value[i] == '}')
            {
                end = i;
                return i > start;
            }
        }

        end = -1;
        return false;
    }

    private readonly record struct PerlBodyLineSegment(int BodyStartIndex, int LineIndex, int ColumnOffset);

    private static bool TryCollectPerlHashConstantBody(
        string[] lines,
        int startLineIndex,
        out string body,
        out List<PerlBodyLineSegment> lineSegments,
        out int endLineIndex,
        out string signature)
    {
        body = string.Empty;
        lineSegments = [];
        endLineIndex = startLineIndex;
        signature = lines[startLineIndex].Trim();

        var startLine = lines[startLineIndex];
        var startMatch = PerlHashConstantStartRegex.Match(startLine);
        if (!startMatch.Success)
            return false;

        var openBraceIndex = startLine.IndexOf('{', startMatch.Index + startMatch.Length - 1);
        if (openBraceIndex < 0)
            return false;

        var builder = new System.Text.StringBuilder();
        var inQuotedKey = false;
        var quotedKeyDelimiter = '\0';
        for (var lineIndex = startLineIndex; lineIndex < lines.Length; lineIndex++)
        {
            var line = lines[lineIndex];
            var segmentStart = lineIndex == startLineIndex ? openBraceIndex + 1 : 0;
            var closeBraceIndex = FindPerlHashConstantBlockCloseBrace(line, segmentStart, ref inQuotedKey, ref quotedKeyDelimiter);
            var segmentEnd = closeBraceIndex >= 0 ? closeBraceIndex : line.Length;
            if (segmentEnd > segmentStart)
            {
                lineSegments.Add(new PerlBodyLineSegment(builder.Length, lineIndex, segmentStart));
                builder.Append(line, segmentStart, segmentEnd - segmentStart);
            }

            if (closeBraceIndex >= 0)
            {
                body = builder.ToString();
                endLineIndex = lineIndex;
                return true;
            }

            builder.Append('\n');
        }

        return false;
    }

    private static int FindPerlHashConstantBlockCloseBrace(
        string line,
        int start,
        ref bool inQuotedKey,
        ref char quotedKeyDelimiter)
    {
        for (var i = start; i < line.Length; i++)
        {
            if (inQuotedKey)
            {
                if (line[i] == '\\' && i + 1 < line.Length)
                {
                    i++;
                    continue;
                }

                if (line[i] == quotedKeyDelimiter)
                {
                    inQuotedKey = false;
                    quotedKeyDelimiter = '\0';
                }

                continue;
            }

            if (line[i] == '"' || line[i] == '\'')
            {
                inQuotedKey = true;
                quotedKeyDelimiter = line[i];
                continue;
            }

            if (line[i] == '}')
                return i;
        }

        return -1;
    }

    private static (int LineIndex, int Column) ResolvePerlHashConstantBodyPosition(
        IReadOnlyList<PerlBodyLineSegment> lineSegments,
        int bodyIndex)
    {
        var segment = lineSegments[0];
        for (var i = 1; i < lineSegments.Count; i++)
        {
            if (lineSegments[i].BodyStartIndex > bodyIndex)
                break;
            segment = lineSegments[i];
        }

        return (segment.LineIndex, segment.ColumnOffset + bodyIndex - segment.BodyStartIndex);
    }
}
