using System.Text.RegularExpressions;
using Regex = CodeIndex.Indexer.BoundedRegex;
using CodeIndex.Models;

namespace CodeIndex.Indexer;

internal static partial class TypeScriptReferenceExtractor
{
    internal readonly record struct LineRange(int StartLine, int EndLine);
    internal readonly record struct TypeAliasBinding(
        string Alias,
        string Target,
        int BindingLine,
        int? EndLine,
        int BraceDepth,
        IReadOnlyList<LineRange> ShadowRanges,
        IReadOnlySet<string> TypeParameters);
    internal sealed record NamespaceAliasBinding(
        string Alias,
        string ModuleSpecifier,
        int BindingLine,
        int? ShadowLine,
        int? EndLine,
        IReadOnlyList<LineRange> ScopedShadowRanges);

    private static readonly string[] DeclarationKeywords = ["const", "let", "var"];
    private static readonly string[] TypeOperatorKeywords = ["satisfies", "instanceof"];
    private static readonly string[] TypeAliasTargetStopKeywords = ["extends", "implements"];
    private static readonly string[] LiteralKeywords = ["true", "false", "null", "undefined"];
    private static readonly IReadOnlySet<string> EmptyTypeParameters = new HashSet<string>(StringComparer.Ordinal);
    private static readonly IReadOnlyDictionary<string, List<int>> EmptyLocalDeclarationLinesByName =
        new Dictionary<string, List<int>>(StringComparer.Ordinal);
    private static readonly Regex NamespaceImportExportRegex = new(
        @"^\s*(?:import|export)\s+(?:type\s+)?\*\s*as\s*(?<alias>[A-Za-z_$][\w$]*)\s+from\s*[""'](?<module>[^""']+)[""']",
        RegexOptions.Compiled);
    private static readonly Regex DynamicImportNamespaceRegex = new(
        @"^\s*(?:const|let|var)\s+(?<alias>[A-Za-z_$][\w$]*)\s*=\s*(?:await\s+)?import\s*\(\s*[""'](?<module>[^""']+)[""']\s*\)",
        RegexOptions.Compiled);
    private static readonly Regex NamedImportRegex = new(
        @"^\s*import\s+(?:type\s+)?\{(?<body>[^}]*)\}\s+from\s*[""'](?<module>[^""']+)[""']",
        RegexOptions.Compiled);
    private static readonly Regex LocalDeclarationRegex = new(
        @"^\s*(?:(?:const|let|var)\s+|(?:export\s+)?(?:default\s+)?(?:async\s+)?function\s+|(?:export\s+)?(?:abstract\s+)?class\s+|(?:export\s+)?interface\s+|(?:export\s+)?type\s+)(?<name>[A-Za-z_$][\w$]*)\b",
        RegexOptions.Compiled);
    private static readonly Regex TypeDeclarationShadowRegex = new(
        @"^\s*(?:export\s+)?(?:abstract\s+)?(?:class|interface|enum)\s+(?<name>[A-Za-z_$][\w$]*)\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly HashSet<string> MappedTypeClauseIgnoredSegments = new(StringComparer.Ordinal)
    {
        "as",
        "extends",
        "in",
        "infer",
        "keyof",
        "readonly",
    };
    private static readonly Regex TypeAliasRegex = new(
        @"^\s*(?:export\s+)?type\s+(?<alias>[A-Za-z_$][\w$]*)(?<params>\s*<[^;]*>)?\s*=\s*(?<target>[^;]+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static IReadOnlyList<NamespaceAliasBinding> BuildNamespaceAliasBindings(
        IReadOnlyList<string> originalLines,
        IReadOnlyList<string> preparedLines)
    {
        List<NamespaceAliasBinding>? bindings = null;
        int[]? braceDepths = null;
        IReadOnlyDictionary<string, List<int>>? localDeclarationLinesByName = null;
        Dictionary<string, IReadOnlyList<LineRange>>? parameterShadowRangesByAlias = null;
        for (var index = 0; index < originalLines.Count; index++)
        {
            var line = originalLines[index];
            var match = NamespaceImportExportRegex.Match(line);
            if (match.Success)
            {
                AddNamespaceAliasBinding(
                    bindings ??= [],
                    preparedLines,
                    match.Groups["alias"].Value,
                    match.Groups["module"].Value,
                    index + 1,
                    null,
                    braceDepths ??= BuildBraceDepthsBeforeLine(preparedLines),
                    localDeclarationLinesByName ??= BuildLocalDeclarationLinesByName(preparedLines),
                    parameterShadowRangesByAlias ??= new Dictionary<string, IReadOnlyList<LineRange>>(4, StringComparer.Ordinal));
                continue;
            }

            match = DynamicImportNamespaceRegex.Match(line);
            if (match.Success)
            {
                var bindingLine = index + 1;
                var sharedBraceDepths = braceDepths ??= BuildBraceDepthsBeforeLine(preparedLines);
                AddNamespaceAliasBinding(
                    bindings ??= [],
                    preparedLines,
                    match.Groups["alias"].Value,
                    match.Groups["module"].Value,
                    bindingLine,
                    FindDynamicImportAliasEndLine(preparedLines, sharedBraceDepths, index),
                    sharedBraceDepths,
                    localDeclarationLinesByName ??= BuildLocalDeclarationLinesByName(preparedLines),
                    parameterShadowRangesByAlias ??= new Dictionary<string, IReadOnlyList<LineRange>>(4, StringComparer.Ordinal));
                continue;
            }

            match = NamedImportRegex.Match(line);
            if (!match.Success)
                continue;

            AddNamedImportExportAliasBindings(
                bindings ??= new List<NamespaceAliasBinding>(4),
                preparedLines,
                match.Groups["body"].Value,
                match.Groups["module"].Value,
                index + 1,
                ref braceDepths,
                ref localDeclarationLinesByName,
                ref parameterShadowRangesByAlias);
        }

        return bindings is null ? Array.Empty<NamespaceAliasBinding>() : bindings;
    }

    private static void AddNamespaceAliasBinding(
        List<NamespaceAliasBinding> bindings,
        IReadOnlyList<string> preparedLines,
        string alias,
        string module,
        int bindingLine,
        int? endLine,
        int[] braceDepths,
        IReadOnlyDictionary<string, List<int>> localDeclarationLinesByName,
        Dictionary<string, IReadOnlyList<LineRange>> parameterShadowRangesByAlias)
    {
        if (string.IsNullOrWhiteSpace(alias) || string.IsNullOrWhiteSpace(module))
            return;

        var shadowLine = FindShadowLine(localDeclarationLinesByName, alias, bindingLine);
        var scopedShadowRanges = GetParameterShadowRanges(preparedLines, braceDepths, parameterShadowRangesByAlias, alias);
        bindings.Add(new NamespaceAliasBinding(alias, module, bindingLine, shadowLine, endLine, scopedShadowRanges));
    }

    private static void AddNamedImportExportAliasBindings(
        List<NamespaceAliasBinding> bindings,
        IReadOnlyList<string> preparedLines,
        string body,
        string module,
        int bindingLine,
        ref int[]? braceDepths,
        ref IReadOnlyDictionary<string, List<int>>? localDeclarationLinesByName,
        ref Dictionary<string, IReadOnlyList<LineRange>>? parameterShadowRangesByAlias)
    {
        var remaining = body.AsSpan();
        while (true)
        {
            var commaIndex = remaining.IndexOf(',');
            var item = commaIndex < 0 ? remaining : remaining[..commaIndex];
            item = item.Trim();
            if (item.Length == 0)
            {
                if (commaIndex < 0)
                    break;

                remaining = remaining[(commaIndex + 1)..];
                continue;
            }

            var asIndex = item.LastIndexOf(" as ".AsSpan());
            var alias = asIndex >= 0 ? item[(asIndex + 4)..].Trim() : item;
            if (IsTypeScriptIdentifier(alias))
            {
                AddNamespaceAliasBinding(
                    bindings,
                    preparedLines,
                    alias.ToString(),
                    module,
                    bindingLine,
                    null,
                    braceDepths ??= BuildBraceDepthsBeforeLine(preparedLines),
                    localDeclarationLinesByName ??= BuildLocalDeclarationLinesByName(preparedLines),
                    parameterShadowRangesByAlias ??= new Dictionary<string, IReadOnlyList<LineRange>>(4, StringComparer.Ordinal));
            }

            if (commaIndex < 0)
                break;

            remaining = remaining[(commaIndex + 1)..];
        }
    }

    public static void EmitTypePositionReferences(
        IReadOnlyList<string> preparedLines,
        IReadOnlyList<string> rawLines,
        int lineIndex,
        string preparedLine,
        string rawLine,
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn,
        IReadOnlyList<NamespaceAliasBinding> namespaceAliases)
    {
        EmitNamespaceAliasQualifiedReferences(
            preparedLines,
            lineIndex,
            preparedLine,
            references,
            seen,
            fileId,
            context,
            lineNumber,
            resolveContainerForColumn,
            namespaceAliases);

        ReferenceExtractor.EmitTypeScriptTypePositionReferences(
            preparedLines,
            lineIndex,
            preparedLine,
            rawLine,
            references,
            seen,
            fileId,
            context,
            lineNumber,
            resolveContainerForColumn);

        EmitMappedTypeMemberReferences(preparedLine, references, seen, fileId, context, lineNumber, resolveContainerForColumn);
        EmitGenericConstraintTypeReferences(preparedLine, references, seen, fileId, context, lineNumber, resolveContainerForColumn);
        EmitHeritageTypeReferences(preparedLine, references, seen, fileId, context, lineNumber, resolveContainerForColumn);
        EmitTypeAliasTypeReferences(preparedLine, references, seen, fileId, context, lineNumber, resolveContainerForColumn);
        EmitCallableSignatureTypeReferences(preparedLine, references, seen, fileId, context, lineNumber, resolveContainerForColumn);
        EmitFunctionPropertyTypeReferences(preparedLine, references, seen, fileId, context, lineNumber, resolveContainerForColumn);
        EmitDecoratedMemberTypeReferences(preparedLine, references, seen, fileId, context, lineNumber, resolveContainerForColumn);
        TypedLanguageReferenceExtractor.EmitColonVariableTypeReferences(
            preparedLine,
            DeclarationKeywords,
            "typescript",
            references,
            seen,
            fileId,
            context,
            lineNumber,
            resolveContainerForColumn);
        if (!IsImportExportAliasLine(preparedLines, lineIndex, preparedLine))
        {
            EmitConstAssertionReferences(
                preparedLines,
                rawLines,
                lineIndex,
                preparedLine,
                rawLine,
                references,
                seen,
                fileId,
                context,
                lineNumber,
                resolveContainerForColumn);
            EmitAsTypeReferences(preparedLine, references, seen, fileId, context, lineNumber, resolveContainerForColumn);
            TypedLanguageReferenceExtractor.EmitKeywordFollowingTypeReferences(
                preparedLine,
                TypeOperatorKeywords,
                "typescript",
                references,
                seen,
                fileId,
                context,
                lineNumber,
                resolveContainerForColumn);
        }
    }

    public static void EmitDeclarationTypeReferences(
        string preparedLine,
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn)
    {
        ReferenceExtractor.EmitTypeScriptDeclarationTypeReferences(
            preparedLine,
            references,
            seen,
            fileId,
            context,
            lineNumber,
            resolveContainerForColumn);
    }

    public static IReadOnlyList<TypeAliasBinding> BuildTypeAliasTargets(IReadOnlyList<string> preparedLines)
    {
        List<TypeAliasBinding>? aliases = null;
        int[]? braceDepths = null;
        for (var index = 0; index < preparedLines.Count; index++)
        {
            var line = preparedLines[index];
            if (line.IndexOf("type", StringComparison.Ordinal) < 0
                || line.IndexOf('=') < 0)
            {
                continue;
            }

            var match = TypeAliasRegex.Match(line);
            if (!match.Success)
                continue;

            var target = TrimAliasTarget(match.Groups["target"].Value);
            if (target.Length > 0)
            {
                var sharedBraceDepths = braceDepths ??= BuildBraceDepthsBeforeLine(preparedLines);
                (aliases ??= new List<TypeAliasBinding>(4)).Add(new TypeAliasBinding(
                    match.Groups["alias"].Value,
                    target,
                    index + 1,
                    FindScopedAliasEndLine(preparedLines, sharedBraceDepths, index),
                    sharedBraceDepths[index],
                    BuildTypeAliasShadowRanges(preparedLines, sharedBraceDepths, match.Groups["alias"].Value),
                    ExtractGenericTypeParameters(match.Groups["params"].Value)));
            }
        }

        return aliases is null ? Array.Empty<TypeAliasBinding>() : aliases;
    }

    public static void EmitAliasTargetReferences(
        string preparedLine,
        IReadOnlyList<TypeAliasBinding> aliases,
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn)
    {
        if (aliases.Count == 0
            || (preparedLine.IndexOf("type", StringComparison.Ordinal) >= 0
                && preparedLine.IndexOf('=') >= 0
                && TypeAliasRegex.IsMatch(preparedLine)))
        {
            return;
        }

        HashSet<string>? emittedAliases = null;
        foreach (var bindingCandidate in aliases)
        {
            var alias = bindingCandidate.Alias;
            var index = preparedLine.IndexOf(alias, StringComparison.Ordinal);
            if (index < 0)
                continue;

            if (aliases.Count > 1
                && !(emittedAliases ??= new HashSet<string>(StringComparer.Ordinal)).Add(alias))
            {
                continue;
            }

            while (true)
            {
                if (!HasIdentifierBoundaries(preparedLine, index, alias.Length))
                {
                    if (!TryAdvanceToNextAliasOccurrence(preparedLine, alias, ref index))
                        break;
                    continue;
                }
                var binding = FindActiveTypeAliasBinding(aliases, alias, lineNumber);
                if (binding is null)
                {
                    if (!TryAdvanceToNextAliasOccurrence(preparedLine, alias, ref index))
                        break;
                    continue;
                }

                var column = index + 1;
                var container = resolveContainerForColumn(index);
                if (!seen.Contains(ReferenceExtractor.CreateReferenceDedupeKey(
                        fileId,
                        "typescript",
                        lineNumber,
                        column,
                        "type_reference",
                        alias,
                        container)))
                {
                    if (!TryAdvanceToNextAliasOccurrence(preparedLine, alias, ref index))
                        break;
                    continue;
                }

                TypedLanguageReferenceExtractor.EmitTypeExpressionReferences(
                    binding.Value.Target,
                    index,
                    "typescript",
                    references,
                    seen,
                    fileId,
                    context,
                    lineNumber,
                    container,
                    binding.Value.TypeParameters);

                if (!TryAdvanceToNextAliasOccurrence(preparedLine, alias, ref index))
                    break;
            }
        }
    }

    private static bool TryAdvanceToNextAliasOccurrence(string preparedLine, string alias, ref int index)
    {
        index = preparedLine.IndexOf(alias, index + alias.Length, StringComparison.Ordinal);
        return index >= 0;
    }

    private static TypeAliasBinding? FindActiveTypeAliasBinding(
        IReadOnlyList<TypeAliasBinding> aliases,
        string alias,
        int lineNumber)
    {
        TypeAliasBinding? best = null;
        foreach (var binding in aliases)
        {
            if (!string.Equals(binding.Alias, alias, StringComparison.Ordinal)
                || lineNumber <= binding.BindingLine
                || (binding.EndLine is int endLine && lineNumber > endLine)
                || IsInsideScopedShadow(binding.ShadowRanges, lineNumber))
            {
                continue;
            }

            if (best is null
                || binding.BraceDepth > best.Value.BraceDepth
                || (binding.BraceDepth == best.Value.BraceDepth && binding.BindingLine > best.Value.BindingLine))
            {
                best = binding;
            }
        }

        return best;
    }

    private static IReadOnlyList<LineRange> BuildTypeAliasShadowRanges(
        IReadOnlyList<string> preparedLines,
        IReadOnlyList<int> braceDepths,
        string alias)
    {
        List<LineRange>? ranges = null;
        for (var index = 0; index < preparedLines.Count; index++)
        {
            var line = preparedLines[index];
            if (!line.Contains(alias, StringComparison.Ordinal))
                continue;

            var typeDeclaration = TypeDeclarationShadowRegex.Match(line);
            if (typeDeclaration.Success && string.Equals(typeDeclaration.Groups["name"].Value, alias, StringComparison.Ordinal))
            {
                (ranges ??= new List<LineRange>(2)).Add(new LineRange(index + 1, FindScopedAliasEndLine(preparedLines, braceDepths, index) ?? preparedLines.Count));
                continue;
            }

            if (DeclaresGenericTypeParameter(line, alias))
                (ranges ??= new List<LineRange>(2)).Add(new LineRange(index + 1, FindScopedAliasEndLine(preparedLines, braceDepths, index) ?? index + 1));
        }

        return ranges is null ? Array.Empty<LineRange>() : ranges;
    }

    private static bool DeclaresGenericTypeParameter(string line, string alias)
    {
        var openAngle = line.IndexOf('<');
        if (openAngle < 0)
            return false;

        var closeAngle = line.IndexOf('>', openAngle + 1);
        if (closeAngle <= openAngle)
            return false;

        var prefix = line[..openAngle];
        if (!ContainsKeyword(prefix, "class")
            && !ContainsKeyword(prefix, "interface")
            && !ContainsKeyword(prefix, "function")
            && !ContainsKeyword(prefix, "type"))
        {
            return false;
        }

        var parameters = line.AsSpan(openAngle + 1, closeAngle - openAngle - 1);
        var start = 0;
        while (TryReadTypeScriptGenericParameterName(parameters, ref start, includeColonDelimiter: false, out var name))
        {
            if (name.Equals(alias.AsSpan(), StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    private static IReadOnlySet<string> ExtractGenericTypeParameters(string parameters)
    {
        var openAngle = parameters.IndexOf('<');
        var closeAngle = parameters.LastIndexOf('>');
        if (openAngle < 0 || closeAngle <= openAngle)
            return EmptyTypeParameters;

        HashSet<string>? names = null;
        var genericParameters = parameters.AsSpan(openAngle + 1, closeAngle - openAngle - 1);
        var start = 0;
        while (TryReadTypeScriptGenericParameterName(genericParameters, ref start, includeColonDelimiter: true, out var name))
        {
            (names ??= new HashSet<string>(StringComparer.Ordinal)).Add(name.ToString());
        }

        return names ?? EmptyTypeParameters;
    }

    private static bool TryReadTypeScriptGenericParameterName(
        ReadOnlySpan<char> parameters,
        ref int start,
        bool includeColonDelimiter,
        out ReadOnlySpan<char> name)
    {
        while (start <= parameters.Length)
        {
            var remaining = parameters[start..];
            var commaIndex = remaining.IndexOf(',');
            ReadOnlySpan<char> parameter;
            if (commaIndex < 0)
            {
                parameter = remaining;
                start = parameters.Length + 1;
            }
            else
            {
                parameter = remaining[..commaIndex];
                start += commaIndex + 1;
            }

            parameter = parameter.Trim();
            if (parameter.IsEmpty)
                continue;

            var delimiterIndex = includeColonDelimiter
                ? parameter.IndexOfAny(' ', '=', ':')
                : parameter.IndexOfAny(' ', '=');
            name = delimiterIndex < 0 ? parameter : parameter[..delimiterIndex].TrimEnd();
            if (!name.IsEmpty)
                return true;
        }

        name = default;
        return false;
    }

    private static bool ContainsKeyword(string text, string keyword)
    {
        var searchStart = 0;
        while (searchStart < text.Length)
        {
            var index = text.IndexOf(keyword, searchStart, StringComparison.Ordinal);
            if (index < 0)
                return false;

            if (HasIdentifierBoundaries(text, index, keyword.Length))
                return true;

            searchStart = index + keyword.Length;
        }

        return false;
    }

    private static int? FindScopedAliasEndLine(
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

    private static string TrimAliasTarget(string target)
    {
        var equalsTarget = target.Trim();
        var stop = equalsTarget.Length;
        foreach (var keyword in TypeAliasTargetStopKeywords)
        {
            var keywordIndex = FindTopLevelKeyword(equalsTarget, keyword);
            if (keywordIndex >= 0)
                stop = Math.Min(stop, keywordIndex);
        }

        return equalsTarget[..stop].Trim();
    }

    private static int FindTopLevelKeyword(string line, string keyword)
    {
        foreach (var index in TypedLanguageReferenceExtractor.EnumerateTopLevelKeywordIndices(line, keyword))
            return index;

        return -1;
    }

    private static bool HasIdentifierBoundaries(string line, int start, int length)
    {
        var before = start == 0 ? '\0' : line[start - 1];
        var afterIndex = start + length;
        var after = afterIndex >= line.Length ? '\0' : line[afterIndex];
        return !IsTypeScriptIdentifierPart(before) && !IsTypeScriptIdentifierPart(after);
    }

}
