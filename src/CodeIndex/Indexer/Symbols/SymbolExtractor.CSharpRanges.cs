namespace CodeIndex.Indexer;

public static partial class SymbolExtractor
{
    private static (int EndLine, int? BodyStartLine, int? BodyEndLine) FindCSharpPatternBraceRange(
        string[] lines,
        string[] csharpMatchLines,
        CSharpLexState[]? csharpLineStartStates,
        int lineIndex,
        int absoluteStartColumn,
        int csharpGateRawStartColumn)
    {
        var line = lines[lineIndex];
        if (csharpLineStartStates != null && IsCSharpRootCodeLineStart(csharpLineStartStates[lineIndex]))
            return FindCSharpBraceRange(lines, lineIndex, Math.Min(csharpGateRawStartColumn, line.Length));

        return FindCSharpBraceRange(csharpMatchLines, lineIndex, absoluteStartColumn, linesAreSanitized: true);
    }

    private static bool IsCSharpRootCodeLineStart(CSharpLexState lineStartState)
        => lineStartState.Mode == CSharpLexMode.Code
            && lineStartState.InterpolationReturnMode == CSharpLexMode.Code
            && lineStartState.InterpolationBraceDepth == 0;
}
