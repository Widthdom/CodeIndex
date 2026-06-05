using System.Text.RegularExpressions;
using Regex = CodeIndex.Indexer.BoundedRegex;
using CodeIndex.Models;

namespace CodeIndex.Indexer;

internal static class TypeScriptReferenceExtractor
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
        var bindings = new List<NamespaceAliasBinding>();
        var braceDepths = BuildBraceDepthsBeforeLine(preparedLines);
        for (var index = 0; index < originalLines.Count; index++)
        {
            var line = originalLines[index];
            var match = NamespaceImportExportRegex.Match(line);
            if (match.Success)
            {
                AddNamespaceAliasBinding(
                    bindings,
                    preparedLines,
                    match.Groups["alias"].Value,
                    match.Groups["module"].Value,
                    index + 1,
                    endLine: null);
                continue;
            }

            match = DynamicImportNamespaceRegex.Match(line);
            if (match.Success)
            {
                var bindingLine = index + 1;
                AddNamespaceAliasBinding(
                    bindings,
                    preparedLines,
                    match.Groups["alias"].Value,
                    match.Groups["module"].Value,
                    bindingLine,
                    FindDynamicImportAliasEndLine(preparedLines, braceDepths, index));
                continue;
            }

            match = NamedImportRegex.Match(line);
            if (!match.Success)
                continue;

            foreach (var alias in ExtractNamedImportExportAliases(match.Groups["body"].Value))
            {
                AddNamespaceAliasBinding(
                    bindings,
                    preparedLines,
                    alias,
                    match.Groups["module"].Value,
                    index + 1,
                    endLine: null);
            }
        }

        return bindings;
    }

    private static void AddNamespaceAliasBinding(
        List<NamespaceAliasBinding> bindings,
        IReadOnlyList<string> preparedLines,
        string alias,
        string module,
        int bindingLine,
        int? endLine)
    {
        if (string.IsNullOrWhiteSpace(alias) || string.IsNullOrWhiteSpace(module))
            return;

        var shadowLine = FindShadowLine(preparedLines, alias, bindingLine);
        var scopedShadowRanges = BuildParameterShadowRanges(preparedLines, alias);
        bindings.Add(new NamespaceAliasBinding(alias, module, bindingLine, shadowLine, endLine, scopedShadowRanges));
    }

    private static IEnumerable<string> ExtractNamedImportExportAliases(string body)
    {
        foreach (var part in body.Split(','))
        {
            var item = part.Trim();
            if (item.Length == 0)
                continue;

            var asIndex = item.LastIndexOf(" as ", StringComparison.Ordinal);
            var alias = asIndex >= 0 ? item[(asIndex + 4)..].Trim() : item;
            if (IsTypeScriptIdentifier(alias))
                yield return alias;
        }
    }

    public static void EmitTypePositionReferences(
        IReadOnlyList<string> preparedLines,
        IReadOnlyList<string> rawLines,
        int lineIndex,
        string preparedLine,
        string rawLine,
        List<ReferenceRecord> references,
        HashSet<string> seen,
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
        HashSet<string> seen,
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
        var aliases = new List<TypeAliasBinding>();
        var braceDepths = BuildBraceDepthsBeforeLine(preparedLines);
        for (var index = 0; index < preparedLines.Count; index++)
        {
            var line = preparedLines[index];
            var match = TypeAliasRegex.Match(line);
            if (!match.Success)
                continue;

            var target = TrimAliasTarget(match.Groups["target"].Value);
            if (target.Length > 0)
                aliases.Add(new TypeAliasBinding(
                    match.Groups["alias"].Value,
                    target,
                    index + 1,
                    FindScopedAliasEndLine(preparedLines, braceDepths, index),
                    braceDepths[index],
                    BuildTypeAliasShadowRanges(preparedLines, braceDepths, match.Groups["alias"].Value),
                    ExtractGenericTypeParameters(match.Groups["params"].Value)));
        }

        return aliases;
    }

    public static void EmitAliasTargetReferences(
        string preparedLine,
        IReadOnlyList<TypeAliasBinding> aliases,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn)
    {
        if (aliases.Count == 0 || TypeAliasRegex.IsMatch(preparedLine))
            return;

        foreach (var alias in aliases.Select(binding => binding.Alias).Distinct(StringComparer.Ordinal))
        {
            var searchStart = 0;
            while (searchStart < preparedLine.Length)
            {
                var index = preparedLine.IndexOf(alias, searchStart, StringComparison.Ordinal);
                if (index < 0)
                    break;

                searchStart = index + alias.Length;
                if (!HasIdentifierBoundaries(preparedLine, index, alias.Length))
                    continue;
                var column = index + 1;
                if (!references.Any(reference =>
                        reference.FileId == fileId
                        && reference.Line == lineNumber
                        && reference.Column == column
                        && reference.ReferenceKind == "type_reference"
                        && string.Equals(reference.SymbolName, alias, StringComparison.Ordinal)))
                {
                    continue;
                }

                var binding = FindActiveTypeAliasBinding(aliases, alias, lineNumber);
                if (binding is null)
                    continue;

                TypedLanguageReferenceExtractor.EmitTypeExpressionReferences(
                    binding.Value.Target,
                    index,
                    "typescript",
                    references,
                    seen,
                    fileId,
                    context,
                    lineNumber,
                    resolveContainerForColumn(index),
                    binding.Value.TypeParameters);
            }
        }
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
        var ranges = new List<LineRange>();
        for (var index = 0; index < preparedLines.Count; index++)
        {
            var line = preparedLines[index];
            var typeDeclaration = TypeDeclarationShadowRegex.Match(line);
            if (typeDeclaration.Success && string.Equals(typeDeclaration.Groups["name"].Value, alias, StringComparison.Ordinal))
            {
                ranges.Add(new LineRange(index + 1, FindScopedAliasEndLine(preparedLines, braceDepths, index) ?? preparedLines.Count));
                continue;
            }

            if (DeclaresGenericTypeParameter(line, alias))
                ranges.Add(new LineRange(index + 1, FindScopedAliasEndLine(preparedLines, braceDepths, index) ?? index + 1));
        }

        return ranges;
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

        foreach (var parameter in line.Substring(openAngle + 1, closeAngle - openAngle - 1).Split(','))
        {
            var name = parameter.Trim().Split([' ', '='], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (string.Equals(name, alias, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    private static IReadOnlySet<string> ExtractGenericTypeParameters(string parameters)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        var openAngle = parameters.IndexOf('<');
        var closeAngle = parameters.LastIndexOf('>');
        if (openAngle < 0 || closeAngle <= openAngle)
            return names;

        foreach (var parameter in parameters.Substring(openAngle + 1, closeAngle - openAngle - 1).Split(','))
        {
            var name = parameter.Trim().Split([' ', '=', ':'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(name))
                names.Add(name);
        }

        return names;
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
        foreach (var keyword in new[] { "extends", "implements" })
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

    private static void EmitAsTypeReferences(
        string preparedLine,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn)
    {
        foreach (var asIndex in TypedLanguageReferenceExtractor.EnumerateTopLevelKeywordIndices(preparedLine, "as"))
        {
            var typeStart = TypedLanguageReferenceExtractor.SkipTypePrefixTrivia(preparedLine, asIndex + "as".Length);
            if (typeStart >= preparedLine.Length || TryConsumeKeywordAt(preparedLine, "const", typeStart))
                continue;

            var typeEnd = TypedLanguageReferenceExtractor.FindKeywordFollowingTypeExpressionEnd(preparedLine, typeStart, "typescript");
            if (typeEnd <= typeStart)
                continue;

            TypedLanguageReferenceExtractor.EmitTypeExpressionReferences(
                preparedLine.Substring(typeStart, typeEnd - typeStart),
                typeStart,
                "typescript",
                references,
                seen,
                fileId,
                context,
                lineNumber,
                resolveContainerForColumn(typeStart));
        }
    }

    private static void EmitConstAssertionReferences(
        IReadOnlyList<string> preparedLines,
        IReadOnlyList<string> rawLines,
        int lineIndex,
        string preparedLine,
        string rawLine,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn)
    {
        foreach (var asIndex in TypedLanguageReferenceExtractor.EnumerateTopLevelKeywordIndices(preparedLine, "as"))
        {
            var constIndex = SkipWhitespace(preparedLine, asIndex + "as".Length);
            if (!TryConsumeKeywordAt(preparedLine, "const", constIndex))
                continue;

            var rawAsIndex = rawLine.IndexOf(" as const", Math.Min(asIndex, rawLine.Length), StringComparison.Ordinal);
            if (rawAsIndex < 0)
                rawAsIndex = asIndex;
            else
                rawAsIndex++;
            var rawConstIndex = SkipWhitespace(rawLine, rawAsIndex + "as".Length);
            ReferenceExtractor.AddReference(
                references,
                seen,
                fileId,
                "const",
                rawConstIndex,
                "const_assertion",
                context,
                lineNumber,
                resolveContainerForColumn(asIndex));

            EmitConstAssertionLiteralTypeReferences(
                preparedLines,
                rawLines,
                lineIndex,
                asIndex,
                rawAsIndex,
                references,
                seen,
                fileId,
                resolveContainerForColumn);
        }
    }

    private static void EmitConstAssertionLiteralTypeReferences(
        IReadOnlyList<string> preparedLines,
        IReadOnlyList<string> rawLines,
        int assertionLineIndex,
        int preparedAsIndex,
        int asIndex,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        Func<int, SymbolRecord?> resolveContainerForColumn)
    {
        if (!TryFindConstAssertionLiteralOpen(
                preparedLines,
                assertionLineIndex,
                preparedAsIndex,
                out var literalOpenLineIndex,
                out var literalOpenColumn))
        {
            return;
        }

        var insideBlockComment = false;
        for (var currentLineIndex = literalOpenLineIndex; currentLineIndex <= assertionLineIndex; currentLineIndex++)
        {
            var rawLine = rawLines[currentLineIndex];
            var scanStart = currentLineIndex == literalOpenLineIndex ? literalOpenColumn + 1 : 0;
            var scanEnd = currentLineIndex == assertionLineIndex ? Math.Min(asIndex, rawLine.Length) : rawLine.Length;
            if (scanStart >= scanEnd)
                continue;

            for (var index = scanStart; index < scanEnd; index++)
            {
                if (SkipConstAssertionComment(rawLine, scanEnd, ref index, ref insideBlockComment))
                    continue;

                if (rawLine[index] is '"' or '\'' or '`')
                {
                    var literalStart = index;
                    index = SkipQuotedLiteral(rawLine, index);
                    if (index <= literalStart + 1
                        || !HasStandaloneConstAssertionLiteralBoundaries(
                            rawLines,
                            literalOpenLineIndex,
                            literalOpenColumn,
                            assertionLineIndex,
                            asIndex,
                            currentLineIndex,
                            literalStart,
                            index + 1))
                    {
                        continue;
                    }

                    ReferenceExtractor.AddReference(
                        references,
                        seen,
                        fileId,
                        rawLine.Substring(literalStart, index - literalStart + 1),
                        literalStart,
                        "type_reference",
                        rawLine.Trim(),
                        currentLineIndex + 1,
                        ResolveConstAssertionLiteralContainer(
                            currentLineIndex,
                            assertionLineIndex,
                            literalStart,
                            resolveContainerForColumn));
                    continue;
                }

                if (IsNumberLiteralStart(rawLine, index))
                {
                    var literalStart = index;
                    index = SkipNumberLiteral(rawLine, index);
                    if (!HasStandaloneConstAssertionLiteralBoundaries(
                            rawLines,
                            literalOpenLineIndex,
                            literalOpenColumn,
                            assertionLineIndex,
                            asIndex,
                            currentLineIndex,
                            literalStart,
                            index))
                    {
                        index--;
                        continue;
                    }

                    ReferenceExtractor.AddReference(
                        references,
                        seen,
                        fileId,
                        rawLine.Substring(literalStart, index - literalStart),
                        literalStart,
                        "type_reference",
                        rawLine.Trim(),
                        currentLineIndex + 1,
                        ResolveConstAssertionLiteralContainer(
                            currentLineIndex,
                            assertionLineIndex,
                            literalStart,
                            resolveContainerForColumn));
                    index--;
                    continue;
                }

                if (!TryReadLiteralKeyword(rawLine, index, scanEnd, out var keyword))
                    continue;
                if (!HasStandaloneConstAssertionLiteralBoundaries(
                        rawLines,
                        literalOpenLineIndex,
                        literalOpenColumn,
                        assertionLineIndex,
                        asIndex,
                        currentLineIndex,
                        index,
                        index + keyword.Length))
                {
                    continue;
                }

                ReferenceExtractor.AddReference(
                    references,
                    seen,
                    fileId,
                    keyword,
                    index,
                    "type_reference",
                    rawLine.Trim(),
                    currentLineIndex + 1,
                    ResolveConstAssertionLiteralContainer(
                        currentLineIndex,
                        assertionLineIndex,
                        index,
                        resolveContainerForColumn));
                index += keyword.Length - 1;
            }
        }
    }

    private static bool SkipConstAssertionComment(string line, int scanEnd, ref int index, ref bool insideBlockComment)
    {
        if (insideBlockComment)
        {
            var end = line.IndexOf("*/", index, Math.Max(0, scanEnd - index), StringComparison.Ordinal);
            if (end < 0)
            {
                index = scanEnd;
                return true;
            }

            index = end + 1;
            insideBlockComment = false;
            return true;
        }

        if (index + 1 >= scanEnd || line[index] != '/')
            return false;

        if (line[index + 1] == '/')
        {
            index = scanEnd;
            return true;
        }

        if (line[index + 1] != '*')
            return false;

        insideBlockComment = true;
        index++;
        return true;
    }

    private static bool HasStandaloneConstAssertionLiteralBoundaries(
        IReadOnlyList<string> preparedLines,
        int literalOpenLineIndex,
        int literalOpenColumn,
        int assertionLineIndex,
        int preparedAsIndex,
        int literalLineIndex,
        int literalStartColumn,
        int literalEndColumn)
    {
        var previous = FindPreviousNonWhitespace(
            preparedLines,
            literalOpenLineIndex,
            literalOpenColumn,
            literalLineIndex,
            literalStartColumn);
        var next = FindNextNonWhitespace(
            preparedLines,
            literalLineIndex,
            literalEndColumn,
            assertionLineIndex,
            preparedAsIndex);

        if (previous == ':' && HasQuestionBeforeLiteralValue(rawLines: preparedLines, literalLineIndex, literalStartColumn))
            return false;

        return previous is '[' or '{' or ':' or ','
               && next is ',' or ']' or '}';
    }

    private static bool HasQuestionBeforeLiteralValue(
        IReadOnlyList<string> rawLines,
        int literalLineIndex,
        int literalStartColumn)
    {
        var line = rawLines[literalLineIndex];
        for (var index = literalStartColumn - 1; index >= 0; index--)
        {
            if (line[index] == '?')
                return true;

            if (line[index] is ',' or '{' or '[')
                return false;
        }

        return false;
    }

    private static char? FindPreviousNonWhitespace(
        IReadOnlyList<string> lines,
        int minLineIndex,
        int minColumn,
        int lineIndex,
        int column)
    {
        for (var currentLineIndex = lineIndex; currentLineIndex >= minLineIndex; currentLineIndex--)
        {
            var line = lines[currentLineIndex];
            var index = currentLineIndex == lineIndex ? column - 1 : line.Length - 1;
            var stop = currentLineIndex == minLineIndex ? minColumn : 0;
            var lineCommentStart = IndexOfLineCommentOutsideString(line, stop, Math.Max(0, index - stop + 1));
            if (lineCommentStart >= 0)
                index = lineCommentStart - 1;
            for (; index >= stop; index--)
            {
                if (line[index] == '/' && index > stop && line[index - 1] == '*')
                {
                    var commentStart = line.LastIndexOf("/*", index - 1, index - stop, StringComparison.Ordinal);
                    if (commentStart >= 0)
                    {
                        index = commentStart;
                        continue;
                    }
                }

                if (!char.IsWhiteSpace(line[index]))
                    return line[index];
            }
        }

        return null;
    }

    private static int IndexOfLineCommentOutsideString(string line, int startIndex, int count)
    {
        var endIndex = Math.Min(line.Length, startIndex + count);
        char? quote = null;
        for (var index = startIndex; index + 1 < endIndex; index++)
        {
            if (quote is char activeQuote)
            {
                if (line[index] == '\\')
                {
                    index++;
                    continue;
                }

                if (line[index] == activeQuote)
                    quote = null;
                continue;
            }

            if (line[index] is '"' or '\'' or '`')
            {
                quote = line[index];
                continue;
            }

            if (line[index] == '/' && line[index + 1] == '/')
                return index;
        }

        return -1;
    }

    private static char? FindNextNonWhitespace(
        IReadOnlyList<string> lines,
        int lineIndex,
        int column,
        int maxLineIndex,
        int maxColumn)
    {
        for (var currentLineIndex = lineIndex; currentLineIndex <= maxLineIndex; currentLineIndex++)
        {
            var line = lines[currentLineIndex];
            var index = currentLineIndex == lineIndex ? column : 0;
            var stop = currentLineIndex == maxLineIndex ? Math.Min(maxColumn, line.Length) : line.Length;
            for (; index < stop; index++)
            {
                if (index + 1 < stop && line[index] == '/' && line[index + 1] == '*')
                {
                    var commentEnd = line.IndexOf("*/", index + 2, stop - index - 2, StringComparison.Ordinal);
                    if (commentEnd < 0)
                        return null;

                    index = commentEnd + 1;
                    continue;
                }

                if (index + 1 < stop && line[index] == '/' && line[index + 1] == '/')
                    return null;

                if (!char.IsWhiteSpace(line[index]))
                    return line[index];
            }
        }

        return null;
    }

    private static SymbolRecord? ResolveConstAssertionLiteralContainer(
        int literalLineIndex,
        int assertionLineIndex,
        int column,
        Func<int, SymbolRecord?> resolveContainerForColumn)
    {
        return literalLineIndex == assertionLineIndex ? resolveContainerForColumn(column) : null;
    }

    private static bool TryFindConstAssertionLiteralOpen(
        IReadOnlyList<string> preparedLines,
        int assertionLineIndex,
        int asIndex,
        out int openLineIndex,
        out int openColumn)
    {
        openLineIndex = -1;
        openColumn = -1;
        for (var lineIndex = assertionLineIndex; lineIndex >= 0; lineIndex--)
        {
            var line = preparedLines[lineIndex];
            var index = lineIndex == assertionLineIndex ? asIndex - 1 : line.Length - 1;
            for (; index >= 0; index--)
            {
                if (char.IsWhiteSpace(line[index]))
                    continue;

                if (line[index] is ']' or '}')
                {
                    var openChar = line[index] == ']' ? '[' : '{';
                    return TryFindMatchingOpenChar(
                        preparedLines,
                        lineIndex,
                        index,
                        openChar,
                        line[index],
                        out openLineIndex,
                        out openColumn);
                }

                return false;
            }
        }

        return false;
    }

    private static bool TryFindMatchingOpenChar(
        IReadOnlyList<string> lines,
        int closeLineIndex,
        int closeColumn,
        char openChar,
        char closeChar,
        out int openLineIndex,
        out int openColumn)
    {
        openLineIndex = -1;
        openColumn = -1;
        var depth = 0;
        for (var lineIndex = closeLineIndex; lineIndex >= 0; lineIndex--)
        {
            var line = lines[lineIndex];
            var index = lineIndex == closeLineIndex ? closeColumn : line.Length - 1;
            for (; index >= 0; index--)
            {
                if (line[index] == closeChar)
                {
                    depth++;
                    continue;
                }

                if (line[index] != openChar)
                    continue;

                depth--;
                if (depth == 0)
                {
                    openLineIndex = lineIndex;
                    openColumn = index;
                    return true;
                }
            }
        }

        return false;
    }

    private static int SkipQuotedLiteral(string text, int quoteIndex)
    {
        var quote = text[quoteIndex];
        for (var index = quoteIndex + 1; index < text.Length; index++)
        {
            if (text[index] == '\\')
            {
                index++;
                continue;
            }

            if (text[index] == quote)
                return index;
        }

        return text.Length - 1;
    }

    private static bool IsNumberLiteralStart(string text, int index)
    {
        if (index >= text.Length)
            return false;

        var startsWithDigit = char.IsDigit(text[index]);
        var startsWithNegativeSign = text[index] == '-'
                                     && index + 1 < text.Length
                                     && char.IsDigit(text[index + 1]);
        if (!startsWithDigit && !startsWithNegativeSign)
            return false;

        return index == 0 || !IsTypeScriptIdentifierPart(text[index - 1]);
    }

    private static int SkipNumberLiteral(string text, int index)
    {
        if (index < text.Length && text[index] == '-')
            index++;

        if (index + 1 < text.Length
            && text[index] == '0'
            && text[index + 1] is 'x' or 'X' or 'b' or 'B' or 'o' or 'O')
        {
            var radixPrefix = text[index + 1];
            index += 2;
            while (index < text.Length && (IsRadixDigit(text[index], radixPrefix) || text[index] == '_'))
                index++;

            if (index < text.Length && text[index] == 'n')
                index++;

            return index;
        }

        while (index < text.Length && (char.IsDigit(text[index]) || text[index] == '_'))
            index++;

        if (index < text.Length && text[index] == '.')
        {
            index++;
            while (index < text.Length && (char.IsDigit(text[index]) || text[index] == '_'))
                index++;
        }

        if (index < text.Length && text[index] is 'e' or 'E')
        {
            var exponentIndex = index + 1;
            if (exponentIndex < text.Length && text[exponentIndex] is '+' or '-')
                exponentIndex++;

            var digitStart = exponentIndex;
            while (exponentIndex < text.Length && (char.IsDigit(text[exponentIndex]) || text[exponentIndex] == '_'))
                exponentIndex++;

            if (exponentIndex > digitStart)
                index = exponentIndex;
        }

        if (index < text.Length && text[index] == 'n')
            index++;

        return index;
    }

    private static bool IsRadixDigit(char ch, char radixPrefix)
    {
        return radixPrefix switch
        {
            'x' or 'X' => char.IsAsciiHexDigit(ch),
            'b' or 'B' => ch is '0' or '1',
            'o' or 'O' => ch is >= '0' and <= '7',
            _ => false,
        };
    }

    private static bool TryReadLiteralKeyword(string text, int index, int endExclusive, out string keyword)
    {
        foreach (var candidate in new[] { "true", "false", "null", "undefined" })
        {
            if (index + candidate.Length > endExclusive
                || string.CompareOrdinal(text, index, candidate, 0, candidate.Length) != 0)
            {
                continue;
            }

            var beforeOk = index == 0 || !IsTypeScriptIdentifierPart(text[index - 1]);
            var after = index + candidate.Length;
            var afterOk = after >= text.Length || !IsTypeScriptIdentifierPart(text[after]);
            if (!beforeOk || !afterOk)
                continue;

            keyword = candidate;
            return true;
        }

        keyword = string.Empty;
        return false;
    }

    private static bool TryConsumeKeywordAt(string text, string keyword, int index)
    {
        if (index < 0 || index + keyword.Length > text.Length)
            return false;

        if (string.CompareOrdinal(text, index, keyword, 0, keyword.Length) != 0)
            return false;

        var beforeOk = index == 0 || !IsTypeScriptIdentifierPart(text[index - 1]);
        var after = index + keyword.Length;
        var afterOk = after >= text.Length || !IsTypeScriptIdentifierPart(text[after]);
        return beforeOk && afterOk;
    }

    private static int SkipWhitespace(string text, int index)
    {
        while (index < text.Length && char.IsWhiteSpace(text[index]))
            index++;

        return index;
    }

    private static void EmitNamespaceAliasQualifiedReferences(
        IReadOnlyList<string> preparedLines,
        int lineIndex,
        string preparedLine,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn,
        IReadOnlyList<NamespaceAliasBinding> namespaceAliases)
    {
        if (namespaceAliases.Count == 0 || IsImportExportAliasLine(preparedLines, lineIndex, preparedLine))
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

    private static int? FindShadowLine(IReadOnlyList<string> preparedLines, string alias, int bindingLine)
    {
        for (var index = bindingLine; index < preparedLines.Count; index++)
        {
            var line = preparedLines[index];
            if (NamespaceImportExportRegex.IsMatch(line) || DynamicImportNamespaceRegex.IsMatch(line))
                continue;

            var match = LocalDeclarationRegex.Match(line);
            if (match.Success && string.Equals(match.Groups["name"].Value, alias, StringComparison.Ordinal))
                return index + 1;
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

    private static IReadOnlyList<LineRange> BuildParameterShadowRanges(IReadOnlyList<string> preparedLines, string alias)
    {
        var ranges = new List<LineRange>();
        var braceDepths = BuildBraceDepthsBeforeLine(preparedLines);
        for (var index = 0; index < preparedLines.Count; index++)
        {
            if (!TryGetSingleLineCallableParameters(preparedLines[index], out var parameters)
                || !ParameterListDeclaresName(parameters, alias))
            {
                continue;
            }

            var endLine = FindBlockEndLine(preparedLines, braceDepths, index);
            if (endLine >= index + 1)
                ranges.Add(new LineRange(index + 1, endLine));
        }

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
        foreach (var part in parameters.Split(','))
        {
            var item = part.TrimStart();
            if (item.StartsWith("...", StringComparison.Ordinal))
                item = item[3..].TrimStart();

            if (!item.StartsWith(alias, StringComparison.Ordinal))
                continue;

            var after = item.Length == alias.Length ? '\0' : item[alias.Length];
            if (after is '\0' or ':' or '?' or '=' || char.IsWhiteSpace(after))
                return true;
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

    private static void EmitMappedTypeMemberReferences(
        string preparedLine,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn)
    {
        var bracketStart = preparedLine.IndexOf('[');
        if (bracketStart < 0)
            return;

        var bracketEnd = ReferenceExtractor.FindMatchingChar(preparedLine, bracketStart, '[', ']');
        if (bracketEnd <= bracketStart)
            return;

        var clause = preparedLine.Substring(bracketStart + 1, bracketEnd - bracketStart - 1);
        if (!clause.Contains("keyof", StringComparison.Ordinal)
            && !clause.Contains(" in ", StringComparison.Ordinal)
            && !clause.Contains(" as ", StringComparison.Ordinal))
        {
            return;
        }

        var clauseStart = bracketStart + 1;
        ReferenceExtractor.AddTypeExpressionSegments(
            references,
            seen,
            fileId,
            clause,
            clauseStart,
            context,
            lineNumber,
            resolveContainerForColumn(clauseStart),
            "typescript",
            MappedTypeClauseIgnoredSegments);

        var colonIndex = TypedLanguageReferenceExtractor.FindTopLevelChar(preparedLine, ':', bracketEnd + 1);
        if (colonIndex < 0)
            return;

        var typeStart = TypedLanguageReferenceExtractor.SkipTypePrefixTrivia(preparedLine, colonIndex + 1);
        if (typeStart >= preparedLine.Length)
            return;

        var typeEnd = TypedLanguageReferenceExtractor.FindTypeExpressionEnd(preparedLine, typeStart, stopAtArrow: false);
        if (typeEnd <= typeStart)
            return;

        ReferenceExtractor.AddTypeExpressionSegments(
            references,
            seen,
            fileId,
            preparedLine.Substring(typeStart, typeEnd - typeStart),
            typeStart,
            context,
            lineNumber,
            resolveContainerForColumn(typeStart),
            "typescript");
    }

    public static bool IsSatisfiesTypeOperand(string preparedLine, int tokenIndex)
    {
        foreach (var keywordIndex in TypedLanguageReferenceExtractor.EnumerateTopLevelKeywordIndices(preparedLine, "satisfies"))
        {
            var typeStart = TypedLanguageReferenceExtractor.SkipTypePrefixTrivia(preparedLine, keywordIndex + "satisfies".Length);
            if (typeStart >= preparedLine.Length || tokenIndex < typeStart)
                continue;

            var typeEnd = TypedLanguageReferenceExtractor.FindKeywordFollowingTypeExpressionEnd(preparedLine, typeStart, "typescript");
            if (typeEnd <= typeStart)
                continue;

            if (tokenIndex < typeEnd)
                return true;
        }

        return false;
    }

    private static void EmitGenericConstraintTypeReferences(
        string preparedLine,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn)
    {
        for (var index = 0; index < preparedLine.Length; index++)
        {
            if (preparedLine[index] != '<')
                continue;

            var closeIndex = ReferenceExtractor.FindMatchingChar(preparedLine, index, '<', '>');
            if (closeIndex <= index)
                continue;

            var clauseStart = index + 1;
            var clause = preparedLine.Substring(clauseStart, closeIndex - clauseStart);
            foreach (var (segmentStart, segmentLength) in ReferenceExtractor.SplitTopLevelCommaSpans(clause))
            {
                var fragment = clause.Substring(segmentStart, segmentLength);
                foreach (var extendsIndex in TypedLanguageReferenceExtractor.EnumerateTopLevelKeywordIndices(fragment, "extends"))
                {
                    var typeStart = TypedLanguageReferenceExtractor.SkipTypePrefixTrivia(fragment, extendsIndex + "extends".Length);
                    if (typeStart >= fragment.Length)
                        continue;

                    var typeEnd = FindGenericConstraintExpressionEnd(fragment, typeStart);
                    if (typeEnd <= typeStart)
                        continue;

                    var absoluteStart = clauseStart + segmentStart + typeStart;
                    ReferenceExtractor.AddTypeScriptTypeExpressionSegments(
                        references,
                        seen,
                        fileId,
                        fragment.Substring(typeStart, typeEnd - typeStart),
                        absoluteStart,
                        context,
                        lineNumber,
                        resolveContainerForColumn(absoluteStart));
                }
            }

            index = closeIndex;
        }
    }

    private static int FindGenericConstraintExpressionEnd(string fragment, int typeStart)
    {
        var equalsIndex = TypedLanguageReferenceExtractor.FindTopLevelChar(fragment, '=', typeStart);
        return equalsIndex >= 0 ? equalsIndex : fragment.Length;
    }

    private static void EmitHeritageTypeReferences(
        string preparedLine,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn)
    {
        var trimmed = preparedLine.TrimStart();
        if (!(trimmed.StartsWith("class ", StringComparison.Ordinal)
              || trimmed.StartsWith("abstract class ", StringComparison.Ordinal)
              || trimmed.StartsWith("export class ", StringComparison.Ordinal)
              || trimmed.StartsWith("export abstract class ", StringComparison.Ordinal)
              || trimmed.StartsWith("interface ", StringComparison.Ordinal)
              || trimmed.StartsWith("export interface ", StringComparison.Ordinal)))
        {
            return;
        }

        EmitHeritageKeyword(preparedLine, "extends", references, seen, fileId, context, lineNumber, resolveContainerForColumn);
        EmitHeritageKeyword(preparedLine, "implements", references, seen, fileId, context, lineNumber, resolveContainerForColumn);
    }

    private static void EmitTypeAliasTypeReferences(
        string preparedLine,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn)
    {
        if (!TryFindTypeAliasShape(preparedLine, out var nameEnd, out var assignmentIndex))
            return;

        var genericOpen = TypedLanguageReferenceExtractor.FindTopLevelChar(preparedLine, '<', nameEnd);
        if (genericOpen >= 0 && genericOpen < assignmentIndex)
        {
            var genericClose = ReferenceExtractor.FindMatchingChar(preparedLine, genericOpen, '<', '>');
            if (genericClose > genericOpen && genericClose < assignmentIndex)
            {
                EmitTypeParameterDefaultReferences(
                    preparedLine,
                    genericOpen + 1,
                    genericClose,
                    references,
                    seen,
                    fileId,
                    context,
                    lineNumber,
                    resolveContainerForColumn);
            }
        }

        var rhsStart = TypedLanguageReferenceExtractor.SkipTypePrefixTrivia(preparedLine, assignmentIndex + 1);
        if (rhsStart >= preparedLine.Length)
            return;

        var rhsEnd = TypedLanguageReferenceExtractor.FindTypeExpressionEnd(
            preparedLine,
            rhsStart,
            stopAtComma: false,
            stopAtArrow: false);
        if (rhsEnd <= rhsStart)
            return;

        TypedLanguageReferenceExtractor.EmitTypeExpressionReferences(
            preparedLine.Substring(rhsStart, rhsEnd - rhsStart),
            rhsStart,
            "typescript",
            references,
            seen,
            fileId,
            context,
            lineNumber,
            resolveContainerForColumn(rhsStart));
    }

    private static bool TryFindTypeAliasShape(string line, out int nameEnd, out int assignmentIndex)
    {
        nameEnd = -1;
        assignmentIndex = -1;

        var index = 0;
        SkipWhitespace(line, ref index);
        TryConsumeKeyword(line, "export", ref index);
        SkipWhitespace(line, ref index);
        TryConsumeKeyword(line, "declare", ref index);
        SkipWhitespace(line, ref index);
        if (!TryConsumeKeyword(line, "type", ref index))
            return false;

        SkipWhitespace(line, ref index);
        if (index >= line.Length || !IsTypeScriptIdentifierStart(line[index]))
            return false;

        index++;
        while (index < line.Length && IsTypeScriptIdentifierPart(line[index]))
            index++;

        nameEnd = index;
        assignmentIndex = TypedLanguageReferenceExtractor.FindTopLevelChar(line, '=', nameEnd);
        return assignmentIndex > nameEnd;
    }

    private static void EmitTypeParameterDefaultReferences(
        string line,
        int listStart,
        int listEnd,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn)
    {
        var parameterList = line.Substring(listStart, listEnd - listStart);
        foreach (var (segmentStart, segmentLength) in ReferenceExtractor.SplitTopLevelCommaSpans(parameterList))
        {
            var fragment = parameterList.Substring(segmentStart, segmentLength);
            var equalsIndex = TypedLanguageReferenceExtractor.FindTopLevelChar(fragment, '=');
            if (equalsIndex < 0)
                continue;

            var typeStart = TypedLanguageReferenceExtractor.SkipTypePrefixTrivia(fragment, equalsIndex + 1);
            if (typeStart >= fragment.Length)
                continue;

            var typeEnd = TypedLanguageReferenceExtractor.FindTypeExpressionEnd(fragment, typeStart, stopAtArrow: false);
            if (typeEnd <= typeStart)
                continue;

            var absoluteStart = listStart + segmentStart + typeStart;
            TypedLanguageReferenceExtractor.EmitTypeExpressionReferences(
                fragment.Substring(typeStart, typeEnd - typeStart),
                absoluteStart,
                "typescript",
                references,
                seen,
                fileId,
                context,
                lineNumber,
                resolveContainerForColumn(absoluteStart));
        }
    }

    private static void EmitHeritageKeyword(
        string preparedLine,
        string keyword,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn)
    {
        foreach (var keywordIndex in TypedLanguageReferenceExtractor.EnumerateTopLevelKeywordIndices(preparedLine, keyword))
        {
            var listStart = TypedLanguageReferenceExtractor.SkipTypePrefixTrivia(preparedLine, keywordIndex + keyword.Length);
            var listEnd = TypedLanguageReferenceExtractor.FindTypeExpressionEnd(preparedLine, listStart, stopAtComma: false);
            if (listEnd <= listStart)
                continue;

            TypedLanguageReferenceExtractor.EmitCommaSeparatedTypeListReferences(
                preparedLine,
                listStart,
                listEnd,
                "typescript",
                references,
                seen,
                fileId,
                context,
                lineNumber,
                resolveContainerForColumn);
        }
    }

    private static void EmitCallableSignatureTypeReferences(
        string preparedLine,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn)
    {
        var openParen = TypedLanguageReferenceExtractor.FindTopLevelChar(
            preparedLine,
            '(',
            SkipLeadingDecorators(preparedLine));
        if (openParen <= 0)
            return;

        var closeParen = ReferenceExtractor.FindMatchingChar(preparedLine, openParen, '(', ')');
        if (closeParen < 0)
            return;

        TypedLanguageReferenceExtractor.EmitColonParameterTypeReferences(
            preparedLine,
            openParen + 1,
            closeParen,
            "typescript",
            references,
            seen,
            fileId,
            context,
            lineNumber,
            resolveContainerForColumn);

        var returnColon = TypedLanguageReferenceExtractor.SkipTypePrefixTrivia(preparedLine, closeParen + 1);
        if (returnColon >= preparedLine.Length || preparedLine[returnColon] != ':')
            return;

        var typeStart = TypedLanguageReferenceExtractor.SkipTypePrefixTrivia(preparedLine, returnColon + 1);
        if (typeStart >= preparedLine.Length)
            return;

        var typeEnd = TypedLanguageReferenceExtractor.FindTypeExpressionEnd(preparedLine, typeStart, stopAtArrow: false);
        if (typeEnd <= typeStart)
            return;

        TypedLanguageReferenceExtractor.EmitTypeExpressionReferences(
            preparedLine.Substring(typeStart, typeEnd - typeStart),
            typeStart,
            "typescript",
            references,
            seen,
            fileId,
            context,
            lineNumber,
            resolveContainerForColumn(typeStart));
    }

    private static void EmitDecoratedMemberTypeReferences(
        string preparedLine,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn)
    {
        var memberStart = SkipLeadingDecorators(preparedLine);
        if (memberStart <= 0 || memberStart >= preparedLine.Length)
            return;

        var colonIndex = TypedLanguageReferenceExtractor.FindTopLevelChar(preparedLine, ':', memberStart);
        if (colonIndex < 0)
            return;

        var openParen = TypedLanguageReferenceExtractor.FindTopLevelChar(preparedLine, '(', memberStart);
        if (openParen >= 0 && openParen < colonIndex)
            return;

        var equalsIndex = TypedLanguageReferenceExtractor.FindTopLevelChar(preparedLine, '=', memberStart);
        if (equalsIndex >= 0 && equalsIndex < colonIndex)
            return;

        var typeStart = TypedLanguageReferenceExtractor.SkipTypePrefixTrivia(preparedLine, colonIndex + 1);
        if (typeStart >= preparedLine.Length)
            return;

        var typeEnd = TypedLanguageReferenceExtractor.FindTypeExpressionEnd(preparedLine, typeStart, stopAtArrow: false);
        if (typeEnd <= typeStart)
            return;

        TypedLanguageReferenceExtractor.EmitTypeExpressionReferences(
            preparedLine.Substring(typeStart, typeEnd - typeStart),
            typeStart,
            "typescript",
            references,
            seen,
            fileId,
            context,
            lineNumber,
            resolveContainerForColumn(typeStart));
    }

    private static void EmitFunctionPropertyTypeReferences(
        string preparedLine,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn)
    {
        var colonIndex = TypedLanguageReferenceExtractor.FindTopLevelChar(preparedLine, ':');
        if (colonIndex < 0)
            return;

        var equalsIndex = TypedLanguageReferenceExtractor.FindTopLevelChar(preparedLine, '=');
        if (equalsIndex >= 0 && equalsIndex < colonIndex)
            return;

        var questionIndex = TypedLanguageReferenceExtractor.FindTopLevelChar(preparedLine, '?');
        if (questionIndex >= 0 && questionIndex != colonIndex - 1)
            return;

        var prefix = preparedLine.Substring(0, colonIndex).TrimEnd();
        if (prefix.Length == 0 || prefix.EndsWith(")", StringComparison.Ordinal))
            return;

        var typeStart = TypedLanguageReferenceExtractor.SkipTypePrefixTrivia(preparedLine, colonIndex + 1);
        if (typeStart >= preparedLine.Length)
            return;

        var typeEnd = TypedLanguageReferenceExtractor.FindTypeExpressionEnd(preparedLine, typeStart, stopAtArrow: false);
        if (typeEnd <= typeStart)
            return;

        var container = resolveContainerForColumn(typeStart);
        TypedLanguageReferenceExtractor.TryEmitTypeScriptFunctionTypeExpressionReferences(
            preparedLine.Substring(typeStart, typeEnd - typeStart),
            typeStart,
            references,
            seen,
            fileId,
            context,
            lineNumber,
            container);
    }

    private static bool IsImportExportAliasLine(IReadOnlyList<string> preparedLines, int lineIndex, string preparedLine)
    {
        var trimmed = preparedLine.TrimStart();
        return IsImportDeclarationLine(trimmed)
               || IsNamedExportLine(trimmed)
               || IsExportStarAliasLine(trimmed)
               || IsInsideMultilineImportExportAlias(preparedLines, lineIndex, preparedLine);
    }

    private static bool IsImportDeclarationLine(string text)
    {
        const string importKeyword = "import";
        if (!text.StartsWith(importKeyword, StringComparison.Ordinal))
            return false;

        var index = importKeyword.Length;
        if (index >= text.Length || IsTypeScriptIdentifierPart(text[index]))
            return false;

        return char.IsWhiteSpace(text[index]) || text[index] is '{' or '*';
    }

    private static bool IsNamedExportLine(string text)
    {
        var index = 0;
        if (!TryConsumeKeyword(text, "export", ref index))
            return false;

        SkipWhitespace(text, ref index);
        if (index < text.Length && text[index] == '{')
            return true;

        if (!TryConsumeKeyword(text, "type", ref index))
            return false;

        SkipWhitespace(text, ref index);
        return index < text.Length && text[index] == '{';
    }

    private static bool IsExportStarAliasLine(string text)
    {
        var index = 0;
        if (!TryConsumeKeyword(text, "export", ref index))
            return false;

        SkipWhitespace(text, ref index);
        if (TryConsumeKeyword(text, "type", ref index))
            SkipWhitespace(text, ref index);

        if (index >= text.Length || text[index] != '*')
            return false;

        index++;
        SkipWhitespace(text, ref index);
        return TryConsumeKeyword(text, "as", ref index);
    }

    private static bool IsInsideMultilineImportExportAlias(
        IReadOnlyList<string> preparedLines,
        int lineIndex,
        string preparedLine)
    {
        var asIndex = -1;
        foreach (var keywordIndex in TypedLanguageReferenceExtractor.EnumerateTopLevelKeywordIndices(preparedLine, "as"))
        {
            asIndex = keywordIndex;
            break;
        }

        return asIndex >= 0 && IsInsideImportExportBraceAt(preparedLines, lineIndex, asIndex);
    }

    private static bool IsInsideImportExportBraceAt(IReadOnlyList<string> preparedLines, int lineIndex, int column)
    {
        var unmatchedClosingBraces = 0;
        for (var currentLine = lineIndex; currentLine >= 0; currentLine--)
        {
            var line = preparedLines[currentLine];
            var startColumn = currentLine == lineIndex ? Math.Min(column, line.Length) - 1 : line.Length - 1;
            for (var index = startColumn; index >= 0; index--)
            {
                if (line[index] == '}')
                {
                    unmatchedClosingBraces++;
                    continue;
                }

                if (line[index] != '{')
                    continue;

                if (unmatchedClosingBraces > 0)
                {
                    unmatchedClosingBraces--;
                    continue;
                }

                return IsImportExportOpeningBrace(preparedLines, currentLine, index);
            }
        }

        return false;
    }

    private static int SkipLeadingDecorators(string line)
    {
        var index = 0;
        while (index < line.Length)
        {
            while (index < line.Length && char.IsWhiteSpace(line[index]))
                index++;

            if (index >= line.Length || line[index] != '@')
                return index;

            index++;
            while (index < line.Length && (IsTypeScriptIdentifierPart(line[index]) || line[index] == '.'))
                index++;

            while (index < line.Length && char.IsWhiteSpace(line[index]))
                index++;

            if (index < line.Length && line[index] == '(')
            {
                var closeParen = ReferenceExtractor.FindMatchingChar(line, index, '(', ')');
                if (closeParen < 0)
                    return index;

                index = closeParen + 1;
            }

            while (index < line.Length && char.IsWhiteSpace(line[index]))
                index++;
        }

        return index;
    }

    private static bool IsImportExportOpeningBrace(IReadOnlyList<string> preparedLines, int openLineIndex, int openColumn)
    {
        var sameLinePrefix = preparedLines[openLineIndex].Substring(0, openColumn).Trim();
        if (sameLinePrefix.Length > 0)
            return IsImportBracePrefix(sameLinePrefix) || IsNamedExportBracePrefix(sameLinePrefix);

        for (var lineIndex = openLineIndex - 1; lineIndex >= 0; lineIndex--)
        {
            var previousLine = preparedLines[lineIndex].Trim();
            if (previousLine.Length == 0)
                continue;

            return IsImportBracePrefix(previousLine) || IsNamedExportBracePrefix(previousLine);
        }

        return false;
    }

    private static bool IsImportBracePrefix(string text)
    {
        if (text.IndexOf(';') >= 0 || ContainsTopLevelKeyword(text, "from"))
            return false;

        var index = 0;
        if (!TryConsumeKeyword(text, "import", ref index))
            return false;

        SkipWhitespace(text, ref index);
        if (index >= text.Length)
            return true;

        if (TryConsumeKeyword(text, "type", ref index))
        {
            SkipWhitespace(text, ref index);
            if (index >= text.Length)
                return true;
        }

        return text.TrimEnd().EndsWith(",", StringComparison.Ordinal);
    }

    private static bool ContainsTopLevelKeyword(string text, string keyword)
    {
        foreach (var _ in TypedLanguageReferenceExtractor.EnumerateTopLevelKeywordIndices(text, keyword))
            return true;

        return false;
    }

    private static bool IsNamedExportBracePrefix(string text)
    {
        var index = 0;
        if (!TryConsumeKeyword(text, "export", ref index))
            return false;

        SkipWhitespace(text, ref index);
        if (index >= text.Length)
            return true;

        if (!TryConsumeKeyword(text, "type", ref index))
            return false;

        SkipWhitespace(text, ref index);
        return index >= text.Length;
    }

    private static bool TryConsumeKeyword(string text, string keyword, ref int index)
    {
        if (index + keyword.Length > text.Length
            || string.CompareOrdinal(text, index, keyword, 0, keyword.Length) != 0)
        {
            return false;
        }

        var after = index + keyword.Length;
        if (after < text.Length && IsTypeScriptIdentifierPart(text[after]))
            return false;

        index = after;
        return true;
    }

    private static void SkipWhitespace(string text, ref int index)
    {
        while (index < text.Length && char.IsWhiteSpace(text[index]))
            index++;
    }

    private static bool IsTypeScriptIdentifierPart(char ch) =>
        ch == '_' || ch == '$' || char.IsLetterOrDigit(ch);

    private static bool IsTypeScriptIdentifier(string text)
    {
        if (text.Length == 0 || !IsTypeScriptIdentifierStart(text[0]))
            return false;

        for (var index = 1; index < text.Length; index++)
        {
            if (!IsTypeScriptIdentifierPart(text[index]))
                return false;
        }

        return true;
    }

    private static bool IsTypeScriptIdentifierStart(char ch) =>
        ch == '_' || ch == '$' || char.IsLetter(ch);
}
