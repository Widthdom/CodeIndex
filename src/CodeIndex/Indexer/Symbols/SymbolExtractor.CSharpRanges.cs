using System.Text.RegularExpressions;
using Regex = CodeIndex.Indexer.BoundedRegex;

namespace CodeIndex.Indexer;

public static partial class SymbolExtractor
{
    private const string CSharpRecordBoundaryModifierPattern =
        "(?:" + CSharpVisibilityPattern + @"|file|static|abstract|sealed|partial|readonly|ref|unsafe|new)";
    private const string CSharpRecordMemberBoundaryModifierPattern =
        "(?:" + CSharpVisibilityPattern + @"|static|abstract|virtual|override|sealed|partial|readonly|volatile|ref|required|unsafe|extern|async|fixed|new)";
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
        + @"|" + CSharpTypePattern + @"\s+" + CSharpExplicitInterfaceQualifierPattern + @"\s*\.\s*"
            + CSharpIdentifierPattern + CSharpMethodTypeParameterListPattern + @"(?=\s*(?:\(|[;{=]|=>))"
        + @"|" + CSharpTypePattern + @"\s+" + CSharpExplicitInterfaceQualifierPattern + @"\s*\.\s*this(?=\s*\[)"
        + @"|" + CSharpTypePattern + @"\s+" + CSharpIdentifierPattern + @"(?=\s*\[)"
        + @"|(?:(?:unsafe|extern)\s+)*(?:" + CSharpVisibilityPattern + @")\s+"
            + @"(?:(?:unsafe|extern|partial)\s+)*" + CSharpIdentifierPattern + @"(?=\s*\())",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex CSharpFollowingUnqualifiedConstructorDeclarationRegex = new(
        @"^\s*(?:(?:" + CSharpVisibilityPattern + @"|static|unsafe|extern|partial)\s+)*"
        + CSharpIdentifierPattern + @"(?=\s*\()",
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
        var hasTopLevelBaseList = false;
        var hasTopLevelWhereClause = false;
        var lastTopLevelHeaderCharacter = '\0';

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
                && bracketDepth == 0)
            {
                var boundaryLine = NormalizeCSharpQualifiedTypeWhitespaceForRecordBoundary(sanitizedLine);
                var unqualifiedConstructorBoundary =
                    CSharpFollowingUnqualifiedConstructorDeclarationRegex.IsMatch(boundaryLine)
                    && (!hasTopLevelBaseList && !hasTopLevelWhereClause
                        || lastTopLevelHeaderCharacter is not ':' and not ',');
                if (CSharpFollowingTypeDeclarationRegex.IsMatch(boundaryLine)
                    || CSharpFollowingMemberDeclarationRegex.IsMatch(boundaryLine)
                    || unqualifiedConstructorBoundary)
                {
                    declarationRange = (startLineIndex + 1, null, null);
                    return true;
                }
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
                    case 'w' when parenDepth == 0
                        && bracketDepth == 0
                        && IsCSharpRecordWhereKeywordAt(sanitizedLine, column):
                        hasTopLevelWhereClause = true;
                        break;
                    case ':' when parenDepth == 0
                        && bracketDepth == 0
                        && !hasTopLevelWhereClause
                        && (column == 0 || sanitizedLine[column - 1] != ':')
                        && (column + 1 >= sanitizedLine.Length || sanitizedLine[column + 1] != ':'):
                        hasTopLevelBaseList = true;
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

                if (!char.IsWhiteSpace(sanitizedLine[column])
                    && parenDepth == 0
                    && bracketDepth == 0)
                {
                    lastTopLevelHeaderCharacter = sanitizedLine[column];
                }
            }
        }

        declarationRange = default;
        return false;
    }

    // C# permits whitespace around `.` and `::` in qualified base and constraint types. Normalize
    // only that whitespace before the following-member heuristic so `N . Base(X) {` is not
    // backtracked into a return type plus member name; a real `N . Type M()` member still
    // normalizes to `N.Type M()` and remains a boundary.
    // C# では base / constraint の修飾型で `.` と `::` 周辺の空白を許す。後続 member 判定の前だけ
    // その空白を正規化し、`N . Base(X) {` を return type と member name に誤分割しない。
    // 実際の `N . Type M()` は `N.Type M()` となるため、引き続き boundary として検出する。
    private static string NormalizeCSharpQualifiedTypeWhitespaceForRecordBoundary(string line)
    {
        if (!line.Contains('.', StringComparison.Ordinal)
            && !line.Contains(':', StringComparison.Ordinal))
            return line;

        var normalized = new System.Text.StringBuilder(line.Length);
        var changed = false;
        for (var index = 0; index < line.Length; index++)
        {
            if (!char.IsWhiteSpace(line[index]))
            {
                normalized.Append(line[index]);
                continue;
            }

            var whitespaceStart = index;
            while (index + 1 < line.Length && char.IsWhiteSpace(line[index + 1]))
                index++;

            var previous = whitespaceStart > 0 ? line[whitespaceStart - 1] : '\0';
            var next = index + 1 < line.Length ? line[index + 1] : '\0';
            var followsAliasQualifier = previous == ':'
                && whitespaceStart >= 2
                && line[whitespaceStart - 2] == ':';
            var precedesAliasQualifier = next == ':'
                && index + 2 < line.Length
                && line[index + 2] == ':';
            if (previous == '.' || next == '.' || followsAliasQualifier || precedesAliasQualifier)
            {
                changed = true;
                continue;
            }

            normalized.Append(line, whitespaceStart, index - whitespaceStart + 1);
        }

        return changed ? normalized.ToString() : line;
    }

    private static bool IsCSharpRecordWhereKeywordAt(string line, int column)
    {
        const string Keyword = "where";
        if (column + Keyword.Length > line.Length
            || !line.AsSpan(column, Keyword.Length).SequenceEqual(Keyword))
        {
            return false;
        }

        var hasIdentifierBefore = column > 0
            && (line[column - 1] == '_' || char.IsLetterOrDigit(line[column - 1]));
        var afterKeyword = column + Keyword.Length;
        var hasIdentifierAfter = afterKeyword < line.Length
            && (line[afterKeyword] == '_' || char.IsLetterOrDigit(line[afterKeyword]));
        return !hasIdentifierBefore && !hasIdentifierAfter;
    }

    private static bool IsCSharpRootCodeLineStart(CSharpLexState lineStartState)
        => lineStartState.Mode == CSharpLexMode.Code
            && lineStartState.InterpolationReturnMode == CSharpLexMode.Code
            && lineStartState.InterpolationBraceDepth == 0;
}
