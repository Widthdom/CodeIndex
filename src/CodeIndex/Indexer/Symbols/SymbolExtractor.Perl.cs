using System.Text.RegularExpressions;
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
        @"(?:^|,)\s*(?:""(?<quoted>[\p{L}_][\p{L}\p{Nd}_]*)""|'(?<quoted>[\p{L}_][\p{L}\p{Nd}_]*)'|(?<bare>[\p{L}_][\p{L}\p{Nd}_]*))\s*=>",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static void ExtractPerlHashConstantSymbols(long fileId, string[] lines, List<SymbolRecord> symbols)
    {
        for (var i = 0; i < lines.Length; i++)
        {
            if (!TryCollectPerlHashConstantBody(lines, i, out var body, out var lineSegments, out var endLineIndex, out var signature))
                continue;

            foreach (Match keyMatch in PerlHashConstantKeyRegex.Matches(body))
            {
                var nameGroup = keyMatch.Groups["bare"].Success
                    ? keyMatch.Groups["bare"]
                    : keyMatch.Groups["quoted"];
                if (!nameGroup.Success)
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
                        Name = nameGroup.Value,
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
        for (var lineIndex = startLineIndex; lineIndex < lines.Length; lineIndex++)
        {
            var line = lines[lineIndex];
            var segmentStart = lineIndex == startLineIndex ? openBraceIndex + 1 : 0;
            var closeBraceIndex = line.IndexOf('}', segmentStart);
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
