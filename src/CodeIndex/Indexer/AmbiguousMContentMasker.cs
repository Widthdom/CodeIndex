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
        if ((!maskMatlabComments || content.IndexOf('%') < 0)
            && (!maskObjectiveCComments || content.IndexOf('/') < 0))
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
                quote = '\0';
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
                if (preserveObjectiveCModuloExpressions
                    && current == '\\'
                    && index + 1 < content.Length)
                {
                    index++;
                    continue;
                }

                if (current == quote)
                {
                    if (quote == '\'' && index + 1 < content.Length && content[index + 1] == '\'')
                    {
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

        if (previousIndex < 0
            || content[previousIndex] is '\r' or '\n'
            || !IsTransposeOperandEnd(content[previousIndex]))
        {
            return false;
        }

        var nextIndex = percentIndex + 1;
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
                || content[nextIndex] is '_' or '(' or '\'' or '"');
    }

    private static bool IsTransposeOperandEnd(char value) =>
        char.IsLetterOrDigit(value) || value is '_' or ')' or ']' or '}';
}
