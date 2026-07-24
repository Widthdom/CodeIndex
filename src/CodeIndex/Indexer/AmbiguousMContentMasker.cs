using System.Text;

namespace CodeIndex.Indexer;

/// <summary>
/// Masks MATLAB and Objective-C comments in unresolved <c>.m</c> content while preserving positions.
/// 未確定の <c>.m</c> 内容にある MATLAB / Objective-C コメントを、位置を保ったままマスクする。
/// </summary>
internal static class AmbiguousMContentMasker
{
    internal static string MaskComments(string content)
    {
        if (content.IndexOfAny(['%', '/']) < 0)
            return content;

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
                if (current == '\\' && index + 1 < content.Length)
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

            if (current == '"' || (current == '\'' && IsSingleQuoteStart(content, index)))
            {
                quote = current;
                continue;
            }

            if (current == '%')
            {
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

            if (current != '/' || index + 1 >= content.Length)
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

    private static bool IsSingleQuoteStart(string content, int quoteIndex)
    {
        for (var index = quoteIndex - 1; index >= 0 && content[index] is not '\r' and not '\n'; index--)
        {
            if (char.IsWhiteSpace(content[index]))
                continue;

            return !IsTransposeOperandEnd(content[index]);
        }

        return true;
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

    private static bool IsTransposeOperandEnd(char value) =>
        char.IsLetterOrDigit(value) || value is '_' or ')' or ']' or '}';
}
