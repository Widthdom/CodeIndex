using System.Text;
using System.Text.RegularExpressions;
using Regex = CodeIndex.Indexer.BoundedRegex;
using System.Runtime.CompilerServices;
using CodeIndex.Models;

namespace CodeIndex.Indexer;

public static partial class SymbolExtractor
{
    private static readonly Regex FortranRoutineStartLineRegex = new(
        @"^\s*(?:(?:pure|elemental|recursive|module|impure)\s+)*(?:subroutine\b|(?:(?:(?:integer|real|logical|complex)(?:\s*\([^)]+\))?|character(?:\s*\([^)]+\))?|double\s+precision|type\s*\([^)]+\)|class\s*\([^)]+\)|procedure\s*\([^)]+\))\s+)?function\b)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static (int EndLine, int? BodyStartLine, int? BodyEndLine) FindFortranRange(string[] lines, int startIndex)
    {
        if (!TryGetFortranBlockStartKind(lines[startIndex], out var blockKind))
            return (startIndex + 1, null, null);

        int? bodyStartLine = null;
        var endLine = startIndex + 1;
        var nestedRoutineDepth = 0;

        for (int i = startIndex + 1; i < lines.Length; i++)
        {
            var trimmed = StripFortranComment(MaskFortranStringLiterals(lines[i])).Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('!'))
                continue;

            if (blockKind == "module" && IsFortranModuleProcedureEndLine(trimmed))
                continue;

            if (blockKind is "subroutine" or "function" or "procedure" && IsFortranRoutineEndLine(trimmed))
            {
                if (nestedRoutineDepth > 0)
                {
                    nestedRoutineDepth--;
                    endLine = i + 1;
                    continue;
                }
            }

            if (IsFortranBlockEndLine(trimmed, blockKind))
            {
                if (bodyStartLine == null)
                    return (i + 1, null, null);

                return (i + 1, bodyStartLine, i + 1);
            }

            if (bodyStartLine == null)
                bodyStartLine = i + 1;

            if (blockKind is "subroutine" or "function" or "procedure"
                && IsFortranRoutineStartLine(trimmed))
            {
                nestedRoutineDepth++;
            }

            endLine = i + 1;
        }

        return bodyStartLine == null
            ? (startIndex + 1, null, null)
            : (endLine, bodyStartLine, endLine);
    }

    private static FortranContinuationMatchCandidate? TryBuildFortranContinuationMatchLine(string[] lines, int startIndex)
    {
        var firstLine = lines[startIndex];
        var firstTrimmed = firstLine.TrimStart();
        if (!StartsWithFortranContinuationCandidate(firstTrimmed))
            return null;

        var firstCode = StripFortranComment(firstLine).TrimEnd();
        if (!firstCode.Contains('&'))
            return null;

        var builder = new StringBuilder(firstCode.Length + 32);
        var lastConsumedLineIndex = startIndex;
        var currentLine = firstCode;

        while (true)
        {
            var continuationIndex = currentLine.LastIndexOf('&');
            if (continuationIndex < 0)
            {
                if (currentLine.Length > 0)
                {
                    if (builder.Length > 0)
                        builder.Append(' ');
                    builder.Append(currentLine.Trim());
                }

                break;
            }

            if (builder.Length > 0)
                builder.Append(' ');
            builder.Append(currentLine[..continuationIndex].TrimEnd());

            if (lastConsumedLineIndex + 1 >= lines.Length)
                break;

            var nextLine = StripFortranComment(lines[lastConsumedLineIndex + 1]).TrimStart();
            if (nextLine.StartsWith('&'))
                nextLine = nextLine[1..].TrimStart();

            lastConsumedLineIndex++;
            currentLine = nextLine.TrimEnd();
            if (currentLine.Length == 0)
                break;
        }

        var normalized = builder.ToString().Trim();
        return normalized.Length == 0 || lastConsumedLineIndex == startIndex
            ? null
            : new FortranContinuationMatchCandidate(normalized, lastConsumedLineIndex);
    }

    private static bool StartsWithFortranContinuationCandidate(string trimmedLine)
    {
        if (StartsWithFortranWord(trimmedLine, "interface"))
            return true;

        if (StartsWithFortranWord(trimmedLine, "abstract"))
        {
            var afterAbstract = trimmedLine["abstract".Length..].TrimStart();
            return StartsWithFortranWord(afterAbstract, "interface");
        }

        return StartsWithFortranContinuationPrefix(trimmedLine);
    }

    private static bool StartsWithFortranContinuationPrefix(string trimmedLine)
    {
        return StartsWithFortranWord(trimmedLine, "module")
            || StartsWithFortranWord(trimmedLine, "subroutine")
            || StartsWithFortranWord(trimmedLine, "function")
            || StartsWithFortranWord(trimmedLine, "procedure")
            || StartsWithFortranWord(trimmedLine, "pure")
            || StartsWithFortranWord(trimmedLine, "elemental")
            || StartsWithFortranWord(trimmedLine, "recursive")
            || StartsWithFortranWord(trimmedLine, "impure")
            || StartsWithFortranWord(trimmedLine, "integer")
            || StartsWithFortranWord(trimmedLine, "real")
            || StartsWithFortranWord(trimmedLine, "logical")
            || StartsWithFortranWord(trimmedLine, "complex")
            || StartsWithFortranWord(trimmedLine, "character")
            || StartsWithFortranWord(trimmedLine, "double")
            || StartsWithFortranWord(trimmedLine, "type")
            || StartsWithFortranWord(trimmedLine, "class");
    }

    private static string StripFortranComment(string line)
    {
        var commentIndex = line.IndexOf('!');
        return commentIndex >= 0 ? line[..commentIndex] : line;
    }

    private static bool TryGetFortranBlockStartKind(string line, out string kind)
    {
        kind = string.Empty;
        var trimmed = line.TrimStart();
        if (trimmed.Length == 0)
            return false;

        if (StartsWithFortranWord(trimmed, "module"))
        {
            var remainder = trimmed["module".Length..].TrimStart();
            if (StartsWithFortranWord(remainder, "procedure"))
            {
                kind = "procedure";
                return true;
            }

            if (StartsWithFortranWord(remainder, "subroutine") || StartsWithFortranWord(remainder, "function"))
                return false;

            kind = "module";
            return true;
        }

        if (StartsWithFortranWord(trimmed, "interface"))
        {
            kind = "interface";
            return true;
        }

        if (StartsWithFortranWord(trimmed, "submodule"))
        {
            kind = "submodule";
            return true;
        }

        if (StartsWithFortranWord(trimmed, "program"))
        {
            kind = "program";
            return true;
        }

        if (StartsWithFortranWord(trimmed, "block"))
        {
            var remainder = trimmed["block".Length..].TrimStart();
            if (StartsWithFortranWord(remainder, "data"))
            {
                kind = "block data";
                return true;
            }
        }

        if (IsFortranDerivedTypeStartLine(trimmed))
        {
            kind = "type";
            return true;
        }

        if (ContainsFortranWord(trimmed, "subroutine"))
        {
            kind = "subroutine";
            return true;
        }

        if (ContainsFortranWord(trimmed, "function"))
        {
            kind = "function";
            return true;
        }

        return false;
    }

    private static bool IsFortranDerivedTypeStartLine(string trimmedLine)
    {
        if (!StartsWithFortranWord(trimmedLine, "type"))
            return false;

        var remainder = trimmedLine["type".Length..].TrimStart();
        if (remainder.Length == 0 || remainder.StartsWith('('))
            return false;

        return !StartsWithFortranWord(remainder, "is")
            && !StartsWithFortranWord(remainder, "default");
    }

    private static bool IsFortranBlockEndLine(string trimmedLine, string blockKind)
    {
        if (!StartsWithFortranWord(trimmedLine, "end"))
            return false;

        var remainder = trimmedLine["end".Length..].TrimStart();
        if (remainder.Length == 0)
            return blockKind is "subroutine" or "function";

        if (!StartsWithFortranWord(remainder, blockKind))
            return false;

        var afterKind = remainder[blockKind.Length..].TrimStart();
        if (blockKind == "module" && StartsWithFortranWord(afterKind, "procedure"))
            return false;

        return afterKind.Length == 0 || afterKind.StartsWith('!') || afterKind.StartsWith('(') || afterKind.StartsWith(':') || char.IsLetterOrDigit(afterKind[0]) || afterKind[0] == '_';
    }

    private static bool IsFortranModuleProcedureEndLine(string trimmedLine)
    {
        if (!StartsWithFortranWord(trimmedLine, "end"))
            return false;

        var remainder = trimmedLine["end".Length..].TrimStart();
        if (!StartsWithFortranWord(remainder, "module"))
            return false;

        var afterModule = remainder["module".Length..].TrimStart();
        return StartsWithFortranWord(afterModule, "procedure");
    }

    private static bool IsFortranRoutineEndLine(string trimmedLine)
    {
        if (!StartsWithFortranWord(trimmedLine, "end"))
            return false;

        var remainder = trimmedLine["end".Length..].TrimStart();
        return remainder.Length == 0
            || StartsWithFortranWord(remainder, "subroutine")
            || StartsWithFortranWord(remainder, "function")
            || StartsWithFortranWord(remainder, "procedure");
    }

    private static bool IsFortranRoutineStartLine(string trimmedLine) =>
        !StartsWithFortranWord(trimmedLine, "end")
        && FortranRoutineStartLineRegex.IsMatch(trimmedLine);

    private static string MaskFortranStringLiterals(string line)
    {
        var chars = line.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            if (chars[i] is not ('\'' or '"'))
                continue;

            var quote = chars[i];
            chars[i] = ' ';
            i++;
            while (i < chars.Length)
            {
                if (chars[i] == quote && i + 1 < chars.Length && chars[i + 1] == quote)
                {
                    chars[i++] = ' ';
                    chars[i] = ' ';
                    i++;
                    continue;
                }

                var closes = chars[i] == quote;
                chars[i] = ' ';
                if (closes)
                    break;
                i++;
            }
        }

        return new string(chars);
    }

    private static bool StartsWithFortranWord(string input, string word)
    {
        if (!input.StartsWith(word, StringComparison.OrdinalIgnoreCase))
            return false;

        return input.Length == word.Length || !char.IsLetterOrDigit(input[word.Length]) && input[word.Length] != '_';
    }

    private static bool ContainsFortranWord(string input, string word)
    {
        for (var index = input.IndexOf(word, StringComparison.OrdinalIgnoreCase);
             index >= 0;
             index = input.IndexOf(word, index + word.Length, StringComparison.OrdinalIgnoreCase))
        {
            var beforeOk = index == 0 || !char.IsLetterOrDigit(input[index - 1]) && input[index - 1] != '_';
            var afterIndex = index + word.Length;
            var afterOk = afterIndex == input.Length || !char.IsLetterOrDigit(input[afterIndex]) && input[afterIndex] != '_';
            if (beforeOk && afterOk)
                return true;
        }

        return false;
    }

}
