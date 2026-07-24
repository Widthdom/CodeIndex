using System.Text;

namespace CodeIndex.Indexer;

/// <summary>
/// Masks MATLAB and Objective-C comments in unresolved <c>.m</c> content while preserving positions.
/// 未確定の <c>.m</c> 内容にある MATLAB / Objective-C コメントを、位置を保ったままマスクする。
/// </summary>
internal static class AmbiguousMContentMasker
{
    internal static string MaskComments(
        string content,
        bool maskMatlabComments,
        bool maskObjectiveCComments,
        bool preserveObjectiveCModuloExpressions = false)
    {
        if (preserveObjectiveCModuloExpressions)
        {
            preserveObjectiveCModuloExpressions =
                HasStrongObjectiveCModuloEvidence(content);
        }

        return MaskContent(
            content,
            maskMatlabComments,
            maskObjectiveCComments,
            preserveObjectiveCModuloExpressions,
            maskStrings: false);
    }

    private static string MaskContent(
        string content,
        bool maskMatlabComments,
        bool maskObjectiveCComments,
        bool preserveObjectiveCModuloExpressions,
        bool maskStrings)
    {
        if ((!maskMatlabComments || content.IndexOf('%') < 0)
            && (!maskObjectiveCComments || content.IndexOf('/') < 0)
            && (!maskStrings
                || (content.IndexOf('"') < 0 && content.IndexOf('\'') < 0)))
        {
            return content;
        }

        StringBuilder? masked = null;
        var inBlockComment = false;
        var inMatlabBlockComment = false;
        var inLineComment = false;
        var quote = '\0';

        for (var index = 0; index < content.Length; index++)
        {
            var current = content[index];
            if (current is '\r' or '\n')
            {
                inLineComment = false;
                if (!maskStrings
                    || quote == '\0'
                    || !IsEscapedLineBreak(content, index))
                {
                    quote = '\0';
                }
                continue;
            }

            if (inLineComment)
            {
                MaskAt(index);
                continue;
            }

            if (inMatlabBlockComment)
            {
                MaskAt(index);
                if (current == '%'
                    && index + 1 < content.Length
                    && content[index + 1] == '}'
                    && IsStandaloneMatlabBlockDelimiter(content, index))
                {
                    MaskAt(++index);
                    inMatlabBlockComment = false;
                }
                continue;
            }

            if (inBlockComment)
            {
                MaskAt(index);
                if (current == '*' && index + 1 < content.Length && content[index + 1] == '/')
                {
                    MaskAt(++index);
                    inBlockComment = false;
                }
                continue;
            }

            if (quote != '\0')
            {
                if (maskStrings)
                    MaskAt(index);

                if ((preserveObjectiveCModuloExpressions || maskStrings)
                    && current == '\\'
                    && index + 1 < content.Length)
                {
                    if (maskStrings && content[index + 1] is not ('\r' or '\n'))
                        MaskAt(index + 1);
                    index++;
                    continue;
                }

                if (current == quote)
                {
                    if (quote == '\'' && index + 1 < content.Length && content[index + 1] == '\'')
                    {
                        if (maskStrings)
                            MaskAt(index + 1);
                        index++;
                        continue;
                    }

                    quote = '\0';
                }
                continue;
            }

            if (current == '"'
                || (current == '\''
                    && (preserveObjectiveCModuloExpressions
                        || IsMatlabSingleQuoteStart(content, index))))
            {
                quote = current;
                if (maskStrings)
                    MaskAt(index);
                continue;
            }

            if (maskMatlabComments && current == '%')
            {
                if (preserveObjectiveCModuloExpressions
                    && LooksLikeObjectiveCModuloOperator(content, index))
                {
                    continue;
                }

                MaskAt(index);
                if (index + 1 < content.Length
                    && content[index + 1] == '{'
                    && IsStandaloneMatlabBlockDelimiter(content, index))
                {
                    inMatlabBlockComment = true;
                }
                else
                {
                    inLineComment = true;
                }
                continue;
            }

            if (!maskObjectiveCComments || current != '/' || index + 1 >= content.Length)
                continue;

            var next = content[index + 1];
            if (next == '/')
            {
                MaskAt(index);
                MaskAt(++index);
                inLineComment = true;
            }
            else if (next == '*')
            {
                MaskAt(index);
                MaskAt(++index);
                inBlockComment = true;
            }
        }

        return masked?.ToString() ?? content;

        void MaskAt(int index)
        {
            masked ??= new StringBuilder(content);
            masked[index] = ' ';
        }
    }

    private static bool IsMatlabSingleQuoteStart(string content, int quoteIndex)
    {
        var previousIndex = quoteIndex - 1;
        return previousIndex < 0
            || content[previousIndex] is '\r' or '\n'
            || (!IsTransposeOperandEnd(content[previousIndex])
                && !(content[previousIndex] == '.'
                    && previousIndex > 0
                    && IsTransposeOperandEnd(content[previousIndex - 1])));
    }

    private static bool IsStandaloneMatlabBlockDelimiter(string content, int percentIndex)
    {
        for (var index = percentIndex - 1; index >= 0 && content[index] is not '\r' and not '\n'; index--)
        {
            if (!char.IsWhiteSpace(content[index]))
                return false;
        }

        for (var index = percentIndex + 2; index < content.Length && content[index] is not '\r' and not '\n'; index++)
        {
            if (!char.IsWhiteSpace(content[index]))
                return false;
        }

        return true;
    }

    private static bool LooksLikeObjectiveCModuloOperator(string content, int percentIndex)
    {
        var previousIndex = percentIndex - 1;
        while (previousIndex >= 0
            && content[previousIndex] is not '\r' and not '\n'
            && char.IsWhiteSpace(content[previousIndex]))
        {
            previousIndex--;
        }

        if (previousIndex > 0
            && content[previousIndex] is '+' or '-'
            && content[previousIndex - 1] == content[previousIndex])
        {
            previousIndex -= 2;
            while (previousIndex >= 0
                && content[previousIndex] is not '\r' and not '\n'
                && char.IsWhiteSpace(content[previousIndex]))
            {
                previousIndex--;
            }
        }

        if (previousIndex < 0
            || content[previousIndex] is '\r' or '\n'
            || !IsObjectiveCModuloLeftOperandEnd(content[previousIndex]))
        {
            return false;
        }

        var nextIndex = percentIndex + 1;
        if (nextIndex < content.Length && content[nextIndex] == '=')
            nextIndex++;

        while (nextIndex < content.Length
            && content[nextIndex] is not '\r' and not '\n'
            && char.IsWhiteSpace(content[nextIndex]))
        {
            nextIndex++;
        }

        while (nextIndex < content.Length
            && content[nextIndex] is not '\r' and not '\n'
            && content[nextIndex] is '+' or '-' or '!' or '~' or '*' or '&')
        {
            nextIndex++;
            while (nextIndex < content.Length
                && content[nextIndex] is not '\r' and not '\n'
                && char.IsWhiteSpace(content[nextIndex]))
            {
                nextIndex++;
            }
        }

        return nextIndex < content.Length
            && content[nextIndex] is not '\r' and not '\n'
            && (char.IsLetterOrDigit(content[nextIndex])
                || content[nextIndex] is '_' or '(' or '[' or '@' or '\'' or '"');
    }

    private static bool IsObjectiveCModuloLeftOperandEnd(char value) =>
        IsTransposeOperandEnd(value) || value is '\'' or '"';

    private static bool HasStrongObjectiveCModuloEvidence(string content)
    {
        var evidenceContent = MaskContent(
            content,
            maskMatlabComments: true,
            maskObjectiveCComments: true,
            preserveObjectiveCModuloExpressions: false,
            maskStrings: true);
        var lineStart = 0;
        while (lineStart < evidenceContent.Length)
        {
            var lineEnd = lineStart;
            while (lineEnd < evidenceContent.Length
                && evidenceContent[lineEnd] is not ('\r' or '\n'))
            {
                lineEnd++;
            }

            var line = evidenceContent.AsSpan(lineStart, lineEnd - lineStart).TrimStart();
            if (StartsWithObjectiveCPreprocessorDirective(line)
                || StartsWithObjectiveCAtKeyword(line)
                || StartsWithObjectiveCMethodDeclaration(line))
            {
                return true;
            }

            lineStart = lineEnd + 1;
            if (lineEnd < evidenceContent.Length
                && evidenceContent[lineEnd] == '\r'
                && lineStart < evidenceContent.Length
                && evidenceContent[lineStart] == '\n')
            {
                lineStart++;
            }
        }

        return false;
    }

    private static bool IsEscapedLineBreak(string content, int lineBreakIndex)
    {
        var previousIndex = lineBreakIndex - 1;
        if (content[lineBreakIndex] == '\n'
            && previousIndex >= 0
            && content[previousIndex] == '\r')
        {
            previousIndex--;
        }

        var backslashCount = 0;
        while (previousIndex >= 0 && content[previousIndex] == '\\')
        {
            backslashCount++;
            previousIndex--;
        }

        return backslashCount % 2 != 0;
    }

    private static bool StartsWithObjectiveCPreprocessorDirective(ReadOnlySpan<char> line)
    {
        if (line.IsEmpty || line[0] != '#')
            return false;

        var keywordStart = 1;
        while (keywordStart < line.Length && char.IsWhiteSpace(line[keywordStart]))
            keywordStart++;

        return StartsWithToken(line[keywordStart..], "import")
            || StartsWithToken(line[keywordStart..], "include");
    }

    private static bool StartsWithObjectiveCAtKeyword(ReadOnlySpan<char> line) =>
        StartsWithToken(line, "@interface")
        || StartsWithToken(line, "@implementation")
        || StartsWithToken(line, "@protocol")
        || StartsWithToken(line, "@class")
        || StartsWithToken(line, "@property")
        || StartsWithToken(line, "@synthesize")
        || StartsWithToken(line, "@dynamic")
        || StartsWithToken(line, "@autoreleasepool");

    private static bool StartsWithObjectiveCMethodDeclaration(ReadOnlySpan<char> line)
    {
        if (line.IsEmpty || line[0] is not ('-' or '+'))
            return false;

        var openingParenthesis = 1;
        while (openingParenthesis < line.Length && char.IsWhiteSpace(line[openingParenthesis]))
            openingParenthesis++;

        return openingParenthesis < line.Length && line[openingParenthesis] == '(';
    }

    private static bool StartsWithToken(ReadOnlySpan<char> line, string token) =>
        line.StartsWith(token, StringComparison.Ordinal)
        && (line.Length == token.Length || !IsTransposeOperandEnd(line[token.Length]));

    private static bool IsTransposeOperandEnd(char value) =>
        char.IsLetterOrDigit(value) || value is '_' or ')' or ']' or '}';
}
