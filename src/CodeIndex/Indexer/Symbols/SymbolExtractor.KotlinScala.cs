using System.Text;
using System.Text.RegularExpressions;
using Regex = CodeIndex.Indexer.BoundedRegex;
using System.Runtime.CompilerServices;
using CodeIndex.Models;

namespace CodeIndex.Indexer;

public static partial class SymbolExtractor
{
    private static readonly Regex KotlinValueClassSignatureRegex = new(
        @"\bvalue\s+class\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex KotlinInlineClassSignatureRegex = new(
        @"\binline\s+class\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex KotlinInlineFunctionSignatureRegex = new(
        @"\binline\b(?:(?:\s+(?:suspend|infix|operator|tailrec|external|expect|actual|abstract|override|open|final)))*\s+fun\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex KotlinReifiedTypeParameterSignatureRegex = new(
        @"<[^>\r\n]*\breified\b[^>\r\n]*>",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static string? ResolveLanguageSubKind(string lang, string kind, string signature, string matchLine)
    {
        if (lang != "kotlin")
            return null;

        var metadataSource = signature + "\n" + matchLine;
        if (kind == "class")
        {
            if (KotlinValueClassSignatureRegex.IsMatch(metadataSource))
                return "kotlin_value_class";
            if (KotlinInlineClassSignatureRegex.IsMatch(metadataSource))
                return "kotlin_inline_class";
        }

        if (kind == "function" && KotlinInlineFunctionSignatureRegex.IsMatch(signature))
        {
            return KotlinReifiedTypeParameterSignatureRegex.IsMatch(signature)
                ? "kotlin_inline_reified_function"
                : "kotlin_inline_function";
        }

        return null;
    }

    private static bool TryFindKotlinScalaExpressionBodyEndLine(string line, int startColumn)
    {
        var parenDepth = 0;
        var bracketDepth = 0;
        var braceDepth = 0;
        var inBlockComment = false;
        var inString = false;
        var inChar = false;

        for (var i = Math.Max(0, startColumn); i < line.Length; i++)
        {
            var c = line[i];

            if (inBlockComment)
            {
                if (c == '*' && i + 1 < line.Length && line[i + 1] == '/')
                {
                    inBlockComment = false;
                    i++;
                }
                continue;
            }

            if (inString)
            {
                if (c == '\\' && i + 1 < line.Length)
                {
                    i++;
                    continue;
                }

                if (c == '"')
                    inString = false;
                continue;
            }

            if (inChar)
            {
                if (c == '\\' && i + 1 < line.Length)
                {
                    i++;
                    continue;
                }

                if (c == '\'')
                    inChar = false;
                continue;
            }

            if (c == '/' && i + 1 < line.Length)
            {
                if (line[i + 1] == '/')
                    break;

                if (line[i + 1] == '*')
                {
                    inBlockComment = true;
                    i++;
                    continue;
                }
            }

            if (c == '"')
            {
                inString = true;
                continue;
            }

            if (c == '\'')
            {
                inChar = true;
                continue;
            }

            if (c == '(')
            {
                parenDepth++;
                continue;
            }

            if (c == ')' && parenDepth > 0)
            {
                parenDepth--;
                continue;
            }

            if (c == '[')
            {
                bracketDepth++;
                continue;
            }

            if (c == ']' && bracketDepth > 0)
            {
                bracketDepth--;
                continue;
            }

            if (c == '{')
            {
                braceDepth++;
                continue;
            }

            if (c == '}' && braceDepth > 0)
            {
                braceDepth--;
                continue;
            }

            if (c != '=' || parenDepth > 0 || bracketDepth > 0 || braceDepth > 0)
                continue;

            if (i + 1 < line.Length && (line[i + 1] == '=' || line[i + 1] == '>'))
                continue;

            var next = i + 1;
            while (next < line.Length)
            {
                if (char.IsWhiteSpace(line[next]))
                {
                    next++;
                    continue;
                }

                if (line[next] == '/' && next + 1 < line.Length)
                {
                    if (line[next + 1] == '/')
                        return false;

                    if (line[next + 1] == '*')
                    {
                        var commentEnd = line.IndexOf("*/", next + 2, StringComparison.Ordinal);
                        if (commentEnd < 0)
                            return false;

                        next = commentEnd + 2;
                        continue;
                    }
                }

                return line[next] != '{';
            }

            return false;
        }

        return false;
    }

    private static int? TryFindScalaBracelessClassEndLine(string[] lines, int startIndex, int startColumn)
    {
        var parenDepth = 0;
        var bracketDepth = 0;
        var inBlockComment = false;
        var inString = false;
        var inChar = false;

        for (var lineIndex = startIndex; lineIndex < lines.Length; lineIndex++)
        {
            var line = lines[lineIndex];
            var scanStart = lineIndex == startIndex ? Math.Max(0, startColumn) : 0;

            for (var i = scanStart; i < line.Length; i++)
            {
                var c = line[i];

                if (inBlockComment)
                {
                    if (c == '*' && i + 1 < line.Length && line[i + 1] == '/')
                    {
                        inBlockComment = false;
                        i++;
                    }
                    continue;
                }

                if (inString)
                {
                    if (c == '\\' && i + 1 < line.Length)
                    {
                        i++;
                        continue;
                    }

                    if (c == '"')
                        inString = false;
                    continue;
                }

                if (inChar)
                {
                    if (c == '\\' && i + 1 < line.Length)
                    {
                        i++;
                        continue;
                    }

                    if (c == '\'')
                        inChar = false;
                    continue;
                }

                if (c == '/' && i + 1 < line.Length)
                {
                    if (line[i + 1] == '/')
                        break;

                    if (line[i + 1] == '*')
                    {
                        inBlockComment = true;
                        i++;
                        continue;
                    }
                }

                if (c == '"')
                {
                    inString = true;
                    continue;
                }

                if (c == '\'')
                {
                    inChar = true;
                    continue;
                }

                if (c == '(')
                {
                    parenDepth++;
                    continue;
                }

                if (c == ')' && parenDepth > 0)
                {
                    parenDepth--;
                    continue;
                }

                if (c == '[')
                {
                    bracketDepth++;
                    continue;
                }

                if (c == ']' && bracketDepth > 0)
                {
                    bracketDepth--;
                    continue;
                }

                if (c == '{' && parenDepth == 0 && bracketDepth == 0)
                    return null;
            }

            if (parenDepth != 0 || bracketDepth != 0 || inBlockComment || inString || inChar)
                continue;

            var trimmed = line.TrimEnd();
            if (trimmed.EndsWith("extends", StringComparison.Ordinal)
                || trimmed.EndsWith("with", StringComparison.Ordinal)
                || trimmed.EndsWith("derives", StringComparison.Ordinal)
                || trimmed.EndsWith(':'))
            {
                continue;
            }

            var nextLine = TryGetNextScalaHeaderLine(lines, lineIndex + 1);
            if (nextLine is null)
                return lineIndex;

            if (nextLine.StartsWith("extends ", StringComparison.Ordinal)
                || nextLine.StartsWith("with ", StringComparison.Ordinal)
                || nextLine.StartsWith("derives ", StringComparison.Ordinal))
            {
                continue;
            }

            if (nextLine.StartsWith('{'))
                return null;

            return lineIndex;
        }

        return null;
    }

    private static string? TryGetNextScalaHeaderLine(string[] lines, int startIndex)
    {
        for (var i = startIndex; i < lines.Length; i++)
        {
            var nextLine = lines[i].TrimStart();
            if (nextLine.Length == 0 || nextLine.StartsWith("//", StringComparison.Ordinal))
                continue;

            return nextLine;
        }

        return null;
    }

}
