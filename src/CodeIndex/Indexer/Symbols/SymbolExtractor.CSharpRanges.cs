using System.Text.RegularExpressions;
using Regex = CodeIndex.Indexer.BoundedRegex;

namespace CodeIndex.Indexer;

public static partial class SymbolExtractor
{
    private static readonly Regex CSharpFollowingTypeDeclarationRegex = new(
        @"^\s*(?:(?:public|protected|internal|private|file|static|abstract|sealed|partial|readonly|ref|unsafe|new)\s+)*(?:record(?:\s+(?:class|struct))?|class|struct|interface|enum|delegate|namespace)\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static (int EndLine, int? BodyStartLine, int? BodyEndLine) FindCSharpPatternBodyRange(
        string[] lines,
        string[] csharpMatchLines,
        Func<CSharpLexState[]>? getCSharpLineStartStates,
        int lineIndex,
        int absoluteStartColumn,
        int csharpGateRawStartColumn,
        bool isRecord)
    {
        if (isRecord
            && TryFindCSharpSemicolonRecordDeclarationRange(
                csharpMatchLines,
                lineIndex,
                absoluteStartColumn,
                out var declarationRange))
        {
            return declarationRange;
        }

        var line = lines[lineIndex];
        var csharpLineStartStates = getCSharpLineStartStates?.Invoke();
        if (csharpLineStartStates != null && IsCSharpRootCodeLineStart(csharpLineStartStates[lineIndex]))
            return FindCSharpBraceRange(lines, lineIndex, Math.Min(csharpGateRawStartColumn, line.Length));

        return FindCSharpBraceRange(csharpMatchLines, lineIndex, absoluteStartColumn, linesAreSanitized: true);
    }

    private static bool TryFindCSharpSemicolonRecordDeclarationRange(
        string[] csharpMatchLines,
        int startLineIndex,
        int startColumn,
        out (int EndLine, int? BodyStartLine, int? BodyEndLine) declarationRange)
    {
        var parenDepth = 0;
        var bracketDepth = 0;
        var limit = Math.Min(csharpMatchLines.Length, startLineIndex + CSharpTypeHeaderLookaheadLineLimit);

        for (var lineIndex = startLineIndex; lineIndex < limit; lineIndex++)
        {
            var sanitizedLine = csharpMatchLines[lineIndex];
            if (lineIndex > startLineIndex && CSharpFollowingTypeDeclarationRegex.IsMatch(sanitizedLine))
            {
                declarationRange = (startLineIndex + 1, null, null);
                return true;
            }

            var fromColumn = lineIndex == startLineIndex ? Math.Min(startColumn, sanitizedLine.Length) : 0;
            for (var column = fromColumn; column < sanitizedLine.Length; column++)
            {
                switch (sanitizedLine[column])
                {
                    case '(':
                        parenDepth++;
                        break;
                    case ')' when parenDepth > 0:
                        parenDepth--;
                        break;
                    case '[':
                        bracketDepth++;
                        break;
                    case ']' when bracketDepth > 0:
                        bracketDepth--;
                        break;
                    case '{' when parenDepth == 0 && bracketDepth == 0:
                        declarationRange = default;
                        return false;
                    case ';' when parenDepth == 0 && bracketDepth == 0:
                        var declarationEndLine = lineIndex + 1;
                        declarationRange = (declarationEndLine, startLineIndex + 1, declarationEndLine);
                        return true;
                }
            }
        }

        declarationRange = default;
        return false;
    }

    private static bool IsCSharpRootCodeLineStart(CSharpLexState lineStartState)
        => lineStartState.Mode == CSharpLexMode.Code
            && lineStartState.InterpolationReturnMode == CSharpLexMode.Code
            && lineStartState.InterpolationBraceDepth == 0;
}
