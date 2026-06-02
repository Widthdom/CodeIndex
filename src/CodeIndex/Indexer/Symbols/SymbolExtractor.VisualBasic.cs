using System.Text;
using System.Text.RegularExpressions;
using Regex = CodeIndex.Indexer.BoundedRegex;
using System.Runtime.CompilerServices;
using CodeIndex.Models;

namespace CodeIndex.Indexer;

public static partial class SymbolExtractor
{
    private static void ExtractVisualBasicEnumMembers(long fileId, string[] lines, List<SymbolRecord> symbols)
    {
        var enumDeclarations = symbols
            .Where(symbol =>
                symbol.FileId == fileId
                && symbol.Kind == "enum"
                && symbol.BodyStartLine != null
                && symbol.BodyEndLine != null)
            .OrderBy(symbol => symbol.StartLine)
            .ThenByDescending(symbol => symbol.EndLine)
            .ToList();

        foreach (var enumSymbol in enumDeclarations)
        {
            var bodyStartLineIndex = enumSymbol.BodyStartLine!.Value - 1;
            var bodyEndLineIndex = enumSymbol.BodyEndLine!.Value - 1;
            var inAttributeBlock = false;
            var inInitializer = false;
            var initializerParenDepth = 0;

            for (var lineIndex = bodyStartLineIndex; lineIndex <= bodyEndLineIndex && lineIndex < lines.Length; lineIndex++)
            {
                var trimmed = lines[lineIndex].Trim();
                if (trimmed.Length == 0
                    || trimmed.StartsWith("'", StringComparison.Ordinal)
                    || trimmed.StartsWith("Rem ", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (trimmed.StartsWith("End Enum", StringComparison.OrdinalIgnoreCase))
                    break;

                if (inAttributeBlock)
                {
                    var attributeCloseIndex = trimmed.IndexOf('>');
                    if (attributeCloseIndex < 0)
                        continue;

                    trimmed = trimmed[(attributeCloseIndex + 1)..].TrimStart();
                    inAttributeBlock = false;
                    if (trimmed.Length == 0)
                        continue;
                }

                while (trimmed.StartsWith("<", StringComparison.Ordinal))
                {
                    var attributeCloseIndex = trimmed.IndexOf('>');
                    if (attributeCloseIndex < 0)
                    {
                        inAttributeBlock = true;
                        trimmed = string.Empty;
                        break;
                    }

                    trimmed = trimmed[(attributeCloseIndex + 1)..].TrimStart();
                    if (trimmed.Length == 0)
                        break;
                }

                if (trimmed.Length == 0)
                    continue;

                if (inInitializer)
                {
                    UpdateVisualBasicEnumInitializerState(trimmed, ref initializerParenDepth, ref inInitializer);
                    continue;
                }

                if (!TryExtractVisualBasicEnumMemberName(trimmed, out var name))
                    continue;

                symbols.Add(new SymbolRecord
                {
                    FileId = fileId,
                    Kind = "enum",
                    Name = name,
                    Line = lineIndex + 1,
                    StartLine = lineIndex + 1,
                    EndLine = lineIndex + 1,
                    Signature = trimmed,
                });

                UpdateVisualBasicEnumInitializerState(trimmed, ref initializerParenDepth, ref inInitializer);
            }
        }
    }

    private static bool TryExtractVisualBasicEnumMemberName(string line, out string name)
    {
        var match = VisualBasicEnumMemberRegex.Match(line);
        if (!match.Success)
        {
            name = string.Empty;
            return false;
        }

        name = match.Groups["name"].Value;
        if (string.IsNullOrWhiteSpace(name))
            return false;

        if (name.Length >= 2 && name[0] == '[' && name[^1] == ']')
            name = name[1..^1];

        return true;
    }

    private static void UpdateVisualBasicEnumInitializerState(string line, ref int parenDepth, ref bool inInitializer)
    {
        var code = StripVisualBasicComment(line);
        foreach (var ch in code)
        {
            if (ch == '(')
                parenDepth++;
            else if (ch == ')' && parenDepth > 0)
                parenDepth--;
        }

        var trimmed = code.TrimEnd();
        inInitializer = parenDepth > 0
            || trimmed.EndsWith("_", StringComparison.Ordinal)
            || trimmed.EndsWith("=", StringComparison.Ordinal)
            || trimmed.EndsWith("+", StringComparison.Ordinal)
            || trimmed.EndsWith("-", StringComparison.Ordinal)
            || trimmed.EndsWith("*", StringComparison.Ordinal)
            || trimmed.EndsWith("/", StringComparison.Ordinal)
            || trimmed.EndsWith("&", StringComparison.Ordinal)
            || trimmed.EndsWith(",", StringComparison.Ordinal)
            || trimmed.EndsWith(".", StringComparison.Ordinal);
    }

    private static string StripVisualBasicComment(string line)
    {
        var inString = false;
        for (var i = 0; i < line.Length; i++)
        {
            var ch = line[i];
            if (ch == '"')
            {
                if (inString && i + 1 < line.Length && line[i + 1] == '"')
                {
                    i++;
                    continue;
                }

                inString = !inString;
                continue;
            }

            if (!inString && ch == '\'')
                return line[..i];
        }

        return line;
    }

    private static (int EndLine, int? BodyStartLine, int? BodyEndLine) FindVisualBasicRange(string[] lines, int startIndex)
    {
        int depth = 0;
        int? bodyStartLine = null;

        for (int i = startIndex; i < lines.Length; i++)
        {
            var trimmed = lines[i].Trim();
            if (trimmed.Length == 0)
                continue;

            if (VisualBasicContainerStartRegex.IsMatch(trimmed))
            {
                depth++;
                if (i > startIndex && bodyStartLine == null)
                    bodyStartLine = i + 1;
                continue;
            }

            if (VisualBasicContainerEndRegex.IsMatch(trimmed))
            {
                depth--;
                if (depth <= 0)
                {
                    if (bodyStartLine == null || bodyStartLine > i + 1)
                        return (i + 1, null, null);

                    return (i + 1, bodyStartLine, i + 1);
                }
            }
            else if (i > startIndex && bodyStartLine == null)
            {
                bodyStartLine = i + 1;
            }
        }

        return bodyStartLine == null
            ? (startIndex + 1, null, null)
            : (lines.Length, bodyStartLine, lines.Length);
    }

    private static readonly Regex VisualBasicContainerStartRegex = new(@$"^(?:Namespace\b|(?:(?:{VbTypeModifierPattern})\s+)*(?:(?:{VbVisibilityPattern})\s+)?(?:(?:{VbTypeModifierPattern})\s+)*(?:Class|Module|Structure|Interface|Enum)\b|(?:(?:{VbOperatorModifierPattern})\s+)*(?:(?:{VbVisibilityPattern})\s+)?(?:(?:{VbOperatorModifierPattern})\s+)*Operator\b)", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex VisualBasicContainerEndRegex = new(@"^End\s+(?:Namespace|Class|Module|Structure|Interface|Enum|Operator)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex VisualBasicEnumMemberRegex = new(@"^\s*(?<name>(?:\[[^\]\r\n]+\]|\w+))\s*(?:=\s*[^'\r\n]+)?\s*(?:'|$)", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

}
