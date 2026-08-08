using System.Text.RegularExpressions;
using Regex = CodeIndex.Indexer.BoundedRegex;
using CodeIndex.Models;

namespace CodeIndex.Indexer;

internal static partial class TypeScriptReferenceExtractor
{
    private static void EmitNamespaceAliasQualifiedReferences(
        IReadOnlyList<string> preparedLines,
        int lineIndex,
        string preparedLine,
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn,
        IReadOnlyList<NamespaceAliasBinding> namespaceAliases)
    {
        if (namespaceAliases.Count == 0
            || preparedLine.IndexOf('.') < 0
            || IsImportExportAliasLine(preparedLines, lineIndex, preparedLine))
            return;

        foreach (var binding in namespaceAliases)
        {
            if (lineNumber <= binding.BindingLine
                || (binding.EndLine is int endLine && lineNumber > endLine)
                || (binding.ShadowLine is int shadowLine && lineNumber >= shadowLine)
                || IsInsideScopedShadow(binding.ScopedShadowRanges, lineNumber))
            {
                continue;
            }

            foreach (var matchIndex in EnumerateNamespaceAliasQualifiedReferenceStarts(preparedLine, binding.Alias))
            {
                ReferenceExtractor.AddReference(
                    references,
                    seen,
                    fileId,
                    binding.ModuleSpecifier,
                    matchIndex,
                    "reference",
                    context,
                    lineNumber,
                    resolveContainerForColumn(matchIndex));
            }
        }
    }

    private static IEnumerable<int> EnumerateNamespaceAliasQualifiedReferenceStarts(string text, string alias)
    {
        if (string.IsNullOrEmpty(alias))
            yield break;

        var searchIndex = 0;
        while (searchIndex < text.Length)
        {
            var aliasIndex = text.IndexOf(alias, searchIndex, StringComparison.Ordinal);
            if (aliasIndex < 0)
                yield break;

            searchIndex = aliasIndex + Math.Max(1, alias.Length);
            if (aliasIndex > 0 && IsTypeScriptIdentifierPart(text[aliasIndex - 1]))
                continue;

            var afterAlias = aliasIndex + alias.Length;
            if (afterAlias < text.Length && IsTypeScriptIdentifierPart(text[afterAlias]))
                continue;

            var dotIndex = SkipWhitespace(text, afterAlias);
            if (dotIndex >= text.Length || text[dotIndex] != '.')
                continue;

            var memberIndex = SkipWhitespace(text, dotIndex + 1);
            if (memberIndex >= text.Length || !IsTypeScriptNamespaceMemberStart(text[memberIndex]))
                continue;

            yield return aliasIndex;
        }
    }

    private static bool IsTypeScriptNamespaceMemberStart(char ch) =>
        ch == '_' || ch == '$' || ch is >= 'A' and <= 'Z' || ch is >= 'a' and <= 'z';

    private static IReadOnlyDictionary<string, List<int>> BuildLocalDeclarationLinesByName(IReadOnlyList<string> preparedLines)
    {
        Dictionary<string, List<int>>? linesByName = null;
        for (var index = 0; index < preparedLines.Count; index++)
        {
            var line = preparedLines[index];
            if (NamespaceImportExportRegex.IsMatch(line) || DynamicImportNamespaceRegex.IsMatch(line))
                continue;

            var match = LocalDeclarationRegex.Match(line);
            if (!match.Success)
                continue;

            var name = match.Groups["name"].Value;
            linesByName ??= new Dictionary<string, List<int>>(16, StringComparer.Ordinal);
            if (!linesByName.TryGetValue(name, out var lines))
            {
                lines = new List<int>(1);
                linesByName[name] = lines;
            }

            lines.Add(index + 1);
        }

        return linesByName ?? EmptyLocalDeclarationLinesByName;
    }

    private static int? FindShadowLine(
        IReadOnlyDictionary<string, List<int>> localDeclarationLinesByName,
        string alias,
        int bindingLine)
    {
        if (!localDeclarationLinesByName.TryGetValue(alias, out var declarationLines))
            return null;

        foreach (var line in declarationLines)
        {
            if (line > bindingLine)
                return line;
        }

        return null;
    }

    private static int[] BuildBraceDepthsBeforeLine(IReadOnlyList<string> preparedLines)
    {
        var depths = new int[preparedLines.Count];
        var depth = 0;
        for (var index = 0; index < preparedLines.Count; index++)
        {
            depths[index] = depth;
            foreach (var ch in preparedLines[index])
            {
                if (ch == '{')
                    depth++;
                else if (ch == '}' && depth > 0)
                    depth--;
            }
        }

        return depths;
    }

    private static int? FindDynamicImportAliasEndLine(
        IReadOnlyList<string> preparedLines,
        IReadOnlyList<int> braceDepths,
        int bindingLineIndex)
    {
        var bindingDepth = braceDepths[bindingLineIndex];
        if (bindingDepth <= 0)
            return null;

        for (var index = bindingLineIndex + 1; index < preparedLines.Count; index++)
        {
            if (braceDepths[index] < bindingDepth)
                return index;
        }

        return preparedLines.Count;
    }

    private static IReadOnlyList<LineRange> BuildParameterShadowRanges(
        IReadOnlyList<string> preparedLines,
        int[] braceDepths,
        string alias)
    {
        List<LineRange>? ranges = null;
        for (var index = 0; index < preparedLines.Count; index++)
        {
            if (!TryGetSingleLineCallableParameters(preparedLines[index], out var parameters)
                || !ParameterListDeclaresName(parameters, alias))
            {
                continue;
            }

            var endLine = FindBlockEndLine(preparedLines, braceDepths, index);
            if (endLine >= index + 1)
                (ranges ??= new List<LineRange>(2)).Add(new LineRange(index + 1, endLine));
        }

        return ranges is null ? Array.Empty<LineRange>() : ranges;
    }

    private static IReadOnlyList<LineRange> GetParameterShadowRanges(
        IReadOnlyList<string> preparedLines,
        int[] braceDepths,
        Dictionary<string, IReadOnlyList<LineRange>> parameterShadowRangesByAlias,
        string alias)
    {
        if (parameterShadowRangesByAlias.TryGetValue(alias, out var ranges))
            return ranges;

        ranges = BuildParameterShadowRanges(preparedLines, braceDepths, alias);
        parameterShadowRangesByAlias[alias] = ranges;
        return ranges;
    }

    private static bool TryGetSingleLineCallableParameters(string line, out string parameters)
    {
        parameters = string.Empty;
        var trimmed = line.TrimStart();
        if (trimmed.StartsWith("if ", StringComparison.Ordinal)
            || trimmed.StartsWith("if(", StringComparison.Ordinal)
            || trimmed.StartsWith("for ", StringComparison.Ordinal)
            || trimmed.StartsWith("for(", StringComparison.Ordinal)
            || trimmed.StartsWith("while ", StringComparison.Ordinal)
            || trimmed.StartsWith("while(", StringComparison.Ordinal)
            || trimmed.StartsWith("switch ", StringComparison.Ordinal)
            || trimmed.StartsWith("switch(", StringComparison.Ordinal)
            || trimmed.Contains("=>", StringComparison.Ordinal))
        {
            return false;
        }

        var openParen = TypedLanguageReferenceExtractor.FindTopLevelChar(line, '(');
        if (openParen < 0)
            return false;

        var closeParen = ReferenceExtractor.FindMatchingChar(line, openParen, '(', ')');
        if (closeParen <= openParen)
            return false;

        var afterParameters = line[(closeParen + 1)..];
        if (!afterParameters.Contains('{', StringComparison.Ordinal))
            return false;

        parameters = line.Substring(openParen + 1, closeParen - openParen - 1);
        return trimmed.StartsWith("function ", StringComparison.Ordinal)
               || trimmed.StartsWith("export function ", StringComparison.Ordinal)
               || trimmed.StartsWith("export async function ", StringComparison.Ordinal)
               || trimmed.StartsWith("async function ", StringComparison.Ordinal)
               || IsLikelyMethodDeclarationPrefix(line[..openParen]);
    }

    private static bool IsLikelyMethodDeclarationPrefix(string prefix)
    {
        var trimmed = prefix.Trim();
        if (trimmed.Length == 0 || trimmed.Contains('='))
            return false;

        var lastSpace = trimmed.LastIndexOf(' ');
        var name = lastSpace >= 0 ? trimmed[(lastSpace + 1)..] : trimmed;
        return IsTypeScriptIdentifier(name);
    }

    private static bool ParameterListDeclaresName(string parameters, string alias)
    {
        var remaining = parameters.AsSpan();
        var aliasSpan = alias.AsSpan();
        while (true)
        {
            var commaIndex = remaining.IndexOf(',');
            var item = commaIndex < 0 ? remaining : remaining[..commaIndex];
            item = item.TrimStart();
            if (item.StartsWith("...".AsSpan(), StringComparison.Ordinal))
                item = item[3..].TrimStart();

            if (item.StartsWith(aliasSpan, StringComparison.Ordinal))
            {
                var after = item.Length == alias.Length ? '\0' : item[alias.Length];
                if (after is '\0' or ':' or '?' or '=' || char.IsWhiteSpace(after))
                    return true;
            }

            if (commaIndex < 0)
                break;

            remaining = remaining[(commaIndex + 1)..];
        }

        return false;
    }

    private static int FindBlockEndLine(IReadOnlyList<string> preparedLines, IReadOnlyList<int> braceDepths, int startLineIndex)
    {
        var startDepth = braceDepths[startLineIndex];
        for (var index = startLineIndex + 1; index < preparedLines.Count; index++)
        {
            if (braceDepths[index] <= startDepth)
                return index;
        }

        return preparedLines.Count;
    }

    private static bool IsInsideScopedShadow(IReadOnlyList<LineRange> ranges, int lineNumber)
    {
        foreach (var range in ranges)
        {
            if (lineNumber >= range.StartLine && lineNumber <= range.EndLine)
                return true;
        }

        return false;
    }

}
