using CodeIndex.Models;

namespace CodeIndex.Indexer;

public partial class FileIndexer
{
    internal static int CountPhysicalLines(string content)
    {
        if (content.Length == 0)
            return 0;

        var lines = 1;
        var lastWasLineBreak = false;
        for (var i = 0; i < content.Length; i++)
        {
            var c = content[i];
            if (c != '\r' && c != '\n')
            {
                lastWasLineBreak = false;
                continue;
            }

            lastWasLineBreak = true;
            if (c == '\r' && i + 1 < content.Length && content[i + 1] == '\n')
                i++;

            if (i + 1 < content.Length)
                lines++;
        }

        return lastWasLineBreak ? Math.Max(lines, 1) : lines;
    }

    public static void ValidateSymbolLineRanges(FileRecord record, IReadOnlyList<SymbolRecord> symbols)
    {
        foreach (var symbol in symbols)
        {
            ValidateSymbolLine(record, symbol, symbol.Line, nameof(symbol.Line));
            ValidateSymbolLine(record, symbol, symbol.StartLine, nameof(symbol.StartLine), allowZero: true);
            ValidateSymbolLine(record, symbol, symbol.EndLine, nameof(symbol.EndLine), allowZero: true, allowOnePastEnd: true);
            ValidateSymbolLine(record, symbol, symbol.BodyStartLine, nameof(symbol.BodyStartLine), allowOnePastEnd: true);
            ValidateSymbolLine(record, symbol, symbol.BodyEndLine, nameof(symbol.BodyEndLine), allowOnePastEnd: true);
        }
    }

    private static void ValidateSymbolLine(FileRecord record, SymbolRecord symbol, int? line, string fieldName, bool allowZero = false, bool allowOnePastEnd = false)
    {
        if (line is null)
            return;

        if (allowZero && line == 0)
            return;

        var maxLine = record.Lines + (allowOnePastEnd ? 1 : 0);
        if (line < 1 || line > maxLine)
        {
            throw new InvalidOperationException(
                $"{record.Path}: extracted symbol '{symbol.Name}' has {fieldName}={line}, outside file line range 1..{maxLine}");
        }
    }
}
