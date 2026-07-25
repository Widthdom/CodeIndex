using System.Text.RegularExpressions;
using Regex = CodeIndex.Indexer.BoundedRegex;
using CodeIndex.Models;

namespace CodeIndex.Indexer;

public static partial class ReferenceExtractor
{
    private static string MaskHdlCommentsAndStrings(
        string line,
        string language,
        ref bool inVerilogBlockComment)
    {
        char[]? masked = null;
        var inString = false;
        for (var index = 0; index < line.Length; index++)
        {
            if (language != "vhdl" && inVerilogBlockComment)
            {
                MaskCharacter(ref masked, line, index);
                if (line[index] == '*' && index + 1 < line.Length && line[index + 1] == '/')
                {
                    MaskCharacter(ref masked, line, ++index);
                    inVerilogBlockComment = false;
                }

                continue;
            }

            if (inString)
            {
                MaskCharacter(ref masked, line, index);
                if (language == "vhdl"
                    && line[index] == '"'
                    && index + 1 < line.Length
                    && line[index + 1] == '"')
                {
                    MaskCharacter(ref masked, line, ++index);
                    continue;
                }

                if (line[index] == '\\' && language != "vhdl" && index + 1 < line.Length)
                {
                    MaskCharacter(ref masked, line, ++index);
                    continue;
                }

                if (line[index] == '"')
                    inString = false;
                continue;
            }

            if (language == "vhdl"
                && line[index] == '\''
                && index + 2 < line.Length
                && line[index + 2] == '\'')
            {
                MaskRange(ref masked, line, index, index + 3);
                index += 2;
                continue;
            }

            if (language != "vhdl" && line[index] == '\'')
            {
                var literalEnd = FindVerilogNumericLiteralEnd(line, index);
                if (literalEnd > index)
                {
                    MaskRange(ref masked, line, index, literalEnd);
                    index = literalEnd - 1;
                    continue;
                }
            }

            if (line[index] == '"')
            {
                if (language == "vhdl")
                {
                    var bitStringStart = FindVhdlBitStringLiteralStart(line, index);
                    if (bitStringStart < index)
                        MaskRange(ref masked, line, bitStringStart, index);
                }
                inString = true;
                MaskCharacter(ref masked, line, index);
                continue;
            }

            if (language == "vhdl"
                && line[index] == '-'
                && index + 1 < line.Length
                && line[index + 1] == '-')
            {
                MaskRange(ref masked, line, index, line.Length);
                break;
            }

            if (language != "vhdl"
                && line[index] == '/'
                && index + 1 < line.Length)
            {
                if (line[index + 1] == '/')
                {
                    MaskRange(ref masked, line, index, line.Length);
                    break;
                }

                if (line[index + 1] == '*')
                {
                    MaskCharacter(ref masked, line, index);
                    MaskCharacter(ref masked, line, ++index);
                    inVerilogBlockComment = true;
                }
            }
        }

        return masked == null ? line : new string(masked);
    }

    private static int FindVhdlBitStringLiteralStart(string line, int quoteIndex)
    {
        var baseIndex = quoteIndex - 1;
        if (baseIndex < 0 || !"BOXDboxd".Contains(line[baseIndex]))
            return quoteIndex;

        var start = baseIndex;
        if (start > 0 && (line[start - 1] is 'U' or 'u' or 'S' or 's'))
            start--;
        while (start > 0 && (char.IsDigit(line[start - 1]) || line[start - 1] == '_'))
            start--;

        return start == 0 || !IsVhdlIdentifierCharacter(line[start - 1])
            ? start
            : quoteIndex;
    }

    private static bool IsVhdlIdentifierCharacter(char value)
        => char.IsAsciiLetterOrDigit(value) || value == '_';

    private static int FindVerilogNumericLiteralEnd(string line, int apostropheIndex)
    {
        var index = apostropheIndex + 1;
        if (index >= line.Length)
            return apostropheIndex;

        if (line[index] is '0' or '1' or 'x' or 'X' or 'z' or 'Z' or '?')
            return index + 1;

        if (line[index] is 's' or 'S')
            index++;
        if (index >= line.Length || line[index] is not ('b' or 'B' or 'o' or 'O' or 'd' or 'D' or 'h' or 'H'))
            return apostropheIndex;

        index++;
        var digitStart = index;
        while (index < line.Length
            && (char.IsLetterOrDigit(line[index]) || line[index] is '_' or '?'))
        {
            index++;
        }

        return index > digitStart ? index : apostropheIndex;
    }

    private static void MaskRange(ref char[]? masked, string source, int start, int end)
    {
        for (var index = start; index < end; index++)
            MaskCharacter(ref masked, source, index);
    }

    private static void MaskCharacter(ref char[]? masked, string source, int index)
    {
        masked ??= source.ToCharArray();
        masked[index] = ' ';
    }
}
