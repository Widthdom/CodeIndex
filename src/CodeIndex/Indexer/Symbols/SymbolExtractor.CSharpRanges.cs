using System.Text.RegularExpressions;
using Regex = CodeIndex.Indexer.BoundedRegex;

namespace CodeIndex.Indexer;

public static partial class SymbolExtractor
{
    private const string CSharpRecordBoundaryModifierPattern =
        "(?:" + CSharpVisibilityPattern + @"|file|static|abstract|sealed|partial|readonly|ref|unsafe|new)";
    private const string CSharpRecordMemberBoundaryModifierPattern =
        "(?:" + CSharpVisibilityPattern + @"|static|abstract|virtual|override|sealed|partial|readonly|volatile|ref|required|unsafe|extern|async|new)";
    private static readonly Regex CSharpFollowingTypeDeclarationRegex = new(
        @"^\s*(?:" + CSharpRecordBoundaryModifierPattern + @"\s+)*(?:"
        + @"(?:record(?:\s+(?:class|struct))?|class|struct|interface|enum)\s+" + CSharpIdentifierPattern + @"\b"
        + @"|delegate\s+" + CSharpTypePattern + @"\s+" + CSharpIdentifierPattern + @"(?=\s*[\(<])"
        + @"|namespace\s+" + CSharpNamespacePattern + @"\b)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex CSharpFollowingMemberDeclarationRegex = new(
        @"^\s*(?![?:,])(?!where\b)(?!allows\s+ref\s+struct\b)(?:" + CSharpRecordMemberBoundaryModifierPattern + @"\s+)*(?:"
        + @"event\s+" + CSharpTypePattern + @"\s+" + CSharpIdentifierPattern + @"(?=\s*(?:[;{=]|=>))"
        + @"|" + CSharpTypePattern + @"\s+this(?=\s*\[)"
        + @"|~\s*" + CSharpIdentifierPattern + @"(?=\s*\()"
        + @"|static\s+" + CSharpIdentifierPattern + @"(?=\s*\()"
        + @"|(?:" + CSharpTypePattern + @"\s+)?(?:(?:implicit|explicit)\s+)?operator\b"
        + @"|" + CSharpTypePattern + @"\s+" + CSharpIdentifierPattern + CSharpMethodTypeParameterListPattern
            + @"(?=\s*(?:\(|[;{=]|=>))"
        + @"|(?:(?:unsafe|extern)\s+)*(?:" + CSharpVisibilityPattern + @")\s+"
            + @"(?:(?:unsafe|extern|partial)\s+)*" + CSharpIdentifierPattern + @"(?=\s*\())",
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
            && TryFindCSharpRecordDeclarationRange(
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

    private static bool TryFindCSharpRecordDeclarationRange(
        string[] csharpMatchLines,
        int startLineIndex,
        int startColumn,
        out (int EndLine, int? BodyStartLine, int? BodyEndLine) declarationRange)
    {
        var parenDepth = 0;
        var bracketDepth = 0;

        // Unlike the lightweight signature lookahead, definition content must remain available
        // for legal positional records longer than 64 lines. Following-declaration checks keep an
        // incomplete edit from walking into later declarations while this scan stays linear.
        // 軽量な signature lookahead と異なり、definition content は 64 行を超える合法な
        // positional record でも返す。線形走査を維持しつつ、後続宣言の検査で未完入力が
        // 後続宣言まで取り込むことを防ぐ。
        for (var lineIndex = startLineIndex; lineIndex < csharpMatchLines.Length; lineIndex++)
        {
            var sanitizedLine = csharpMatchLines[lineIndex];
            if (lineIndex > startLineIndex
                && parenDepth == 0
                && bracketDepth == 0
                && (CSharpFollowingTypeDeclarationRegex.IsMatch(sanitizedLine)
                    || CSharpFollowingMemberDeclarationRegex.IsMatch(sanitizedLine)))
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
                        declarationRange = FindCSharpBraceRange(
                            csharpMatchLines,
                            lineIndex,
                            column,
                            linesAreSanitized: true);
                        return true;
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
