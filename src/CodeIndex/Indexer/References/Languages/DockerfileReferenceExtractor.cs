using System.Text.RegularExpressions;
using Regex = CodeIndex.Indexer.BoundedRegex;
using CodeIndex.Models;

namespace CodeIndex.Indexer;

internal static class DockerfileReferenceExtractor
{
    private static readonly Regex StageReferenceRegex = new(
        @"^\s*FROM\s+(?:--platform=\S+\s+)?(?<name>[A-Za-z0-9_.-]+)\s+AS\s+[A-Za-z0-9_.-]+(?:\s+#.*)?\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex CopyFromReferenceRegex = new(
        @"^\s*(?:ONBUILD\s+)?(?:COPY|ADD)\b.*?--from=[""']?(?<name>[A-Za-z0-9_.-]+)(?![:/@])\b[""']?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex RunMountFromReferenceRegex = new(
        @"(?:^|,)from=[""']?(?<name>[A-Za-z0-9_.-]+)(?![:/@])\b[""']?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex UnbracedVariableReferenceRegex = new(
        @"(?<![\$\\])\$(?<name>[A-Za-z_][A-Za-z0-9_]*)",
        RegexOptions.Compiled);

    public static HashSet<string>? BuildStageNames(string language, IReadOnlyList<SymbolRecord> symbols)
    {
        if (language != "dockerfile")
            return null;

        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var symbol in symbols)
        {
            if (symbol.Kind != "function" || string.IsNullOrWhiteSpace(symbol.Name))
                continue;

            names.Add(symbol.Name);
        }

        return names;
    }

    public static HashSet<string>? BuildVariableNames(string language, IReadOnlyList<SymbolRecord> symbols)
    {
        if (language != "dockerfile")
            return null;

        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var symbol in symbols)
        {
            if (symbol.Kind != "property" || string.IsNullOrWhiteSpace(symbol.Name))
                continue;

            names.Add(symbol.Name);
        }

        return names;
    }

    public static void EmitStageReferences(
        string preparedLine,
        string originalLine,
        string context,
        int lineNumber,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        HashSet<string>? stageNames,
        SymbolRecord? container)
    {
        if (stageNames == null || stageNames.Count == 0)
            return;

        var fromMatch = StageReferenceRegex.Match(originalLine);
        if (fromMatch.Success)
        {
            var name = fromMatch.Groups["name"].Value;
            if (stageNames.Contains(name))
            {
                ReferenceExtractor.AddReference(
                    references,
                    seen,
                    fileId,
                    name,
                    fromMatch.Groups["name"].Index,
                    "call",
                    context,
                    lineNumber,
                    container);
            }
        }

        foreach (Match match in CopyFromReferenceRegex.Matches(originalLine))
        {
            var name = match.Groups["name"].Value;
            if (!stageNames.Contains(name))
                continue;

            ReferenceExtractor.AddReference(
                references,
                seen,
                fileId,
                name,
                match.Groups["name"].Index,
                "call",
                context,
                lineNumber,
                container);
        }

        EmitRunMountReferences(
            originalLine,
            context,
            lineNumber,
            references,
            seen,
            fileId,
            stageNames,
            container);
    }

    private static void EmitRunMountReferences(
        string line,
        string context,
        int lineNumber,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        HashSet<string> stageNames,
        SymbolRecord? container)
    {
        if (!TryGetRunOptionsStart(line, out var index))
            return;

        while (index < line.Length)
        {
            index = SkipWhitespace(line, index);
            if (index >= line.Length || !StartsWith(line, index, "--"))
                return;

            var optionStart = index;
            var optionEnd = ScanOptionToken(line, optionStart);
            if (optionEnd <= optionStart)
                return;

            var option = line.Substring(optionStart, optionEnd - optionStart);
            if (option.StartsWith("--mount=", StringComparison.OrdinalIgnoreCase))
            {
                foreach (Match match in RunMountFromReferenceRegex.Matches(option["--mount=".Length..]))
                {
                    var name = match.Groups["name"].Value;
                    if (!stageNames.Contains(name))
                        continue;

                    ReferenceExtractor.AddReference(
                        references,
                        seen,
                        fileId,
                        name,
                        optionStart + "--mount=".Length + match.Groups["name"].Index,
                        "call",
                        context,
                        lineNumber,
                        container);
                }
            }

            index = optionEnd;
        }
    }

    private static bool TryGetRunOptionsStart(string line, out int index)
    {
        index = SkipWhitespace(line, 0);
        if (StartsWithKeyword(line, index, "ONBUILD"))
        {
            index = SkipWhitespace(line, index + "ONBUILD".Length);
        }

        if (!StartsWith(line, index, "RUN"))
            return false;

        var afterRun = index + "RUN".Length;
        if (afterRun < line.Length && !char.IsWhiteSpace(line[afterRun]))
            return false;

        index = afterRun;
        return true;
    }

    private static int ScanOptionToken(string line, int index)
    {
        var quote = '\0';
        while (index < line.Length)
        {
            var c = line[index];
            if (quote != '\0')
            {
                if (c == '\\' && index + 1 < line.Length)
                {
                    index += 2;
                    continue;
                }

                if (c == quote)
                    quote = '\0';

                index++;
                continue;
            }

            if (c is '"' or '\'')
            {
                quote = c;
                index++;
                continue;
            }

            if (char.IsWhiteSpace(c))
                break;

            index++;
        }

        return index;
    }

    private static int SkipWhitespace(string text, int index)
    {
        while (index < text.Length && char.IsWhiteSpace(text[index]))
            index++;

        return index;
    }

    private static bool StartsWith(string text, int index, string value)
        => index >= 0
           && index + value.Length <= text.Length
           && string.Compare(text, index, value, 0, value.Length, StringComparison.OrdinalIgnoreCase) == 0;

    private static bool StartsWithKeyword(string text, int index, string value)
        => StartsWith(text, index, value)
           && (index + value.Length >= text.Length || char.IsWhiteSpace(text[index + value.Length]));

    public static void EmitVariableReferences(
        string preparedLine,
        string context,
        int lineNumber,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        HashSet<string>? variableNames,
        SymbolRecord? container)
    {
        if (variableNames == null || variableNames.Count == 0)
            return;

        EmitBracedVariableReferences(
            preparedLine,
            context,
            lineNumber,
            references,
            seen,
            fileId,
            variableNames,
            container);

        foreach (Match match in UnbracedVariableReferenceRegex.Matches(preparedLine))
        {
            var name = match.Groups["name"].Value;
            if (!variableNames.Contains(name))
                continue;

            ReferenceExtractor.AddReference(
                references,
                seen,
                fileId,
                name,
                match.Groups["name"].Index,
                "reference",
                context,
                lineNumber,
                container);
        }
    }

    private static void EmitBracedVariableReferences(
        string line,
        string context,
        int lineNumber,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        HashSet<string> variableNames,
        SymbolRecord? container)
    {
        for (var index = 0; index < line.Length - 2; index++)
        {
            if (line[index] != '$' || line[index + 1] != '{')
                continue;

            if (IsEscapedDollar(line, index))
            {
                if (TryScanBracedVariable(line, index, out _, out _, out var escapedEnd))
                    index = escapedEnd;

                continue;
            }

            if (!TryScanBracedVariable(line, index, out var nameStart, out var nameLength, out _))
                continue;

            var name = line.Substring(nameStart, nameLength);
            if (!variableNames.Contains(name))
                continue;

            ReferenceExtractor.AddReference(
                references,
                seen,
                fileId,
                name,
                nameStart,
                "reference",
                context,
                lineNumber,
                container);
        }
    }

    private static bool TryScanBracedVariable(
        string line,
        int start,
        out int nameStart,
        out int nameLength,
        out int end)
    {
        nameStart = start + 2;
        nameLength = 0;
        end = -1;

        if (start < 0
            || start + 2 >= line.Length
            || line[start] != '$'
            || line[start + 1] != '{'
            || !IsVariableNameStart(line[nameStart]))
        {
            return false;
        }

        var index = nameStart + 1;
        while (index < line.Length && IsVariableNamePart(line[index]))
            index++;

        nameLength = index - nameStart;
        if (index >= line.Length)
            return false;

        if (line[index] == '}')
        {
            end = index;
            return true;
        }

        var operatorIndex = index;
        if (line[operatorIndex] == ':')
            operatorIndex++;

        if (operatorIndex >= line.Length || !IsParameterExpansionOperator(line[operatorIndex]))
            return false;

        return TryScanBracedVariableBody(line, operatorIndex + 1, out end);
    }

    private static bool TryScanBracedVariableBody(string line, int index, out int end)
    {
        var nestedDepth = 0;
        while (index < line.Length)
        {
            if (line[index] == '$' && index + 1 < line.Length && line[index + 1] == '{' && !IsEscapedDollar(line, index))
            {
                nestedDepth++;
                index += 2;
                continue;
            }

            if (line[index] == '}')
            {
                if (nestedDepth == 0)
                {
                    end = index;
                    return true;
                }

                nestedDepth--;
            }

            index++;
        }

        end = -1;
        return false;
    }

    private static bool IsEscapedDollar(string line, int index)
        => index > 0 && (line[index - 1] == '\\' || line[index - 1] == '$');

    private static bool IsVariableNameStart(char value)
        => value == '_' || char.IsAsciiLetter(value);

    private static bool IsVariableNamePart(char value)
        => value == '_' || char.IsAsciiLetterOrDigit(value);

    private static bool IsParameterExpansionOperator(char value)
        => value is '-' or '+' or '?' or '=';
}
