using System.Text;
using CodeIndex.Models;
using CSharpFunctionValueReceiverNameRecord = CodeIndex.Indexer.ReferenceExtractor.CSharpFunctionValueReceiverNameRecord;
using CSharpUsingAliasRecord = CodeIndex.Indexer.ReferenceExtractor.CSharpUsingAliasRecord;

namespace CodeIndex.Indexer;

public static partial class ReferenceExtractor
{
    private static CSharpLineColumn FindCSharpQueryExpressionEndPosition(
        IReadOnlyList<string> structuralLines,
        int bodyEndIndex,
        int startLineIndex,
        int startColumn,
        IReadOnlySet<string> csharpKnownTypeNames,
        IReadOnlyList<CSharpUsingAliasRecord> csharpUsingAliases,
        IReadOnlyList<CSharpFunctionValueReceiverNameRecord> csharpFunctionValueReceiverNames)
    {
        var foundContent = false;
        var parenDepth = 0;
        var bracketDepth = 0;
        var braceDepth = 0;
        var angleDepth = 0;
        var terminalClauseSeen = false;
        var queryClauseSeen = false;
        var clauseHasTopLevelExpressionContent = false;
        var lastTopLevelSignificantLineIndex = -1;
        var lastTopLevelSignificantColumn = -1;

        for (var lineIndex = startLineIndex; lineIndex <= bodyEndIndex; lineIndex++)
        {
            var line = structuralLines[lineIndex];
            var columnStart = lineIndex == startLineIndex ? Math.Min(startColumn, line.Length) : 0;
            for (var column = columnStart; column < line.Length; column++)
            {
                var current = line[column];
                if (!foundContent)
                {
                    if (char.IsWhiteSpace(current))
                        continue;

                    foundContent = true;
                }

                if (parenDepth == 0 && bracketDepth == 0 && braceDepth == 0 && angleDepth == 0
                    && TryConsumeCSharpQueryClauseKeyword(line, column, out var keyword, out var nextColumn))
                {
                    if ((!queryClauseSeen || clauseHasTopLevelExpressionContent)
                        && IsCSharpQueryClauseKeyword(keyword)
                        && IsCSharpQueryClauseKeywordSuffix(
                            structuralLines,
                            bodyEndIndex,
                            lineIndex,
                            line,
                            nextColumn,
                            keyword,
                            lastTopLevelSignificantLineIndex,
                            lastTopLevelSignificantColumn,
                            csharpKnownTypeNames,
                            csharpUsingAliases,
                            csharpFunctionValueReceiverNames))
                    {
                        if ((string.Equals(keyword, "by", StringComparison.Ordinal)
                                || string.Equals(keyword, "ascending", StringComparison.Ordinal)
                                || string.Equals(keyword, "descending", StringComparison.Ordinal))
                            && terminalClauseSeen)
                        {
                            terminalClauseSeen = true;
                        }
                        else
                        {
                            terminalClauseSeen = IsCSharpTerminalQueryClauseKeyword(keyword);
                        }

                        queryClauseSeen = true;
                        clauseHasTopLevelExpressionContent = false;
                        lastTopLevelSignificantLineIndex = lineIndex;
                        lastTopLevelSignificantColumn = nextColumn - 1;
                        column = nextColumn - 1;
                        continue;
                    }
                }

                switch (current)
                {
                    case '<':
                        if (parenDepth == 0
                            && bracketDepth == 0
                            && braceDepth == 0
                            && LooksLikeCSharpQueryGenericTypeArgumentStart(structuralLines, bodyEndIndex, lineIndex, column))
                        {
                            angleDepth++;
                        }
                        break;
                    case '>':
                        if (angleDepth > 0)
                        {
                            angleDepth--;
                        }
                        break;
                    case '(':
                        parenDepth++;
                        break;
                    case ')':
                        if (parenDepth == 0 && bracketDepth == 0 && braceDepth == 0 && angleDepth == 0)
                            return new CSharpLineColumn(lineIndex + 1, column);
                        if (parenDepth > 0)
                            parenDepth--;
                        break;
                    case '[':
                        bracketDepth++;
                        break;
                    case ']':
                        if (parenDepth == 0 && bracketDepth == 0 && braceDepth == 0 && angleDepth == 0)
                            return new CSharpLineColumn(lineIndex + 1, column);
                        if (bracketDepth > 0)
                            bracketDepth--;
                        break;
                    case '{':
                        braceDepth++;
                        break;
                    case '}':
                        if (braceDepth > 0)
                            braceDepth--;
                        break;
                    case ';':
                        if (parenDepth == 0 && bracketDepth == 0 && braceDepth == 0 && angleDepth == 0)
                            return new CSharpLineColumn(lineIndex + 1, column);
                        break;
                    case ',':
                        if (parenDepth == 0 && bracketDepth == 0 && braceDepth == 0 && angleDepth == 0 && terminalClauseSeen)
                            return new CSharpLineColumn(lineIndex + 1, column);
                        if (parenDepth == 0 && bracketDepth == 0 && braceDepth == 0 && angleDepth == 0)
                        {
                            clauseHasTopLevelExpressionContent = false;
                            lastTopLevelSignificantLineIndex = lineIndex;
                            lastTopLevelSignificantColumn = column;
                        }
                        break;
                }

                if (!char.IsWhiteSpace(current)
                    && parenDepth == 0
                    && bracketDepth == 0
                    && braceDepth == 0
                    && angleDepth == 0
                    && current != ','
                    && current != ';')
                {
                    clauseHasTopLevelExpressionContent = true;
                    lastTopLevelSignificantLineIndex = lineIndex;
                    lastTopLevelSignificantColumn = column;
                }
            }
        }

        return new CSharpLineColumn(bodyEndIndex + 1, 0);
    }


}
