using System.Text;
using System.Text.RegularExpressions;
using Regex = CodeIndex.Indexer.BoundedRegex;
using CodeIndex.Models;

namespace CodeIndex.Indexer;

public static partial class ReferenceExtractor
{
    private const string CSharpImplicitImplementationReferenceKind = "implicit_implementation";
    private sealed record CSharpStaticInterfaceMemberContract(string Name, string Kind, string? ParameterShape, string? ReturnTypeShape);
    private sealed record CSharpImplementedInterface(string Name, IReadOnlyDictionary<string, string> TypeArguments);

    private static void EmitCSharpAsyncIteratorReferences(
        long fileId,
        string[] lines,
        string[] structuralLines,
        IReadOnlyList<SymbolRecord> symbols,
        List<ReferenceRecord> references,
        HashSet<string> seen)
    {
        foreach (var symbol in symbols)
        {
            if (!IsCSharpAsyncIteratorFunction(symbol, structuralLines))
                continue;

            var lineIndex = Math.Clamp(symbol.StartLine - 1, 0, Math.Max(0, lines.Length - 1));
            if (lineIndex < 0 || lineIndex >= lines.Length)
                continue;

            var context = lines[lineIndex].Trim();
            var nameIndex = GetCSharpSymbolNameIndex(lines[lineIndex], symbol);

            if (!string.IsNullOrWhiteSpace(symbol.ReturnType))
            {
                var returnTypeStart = lines[lineIndex].IndexOf(symbol.ReturnType, StringComparison.Ordinal);
                if (returnTypeStart < 0)
                    returnTypeStart = Math.Max(0, symbol.StartColumn ?? 0);

                AddTypeExpressionSegments(
                    references,
                    seen,
                    fileId,
                    symbol.ReturnType!,
                    returnTypeStart,
                    context,
                    symbol.StartLine,
                    symbol,
                    "csharp");

                AddTypeReferenceSegment(
                    references,
                    seen,
                    fileId,
                    "IAsyncEnumerator",
                    nameIndex,
                    context,
                    symbol.StartLine,
                    symbol,
                    "csharp");
            }

            AddReference(
                references,
                seen,
                fileId,
                "GetAsyncEnumerator",
                nameIndex,
                CSharpImplicitImplementationReferenceKind,
                context,
                symbol.StartLine,
                symbol);

            var moveNextPosition = FindFirstCSharpYieldReturnPosition(structuralLines, symbol);
            var moveNextLine = moveNextPosition?.Line ?? symbol.StartLine;
            var moveNextContext = moveNextLine > 0 && moveNextLine <= lines.Length
                ? lines[moveNextLine - 1].Trim()
                : context;
            AddReference(
                references,
                seen,
                fileId,
                "MoveNextAsync",
                moveNextPosition?.Column ?? nameIndex,
                CSharpImplicitImplementationReferenceKind,
                moveNextContext,
                moveNextLine,
                symbol);
        }
    }

    private static void EmitCSharpStaticInterfaceMemberImplementationReferences(
        long fileId,
        string[] lines,
        string[] structuralLines,
        IReadOnlyList<SymbolRecord> symbols,
        IReadOnlyList<SymbolRecord> workspaceSymbols,
        List<ReferenceRecord> references,
        HashSet<string> seen)
    {
        var interfaceMembersByType = workspaceSymbols
            .Where(IsCSharpStaticInterfaceMemberContract)
            .GroupBy(symbol => symbol.ContainerName!, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group
                    .Select(symbol => new CSharpStaticInterfaceMemberContract(
                        symbol.Name,
                        symbol.Kind,
                        GetCSharpCallableParameterShape(symbol.Signature),
                        NormalizeCSharpTypeArgumentShape(symbol.ReturnType ?? string.Empty)))
                    .ToList(),
                StringComparer.Ordinal);
        if (interfaceMembersByType.Count == 0)
            return;

        var interfaceGenericParameters = BuildCSharpInterfaceGenericParameterLookup(workspaceSymbols);
        var staticMembersByContainer = symbols
            .Where(symbol => symbol.Kind is "function" or "operator" or "property"
                             && !string.IsNullOrWhiteSpace(symbol.ContainerName)
                             && !string.IsNullOrWhiteSpace(symbol.Signature)
                             && ContainsCSharpWord(symbol.Signature!, "static"))
            .GroupBy(symbol => symbol.ContainerName!, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.ToList(),
                StringComparer.Ordinal);

        foreach (var typeSymbol in symbols)
        {
            if (typeSymbol.Kind is not ("class" or "struct")
                || typeSymbol.BodyStartLine == null
                || typeSymbol.BodyEndLine == null)
            {
                continue;
            }

            var implementedInterfaces = ExtractCSharpImplementedInterfaces(
                CollectCSharpRecordHeader(structuralLines, typeSymbol.StartLine).Text,
                interfaceGenericParameters);
            if (implementedInterfaces.Count == 0)
                continue;

            foreach (var implementedInterface in implementedInterfaces)
            {
                if (!interfaceMembersByType.TryGetValue(implementedInterface.Name, out var interfaceMembers))
                    continue;

                if (!staticMembersByContainer.TryGetValue(typeSymbol.Name, out var implementationMembers))
                    continue;

                foreach (var implementation in implementationMembers)
                {
                    if (!IsCSharpStaticMemberImplementationCandidate(typeSymbol, implementation))
                        continue;

                    if (!interfaceMembers.Any(contract => MatchesCSharpStaticInterfaceMemberContract(contract, implementation, implementedInterface.TypeArguments)))
                        continue;

                    var lineIndex = implementation.StartLine - 1;
                    if (lineIndex < 0 || lineIndex >= lines.Length)
                        continue;

                    var context = lines[lineIndex].Trim();
                    AddReference(
                        references,
                        seen,
                        fileId,
                        implementation.Name,
                        GetCSharpSymbolNameIndex(lines[lineIndex], implementation),
                        CSharpImplicitImplementationReferenceKind,
                        context,
                        implementation.StartLine,
                        implementation);
                }
            }
        }
    }

    private static bool IsCSharpStaticInterfaceMemberContract(SymbolRecord symbol)
    {
        if (symbol.Kind is not ("function" or "property")
            || symbol.ContainerKind != "interface"
            || string.IsNullOrWhiteSpace(symbol.ContainerName)
            || string.IsNullOrWhiteSpace(symbol.Name)
            || string.IsNullOrWhiteSpace(symbol.Signature))
        {
            return false;
        }

        return ContainsCSharpWord(symbol.Signature!, "static")
               && (ContainsCSharpWord(symbol.Signature!, "abstract")
                   || ContainsCSharpWord(symbol.Signature!, "virtual"));
    }

    private static bool IsCSharpStaticMemberImplementationCandidate(SymbolRecord typeSymbol, SymbolRecord member)
    {
        if (member.Kind is not ("function" or "property")
            || string.IsNullOrWhiteSpace(member.Name)
            || string.IsNullOrWhiteSpace(member.Signature)
            || member.StartLine < typeSymbol.BodyStartLine
            || member.EndLine > typeSymbol.BodyEndLine)
        {
            return false;
        }

        if (!string.Equals(member.ContainerName, typeSymbol.Name, StringComparison.Ordinal))
            return false;

        return ContainsCSharpWord(member.Signature!, "static");
    }

    private static bool MatchesCSharpStaticInterfaceMemberContract(
        CSharpStaticInterfaceMemberContract contract,
        SymbolRecord implementation,
        IReadOnlyDictionary<string, string> typeArguments)
    {
        if (!string.Equals(contract.Name, implementation.Name, StringComparison.Ordinal)
            || !string.Equals(contract.Kind, implementation.Kind, StringComparison.Ordinal))
        {
            return false;
        }

        var implementationParameterShape = GetCSharpCallableParameterShape(implementation.Signature);
        var contractParameterShape = SubstituteCSharpGenericTypeParameters(contract.ParameterShape, typeArguments);
        if (!string.Equals(contractParameterShape, implementationParameterShape, StringComparison.Ordinal))
            return false;

        var contractReturnTypeShape = SubstituteCSharpGenericTypeParameters(contract.ReturnTypeShape, typeArguments);
        var implementationReturnTypeShape = NormalizeCSharpTypeArgumentShape(implementation.ReturnType ?? string.Empty);
        return string.Equals(contractReturnTypeShape, implementationReturnTypeShape, StringComparison.Ordinal);
    }

    private static string? GetCSharpCallableParameterShape(string? signature)
    {
        if (string.IsNullOrWhiteSpace(signature))
            return null;
        if (!TryFindCallableParameterList(signature!, "csharp", out _, out var paramStart, out var paramEnd))
            return null;

        var parameterList = signature!.Substring(paramStart, paramEnd - paramStart).Trim();
        if (parameterList.Length == 0)
            return string.Empty;

        return string.Join(
            ",",
            SplitTopLevelCommaSpans(parameterList)
                .Select(span => NormalizeCSharpParameterTypeShape(parameterList.Substring(span.Start, span.Length))));
    }

    private static string NormalizeCSharpParameterTypeShape(string parameter)
    {
        var text = TrimCSharpParameterDefaultValue(parameter).Trim();
        var refKind = string.Empty;
        foreach (var modifier in new[] { "ref", "out", "in" })
        {
            if (StartsWithCSharpWord(text, modifier))
            {
                refKind = modifier + ":";
                text = text.Substring(modifier.Length).TrimStart();
                break;
            }
        }

        while (true)
        {
            var before = text;
            text = StripLeadingCSharpParameterModifier(text);
            if (string.Equals(before, text, StringComparison.Ordinal))
                break;
        }

        var nameStart = FindTrailingCSharpParameterNameStart(text);
        if (nameStart > 0)
            text = text.Substring(0, nameStart).TrimEnd();

        return refKind + new string(text.Where(static ch => !char.IsWhiteSpace(ch)).ToArray());
    }

    private static string StripLeadingCSharpParameterModifier(string text)
    {
        foreach (var modifier in new[] { "this", "scoped" })
        {
            if (StartsWithCSharpWord(text, modifier))
                return text.Substring(modifier.Length).TrimStart();
        }

        return text;
    }

    private static string? SubstituteCSharpGenericTypeParameters(string? shape, IReadOnlyDictionary<string, string> typeArguments)
    {
        if (string.IsNullOrWhiteSpace(shape) || typeArguments.Count == 0)
            return shape;

        var sb = new StringBuilder(shape.Length);
        for (var i = 0; i < shape.Length;)
        {
            if (IsCSharpIdentifierPart(shape[i]))
            {
                var start = i;
                while (i < shape.Length && IsCSharpIdentifierPart(shape[i]))
                    i++;

                var token = shape.Substring(start, i - start);
                sb.Append(typeArguments.TryGetValue(token, out var replacement) ? replacement : token);
                continue;
            }

            sb.Append(shape[i]);
            i++;
        }

        return sb.ToString();
    }

    private static string TrimCSharpParameterDefaultValue(string parameter)
    {
        int angleDepth = 0;
        int parenDepth = 0;
        int bracketDepth = 0;
        for (var i = 0; i < parameter.Length; i++)
        {
            var ch = parameter[i];
            if (ch == '<')
                angleDepth++;
            else if (ch == '>' && angleDepth > 0)
                angleDepth--;
            else if (ch == '(')
                parenDepth++;
            else if (ch == ')' && parenDepth > 0)
                parenDepth--;
            else if (ch == '[')
                bracketDepth++;
            else if (ch == ']' && bracketDepth > 0)
                bracketDepth--;
            else if (ch == '=' && angleDepth == 0 && parenDepth == 0 && bracketDepth == 0)
                return parameter.Substring(0, i);
        }

        return parameter;
    }

    private static int FindTrailingCSharpParameterNameStart(string text)
    {
        var end = text.Length - 1;
        while (end >= 0 && char.IsWhiteSpace(text[end]))
            end--;
        if (end < 0 || !IsCSharpIdentifierPart(text[end]))
            return -1;

        var start = end;
        while (start >= 0 && IsCSharpIdentifierPart(text[start]))
            start--;

        return start + 1;
    }

    private static bool StartsWithCSharpWord(string text, string word)
    {
        if (!text.StartsWith(word, StringComparison.Ordinal))
            return false;

        return text.Length == word.Length || !IsCSharpIdentifierPart(text[word.Length]);
    }

    private static Dictionary<string, List<string>> BuildCSharpInterfaceGenericParameterLookup(IReadOnlyList<SymbolRecord> symbols)
    {
        var lookup = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var symbol in symbols)
        {
            if (symbol.Kind != "interface"
                || string.IsNullOrWhiteSpace(symbol.Name)
                || string.IsNullOrWhiteSpace(symbol.Signature))
            {
                continue;
            }

            var parameters = ExtractCSharpGenericArgumentList(symbol.Signature!, symbol.Name)
                .Select(ExtractCSharpGenericParameterName)
                .Where(static name => !string.IsNullOrWhiteSpace(name))
                .ToList()!;
            if (parameters.Count > 0)
                lookup[symbol.Name] = parameters;
        }

        return lookup;
    }

    private static List<CSharpImplementedInterface> ExtractCSharpImplementedInterfaces(
        string headerText,
        IReadOnlyDictionary<string, List<string>> interfaceGenericParameters)
    {
        var interfaces = new List<CSharpImplementedInterface>();
        if (string.IsNullOrWhiteSpace(headerText))
            return interfaces;

        var colonIndex = FindSignatureColonIndex(headerText);
        if (colonIndex < 0)
            return interfaces;

        var baseList = headerText.Substring(colonIndex + 1);
        var whereMatch = CSharpWhereClauseRegex.Match(baseList);
        if (whereMatch.Success)
            baseList = baseList.Substring(0, whereMatch.Index);
        baseList = TrimTrailingTypeListTerminator(baseList);

        foreach (var (segmentStart, segmentLength) in SplitTopLevelCommaSpans(baseList))
        {
            var rawSegment = baseList.Substring(segmentStart, segmentLength).Trim();
            var name = ExtractBareTypeName(rawSegment);
            if (string.IsNullOrWhiteSpace(name))
                continue;

            var typeArguments = BuildCSharpImplementedInterfaceTypeArgumentMap(
                rawSegment,
                name!,
                interfaceGenericParameters);
            interfaces.Add(new CSharpImplementedInterface(name!, typeArguments));
        }

        return interfaces;
    }

    private static Dictionary<string, string> BuildCSharpImplementedInterfaceTypeArgumentMap(
        string rawSegment,
        string interfaceName,
        IReadOnlyDictionary<string, List<string>> interfaceGenericParameters)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!interfaceGenericParameters.TryGetValue(interfaceName, out var parameters) || parameters.Count == 0)
            return map;

        var arguments = ExtractCSharpGenericArgumentList(rawSegment, interfaceName)
            .Select(NormalizeCSharpTypeArgumentShape)
            .ToList();
        var count = Math.Min(parameters.Count, arguments.Count);
        for (var i = 0; i < count; i++)
            map[parameters[i]] = arguments[i];

        return map;
    }

    private static List<string> ExtractCSharpGenericArgumentList(string text, string typeName)
    {
        var typeNameIndex = text.IndexOf(typeName, StringComparison.Ordinal);
        if (typeNameIndex < 0)
            return [];

        var genericStart = SkipWhitespace(text, typeNameIndex + typeName.Length);
        if (genericStart >= text.Length || text[genericStart] != '<')
            return [];

        var genericEnd = FindMatchingChar(text, genericStart, '<', '>');
        if (genericEnd <= genericStart)
            return [];

        var list = text.Substring(genericStart + 1, genericEnd - genericStart - 1);
        return SplitTopLevelCommaSpans(list)
            .Select(span => list.Substring(span.Start, span.Length).Trim())
            .Where(static item => item.Length > 0)
            .ToList();
    }

    private static string ExtractCSharpGenericParameterName(string parameter)
    {
        var text = parameter.Trim();
        var nameStart = FindTrailingCSharpParameterNameStart(text);
        return nameStart >= 0 ? text.Substring(nameStart).Trim() : text;
    }

    private static string NormalizeCSharpTypeArgumentShape(string argument)
    {
        return new string(argument.Where(static ch => !char.IsWhiteSpace(ch)).ToArray());
    }

    private static bool IsCSharpAsyncIteratorFunction(SymbolRecord symbol, string[] structuralLines)
    {
        if (symbol.Kind != "function" || string.IsNullOrWhiteSpace(symbol.Signature))
            return false;
        if (!ContainsCSharpWord(symbol.Signature!, "async"))
            return false;

        return ContainsCSharpAsyncIteratorReturnType(symbol.ReturnType)
            || FindFirstCSharpYieldReturnPosition(structuralLines, symbol).HasValue;
    }

    private static bool ContainsCSharpAsyncIteratorReturnType(string? returnType)
        => !string.IsNullOrWhiteSpace(returnType)
            && (ContainsCSharpWord(returnType, "IAsyncEnumerable")
                || ContainsCSharpWord(returnType, "IAsyncEnumerator"));

    private static (int Line, int Column)? FindFirstCSharpYieldReturnPosition(string[] structuralLines, SymbolRecord symbol)
    {
        var start = Math.Max(0, (symbol.BodyStartLine ?? symbol.StartLine) - 1);
        var end = Math.Min(structuralLines.Length - 1, (symbol.BodyEndLine ?? symbol.EndLine) - 1);
        if (end < start)
            return null;

        for (var i = start; i <= end; i++)
        {
            var yieldIndex = IndexOfCSharpWordPair(structuralLines[i], "yield", "return");
            if (yieldIndex >= 0)
                return (i + 1, yieldIndex);
        }

        return null;
    }

    private static int GetCSharpSymbolNameIndex(string line, SymbolRecord symbol)
    {
        if (!string.IsNullOrWhiteSpace(symbol.Name))
        {
            var index = line.IndexOf(symbol.Name, StringComparison.Ordinal);
            if (index >= 0)
                return index;
        }

        return Math.Max(0, symbol.StartColumn ?? 0);
    }

    private static bool ContainsCSharpWordPair(string text, string first, string second)
        => IndexOfCSharpWordPair(text, first, second) >= 0;

    private static int IndexOfCSharpWordPair(string text, string first, string second)
    {
        var firstIndex = IndexOfCSharpWord(text, first, 0);
        if (firstIndex < 0)
            return -1;

        return IndexOfCSharpWord(text, second, firstIndex + first.Length) >= 0
            ? firstIndex
            : -1;
    }

    private static bool ContainsCSharpWord(string text, string word)
        => IndexOfCSharpWord(text, word, 0) >= 0;

    private static int IndexOfCSharpWord(string text, string word, int startIndex)
    {
        var index = Math.Max(0, startIndex);
        while (index < text.Length)
        {
            index = text.IndexOf(word, index, StringComparison.Ordinal);
            if (index < 0)
                return -1;

            var before = index == 0 ? '\0' : text[index - 1];
            var afterIndex = index + word.Length;
            var after = afterIndex >= text.Length ? '\0' : text[afterIndex];
            if (!IsCSharpIdentifierPart(before) && !IsCSharpIdentifierPart(after))
                return index;

            index += word.Length;
        }

        return -1;
    }

    internal static void AddTypeReferenceSegment(
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string segment,
        int startInLine,
        string context,
        int lineNumber,
        SymbolRecord? container,
        string language,
        bool isEscapedCSharpIdentifier = false,
        IReadOnlySet<string>? ignoredSegments = null,
        string referenceKind = "type_reference")
    {
        if (segment.Length == 0 || IsIgnoredTypeReferenceSegment(language, segment, isEscapedCSharpIdentifier, ignoredSegments))
            return;

        int column = startInLine + 1; // 1-based / 1始まり
        var dedupeKey = BuildReferenceDedupeKey(fileId, language, lineNumber, column, referenceKind, segment, container);
        if (!seen.Add(dedupeKey))
            return;

        references.Add(new ReferenceRecord
        {
            FileId = fileId,
            SymbolName = segment,
            ReferenceKind = referenceKind,
            Line = lineNumber,
            Column = column,
            Context = context,
            ContainerKind = container?.Kind,
            ContainerName = container?.Name,
        });
    }

    private static SymbolRecord? FindInnermostContainer(IReadOnlyList<SymbolRecord> candidates, int lineNumber)
    {
        foreach (var candidate in candidates)
        {
            if (candidate.BodyStartLine!.Value <= lineNumber && candidate.BodyEndLine!.Value >= lineNumber)
                return candidate;
        }

        return null;
    }

    internal sealed class InnermostContainerResolver
    {
        private readonly IReadOnlyList<SymbolRecord> candidates;
        private readonly List<(SymbolRecord Symbol, int SpanLength, int OriginalIndex)> candidatesByStart;
        private readonly SortedSet<ActiveContainer> activeContainers = new();
        private int nextCandidateIndex;
        private int currentLine;
        private int? cachedLine;
        private SymbolRecord? cachedContainer;

        internal InnermostContainerResolver(IReadOnlyList<SymbolRecord> candidates)
        {
            this.candidates = candidates;
            candidatesByStart = candidates
                .Select((symbol, index) => (Symbol: symbol, SpanLength: GetContainerSpanLength(symbol), OriginalIndex: index))
                .OrderBy(item => item.Symbol.BodyStartLine!.Value)
                .ThenBy(item => item.Symbol.BodyEndLine!.Value)
                .ThenBy(item => item.SpanLength)
                .ThenBy(item => item.OriginalIndex)
                .ToList();
        }

        internal SymbolRecord? Find(int lineNumber)
        {
            if (cachedLine == lineNumber)
                return cachedContainer;

            if (lineNumber < currentLine)
                return Cache(lineNumber, FindInnermostContainer(candidates, lineNumber));

            AdvanceTo(lineNumber);
            return Cache(lineNumber, activeContainers.Count == 0 ? null : activeContainers.Min.Symbol);
        }

        private void AdvanceTo(int lineNumber)
        {
            while (nextCandidateIndex < candidatesByStart.Count
                   && candidatesByStart[nextCandidateIndex].Symbol.BodyStartLine!.Value <= lineNumber)
            {
                var candidate = candidatesByStart[nextCandidateIndex];
                activeContainers.Add(new ActiveContainer(candidate.Symbol, candidate.SpanLength, candidate.OriginalIndex));
                nextCandidateIndex++;
            }

            activeContainers.RemoveWhere(active => active.Symbol.BodyEndLine!.Value < lineNumber);
            currentLine = lineNumber;
        }

        private SymbolRecord? Cache(int lineNumber, SymbolRecord? container)
        {
            cachedLine = lineNumber;
            cachedContainer = container;
            return container;
        }

        private static int GetContainerSpanLength(SymbolRecord symbol) =>
            (symbol.BodyEndLine ?? symbol.EndLine) - (symbol.BodyStartLine ?? symbol.StartLine);

        private readonly record struct ActiveContainer(SymbolRecord Symbol, int SpanLength, int OriginalIndex) : IComparable<ActiveContainer>
        {
            public int CompareTo(ActiveContainer other)
            {
                var spanComparison = SpanLength.CompareTo(other.SpanLength);
                if (spanComparison != 0)
                    return spanComparison;

                return OriginalIndex.CompareTo(other.OriginalIndex);
            }
        }
    }

    private static bool CanAttachCSharpXmlDocCommentToNextDeclaration(
        SymbolRecord? innermostContainer,
        IReadOnlyList<SymbolRecord>? scopeCandidates,
        List<List<(int start, int end)>>? csharpAttrRanges,
        string[] preparedLines,
        int lineNumber,
        SymbolRecord documentedContainer)
    {
        if (!HasOnlyCSharpWhitespaceOrAttributesBetweenCommentAndDeclaration(
                csharpAttrRanges,
                preparedLines,
                lineNumber,
                documentedContainer.StartLine))
        {
            return false;
        }

        if (innermostContainer != null
            && innermostContainer.Kind is not "class" or "struct" or "interface" or "enum" or "namespace")
        {
            return false;
        }

        var enclosingScope = scopeCandidates == null
            ? null
            : FindInnermostContainer(scopeCandidates, lineNumber);
        if (enclosingScope?.BodyStartLine == null)
            return true;

        return IsAtCSharpXmlDocAttachmentDepth(enclosingScope, preparedLines, lineNumber);
    }

    private static bool HasOnlyCSharpWhitespaceOrAttributesBetweenCommentAndDeclaration(
        List<List<(int start, int end)>>? csharpAttrRanges,
        string[] preparedLines,
        int commentLineNumber,
        int declarationLineNumber)
    {
        var startLineIndex = Math.Max(commentLineNumber, 0);
        var endLineIndex = Math.Min(declarationLineNumber - 1, preparedLines.Length);
        for (var lineIndex = startLineIndex; lineIndex < endLineIndex; lineIndex++)
        {
            var line = preparedLines[lineIndex];
            if (string.IsNullOrWhiteSpace(line))
                continue;

            if (IsCSharpAttributeOnlyLine(line, csharpAttrRanges?[lineIndex]))
                continue;

            return false;
        }

        return true;
    }

    private static bool IsCSharpAttributeOnlyLine(string preparedLine, List<(int start, int end)>? ranges)
    {
        if (ranges == null || ranges.Count == 0)
            return false;

        for (var i = 0; i < preparedLine.Length; i++)
        {
            if (char.IsWhiteSpace(preparedLine[i]))
                continue;

            var covered = false;
            foreach (var (start, end) in ranges)
            {
                if (i >= start && i < end)
                {
                    covered = true;
                    break;
                }
            }

            if (!covered)
                return false;
        }

        return true;
    }

    private static bool IsAtCSharpXmlDocAttachmentDepth(
        SymbolRecord enclosingScope,
        string[] preparedLines,
        int lineNumber)
    {
        var scopeBodyStartIndex = enclosingScope.BodyStartLine!.Value - 1;
        var commentLineIndex = lineNumber - 1;
        if (scopeBodyStartIndex < 0
            || scopeBodyStartIndex >= preparedLines.Length
            || scopeBodyStartIndex >= commentLineIndex)
        {
            return true;
        }

        var sawScopeOpenBrace = false;
        var nestedBraceDepth = 0;
        var angleDepth = 0;
        var parenDepth = 0;
        var bracketDepth = 0;
        var topLevelExecutableContinuation = false;
        var topLevelArrowExpressionContinuation = false;

        for (var i = scopeBodyStartIndex; i < commentLineIndex && i < preparedLines.Length; i++)
        {
            var line = preparedLines[i];
            for (var j = 0; j < line.Length; j++)
            {
                var ch = line[j];
                if (!sawScopeOpenBrace)
                {
                    if (ch == '{')
                        sawScopeOpenBrace = true;

                    continue;
                }

                if (nestedBraceDepth == 0)
                {
                    if (ch == '<')
                    {
                        angleDepth++;
                        continue;
                    }

                    if (ch == '>' && angleDepth > 0)
                    {
                        angleDepth--;
                        continue;
                    }

                    if (IsCSharpTopLevelArrowToken(line, j))
                    {
                        topLevelExecutableContinuation = true;
                        topLevelArrowExpressionContinuation = !IsCSharpArrowBlockStart(line, j + 2);
                        j++;
                        continue;
                    }

                    if (IsCSharpTopLevelAssignmentOperator(line, j))
                    {
                        topLevelExecutableContinuation = true;
                    }
                }

                if (ch == '{')
                {
                    nestedBraceDepth++;
                }
                else if (ch == '}')
                {
                    if (nestedBraceDepth == 0)
                        return false;

                    nestedBraceDepth--;
                }
                else if (ch == '(')
                {
                    parenDepth++;
                }
                else if (ch == ')' && parenDepth > 0)
                {
                    parenDepth--;
                }
                else if (ch == '[')
                {
                    bracketDepth++;
                }
                else if (ch == ']' && bracketDepth > 0)
                {
                    bracketDepth--;
                }
                else if (nestedBraceDepth == 0
                         && ch == ';'
                         && parenDepth == 0
                         && bracketDepth == 0)
                {
                    topLevelExecutableContinuation = false;
                    topLevelArrowExpressionContinuation = false;
                }
            }
        }

        return !sawScopeOpenBrace
            || (nestedBraceDepth == 0
                && angleDepth == 0
                && parenDepth == 0
                && bracketDepth == 0
                && !topLevelExecutableContinuation
                && !topLevelArrowExpressionContinuation);
    }

    private static bool[] BuildCSharpBlockCommentLines(string[] lines)
    {
        var insideBlockComment = new bool[lines.Length];
        var inBlockComment = false;
        var inVerbatimString = false;
        var rawStringDelimiterLength = 0;

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            insideBlockComment[i] = inBlockComment;

            var index = 0;
            while (index < line.Length)
            {
                if (inBlockComment)
                {
                    var closeIndex = line.IndexOf("*/", index, StringComparison.Ordinal);
                    if (closeIndex < 0)
                        break;

                    index = closeIndex + 2;
                    inBlockComment = false;
                    continue;
                }

                if (rawStringDelimiterLength > 0)
                {
                    var closeCandidateIndex = index;
                    while (closeCandidateIndex < line.Length && char.IsWhiteSpace(line[closeCandidateIndex]))
                        closeCandidateIndex++;

                    var closeLength = CountCharacterRun(line, closeCandidateIndex, '"');
                    if (closeLength >= rawStringDelimiterLength
                        && closeLength > 0)
                    {
                        rawStringDelimiterLength = 0;
                        index = closeCandidateIndex + closeLength;
                        continue;
                    }

                    break;
                }

                if (inVerbatimString)
                {
                    if (line[index] == '"' && index + 1 < line.Length && line[index + 1] == '"')
                    {
                        index += 2;
                        continue;
                    }

                    if (line[index] == '"')
                    {
                        index++;
                        inVerbatimString = false;
                        continue;
                    }

                    index++;
                    continue;
                }

                if (StartsWithOrdinal(line, index, "//"))
                    break;

                if (StartsWithOrdinal(line, index, "/*"))
                {
                    inBlockComment = true;
                    index += 2;
                    continue;
                }

                if (TryStartCSharpRawString(line, index, out var rawOpeningLength, out var rawDelimiterLength))
                {
                    rawStringDelimiterLength = rawDelimiterLength;
                    index += rawOpeningLength;
                    continue;
                }

                if (TryStartCSharpVerbatimString(line, index, out var verbatimOpeningLength))
                {
                    inVerbatimString = true;
                    index += verbatimOpeningLength;
                    continue;
                }

                if (TryStartCSharpRegularString(line, index, out var regularOpeningLength))
                {
                    index += regularOpeningLength;
                    while (index < line.Length)
                    {
                        if (line[index] == '\\')
                        {
                            index += Math.Min(2, line.Length - index);
                            continue;
                        }

                        if (line[index] == '"')
                        {
                            index++;
                            break;
                        }

                        index++;
                    }

                    continue;
                }

                if (line[index] == '\'')
                {
                    index++;
                    while (index < line.Length)
                    {
                        if (line[index] == '\\')
                        {
                            index += Math.Min(2, line.Length - index);
                            continue;
                        }

                        if (line[index] == '\'')
                        {
                            index++;
                            break;
                        }

                        index++;
                    }

                    continue;
                }

                index++;
            }
        }

        return insideBlockComment;
    }

    private static bool IsCSharpTopLevelAssignmentOperator(string line, int index)
    {
        if (index < 0 || index >= line.Length || line[index] != '=')
            return false;

        var previous = index > 0 ? line[index - 1] : '\0';
        var next = index + 1 < line.Length ? line[index + 1] : '\0';
        return previous is not ('=' or '!' or '<' or '>')
            && next is not ('=' or '>');
    }

    private static bool IsCSharpTopLevelArrowToken(string line, int index) =>
        index >= 0
        && index + 1 < line.Length
        && line[index] == '='
        && line[index + 1] == '>';

    private static bool IsCSharpArrowBlockStart(string line, int index)
    {
        while (index < line.Length && char.IsWhiteSpace(line[index]))
            index++;

        return index < line.Length && line[index] == '{';
    }

    private static int GetCSharpSameLineDocumentedDeclarationStartColumn(
        string originalLine,
        int commentEndExclusive,
        bool nextDelimitedDocComment)
    {
        if (nextDelimitedDocComment
            || commentEndExclusive < 0
            || commentEndExclusive + 1 >= originalLine.Length
            || originalLine[commentEndExclusive] != '*'
            || originalLine[commentEndExclusive + 1] != '/')
        {
            return -1;
        }

        var column = commentEndExclusive + 2;
        while (column < originalLine.Length && char.IsWhiteSpace(originalLine[column]))
            column++;

        return column < originalLine.Length ? column : -1;
    }

    private static bool HasOnlyCSharpWhitespaceOrAttributesAfterColumn(
        string preparedLine,
        List<(int start, int end)>? ranges,
        int startColumn)
    {
        if (startColumn < 0 || startColumn >= preparedLine.Length)
            return true;

        for (var i = startColumn; i < preparedLine.Length; i++)
        {
            if (char.IsWhiteSpace(preparedLine[i]))
                continue;

            if (ranges != null)
            {
                var covered = false;
                foreach (var (start, end) in ranges)
                {
                    if (i >= start && i < end)
                    {
                        covered = true;
                        break;
                    }
                }

                if (covered)
                    continue;
            }

            return false;
        }

        return true;
    }

    private static bool TryGetJvmDocCommentSpan(
        string originalLine,
        bool inDelimitedDocComment,
        out int commentStart,
        out int commentEndExclusive,
        out int sameLineDeclarationStartColumn,
        out bool nextDelimitedDocComment)
    {
        commentStart = -1;
        commentEndExclusive = -1;
        sameLineDeclarationStartColumn = -1;
        nextDelimitedDocComment = inDelimitedDocComment;

        var lineStart = 0;
        while (lineStart < originalLine.Length && char.IsWhiteSpace(originalLine[lineStart]))
            lineStart++;

        if (!inDelimitedDocComment)
        {
            if (lineStart + 3 > originalLine.Length
                || originalLine[lineStart] != '/'
                || originalLine[lineStart + 1] != '*'
                || originalLine[lineStart + 2] != '*')
            {
                return false;
            }

            commentStart = lineStart + 3;
        }
        else
        {
            commentStart = lineStart;
            if (commentStart < originalLine.Length && originalLine[commentStart] == '*')
            {
                if (commentStart + 1 < originalLine.Length && originalLine[commentStart + 1] == '/')
                {
                    commentEndExclusive = commentStart;
                    nextDelimitedDocComment = false;
                    sameLineDeclarationStartColumn = GetJvmSameLineDeclarationStartColumn(originalLine, commentStart);
                    return true;
                }

                commentStart++;
                if (commentStart < originalLine.Length && originalLine[commentStart] == ' ')
                    commentStart++;
            }
        }

        var closeIndex = originalLine.IndexOf("*/", commentStart, StringComparison.Ordinal);
        if (closeIndex >= 0)
        {
            commentEndExclusive = closeIndex;
            nextDelimitedDocComment = false;
            sameLineDeclarationStartColumn = GetJvmSameLineDeclarationStartColumn(originalLine, closeIndex);
        }
        else
        {
            commentEndExclusive = originalLine.Length;
            nextDelimitedDocComment = true;
        }

        return true;
    }

    private static int GetJvmSameLineDeclarationStartColumn(string originalLine, int commentEndExclusive)
    {
        if (commentEndExclusive + 1 >= originalLine.Length
            || originalLine[commentEndExclusive] != '*'
            || originalLine[commentEndExclusive + 1] != '/')
        {
            return -1;
        }

        var column = commentEndExclusive + 2;
        while (column < originalLine.Length && char.IsWhiteSpace(originalLine[column]))
            column++;

        return column < originalLine.Length ? column : -1;
    }

    private static SymbolRecord? FindJvmDocumentedContainer(
        IReadOnlyList<SymbolRecord> candidates,
        IReadOnlyList<string> originalLines,
        string structuralLine,
        int lineNumber,
        int sameLineDeclarationStartColumn)
    {
        var innermostContainer = FindInnermostContainer(candidates, lineNumber);
        if (innermostContainer?.Kind is "function" or "property")
            return null;

        var sameLineCandidate = FindSameLineDocumentedContainer(
            candidates,
            structuralLine,
            lineNumber,
            sameLineDeclarationStartColumn);
        if (sameLineCandidate != null)
            return sameLineCandidate;

        SymbolRecord? best = null;
        foreach (var candidate in candidates)
        {
            if (candidate.StartLine <= lineNumber)
                continue;
            if (!HasOnlyJvmDocTriviaBeforeDeclaration(originalLines, lineNumber, candidate.StartLine))
                continue;

            if (best == null
                || candidate.StartLine < best.StartLine
                || (candidate.StartLine == best.StartLine
                    && ((candidate.BodyEndLine ?? candidate.EndLine) - (candidate.BodyStartLine ?? candidate.StartLine))
                       < ((best.BodyEndLine ?? best.EndLine) - (best.BodyStartLine ?? best.StartLine))))
            {
                best = candidate;
            }
        }

        return best;
    }

    private static bool HasOnlyJvmDocTriviaBeforeDeclaration(
        IReadOnlyList<string> originalLines,
        int docLineNumber,
        int declarationLineNumber)
    {
        for (var lineIndex = docLineNumber; lineIndex < declarationLineNumber - 1 && lineIndex < originalLines.Count; lineIndex++)
        {
            var trimmed = originalLines[lineIndex].TrimStart();
            if (trimmed.Length == 0
                || trimmed.StartsWith("/**", StringComparison.Ordinal)
                || trimmed.StartsWith("*", StringComparison.Ordinal)
                || trimmed.StartsWith("@", StringComparison.Ordinal))
            {
                continue;
            }

            return false;
        }

        return true;
    }

    private static SymbolRecord? FindDocumentedContainer(
        IReadOnlyList<SymbolRecord> candidates,
        string structuralLine,
        string preparedLine,
        List<(int start, int end)>? csharpAttrRangesOnLine,
        int lineNumber,
        int sameLineDeclarationStartColumn)
    {
        var sameLineCandidate = FindSameLineDocumentedContainer(
            candidates,
            structuralLine,
            lineNumber,
            sameLineDeclarationStartColumn);
        if (sameLineCandidate != null)
            return sameLineCandidate;
        if (sameLineDeclarationStartColumn >= 0
            && !HasOnlyCSharpWhitespaceOrAttributesAfterColumn(
                preparedLine,
                csharpAttrRangesOnLine,
                sameLineDeclarationStartColumn))
        {
            return null;
        }

        SymbolRecord? best = null;
        foreach (var candidate in candidates)
        {
            if (candidate.StartLine <= lineNumber)
                continue;

            if (best == null
                || candidate.StartLine < best.StartLine
                || (candidate.StartLine == best.StartLine
                    && ((candidate.BodyEndLine ?? candidate.EndLine) - (candidate.BodyStartLine ?? candidate.StartLine))
                       < ((best.BodyEndLine ?? best.EndLine) - (best.BodyStartLine ?? best.StartLine))))
            {
                best = candidate;
            }
        }

        return best;
    }

    private static SymbolRecord? FindSameLineDocumentedContainer(
        IReadOnlyList<SymbolRecord> candidates,
        string structuralLine,
        int lineNumber,
        int sameLineDeclarationStartColumn)
    {
        if (sameLineDeclarationStartColumn < 0)
            return null;

        SymbolRecord? best = null;
        var bestStartColumn = int.MaxValue;
        var bestSpanLength = int.MaxValue;
        var bestKindRank = int.MaxValue;

        foreach (var candidate in candidates)
        {
            if (candidate.StartLine != lineNumber
                || candidate.EndLine != lineNumber
                || string.IsNullOrEmpty(candidate.Signature))
            {
                continue;
            }

            if (!TryGetSameLineSignatureSpan(candidate, structuralLine, out var startColumn, out var endColumn)
                || startColumn < sameLineDeclarationStartColumn)
            {
                continue;
            }

            var spanLength = endColumn - startColumn;
            var kindRank = GetSameLineContainerKindRank(candidate.Kind);
            if (best == null
                || startColumn < bestStartColumn
                || (startColumn == bestStartColumn && spanLength < bestSpanLength)
                || (startColumn == bestStartColumn && spanLength == bestSpanLength && kindRank < bestKindRank))
            {
                best = candidate;
                bestStartColumn = startColumn;
                bestSpanLength = spanLength;
                bestKindRank = kindRank;
            }
        }

        return best;
    }

    private static SymbolRecord? FindInnermostSameLineCSharpContainer(
        IReadOnlyList<SymbolRecord> candidates,
        string structuralLine,
        int lineNumber,
        int column)
    {
        SymbolRecord? best = null;
        var bestStartColumn = -1;
        var bestSpanLength = int.MaxValue;
        var bestKindRank = int.MaxValue;

        foreach (var candidate in candidates)
        {
            if (candidate.BodyStartLine == null
                || candidate.BodyEndLine == null
                || candidate.BodyStartLine.Value > lineNumber
                || candidate.BodyEndLine.Value < lineNumber
                || candidate.StartLine != lineNumber
                || candidate.EndLine != lineNumber
                || string.IsNullOrEmpty(candidate.Signature))
            {
                continue;
            }

            if (!TryGetSameLineSignatureSpan(candidate, structuralLine, out var startColumn, out var endColumn))
                continue;

            if (column < startColumn || column >= endColumn)
                continue;

            if (candidate.Kind == "function"
                && (!TryFindCSharpFunctionNameColumn(structuralLine, candidate.Name, out var nameColumn)
                    || column < nameColumn))
            {
                continue;
            }

            var spanLength = endColumn - startColumn;
            var kindRank = GetSameLineContainerKindRank(candidate.Kind);
            if (best == null
                || startColumn > bestStartColumn
                || (startColumn == bestStartColumn && spanLength < bestSpanLength)
                || (startColumn == bestStartColumn && spanLength == bestSpanLength && kindRank < bestKindRank))
            {
                best = candidate;
                bestStartColumn = startColumn;
                bestSpanLength = spanLength;
                bestKindRank = kindRank;
            }
        }

        return best;
    }

    private static SymbolRecord? FindInnermostCSharpDeclarationRangeContainer(
        IReadOnlyList<SymbolRecord> candidates,
        string structuralLine,
        int lineNumber,
        int column)
    {
        SymbolRecord? best = null;
        var bestRange = int.MaxValue;

        foreach (var candidate in candidates)
        {
            if (candidate.Kind != "function"
                || candidate.BodyStartLine == null
                || candidate.BodyEndLine == null
                || candidate.StartLine > lineNumber
                || candidate.BodyStartLine.Value < lineNumber
                || candidate.BodyEndLine.Value < lineNumber)
            {
                continue;
            }

            if (candidate.StartLine == lineNumber
                && (!TryFindCSharpFunctionNameColumn(structuralLine, candidate.Name, out var nameColumn)
                    || column < nameColumn))
            {
                continue;
            }

            var range = candidate.BodyEndLine.Value - candidate.StartLine;
            if (best == null || range < bestRange)
            {
                best = candidate;
                bestRange = range;
            }
        }

        return best;
    }

    private static bool TryFindCSharpFunctionNameColumn(string structuralLine, string? name, out int column)
    {
        column = -1;
        if (string.IsNullOrWhiteSpace(structuralLine) || string.IsNullOrWhiteSpace(name))
            return false;

        var searchStart = 0;
        while (searchStart < structuralLine.Length)
        {
            var index = structuralLine.IndexOf(name, searchStart, StringComparison.Ordinal);
            if (index < 0)
                return false;

            var before = index - 1;
            if (before >= 0 && IsTypeExpressionIdentifierPart("csharp", structuralLine[before]))
            {
                searchStart = index + name.Length;
                continue;
            }

            var afterName = index + name.Length;
            if (afterName < structuralLine.Length && IsTypeExpressionIdentifierPart("csharp", structuralLine[afterName]))
            {
                searchStart = afterName;
                continue;
            }

            var after = SkipWhitespace(structuralLine, afterName);
            if (after < structuralLine.Length && structuralLine[after] == '<')
            {
                var genericClose = FindMatchingChar(structuralLine, after, '<', '>');
                if (genericClose > after)
                    after = SkipWhitespace(structuralLine, genericClose + 1);
            }

            if (after < structuralLine.Length && structuralLine[after] == '(')
            {
                column = index;
                return true;
            }

            searchStart = afterName;
        }

        return false;
    }

    private static bool TryGetSameLineSignatureSpan(
        SymbolRecord candidate,
        string structuralLine,
        out int startColumn,
        out int endColumn)
    {
        startColumn = candidate.StartColumn ?? -1;
        if (startColumn < 0 || startColumn > structuralLine.Length)
        {
            startColumn = FindSignatureOccurrenceStartColumn(
                structuralLine,
                candidate.Signature!,
                candidate.SameLineSignatureOccurrenceIndex ?? 0);
            if (startColumn < 0)
            {
                endColumn = -1;
                return false;
            }
        }

        endColumn = Math.Min(structuralLine.Length, startColumn + candidate.Signature!.Length);
        return endColumn > startColumn;
    }

    private static int FindSignatureOccurrenceStartColumn(string structuralLine, string signature, int occurrenceIndex)
    {
        if (occurrenceIndex < 0 || string.IsNullOrEmpty(structuralLine) || string.IsNullOrEmpty(signature))
            return -1;

        var currentOccurrence = 0;
        var searchStart = 0;
        while (searchStart < structuralLine.Length)
        {
            var matchIndex = structuralLine.IndexOf(signature, searchStart, StringComparison.Ordinal);
            if (matchIndex < 0)
                return -1;

            if (currentOccurrence == occurrenceIndex)
                return matchIndex;

            currentOccurrence++;
            searchStart = matchIndex + signature.Length;
        }

        return -1;
    }

    private static bool[] BuildCSharpMultilineStringContentLines(string[] lines)
    {
        var insideStringContent = new bool[lines.Length];
        var inBlockComment = false;
        var inVerbatimString = false;
        var rawStringDelimiterLength = 0;

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            insideStringContent[i] = inVerbatimString || rawStringDelimiterLength > 0;

            var index = 0;
            while (index < line.Length)
            {
                if (inBlockComment)
                {
                    var closeIndex = line.IndexOf("*/", index, StringComparison.Ordinal);
                    if (closeIndex < 0)
                        break;

                    index = closeIndex + 2;
                    inBlockComment = false;
                    continue;
                }

                if (rawStringDelimiterLength > 0)
                {
                    var closeCandidateIndex = index;
                    while (closeCandidateIndex < line.Length && char.IsWhiteSpace(line[closeCandidateIndex]))
                        closeCandidateIndex++;

                    var closeLength = CountCharacterRun(line, closeCandidateIndex, '"');
                    if (closeLength >= rawStringDelimiterLength
                        && closeLength > 0)
                    {
                        rawStringDelimiterLength = 0;
                        index = closeCandidateIndex + closeLength;
                        continue;
                    }

                    break;
                }

                if (inVerbatimString)
                {
                    if (line[index] == '"' && index + 1 < line.Length && line[index + 1] == '"')
                    {
                        index += 2;
                        continue;
                    }

                    if (line[index] == '"')
                    {
                        index++;
                        inVerbatimString = false;
                        continue;
                    }

                    index++;
                    continue;
                }

                if (StartsWithOrdinal(line, index, "//"))
                    break;

                if (StartsWithOrdinal(line, index, "/*"))
                {
                    inBlockComment = true;
                    index += 2;
                    continue;
                }

                if (TryStartCSharpRawString(line, index, out var rawOpeningLength, out var rawDelimiterLength))
                {
                    rawStringDelimiterLength = rawDelimiterLength;
                    index += rawOpeningLength;
                    continue;
                }

                if (TryStartCSharpVerbatimString(line, index, out var verbatimOpeningLength))
                {
                    inVerbatimString = true;
                    index += verbatimOpeningLength;
                    continue;
                }

                if (TryStartCSharpRegularString(line, index, out var regularOpeningLength))
                {
                    index += regularOpeningLength;
                    while (index < line.Length)
                    {
                        if (line[index] == '\\')
                        {
                            index += Math.Min(2, line.Length - index);
                            continue;
                        }

                        if (line[index] == '"')
                        {
                            index++;
                            break;
                        }

                        index++;
                    }

                    continue;
                }

                if (line[index] == '\'')
                {
                    index++;
                    while (index < line.Length)
                    {
                        if (line[index] == '\\')
                        {
                            index += Math.Min(2, line.Length - index);
                            continue;
                        }

                        if (line[index] == '\'')
                        {
                            index++;
                            break;
                        }

                        index++;
                    }

                    continue;
                }

                index++;
            }
        }

        return insideStringContent;
    }

    private static bool TryStartCSharpRawString(
        string line,
        int startIndex,
        out int openingLength,
        out int delimiterLength)
    {
        openingLength = 0;
        delimiterLength = 0;

        var quoteIndex = startIndex;
        while (quoteIndex < line.Length && line[quoteIndex] == '$')
            quoteIndex++;

        delimiterLength = CountCharacterRun(line, quoteIndex, '"');
        if (delimiterLength < 3)
            return false;

        openingLength = (quoteIndex - startIndex) + delimiterLength;
        return true;
    }

    private static bool TryStartCSharpVerbatimString(string line, int startIndex, out int openingLength)
    {
        openingLength = 0;
        if (StartsWithOrdinal(line, startIndex, "$@\"") || StartsWithOrdinal(line, startIndex, "@$\""))
        {
            openingLength = 3;
            return true;
        }

        if (!StartsWithOrdinal(line, startIndex, "@\""))
            return false;

        openingLength = 2;
        return true;
    }

    private static bool TryStartCSharpRegularString(string line, int startIndex, out int openingLength)
    {
        openingLength = 0;
        if (StartsWithOrdinal(line, startIndex, "$\""))
        {
            openingLength = 2;
            return true;
        }

        if (line[startIndex] != '"')
            return false;

        openingLength = 1;
        return true;
    }

    private static bool StartsWithOrdinal(string line, int startIndex, string value)
    {
        if (startIndex + value.Length > line.Length)
            return false;

        return string.Compare(line, startIndex, value, 0, value.Length, StringComparison.Ordinal) == 0;
    }

    private static int CountCharacterRun(string line, int startIndex, char value)
    {
        var index = startIndex;
        while (index < line.Length && line[index] == value)
            index++;

        return index - startIndex;
    }

    private static int GetSameLineContainerKindRank(string? kind) => kind switch
    {
        "function" => 0,
        "property" => 1,
        "class" => 2,
        "struct" => 3,
        "interface" => 4,
        "enum" => 5,
        "namespace" => 6,
        _ => 7,
    };

    internal static SymbolRecord? FindInnermostClassLike(IReadOnlyList<SymbolRecord> candidates, int lineNumber)
    {
        foreach (var candidate in candidates)
        {
            // class/struct/enum are all ctor-owner kinds across supported languages. Java enum bodies
            // can declare constructors and chain via `this(...)`; C# enum cannot declare constructors
            // at all, so the chain regex will not match inside one even if we pick it up here.
            // class/struct/enum はいずれもコンストラクタを持ちうる宿主種別。Java enum は `this(...)`
            // 連鎖を書けるため含める。C# enum はコンストラクタ自体を持てないので副作用は出ない。
            if (candidate.Kind != "class" && candidate.Kind != "struct" && candidate.Kind != "enum")
                continue;
            if (candidate.BodyStartLine!.Value <= lineNumber && candidate.BodyEndLine!.Value >= lineNumber)
                return candidate;
        }

        return null;
    }

    /// <summary>
    /// Same-line Java ctor span capturing the declarator name plus the 0-based indices of the
    /// ctor name, the opening `{` of the body, and the matching `}` on the same line (or -1
    /// when no matching close brace is found). Used to override the container for body-level
    /// calls and to suppress the bogus declarator self-call on the ctor name.
    /// same-line Java ctor の宣言情報。ctor 名位置・body `{` 位置・body `}` 位置を保持し、
    /// body 内の call に合成 function コンテナを流すのと、宣言子 `CtorName(` が誤って
    /// call として記録されるのを抑止するのに使う。
    /// </summary>
    internal readonly record struct JavaSameLineCtorSpan(
        string Name,
        int NameIndex,
        int OpenBraceIndex,
        int CloseBraceIndex);

    /// <summary>
    /// Depth-aware scanner for `@Annot ... <T extends Comparable<Integer>> Ctor(...) { ... }`
    /// style declarations. Returns the constructor name when the line opens a ctor body, or
    /// null otherwise. Handles qualified annotations (`@demo.Ann`), annotation argument lists
    /// with nested parens, and nested generic bounds that a flat regex cannot balance.
    /// 修飾付きアノテーション・引数付きアノテーション・入れ子の generic 境界を含む
    /// same-line ctor 宣言を depth-aware にスキャンして ctor 名を返すヘルパー。
    /// </summary>
    internal static string? TryExtractJavaCtorNameFromLine(string line)
        => JavaReferenceExtractor.TryExtractCtorNameFromLine(line);

    /// <summary>
    /// Same as <see cref="TryExtractJavaCtorNameFromLine"/> but also returns the ctor name
    /// index, body-open `{` index, and the matching body-close `}` index on the same line.
    /// `TryExtractJavaCtorNameFromLine` と同じスキャナだが、ctor 名位置・`{` 位置・対応する
    /// `}` 位置もまとめて返すバリアント。
    /// </summary>
    internal static JavaSameLineCtorSpan? TryExtractJavaSameLineCtorSpan(string line)
        => JavaReferenceExtractor.TryExtractSameLineCtorSpan(line);

    private static void AddChainReference(
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string name,
        int column,
        string referenceKind,
        string context,
        int lineNumber,
        SymbolRecord? container)
    {
        var dedupeKey = BuildReferenceDedupeKey(fileId, null, lineNumber, column, referenceKind, name, container);
        if (!seen.Add(dedupeKey))
            return;

        references.Add(new ReferenceRecord
        {
            FileId = fileId,
            SymbolName = name,
            ReferenceKind = referenceKind,
            Line = lineNumber,
            Column = column,
            Context = context,
            ContainerKind = container?.Kind,
            ContainerName = container?.Name,
        });
    }

    private static void EmitMethodGroupReferences(
        string language,
        string preparedLine,
        HashSet<string>? callableDefinitionNames,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn)
    {
        if (callableDefinitionNames == null || callableDefinitionNames.Count == 0)
            return;

        foreach (Match match in MethodGroupReferenceRegex.Matches(preparedLine))
        {
            var contextTargetGroup = match.Groups["contextTarget"];
            if (contextTargetGroup.Success && MethodGroupContextTargetIgnoreNames.Contains(contextTargetGroup.Value))
                continue;
            if (!contextTargetGroup.Success)
            {
                var prefix = preparedLine.AsSpan(0, match.Groups["name"].Index).TrimEnd();
                if (prefix.EndsWith("+=", StringComparison.Ordinal) || prefix.EndsWith("-=", StringComparison.Ordinal))
                    continue;
            }

            var nameGroup = match.Groups["name"];
            var rawName = nameGroup.Value;
            var name = language == "csharp" ? NormalizeCSharpIdentifier(rawName) : rawName;
            if (!callableDefinitionNames.Contains(name))
                continue;

            var container = resolveContainerForColumn(nameGroup.Index);
            AddChainReference(references, seen, fileId, name, nameGroup.Index, "call", context, lineNumber, container);
        }
    }

    /// <summary>
    /// Build a list of line ranges paired with synthetic function-kind containers for C# primary
    /// constructor declarations that carry a base primary-constructor call. This covers records
    /// (`record Child(int x) : Parent(x)`), C# 12 classes (`class Child(int x) : Parent(x)`) and
    /// structs (`struct Child(int x) : Parent(x)`), including the multi-line form where
    /// `: Parent(x)` sits on a continuation line. SymbolExtractor does not synthesize a separate
    /// ctor symbol for the implicit primary constructor, so the `Parent(x)` reference would
    /// otherwise land on `container = null` (when the declaration line has no body range) or on
    /// the declaring type itself. The synthetic container covers the header range only; methods
    /// inside a braced body still resolve to their real containers via FindInnermostContainer,
    /// and within the end line the override is limited to columns before the terminator so body
    /// calls sharing the same line (e.g. `record Child(int V) : Parent(V) { ... Add(V, 1); }`)
    /// are not pulled onto the synthetic ctor.
    /// C# の primary constructor 宣言に対して合成 function コンテナの (start, end, endColumn, container)
    /// リストを作る。record だけでなく C# 12 の class / struct primary constructor も対象にし、
    /// 宣言ヘッダーの範囲（end line は終端 `;` / `{` のカラムまで）だけ合成 ctor に差し替えることで、
    /// 同一行 braced body の呼び出しや後続メソッドは本来の container に残る。
    /// </summary>
    private static List<(int StartLine, int StartColumn, int EndLine, int EndColumn, SymbolRecord Container)> BuildCSharpPrimaryCtorContainers(
        string language,
        IReadOnlyList<SymbolRecord> symbols,
        string[] structuralLines)
    {
        var ranges = new List<(int, int, int, int, SymbolRecord)>();
        if (language != "csharp")
            return ranges;

        foreach (var symbol in symbols)
        {
            // SymbolExtractor stores C# records as Kind=class and C# 12 structs as Kind=struct.
            // Interfaces / enums / delegates cannot have primary constructors in C# so skip them.
            // C# record は Kind=class、C# 12 struct は Kind=struct として登録されるため両方対象。
            if (symbol.Kind != "class" && symbol.Kind != "struct")
                continue;
            var signature = symbol.Signature;
            if (string.IsNullOrWhiteSpace(signature))
                continue;

            // SymbolRecord.Signature only captures the first declaration line, so the first-line
            // regex filter misses split-line primary-ctor forms such as
            // `public record Child\n(\n    int Value\n)\n    : Parent(Value);`. Walk the
            // structural-masked lines from StartLine until we hit `;` / `{` and run the
            // primary-ctor detection on the joined header text instead.
            // 宣言の signature は 1 行目だけしか持たないので、`record` / `class` / `struct` と
            // `(` を別行に分ける書式では先頭行 regex の前段フィルタが空振りする。ここでは
            // structuralLines から `;` / `{` までヘッダーを連結し、連結後のテキストで判定する。
            var (headerEndLine, headerEndColumn, headerText) = CollectCSharpRecordHeader(structuralLines, symbol.StartLine);
            if (!IsCSharpPrimaryCtorHeader(headerText))
                continue;
            if (!HasCSharpBasePrimaryCtorCall(headerText))
                continue;

            // Restrict the synthetic container to the actual declaration span, starting at the
            // `class` / `struct` / `record` keyword column on the start line. Without this
            // same-line tokens BEFORE the keyword (e.g. attribute arguments in
            // `[Attr(Helper.Get())] public class Child(int x) : Parent(x) {}`) would get
            // attributed to the synthetic ctor and pollute callers / impact with phantom
            // `Child` callers for `Attr` and `Helper.Get`.
            // 合成 ctor コンテナを本物の宣言範囲に限定する。`class` / `struct` / `record`
            // キーワード位置より前（同一行の属性呼び出しなど）は本来の container に残す。
            var startColumn = FindCSharpPrimaryCtorKeywordColumn(structuralLines, symbol.StartLine);

            var synthetic = new SymbolRecord
            {
                FileId = symbol.FileId,
                Kind = "function",
                Name = symbol.Name,
                Line = symbol.Line,
                StartLine = symbol.StartLine,
                EndLine = headerEndLine,
                BodyStartLine = symbol.StartLine,
                BodyEndLine = headerEndLine,
                Signature = signature,
                ContainerKind = symbol.ContainerKind,
                ContainerName = symbol.ContainerName,
                ContainerQualifiedName = symbol.ContainerQualifiedName,
                FamilyKey = symbol.FamilyKey,
                Visibility = symbol.Visibility,
            };

            ranges.Add((symbol.StartLine, startColumn, headerEndLine, headerEndColumn, synthetic));
        }

        return ranges;
    }

    private static int FindCSharpPrimaryCtorKeywordColumn(string[] structuralLines, int startLine)
    {
        var idx = Math.Max(0, startLine - 1);
        if (idx >= structuralLines.Length)
            return 0;
        var line = structuralLines[idx];
        foreach (var keyword in CSharpPrimaryCtorKeywords)
        {
            int pos = 0;
            while (pos < line.Length)
            {
                var found = line.IndexOf(keyword, pos, StringComparison.Ordinal);
                if (found < 0) break;
                var before = found == 0 ? ' ' : line[found - 1];
                var afterIdx = found + keyword.Length;
                var after = afterIdx < line.Length ? line[afterIdx] : ' ';
                if (!IsCSharpIdentifierPart(before) && !IsCSharpIdentifierPart(after))
                    return found;
                pos = found + 1;
            }
        }
        return 0;
    }

    private static readonly string[] CSharpPrimaryCtorKeywords = { "record", "class", "struct" };

    private static bool IsCSharpIdentifierPart(char c) => char.IsLetterOrDigit(c) || c == '_';

    /// <summary>
    /// Walk structural-masked lines starting at the 1-based <paramref name="startLine"/> and collect
    /// the declaration header up to (but not including) the first `;` or `{` that sits outside a
    /// string or comment. Returns the 1-based line number where the terminator was found (or the
    /// final line index when none was found) and the joined header text for further parsing.
    /// Reused for record primary-ctor container synthesis and multi-line `: base(...)` resolution.
    /// structuralLines を使って、class / struct / record 宣言ヘッダーを最初の `;` / `{` まで連結する。
    /// record primary-ctor のコンテナ合成と、複数行 `: base(...)` 解決の両方で使う。
    /// </summary>
    internal static (int EndLine, int EndColumn, string Text) CollectCSharpRecordHeader(string[] structuralLines, int startLine)
    {
        var startIdx = Math.Max(0, startLine - 1);
        if (structuralLines.Length == 0)
            return (startLine, int.MaxValue, string.Empty);

        // Depth-aware termination so that `{` / `;` inside annotation arg lists (e.g. the `{` in
        // `@Ann({A.class, B.class})`) or attribute-argument brackets does not cut the header off
        // before the real base-list terminator, which would silently drop the base type.
        // We intentionally do NOT track `<` / `>` as generic depth here: comparison operators
        // inside annotation / attribute expressions (e.g. `[Attr(Flag = 1 < 2)]` or
        // `@Ann(flag = 1 < 2)`) are raised as `<` without a matching `>`, so angle-depth tracking
        // would leave the counter pinned above zero and silently drop the real top-level `{` / `;`
        // terminator, letting the synthetic primary-ctor container or the Java base-type parse
        // swallow everything up to EOF. `{` / `;` cannot legally appear inside a top-level
        // `<...>` generic arg list in either C# or Java, so paren/bracket masking is sufficient.
        // EndColumn tracks the column index of the top-level terminator on the end line, or
        // int.MaxValue when no terminator was found (end-of-file), so call-site-scoped container
        // overrides can restrict themselves to the header portion of the end line.
        // アノテーション引数の `{` などを本当のヘッダ終端と誤認しないよう、`()` / `[]` の深さを追いながら
        // 最初の top-level `;` / `{` でのみ終了する。`<` / `>` は annotation / attribute 式内の比較演算子で
        // 非対称に現れうるため generic 深度として扱わない。
        // EndColumn は end line 上の終端 `;` / `{` の位置を返す（終端が無ければ int.MaxValue）。
        var sb = new System.Text.StringBuilder();
        int parenDepth = 0;
        int bracketDepth = 0;
        // Comment / string awareness so unbalanced `(` / `[` / `{` / `;` inside a line
        // comment, block comment, or string literal never advances the depth counters,
        // fires the terminator, or leaks into the returned header text. For Java `extends`
        // headers the structuralLines array is an unmasked clone (StructuralLineMasker is a
        // no-op for Java), so this is what keeps `class Leaf extends Root /* ( stray [ */ {`
        // from pinning parenDepth / bracketDepth at 1 and skipping the real `{` terminator,
        // and it also prevents ParseJavaBaseType from seeing the comment body when it parses
        // the header text downstream.
        // コメント・文字列内の不均衡な `(` / `[` / `{` / `;` を terminator 判定・連結テキスト双方から除外する。
        bool inBlockComment = false;
        bool inString = false;
        for (int i = startIdx; i < structuralLines.Length; i++)
        {
            var line = structuralLines[i];
            var masked = line.ToCharArray();
            var terminatorIdx = -1;
            for (int j = 0; j < line.Length; j++)
            {
                var c = line[j];

                if (inBlockComment)
                {
                    masked[j] = ' ';
                    if (c == '*' && j + 1 < line.Length && line[j + 1] == '/')
                    {
                        inBlockComment = false;
                        masked[j + 1] = ' ';
                        j++;
                    }
                    continue;
                }

                if (inString)
                {
                    masked[j] = ' ';
                    if (c == '\\' && j + 1 < line.Length)
                    {
                        masked[j + 1] = ' ';
                        j++;
                        continue;
                    }
                    if (c == '"')
                        inString = false;
                    continue;
                }

                if (c == '/' && j + 1 < line.Length)
                {
                    if (line[j + 1] == '/')
                    {
                        for (int k = j; k < line.Length; k++)
                            masked[k] = ' ';
                        break;
                    }
                    if (line[j + 1] == '*')
                    {
                        inBlockComment = true;
                        masked[j] = ' ';
                        masked[j + 1] = ' ';
                        j++;
                        continue;
                    }
                }

                if (c == '"')
                {
                    inString = true;
                    masked[j] = ' ';
                    continue;
                }

                if (c == '\'')
                {
                    // Rust / OCaml lifetime annotation vs. char literal: only skip when a
                    // closing `'` exists within ~12 chars on this line.
                    // Rust の lifetime と char literal を短距離の閉じ `'` の有無で見分ける。
                    var closeIdx = -1;
                    var limit = Math.Min(line.Length, j + 12);
                    for (int k = j + 1; k < limit; k++)
                    {
                        if (line[k] == '\\' && k + 1 < line.Length)
                        {
                            k++;
                            continue;
                        }
                        if (line[k] == '\'')
                        {
                            closeIdx = k;
                            break;
                        }
                    }
                    if (closeIdx > 0)
                    {
                        for (int k = j; k <= closeIdx; k++)
                            masked[k] = ' ';
                        j = closeIdx;
                    }
                    continue;
                }

                if (c == '(') parenDepth++;
                else if (c == ')') { if (parenDepth > 0) parenDepth--; }
                else if (c == '[') bracketDepth++;
                else if (c == ']') { if (bracketDepth > 0) bracketDepth--; }
                else if ((c == ';' || c == '{') && parenDepth == 0 && bracketDepth == 0)
                {
                    terminatorIdx = j;
                    break;
                }
            }

            var maskedLine = new string(masked);
            if (terminatorIdx >= 0)
            {
                sb.Append(maskedLine, 0, terminatorIdx);
                return (i + 1, terminatorIdx, sb.ToString());
            }

            sb.Append(maskedLine);
            sb.Append('\n');
        }

        return (structuralLines.Length, int.MaxValue, sb.ToString());
    }

    /// <summary>
    /// Returns true when the C# type header text carries a base-list entry that looks like a
    /// primary-constructor call (contains `(`). Accepts multi-line header text already joined by
    /// <see cref="CollectCSharpRecordHeader"/>.
    /// C# 型ヘッダー（複数行連結後でも可）の base-list 先頭エントリが `(` を含むかを判定する。
    /// </summary>
    /// <summary>
    /// Return true when a joined C# type-declaration header (possibly spanning multiple lines,
    /// including line-broken primary-ctor parens) looks like a primary-constructor declaration.
    /// Accepts `record Child(...)`, `record class Child(...)`, `record struct Child(...)`,
    /// C# 12 `class Child(...)`, `struct Child(...)`, generic arity such as `class Child<T>(...)`,
    /// and the split-line form where `record Child\n(\n ... )` places the `(` on a continuation line.
    /// 連結済みの C# 宣言ヘッダーが primary-ctor 宣言かを判定する。`record` だけでなく C# 12 の
    /// `class` / `struct` primary constructor も対象にし、`(` が別行に分かれる書式にも対応する。
    /// </summary>
    private static bool IsCSharpPrimaryCtorHeader(string headerText)
    {
        if (string.IsNullOrWhiteSpace(headerText))
            return false;
        return CSharpPrimaryCtorHeaderRegex.IsMatch(headerText);
    }

    private static bool HasCSharpBasePrimaryCtorCall(string headerText)
    {
        var text = headerText.TrimEnd();
        if (text.EndsWith(";", StringComparison.Ordinal))
            text = text.Substring(0, text.Length - 1).TrimEnd();
        if (text.EndsWith("{", StringComparison.Ordinal))
            text = text.Substring(0, text.Length - 1).TrimEnd();

        var colonIndex = FindSignatureColonIndex(text);
        if (colonIndex < 0)
            return false;

        var baseList = text.Substring(colonIndex + 1);
        var whereMatch = CSharpWhereClauseRegex.Match(baseList);
        if (whereMatch.Success)
            baseList = baseList.Substring(0, whereMatch.Index);

        var firstEntry = TakeFirstBaseEntry(baseList).Trim();
        // Only count a `(` that sits at generic / bracket depth 0 — a primary-ctor base call
        // always puts its argument list directly after the bare type name, whereas generic args
        // and array ranks can legally contain `(` (tuple syntax `<(int, int)>`, function types
        // `<Func<(int, int)>>`, or attribute arg brackets). A naive `.Contains('(')` would treat
        // those as primary-ctor calls and synthesize a phantom record ctor container.
        // 先頭エントリのうち generic/bracket 深度 0 の `(` だけを primary-ctor 呼び出し扱いにする。
        // `IBox<(int, int)>` のような tuple を含む interface 実装を連鎖呼び出しと誤認させない。
        int angleDepth = 0;
        int squareDepth = 0;
        for (int i = 0; i < firstEntry.Length; i++)
        {
            var c = firstEntry[i];
            switch (c)
            {
                case '<':
                    angleDepth++;
                    break;
                case '>':
                    if (angleDepth > 0) angleDepth--;
                    break;
                case '[':
                    squareDepth++;
                    break;
                case ']':
                    if (squareDepth > 0) squareDepth--;
                    break;
                case '(':
                    if (angleDepth == 0 && squareDepth == 0)
                        return true;
                    break;
            }
        }
        return false;
    }

    /// <summary>
    /// Parse the first base-class token from a C# class/struct/record signature such as
    /// `class B : A, IFoo`, `record C(int x) : A(x)`, or `class B<T> : A<T> where T : new()`.
    /// Returns null when no base list is present or when the signature is empty.
    /// C# の class/struct/record シグネチャから最初の基底クラストークンを取り出す。
    /// </summary>
    internal static string? ParseCSharpBaseType(string? signature)
    {
        if (string.IsNullOrWhiteSpace(signature))
            return null;

        var text = signature.TrimEnd();
        if (text.EndsWith("{", StringComparison.Ordinal))
            text = text.Substring(0, text.Length - 1).TrimEnd();

        var colonIndex = FindSignatureColonIndex(text);
        if (colonIndex < 0)
            return null;

        var baseList = text.Substring(colonIndex + 1);
        var whereMatch = CSharpWhereClauseRegex.Match(baseList);
        if (whereMatch.Success)
            baseList = baseList.Substring(0, whereMatch.Index);

        var firstEntry = TakeFirstBaseEntry(baseList).Trim();
        return ExtractBareTypeName(firstEntry);
    }

    /// <summary>
    /// Parse the first extends-clause type from a Java class/interface/record signature.
    /// 例: `class B extends A implements IFoo` → `A`、
    /// `class Leaf extends Outer<Integer>.Base {` → `Base`。
    /// </summary>
    internal static string? ParseJavaBaseType(string? signature)
    {
        if (string.IsNullOrWhiteSpace(signature))
            return null;

        // Locate `extends` at angle/paren depth 0 so bounded type parameters like
        // `class Leaf<T extends Number> extends Root {` do not resolve to the
        // parameter bound (`Number`) instead of the real base (`Root`).
        // 境界付き型パラメータ（`class Leaf<T extends Number> extends Root {`）で
        // 型パラメータ境界の `extends` を先に拾わないよう、angle / paren 深度 0 の
        // `extends` のみを検出する。
        int start = FindTopLevelExtendsEnd(signature!);
        if (start < 0)
            return null;

        int i = start;
        int angleDepth = 0;
        int parenDepth = 0;
        while (i < signature.Length)
        {
            char c = signature[i];
            if (c == '<')
            {
                angleDepth++;
            }
            else if (c == '>')
            {
                if (angleDepth > 0) angleDepth--;
            }
            else if (c == '(')
            {
                // Track `(...)` depth so that commas inside annotation arguments such as
                // `@Ann(a = 1, b = 2) Root` or `@Ann({A.class, B.class}) Root` are not mistaken
                // for top-level base-list separators. Without this the scanner breaks at the
                // inner `,`, feeds a truncated segment to the annotation stripper, and the
                // super(...) edge gets misattributed or dropped entirely.
                // annotation 引数内のカンマ（`@Ann(a = 1, b = 2) Root` や
                // `@Ann({A.class, B.class}) Root`）が base-list 区切りと誤認されないよう `(...)` の
                // 深さも追跡する。これをやらないと内側の `,` で走査が切れ、annotation stripper に
                // 壊れたセグメントが渡って super(...) の連鎖エッジが落ちる。
                parenDepth++;
            }
            else if (c == ')')
            {
                if (parenDepth > 0) parenDepth--;
            }
            else if (angleDepth == 0 && parenDepth == 0)
            {
                if (c == '{' || c == ',' || c == ';')
                    break;
                // Stop at a word-boundary `implements` or `permits` (Java 17+ sealed types).
                // 単語境界の `implements` / `permits` (Java 17+ sealed 型) で停止する。
                if (IsJavaBaseListTerminatorKeyword(signature, i, start, "implements") ||
                    IsJavaBaseListTerminatorKeyword(signature, i, start, "permits"))
                {
                    break;
                }
            }
            i++;
        }

        var segment = signature.Substring(start, i - start).Trim();
        if (segment.Length == 0)
            return null;

        // Strip Java type-use annotations (JLS 9.7.4): `@Ann`, `@pkg.Ann`, `@Ann(value=1)` can
        // appear before the type itself (`extends @Ann Root`) or between nested-type segments
        // (`Outer<Integer>.@Ann Base`). Without this pass the base resolver returns a phantom
        // type name like `@Ann Root` that misattributes references / callers / impact.
        // Java の type-use annotation (JLS 9.7.4) を剥がす。`extends @Ann Root` や
        // `Outer<Integer>.@Ann Base` のような形で基底型の直前やセグメント間に現れるため、
        // 先に除去しないと `@Ann Root` のような幽霊シンボルへ参照が張られてしまう。
        segment = StripJavaTypeAnnotations(segment);
        return segment.Length == 0 ? null : ExtractBareTypeName(segment);
    }

    /// <summary>
    /// Return the index past the first `extends` keyword that appears at angle/paren depth 0,
    /// or -1 when no such occurrence exists. Matches the semantics of the old `\bextends\s+`
    /// regex entrypoint but skips `extends` inside `<...>` (bounded type parameters) and
    /// `(...)` (annotation argument lists).
    /// </summary>
    private static int FindTopLevelExtendsEnd(string signature)
    {
        int angleDepth = 0;
        int parenDepth = 0;
        for (int i = 0; i < signature.Length; i++)
        {
            char c = signature[i];
            if (c == '<')
            {
                angleDepth++;
            }
            else if (c == '>')
            {
                if (angleDepth > 0) angleDepth--;
            }
            else if (c == '(')
            {
                parenDepth++;
            }
            else if (c == ')')
            {
                if (parenDepth > 0) parenDepth--;
            }
            else if (angleDepth == 0 && parenDepth == 0 && IsExtendsKeywordAt(signature, i))
            {
                int end = i + 7; // "extends".Length
                while (end < signature.Length && char.IsWhiteSpace(signature[end]))
                    end++;
                return end;
            }
        }
        return -1;
    }

    private static bool IsExtendsKeywordAt(string signature, int i)
    {
        const string Keyword = "extends";
        if (i + Keyword.Length > signature.Length)
            return false;
        if (i > 0 && IsJavaIdentifierPart(signature[i - 1]))
            return false;
        if (string.CompareOrdinal(signature, i, Keyword, 0, Keyword.Length) != 0)
            return false;
        int after = i + Keyword.Length;
        // `\bextends\s+` equivalence: must be followed by whitespace so that names like
        // `extendsFoo` or identifiers containing `extends` do not match.
        // `\bextends\s+` 相当: `extendsFoo` のような識別子や合成語を誤認しないよう、
        // 直後に空白が続くものだけを `extends` キーワードとして扱う。
        if (after >= signature.Length)
            return false;
        return char.IsWhiteSpace(signature[after]);
    }

    private static string StripJavaTypeAnnotations(string text)
    {
        if (text.IndexOf('@') < 0)
            return text;

        var sb = new System.Text.StringBuilder(text.Length);
        int i = 0;
        while (i < text.Length)
        {
            char c = text[i];
            if (c == '@')
            {
                // Skip `@` + qualified identifier (`@pkg.Ann`) + optional balanced `(...)`.
                i++;
                while (i < text.Length && (IsJavaIdentifierPart(text[i]) || text[i] == '.'))
                    i++;
                if (i < text.Length && text[i] == '(')
                {
                    int parenDepth = 1;
                    i++;
                    while (i < text.Length && parenDepth > 0)
                    {
                        var ch = text[i];
                        // Skip string / char literals so `@Ann(text=")")` does not close early.
                        // 文字列・文字リテラル内の `)` で早期終了しないようスキップする。
                        if (ch == '"' || ch == '\'')
                        {
                            var quote = ch;
                            i++;
                            while (i < text.Length)
                            {
                                var lc = text[i];
                                if (lc == '\\' && i + 1 < text.Length) { i += 2; continue; }
                                if (lc == quote) { i++; break; }
                                i++;
                            }
                            continue;
                        }
                        if (ch == '(') parenDepth++;
                        else if (ch == ')') parenDepth--;
                        i++;
                    }
                }
                // Drop a single trailing whitespace run so `@Ann Root` collapses to `Root`.
                while (i < text.Length && char.IsWhiteSpace(text[i]))
                    i++;
                continue;
            }
            sb.Append(c);
            i++;
        }

        return sb.ToString();
    }

    internal static bool IsJavaIdentifierPart(char c) =>
        char.IsLetterOrDigit(c) || c == '_' || c == '$';

    private static bool IsJavaBaseListTerminatorKeyword(string signature, int i, int start, string keyword)
    {
        if (i + keyword.Length > signature.Length)
            return false;
        if (i != start && IsJavaIdentifierPart(signature[i - 1]))
            return false;
        if (string.CompareOrdinal(signature, i, keyword, 0, keyword.Length) != 0)
            return false;
        if (i + keyword.Length < signature.Length && IsJavaIdentifierPart(signature[i + keyword.Length]))
            return false;
        return true;
    }

    private static int FindSignatureColonIndex(string text)
    {
        var depth = 0;
        for (int i = 0; i < text.Length; i++)
        {
            var c = text[i];
            switch (c)
            {
                case '<':
                case '(':
                case '[':
                    depth++;
                    break;
                case '>':
                case ')':
                case ']':
                    if (depth > 0) depth--;
                    break;
                case ':':
                    if (depth == 0)
                    {
                        // Skip `::` alias qualifier (`global::System.Exception`).
                        // `::` エイリアス修飾子（`global::System.Exception`）はスキップ。
                        if (i + 1 < text.Length && text[i + 1] == ':')
                        {
                            i++;
                            continue;
                        }
                        return i;
                    }
                    break;
            }
        }

        return -1;
    }

    private static string TakeFirstBaseEntry(string baseList)
    {
        var depth = 0;
        for (int i = 0; i < baseList.Length; i++)
        {
            var c = baseList[i];
            switch (c)
            {
                case '<':
                case '(':
                case '[':
                    depth++;
                    break;
                case '>':
                case ')':
                case ']':
                    if (depth > 0) depth--;
                    break;
                case ',':
                    if (depth == 0)
                        return baseList.Substring(0, i);
                    break;
            }
        }

        return baseList;
    }

    private static string? ExtractBareTypeName(string entry)
    {
        var trimmed = entry.Trim();
        if (trimmed.Length == 0)
            return null;

        // Split on `.` / `::` at generic depth 0, then return the last segment with generic
        // args stripped. Naive "first `<`, then last `.`" slicing loses nested types such as
        // `Outer<int>.Base`, `Outer<Integer>.Base`, or `global::Ns.Outer<T>.Inner`.
        // 最初の `<` で切ってから末尾 `.` を探す素朴な方法では `Outer<int>.Base` のような
        // ネスト型を取り違えるため、generic 深度 0 の `.` / `::` でセグメント分割して末尾だけ返す。
        int lastSegmentStart = 0;
        int angleDepth = 0;
        int endIndex = trimmed.Length;
        for (int i = 0; i < trimmed.Length; i++)
        {
            var c = trimmed[i];
            if (c == '<')
            {
                angleDepth++;
            }
            else if (c == '>')
            {
                if (angleDepth > 0) angleDepth--;
            }
            else if (angleDepth == 0)
            {
                if (c == '(')
                {
                    // Strip record primary-ctor args at top level: `A(...)` → `A`.
                    // record のプライマリコンストラクタ引数を剥がす。
                    endIndex = i;
                    break;
                }
                if (c == '.')
                {
                    lastSegmentStart = i + 1;
                }
                else if (c == ':' && i + 1 < trimmed.Length && trimmed[i + 1] == ':')
                {
                    lastSegmentStart = i + 2;
                    i++;
                }
            }
        }

        var segment = trimmed.Substring(lastSegmentStart, endIndex - lastSegmentStart).Trim();
        var ltIndex = segment.IndexOf('<');
        if (ltIndex >= 0)
            segment = segment.Substring(0, ltIndex);

        segment = segment.Trim();
        return segment.Length > 0 ? segment : null;
    }

    private static string ReplaceRegexMatchesWithSpaces(Regex regex, string input)
    {
        return regex.Replace(input, static match => match.Length == 0 ? string.Empty : new string(' ', match.Length));
    }

    private static string PrepareLine(string lang, string line)
    {
        var result = line;
        if (lang == "rust")
            result = MaskRustLifetimeTokens(result);
        if (lang != "cobol")
        {
            var stringLiteralRegex = lang is "kotlin" or "r"
                ? NonBacktickStringLiteralRegex
                : StringLiteralRegex;
            result = stringLiteralRegex.Replace(result, "\"\"");
        }
        result = InlineBlockCommentRegex.Replace(result, " ");

        if (UsesHashComments(lang))
        {
            var hashIndex = lang == "r"
                ? FindRHashCommentStart(result)
                : result.IndexOf('#');
            if (hashIndex >= 0)
                result = result[..hashIndex];
        }

        if (UsesSlashComments(lang))
        {
            var slashIndex = result.IndexOf("//", StringComparison.Ordinal);
            if (slashIndex >= 0)
                result = result[..slashIndex];
        }

        // Lua, SQL, Haskell use -- for line comments / Lua、SQL、Haskell は -- を行コメントに使う
        if (UsesDashDashComments(lang))
        {
            var dashCommentIndex = result.IndexOf("--", StringComparison.Ordinal);
            if (dashCommentIndex >= 0)
                result = result[..dashCommentIndex];
        }

        if (lang is "fortran")
        {
            var bangCommentIndex = result.IndexOf('!');
            if (bangCommentIndex >= 0)
                result = result[..bangCommentIndex];
        }

        if (lang is "pascal")
        {
            result = PascalBraceCommentRegex.Replace(result, " ");
            result = PascalParenStarCommentRegex.Replace(result, " ");
        }

        // VB.NET uses Rem and ' for line comments / VB.NET は Rem と ' を行コメントに使う
        if (lang is "vb")
        {
            var remCommentMatch = VisualBasicRemCommentRegex.Match(result);
            if (remCommentMatch.Success)
                result = result[..remCommentMatch.Index];

            var vbCommentIndex = result.IndexOf('\'');
            if (vbCommentIndex >= 0)
                result = result[..vbCommentIndex];
        }

        return result;
    }

    private static int FindRHashCommentStart(string line)
    {
        var inBacktickIdentifier = false;
        for (var i = 0; i < line.Length; i++)
        {
            var ch = line[i];
            if (inBacktickIdentifier && ch == '\\' && i + 1 < line.Length)
            {
                i++;
                continue;
            }

            if (ch == '`')
            {
                inBacktickIdentifier = !inBacktickIdentifier;
                continue;
            }

            if (ch == '#' && !inBacktickIdentifier)
                return i;
        }

        return -1;
    }

    private static string MaskRustLifetimeTokens(string line)
    {
        var quoteIndex = line.IndexOf('\'');
        if (quoteIndex < 0)
            return line;

        var chars = line.ToCharArray();
        for (var index = quoteIndex; index + 1 < chars.Length; index++)
        {
            if (chars[index] != '\'')
                continue;

            var next = chars[index + 1];
            if (next != '_' && !char.IsLetter(next))
                continue;

            var end = index + 2;
            while (end < chars.Length && IsJavaIdentifierPart(chars[end]))
                end++;

            if (end == index + 2 && end < chars.Length && chars[end] == '\'')
                continue;

            for (var maskIndex = index; maskIndex < end; maskIndex++)
                chars[maskIndex] = ' ';

            index = end - 1;
        }

        return new string(chars);
    }

    private static string[] MaskPascalBlockCommentLines(IReadOnlyList<string> lines)
    {
        var result = new string[lines.Count];
        var inBraceComment = false;
        var inParenStarComment = false;

        for (var lineIndex = 0; lineIndex < lines.Count; lineIndex++)
        {
            var line = lines[lineIndex];
            var chars = line.ToCharArray();
            var cursor = 0;

            while (cursor < chars.Length)
            {
                if (inBraceComment)
                {
                    var closes = chars[cursor] == '}';
                    chars[cursor++] = ' ';
                    if (closes)
                        inBraceComment = false;
                    continue;
                }

                if (inParenStarComment)
                {
                    if (chars[cursor] == '*' && cursor + 1 < chars.Length && chars[cursor + 1] == ')')
                    {
                        chars[cursor++] = ' ';
                        chars[cursor++] = ' ';
                        inParenStarComment = false;
                        continue;
                    }

                    chars[cursor++] = ' ';
                    continue;
                }

                if (chars[cursor] == '\'')
                {
                    cursor++;
                    while (cursor < chars.Length)
                    {
                        if (chars[cursor] == '\'')
                        {
                            cursor++;
                            if (cursor < chars.Length && chars[cursor] == '\'')
                            {
                                cursor++;
                                continue;
                            }
                            break;
                        }

                        cursor++;
                    }
                    continue;
                }

                if (chars[cursor] == '{')
                {
                    chars[cursor++] = ' ';
                    inBraceComment = true;
                    continue;
                }

                if (chars[cursor] == '(' && cursor + 1 < chars.Length && chars[cursor + 1] == '*')
                {
                    chars[cursor++] = ' ';
                    chars[cursor++] = ' ';
                    inParenStarComment = true;
                    continue;
                }

                cursor++;
            }

            result[lineIndex] = new string(chars);
        }

        return result;
    }

    private static bool UsesCStyleBlockComments(string language) =>
        language is "c" or "cpp" or "go" or "objc" or "dart";

    private static string[] MaskCStyleBlockCommentLines(string language, IReadOnlyList<string> lines)
    {
        var result = new string[lines.Count];
        var inBlockComment = false;
        var inGoRawString = false;
        char dartTripleQuote = '\0';
        string? cppRawStringTerminator = null;

        for (var lineIndex = 0; lineIndex < lines.Count; lineIndex++)
        {
            var line = lines[lineIndex];
            var chars = line.ToCharArray();
            var cursor = 0;
            while (cursor < chars.Length)
            {
                if (inBlockComment)
                {
                    chars[cursor] = ' ';
                    if (line[cursor] == '*' && cursor + 1 < chars.Length && line[cursor + 1] == '/')
                    {
                        chars[cursor + 1] = ' ';
                        inBlockComment = false;
                        cursor += 2;
                        continue;
                    }

                    cursor++;
                    continue;
                }

                if (inGoRawString)
                {
                    chars[cursor] = ' ';
                    if (line[cursor] == '`')
                        inGoRawString = false;
                    cursor++;
                    continue;
                }

                if (dartTripleQuote != '\0')
                {
                    if (IsTripleQuoteAt(line, cursor, dartTripleQuote))
                    {
                        ReplaceWithSpaces(chars, cursor, 3);
                        dartTripleQuote = '\0';
                        cursor += 3;
                        continue;
                    }

                    chars[cursor] = ' ';
                    cursor++;
                    continue;
                }

                if (cppRawStringTerminator != null)
                {
                    var closeIndex = line.IndexOf(cppRawStringTerminator, cursor, StringComparison.Ordinal);
                    if (closeIndex < 0)
                    {
                        ReplaceWithSpaces(chars, cursor, chars.Length - cursor);
                        break;
                    }

                    ReplaceWithSpaces(chars, cursor, closeIndex + cppRawStringTerminator.Length - cursor);
                    cursor = closeIndex + cppRawStringTerminator.Length;
                    cppRawStringTerminator = null;
                    continue;
                }

                if (line[cursor] == '/' && cursor + 1 < chars.Length && line[cursor + 1] == '/')
                    break;

                if (language == "go" && line[cursor] == '`')
                {
                    chars[cursor] = ' ';
                    inGoRawString = true;
                    cursor++;
                    continue;
                }

                if (language == "dart" && TryGetDartTripleStringStart(line, cursor, out var dartQuote, out var dartOpeningLength))
                {
                    var closeIndex = IndexOfTripleQuote(line, cursor + dartOpeningLength, dartQuote);
                    if (closeIndex < 0)
                    {
                        ReplaceWithSpaces(chars, cursor, chars.Length - cursor);
                        dartTripleQuote = dartQuote;
                        break;
                    }

                    ReplaceWithSpaces(chars, cursor, closeIndex + 3 - cursor);
                    cursor = closeIndex + 3;
                    continue;
                }

                if (language == "cpp" && TryGetCppRawStringTerminator(line, cursor, out var rawTerminator, out var rawOpeningLength))
                {
                    var closeIndex = line.IndexOf(rawTerminator, cursor + rawOpeningLength, StringComparison.Ordinal);
                    if (closeIndex < 0)
                    {
                        ReplaceWithSpaces(chars, cursor, chars.Length - cursor);
                        cppRawStringTerminator = rawTerminator;
                        break;
                    }

                    ReplaceWithSpaces(chars, cursor, closeIndex + rawTerminator.Length - cursor);
                    cursor = closeIndex + rawTerminator.Length;
                    continue;
                }

                if (line[cursor] is '"' or '\'' or '`')
                {
                    cursor = SkipCStyleQuotedLiteral(line, cursor) + 1;
                    continue;
                }

                if (line[cursor] == '/' && cursor + 1 < chars.Length && line[cursor + 1] == '*')
                {
                    chars[cursor] = ' ';
                    cursor++;
                    chars[cursor] = ' ';
                    inBlockComment = true;
                    cursor++;
                    continue;
                }

                cursor++;
            }

            result[lineIndex] = new string(chars);
        }

        return result;
    }

    private static bool TryGetDartTripleStringStart(string line, int start, out char quote, out int openingLength)
    {
        quote = '\0';
        openingLength = 0;
        var quoteIndex = start;

        if (line[start] is 'r' or 'R')
        {
            if (start > 0 && IsIdentifierChar(line[start - 1]))
                return false;
            quoteIndex = start + 1;
        }

        if (quoteIndex + 2 >= line.Length)
            return false;

        quote = line[quoteIndex];
        if (quote is not ('"' or '\'') || !IsTripleQuoteAt(line, quoteIndex, quote))
            return false;

        openingLength = quoteIndex - start + 3;
        return true;
    }

    private static bool IsTripleQuoteAt(string line, int start, char quote) =>
        start + 2 < line.Length
        && line[start] == quote
        && line[start + 1] == quote
        && line[start + 2] == quote;

    private static int IndexOfTripleQuote(string line, int start, char quote)
    {
        for (var i = start; i + 2 < line.Length; i++)
        {
            if (IsTripleQuoteAt(line, i, quote))
                return i;
        }

        return -1;
    }

    private static bool TryGetCppRawStringTerminator(string line, int start, out string terminator, out int openingLength)
    {
        terminator = string.Empty;
        openingLength = 0;
        if (line[start] != 'R' || start + 2 >= line.Length || line[start + 1] != '"')
            return false;

        var delimiterStart = start + 2;
        var parenIndex = line.IndexOf('(', delimiterStart);
        if (parenIndex < 0)
            return false;

        for (var i = delimiterStart; i < parenIndex; i++)
        {
            if (char.IsWhiteSpace(line[i]) || line[i] is '(' or ')' or '\\')
                return false;
        }

        terminator = ")" + line[delimiterStart..parenIndex] + "\"";
        openingLength = parenIndex - start + 1;
        return true;
    }

    private static string[] MaskHaskellBlockCommentLines(IReadOnlyList<string> lines)
    {
        var result = new string[lines.Count];
        var blockDepth = 0;

        for (var lineIndex = 0; lineIndex < lines.Count; lineIndex++)
        {
            var line = lines[lineIndex];
            var chars = line.ToCharArray();
            var cursor = 0;

            while (cursor < chars.Length)
            {
                if (blockDepth > 0)
                {
                    if (line[cursor] == '{' && cursor + 1 < chars.Length && line[cursor + 1] == '-')
                    {
                        chars[cursor] = ' ';
                        chars[cursor + 1] = ' ';
                        blockDepth++;
                        cursor += 2;
                        continue;
                    }

                    if (line[cursor] == '-' && cursor + 1 < chars.Length && line[cursor + 1] == '}')
                    {
                        chars[cursor] = ' ';
                        chars[cursor + 1] = ' ';
                        blockDepth--;
                        cursor += 2;
                        continue;
                    }

                    chars[cursor++] = ' ';
                    continue;
                }

                if (line[cursor] == '"')
                {
                    cursor = SkipCStyleQuotedLiteral(line, cursor) + 1;
                    continue;
                }

                if (line[cursor] == '-' && cursor + 1 < chars.Length && line[cursor + 1] == '-')
                    break;

                if (line[cursor] == '{' && cursor + 1 < chars.Length && line[cursor + 1] == '-')
                {
                    chars[cursor] = ' ';
                    chars[cursor + 1] = ' ';
                    blockDepth = 1;
                    cursor += 2;
                    continue;
                }

                cursor++;
            }

            result[lineIndex] = new string(chars);
        }

        return result;
    }

    private static int SkipCStyleQuotedLiteral(string line, int start)
    {
        var quote = line[start];
        var cursor = start + 1;
        while (cursor < line.Length)
        {
            if (quote != '`' && line[cursor] == '\\' && cursor + 1 < line.Length)
            {
                cursor += 2;
                continue;
            }

            if (line[cursor] == quote)
                return cursor;
            cursor++;
        }

        return line.Length;
    }

    private static readonly Regex VisualBasicRemCommentRegex = new(
        @"(?:^|:)\s*Rem\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex PascalBraceCommentRegex = new(@"\{[^}\r\n]*\}", RegexOptions.Compiled);
    private static readonly Regex PascalParenStarCommentRegex = new(@"\(\*.*?\*\)", RegexOptions.Compiled);

    private static string MaskPythonSingleLineFStrings(string line)
    {
        if (line.IndexOf('f') < 0 && line.IndexOf('F') < 0)
            return line;

        var masked = line.ToCharArray();
        for (var i = 0; i < line.Length; i++)
        {
            if (!TryOpenPythonSingleLineString(line, i, out var prefixLength, out var quoteChar, out var isRaw, out var isFString))
                continue;

            if (!isFString)
            {
                i += prefixLength;
                continue;
            }

            var quoteStart = i + prefixLength;
            var openingLength = prefixLength + 1;
            ReplaceWithSpaces(masked, i, openingLength);
            i += openingLength;

            var inExpression = false;
            var expressionDepth = 0;
            while (i < line.Length)
            {
                if (!inExpression)
                {
                    if (!isRaw && line[i] == '\\' && i + 1 < line.Length)
                    {
                        ReplaceWithSpaces(masked, i, 2);
                        i += 2;
                        continue;
                    }

                    if (line[i] == '{' && i + 1 < line.Length && line[i + 1] == '{')
                    {
                        ReplaceWithSpaces(masked, i, 2);
                        i += 2;
                        continue;
                    }

                    if (line[i] == '}' && i + 1 < line.Length && line[i + 1] == '}')
                    {
                        ReplaceWithSpaces(masked, i, 2);
                        i += 2;
                        continue;
                    }

                    if (line[i] == '{')
                    {
                        masked[i] = ' ';
                        inExpression = true;
                        expressionDepth = 1;
                        i++;
                        continue;
                    }

                    if (line[i] == quoteChar)
                    {
                        masked[i] = ' ';
                        i++;
                        break;
                    }

                    masked[i] = ' ';
                    i++;
                    continue;
                }

                if (line[i] == '{')
                {
                    expressionDepth++;
                    i++;
                    continue;
                }

                if (line[i] == '}')
                {
                    expressionDepth--;
                    masked[i] = ' ';
                    i++;
                    if (expressionDepth == 0)
                        inExpression = false;
                    continue;
                }

                if (line[i] == '\'' || line[i] == '"')
                {
                    var nestedQuote = line[i];
                    i++;
                    while (i < line.Length)
                    {
                        if (line[i] == '\\' && i + 1 < line.Length)
                        {
                            i += 2;
                            continue;
                        }

                        if (line[i] == nestedQuote)
                        {
                            i++;
                            break;
                        }

                        i++;
                    }

                    // Leave nested string contents in place; the generic string regex
                    // masks them later while the outer f-string wrapper is already gone.
                    // ネスト文字列は内容を残す。外側 f-string の殻を先に除去しておき、
                    // 内側の generic string regex で後からまとめてマスクする。
                    continue;
                }

                if (line[i] == '#')
                {
                    masked[i] = ' ';
                    i++;
                    continue;
                }

                i++;
            }

            i = Math.Max(i - 1, quoteStart);
        }

        return new string(masked);
    }

    private static string[] MaskPythonFStrings(IReadOnlyList<string> lines)
    {
        var result = new string[lines.Count];
        for (var lineIndex = 0; lineIndex < lines.Count; lineIndex++)
            result[lineIndex] = lines[lineIndex];

        for (var lineIndex = 0; lineIndex < result.Length; lineIndex++)
        {
            var line = result[lineIndex];
            if (line.IndexOf('f') < 0 && line.IndexOf('F') < 0)
                continue;

            var chars = line.ToCharArray();
            var changed = false;
            for (var column = 0; column < line.Length; column++)
            {
                if (!TryOpenPythonString(line, column, out var prefixLength, out var quoteChar, out var isRaw, out var isFString, out var isTripleQuoted))
                    continue;

                if (!isFString)
                {
                    column += prefixLength;
                    continue;
                }

                if (!isTripleQuoted)
                {
                    result[lineIndex] = MaskPythonSingleLineFStrings(line);
                    chars = result[lineIndex].ToCharArray();
                    changed = true;
                    break;
                }

                MaskPythonTripleQuotedFString(result, lineIndex, column, prefixLength, quoteChar, isRaw, out var endLineIndex, out var endColumn);
                lineIndex = endLineIndex;
                line = result[lineIndex];
                chars = line.ToCharArray();
                column = endColumn;
                changed = true;
            }

            if (changed)
                result[lineIndex] = new string(chars);
        }

        return result;
    }

    private static void MaskPythonTripleQuotedFString(
        string[] lines,
        int startLineIndex,
        int startColumn,
        int prefixLength,
        char quoteChar,
        bool isRaw,
        out int endLineIndex,
        out int endColumn)
    {
        var lineIndex = startLineIndex;
        var column = startColumn;
        var inExpression = false;
        var inExpressionString = false;
        var expressionDepth = 0;
        var expressionStringQuote = '\0';
        var expressionStringTripleQuoted = false;

        endLineIndex = startLineIndex;
        endColumn = startColumn;

        while (lineIndex < lines.Length)
        {
            var line = lines[lineIndex];
            var chars = line.ToCharArray();
            if (lineIndex == startLineIndex)
            {
                ReplaceWithSpaces(chars, startColumn, prefixLength + 3);
                column = startColumn + prefixLength + 3;
            }
            else
            {
                column = 0;
            }

            while (column < line.Length)
            {
                if (!inExpression)
                {
                    if (!isRaw && line[column] == '\\' && column + 1 < line.Length)
                    {
                        ReplaceWithSpaces(chars, column, 2);
                        column += 2;
                        continue;
                    }

                    if (line[column] == '{' && column + 1 < line.Length && line[column + 1] == '{')
                    {
                        ReplaceWithSpaces(chars, column, 2);
                        column += 2;
                        continue;
                    }

                    if (line[column] == '}' && column + 1 < line.Length && line[column + 1] == '}')
                    {
                        ReplaceWithSpaces(chars, column, 2);
                        column += 2;
                        continue;
                    }

                    if (line[column] == '{')
                    {
                        chars[column++] = ' ';
                        inExpression = true;
                        expressionDepth = 1;
                        continue;
                    }

                    if (column + 2 < line.Length
                        && line[column] == quoteChar
                        && line[column + 1] == quoteChar
                        && line[column + 2] == quoteChar)
                    {
                        ReplaceWithSpaces(chars, column, 3);
                        lines[lineIndex] = new string(chars);
                        endLineIndex = lineIndex;
                        endColumn = column + 2;
                        return;
                    }

                    chars[column++] = ' ';
                    continue;
                }

                if (inExpressionString)
                {
                    if (line[column] == '\\' && column + 1 < line.Length)
                    {
                        column += 2;
                        continue;
                    }

                    if (expressionStringTripleQuoted)
                    {
                        if (column + 2 < line.Length
                            && line[column] == expressionStringQuote
                            && line[column + 1] == expressionStringQuote
                            && line[column + 2] == expressionStringQuote)
                        {
                            column += 3;
                            inExpressionString = false;
                            continue;
                        }

                        column++;
                        continue;
                    }

                    if (line[column] == expressionStringQuote)
                    {
                        column++;
                        inExpressionString = false;
                        continue;
                    }

                    column++;
                    continue;
                }

                if (line[column] == '\'' || line[column] == '"')
                {
                    expressionStringQuote = line[column];
                    expressionStringTripleQuoted = column + 2 < line.Length
                        && line[column + 1] == expressionStringQuote
                        && line[column + 2] == expressionStringQuote;
                    column += expressionStringTripleQuoted ? 3 : 1;
                    inExpressionString = true;
                    continue;
                }

                if (line[column] == '{')
                {
                    expressionDepth++;
                    column++;
                    continue;
                }

                if (line[column] == '}')
                {
                    expressionDepth--;
                    chars[column++] = ' ';
                    if (expressionDepth == 0)
                        inExpression = false;
                    continue;
                }

                column++;
            }

            lines[lineIndex] = new string(chars);
            lineIndex++;
        }

        endLineIndex = Math.Max(startLineIndex, lines.Length - 1);
        endColumn = 0;
    }

    private static void ReplaceWithSpaces(char[] buffer, int start, int length)
    {
        for (var i = start; i < start + length && i < buffer.Length; i++)
            buffer[i] = ' ';
    }

    private static bool TryOpenPythonSingleLineString(
        string line,
        int startIndex,
        out int prefixLength,
        out char quoteChar,
        out bool isRaw,
        out bool isFString)
    {
        prefixLength = 0;
        quoteChar = '\0';
        isRaw = false;
        isFString = false;

        if (startIndex < 0 || startIndex >= line.Length)
            return false;

        if (startIndex > 0 && IsIdentifierChar(line[startIndex - 1]))
            return false;

        var p = startIndex;
        var prefixChars = 0;
        while (p < line.Length && prefixChars < 2 && IsPythonStringPrefixChar(line[p]))
        {
            if (line[p] is 'r' or 'R')
                isRaw = true;
            if (line[p] is 'f' or 'F')
                isFString = true;
            p++;
            prefixChars++;
        }

        if (p >= line.Length || (line[p] != '\'' && line[p] != '"'))
            return false;
        if (p + 2 < line.Length && line[p] == line[p + 1] && line[p] == line[p + 2])
            return false;

        prefixLength = p - startIndex;
        quoteChar = line[p];
        return true;
    }

    private static bool TryOpenPythonString(
        string line,
        int startIndex,
        out int prefixLength,
        out char quoteChar,
        out bool isRaw,
        out bool isFString,
        out bool isTripleQuoted)
    {
        isTripleQuoted = false;
        if (!TryOpenPythonSingleOrTripleString(line, startIndex, out prefixLength, out quoteChar, out isRaw, out isFString, out isTripleQuoted))
            return false;
        return true;
    }

    private static bool TryOpenPythonSingleOrTripleString(
        string line,
        int startIndex,
        out int prefixLength,
        out char quoteChar,
        out bool isRaw,
        out bool isFString,
        out bool isTripleQuoted)
    {
        prefixLength = 0;
        quoteChar = '\0';
        isRaw = false;
        isFString = false;
        isTripleQuoted = false;

        if (startIndex < 0 || startIndex >= line.Length)
            return false;

        if (startIndex > 0 && IsIdentifierChar(line[startIndex - 1]))
            return false;

        var p = startIndex;
        var prefixChars = 0;
        while (p < line.Length && prefixChars < 2 && IsPythonStringPrefixChar(line[p]))
        {
            if (line[p] is 'r' or 'R')
                isRaw = true;
            if (line[p] is 'f' or 'F')
                isFString = true;
            p++;
            prefixChars++;
        }

        if (p >= line.Length || (line[p] != '\'' && line[p] != '"'))
            return false;

        prefixLength = p - startIndex;
        quoteChar = line[p];
        isTripleQuoted = p + 2 < line.Length && line[p + 1] == quoteChar && line[p + 2] == quoteChar;
        return true;
    }

    private static bool IsIgnoredCallName(string language, string name)
    {
        if (LanguageSpecificCallNameKeeps.TryGetValue(language, out var languageSpecificKeepNames)
            && languageSpecificKeepNames.Contains(name))
        {
            return false;
        }

        if (language == "php")
        {
            if (SharedIgnoredCallNamesCaseInsensitive.Contains(name))
                return true;
        }
        else if (SharedIgnoredCallNames.Contains(name))
        {
            return true;
        }

        return LanguageSpecificIgnoredCallNames.TryGetValue(language, out var languageSpecificIgnoredNames)
            && languageSpecificIgnoredNames.Contains(name);
    }

    private static bool IsConstructorCallName(string language, string preparedLine, int nameIndex)
    {
        var probe = nameIndex - 1;
        while (probe >= 0 && char.IsWhiteSpace(preparedLine[probe]))
            probe--;

        if (probe < 0)
            return false;

        while (probe >= 0)
        {
            char? separator = null;
            if (probe >= 1 && preparedLine[probe] == ':' && preparedLine[probe - 1] == ':')
            {
                separator = ':';
                probe -= 2;
            }
            else if (preparedLine[probe] is '.' or '\\')
            {
                separator = preparedLine[probe];
                probe--;
            }

            if (separator == null)
                break;

            var segmentEnd = probe;
            while (probe >= 0 && IsIdentifierChar(preparedLine[probe]))
                probe--;

            var consumedSegment = segmentEnd >= 0 && segmentEnd >= probe + 1;
            if (!consumedSegment && separator != '\\')
                return false;
        }

        while (probe >= 0 && char.IsWhiteSpace(preparedLine[probe]))
            probe--;

        if (probe < 0)
            return false;

        var tokenEnd = probe;
        while (probe >= 0 && IsIdentifierChar(preparedLine[probe]))
            probe--;

        var tokenStart = probe + 1;
        if (tokenStart > tokenEnd)
            return false;

        var token = preparedLine[tokenStart..(tokenEnd + 1)];
        return language == "php"
            ? string.Equals(token, "new", StringComparison.OrdinalIgnoreCase)
            : string.Equals(token, "new", StringComparison.Ordinal);
    }

    private static readonly HashSet<string> KotlinTypeProjectionModifierNames = new(StringComparer.Ordinal)
    {
        "in", "out",
    };

    private readonly record struct NestedGenericCallCandidate(string Name, int NameIndex);

    private static void EmitGenericInvocationTypeArgumentReferences(
        string language,
        string preparedLine,
        int nameIndex,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        SymbolRecord? container)
    {
        if (language is not ("csharp" or "java" or "kotlin"))
            return;

        if (!TryGetPostNameGenericInvocationTypeArgumentSpan(preparedLine, nameIndex, out var argumentsStart, out var argumentsLength)
            && (language != "java"
                || !TryGetJavaExplicitGenericInvocationTypeArgumentSpan(preparedLine, nameIndex, out argumentsStart, out argumentsLength)))
        {
            return;
        }

        if (argumentsLength <= 0)
            return;

        var ignoredSegments = language == "kotlin"
            ? KotlinTypeProjectionModifierNames
            : null;

        AddTypeExpressionSegments(
            references,
            seen,
            fileId,
            preparedLine.Substring(argumentsStart, argumentsLength),
            argumentsStart,
            context,
            lineNumber,
            container,
            language,
            ignoredSegments);
        AddGenericInvocationTypeArgumentSegments(
            references,
            seen,
            fileId,
            preparedLine.Substring(argumentsStart, argumentsLength),
            argumentsStart,
            context,
            lineNumber,
            container,
            language,
            ignoredSegments);
    }

    private static void AddGenericInvocationTypeArgumentSegments(
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string expression,
        int expressionStartInLine,
        string context,
        int lineNumber,
        SymbolRecord? container,
        string language,
        IReadOnlySet<string>? ignoredSegments)
    {
        if (language != "csharp")
            return;

        for (var i = 0; i < expression.Length; i++)
        {
            if (!IsTypeExpressionIdentifierStart(language, expression[i]))
                continue;

            var segmentStart = i;
            if (expression[i] == '@')
                i++;
            while (i < expression.Length && IsTypeExpressionIdentifierPart(language, expression[i]))
                i++;

            var rawSegment = expression.Substring(segmentStart, i - segmentStart);
            var isEscapedCSharpIdentifier = rawSegment.Length > 0 && rawSegment[0] == '@';
            var segment = NormalizeCSharpIdentifier(rawSegment);
            if (i + 1 < expression.Length && expression[i] == ':' && expression[i + 1] == ':')
            {
                i++;
                continue;
            }

            AddTypeReferenceSegment(
                references,
                seen,
                fileId,
                segment,
                expressionStartInLine + segmentStart,
                context,
                lineNumber,
                container,
                language,
                isEscapedCSharpIdentifier,
                ignoredSegments,
                "generic_type_argument");
            i--;
        }
    }

    private static bool TryGetPostNameGenericInvocationTypeArgumentSpan(
        string preparedLine,
        int nameIndex,
        out int argumentsStart,
        out int argumentsLength)
    {
        argumentsStart = -1;
        argumentsLength = 0;

        if (nameIndex < 0 || nameIndex >= preparedLine.Length || !IsAtAwareAsciiIdentifierStart(preparedLine, nameIndex))
            return false;

        var scan = ConsumeAtAwareAsciiIdentifier(preparedLine, nameIndex);
        if (scan + 1 < preparedLine.Length
            && preparedLine[scan] == '?'
            && preparedLine[scan + 1] == '.')
        {
            scan += 2;
        }

        if (scan >= preparedLine.Length || preparedLine[scan] != '<')
            return false;

        var closeAngle = FindMatchingChar(preparedLine, scan, '<', '>');
        if (closeAngle <= scan)
            return false;

        var after = closeAngle + 1;
        while (after < preparedLine.Length && char.IsWhiteSpace(preparedLine[after]))
            after++;

        if (after >= preparedLine.Length || preparedLine[after] != '(')
            return false;

        argumentsStart = scan + 1;
        argumentsLength = closeAngle - scan - 1;
        return true;
    }

    private static bool TryGetJavaExplicitGenericInvocationTypeArgumentSpan(
        string preparedLine,
        int nameIndex,
        out int argumentsStart,
        out int argumentsLength)
    {
        argumentsStart = -1;
        argumentsLength = 0;

        var closeAngle = nameIndex - 1;
        while (closeAngle >= 0 && char.IsWhiteSpace(preparedLine[closeAngle]))
            closeAngle--;

        if (closeAngle < 0 || preparedLine[closeAngle] != '>')
            return false;

        var openAngle = FindMatchingOpenChar(preparedLine, closeAngle, '<', '>');
        if (openAngle < 0)
            return false;

        var beforeOpen = openAngle - 1;
        while (beforeOpen >= 0 && char.IsWhiteSpace(preparedLine[beforeOpen]))
            beforeOpen--;

        if (beforeOpen < 0 || preparedLine[beforeOpen] != '.')
            return false;

        argumentsStart = openAngle + 1;
        argumentsLength = closeAngle - openAngle - 1;
        return true;
    }

    private static int FindMatchingOpenChar(string text, int closeIndex, char openChar, char closeChar)
    {
        if (closeIndex < 0 || closeIndex >= text.Length || text[closeIndex] != closeChar)
            return -1;

        var depth = 0;
        for (var i = closeIndex; i >= 0; i--)
        {
            if (text[i] == closeChar)
            {
                depth++;
                continue;
            }

            if (text[i] != openChar)
                continue;

            depth--;
            if (depth == 0)
                return i;
        }

        return -1;
    }

    private static IEnumerable<NestedGenericCallCandidate> EnumerateNestedGenericCallCandidates(
        string preparedLine,
        HashSet<int> matchedCallIndices)
    {
        for (var i = 0; i < preparedLine.Length; i++)
        {
            if (!IsAtAwareAsciiIdentifierStart(preparedLine, i))
                continue;
            if (i > 0 && (IsIdentifierChar(preparedLine[i - 1]) || preparedLine[i - 1] == '$' || preparedLine[i - 1] == '@'))
                continue;

            var nameStart = i;
            i = ConsumeAtAwareAsciiIdentifier(preparedLine, i);

            if (matchedCallIndices.Contains(nameStart))
            {
                i--;
                continue;
            }

            var scan = i;
            if (scan + 1 < preparedLine.Length
                && preparedLine[scan] == '?'
                && preparedLine[scan + 1] == '.')
            {
                scan += 2;
            }

            if (scan >= preparedLine.Length || preparedLine[scan] != '<')
            {
                i--;
                continue;
            }

            if (!TrySkipBalancedGenericArgs(preparedLine, ref scan, out var sawNestedGeneric) || !sawNestedGeneric)
            {
                i--;
                continue;
            }

            while (scan < preparedLine.Length && char.IsWhiteSpace(preparedLine[scan]))
                scan++;

            if (scan < preparedLine.Length && preparedLine[scan] == '(')
                yield return new NestedGenericCallCandidate(preparedLine[nameStart..i], nameStart);

            i--;
        }
    }

    private static IEnumerable<NestedGenericCallCandidate> EnumerateNestedGenericInitializerCandidates(
        string preparedLine,
        HashSet<int> matchedInitializerIndices,
        bool requireOpeningBrace)
    {
        for (var i = 0; i < preparedLine.Length; i++)
        {
            if (!IsStandaloneNewKeyword(preparedLine, i))
                continue;

            var scan = i + 3;
            if (!TryReadQualifiedTypeName(preparedLine, ref scan, out var name, out var nameIndex))
            {
                i += 2;
                continue;
            }

            if (matchedInitializerIndices.Contains(nameIndex))
            {
                i = scan - 1;
                continue;
            }

            if (!TrySkipBalancedGenericArgs(preparedLine, ref scan, out var sawNestedGeneric) || !sawNestedGeneric)
            {
                i = scan - 1;
                continue;
            }

            if (!TrySkipArraySuffixes(preparedLine, ref scan))
            {
                i = scan - 1;
                continue;
            }

            while (scan < preparedLine.Length && char.IsWhiteSpace(preparedLine[scan]))
                scan++;

            if (requireOpeningBrace)
            {
                if (scan < preparedLine.Length && preparedLine[scan] == '{')
                    yield return new NestedGenericCallCandidate(name, nameIndex);
            }
            else if (scan == preparedLine.Length)
            {
                yield return new NestedGenericCallCandidate(name, nameIndex);
            }

            i = scan - 1;
        }
    }

    private static bool TryReadQualifiedTypeName(
        string preparedLine,
        ref int scan,
        out string name,
        out int nameIndex)
    {
        name = string.Empty;
        nameIndex = -1;

        while (true)
        {
            while (scan < preparedLine.Length && char.IsWhiteSpace(preparedLine[scan]))
                scan++;

            if (scan >= preparedLine.Length || !IsAtAwareAsciiIdentifierStart(preparedLine, scan))
                return false;

            var segmentStart = scan;
            scan = ConsumeAtAwareAsciiIdentifier(preparedLine, scan);

            name = preparedLine[segmentStart..scan];
            nameIndex = segmentStart;

            var separatorScan = scan;
            while (separatorScan < preparedLine.Length && char.IsWhiteSpace(preparedLine[separatorScan]))
                separatorScan++;

            if (separatorScan + 1 < preparedLine.Length
                && preparedLine[separatorScan] == ':'
                && preparedLine[separatorScan + 1] == ':')
            {
                scan = separatorScan + 2;
                continue;
            }

            if (separatorScan < preparedLine.Length && preparedLine[separatorScan] == '.')
            {
                scan = separatorScan + 1;
                continue;
            }

            scan = separatorScan;
            return true;
        }
    }

    private static bool TrySkipArraySuffixes(string preparedLine, ref int scan)
    {
        while (true)
        {
            while (scan < preparedLine.Length && char.IsWhiteSpace(preparedLine[scan]))
                scan++;

            if (scan >= preparedLine.Length || preparedLine[scan] != '[')
                return true;

            scan++;
            while (scan < preparedLine.Length && preparedLine[scan] != ']')
                scan++;

            if (scan >= preparedLine.Length || preparedLine[scan] != ']')
                return false;

            scan++;
        }
    }

    private static bool ShouldSkipInitializerName(string language, string name) =>
        (language == "csharp" && CSharpBuiltInTypeNames.Contains(name))
        || (language == "java" && JavaPrimitiveTypeNames.Contains(name))
        || IsIgnoredCallName(language, name);

    private static bool IsStandaloneNewKeyword(string preparedLine, int index)
    {
        if (index < 0 || index + 3 > preparedLine.Length)
            return false;
        if (preparedLine[index] != 'n'
            || preparedLine[index + 1] != 'e'
            || preparedLine[index + 2] != 'w')
        {
            return false;
        }

        if (index > 0 && IsIdentifierChar(preparedLine[index - 1]))
            return false;

        return index + 3 >= preparedLine.Length || !IsIdentifierChar(preparedLine[index + 3]);
    }

    private static bool TrySkipBalancedGenericArgs(string preparedLine, ref int scan, out bool sawNestedGeneric)
    {
        sawNestedGeneric = false;
        if (scan >= preparedLine.Length || preparedLine[scan] != '<')
            return false;

        var depth = 0;
        while (scan < preparedLine.Length)
        {
            var ch = preparedLine[scan++];
            if (ch == '<')
            {
                depth++;
                if (depth > 1)
                    sawNestedGeneric = true;
            }
            else if (ch == '>')
            {
                depth--;
                if (depth == 0)
                    return true;
                if (depth < 0)
                    return false;
            }
        }

        return false;
    }

    private static bool IsAsciiIdentifierStartChar(char ch) =>
        ch == '_' || (ch >= 'A' && ch <= 'Z') || (ch >= 'a' && ch <= 'z');

    private static bool IsAtAwareAsciiIdentifierStart(string text, int index)
    {
        if (index < 0 || index >= text.Length)
            return false;

        if (text[index] == '@')
            return index + 1 < text.Length && IsAsciiIdentifierStartChar(text[index + 1]);

        return IsAsciiIdentifierStartChar(text[index]);
    }

    private static int ConsumeAtAwareAsciiIdentifier(string text, int startIndex)
    {
        var index = startIndex;
        if (index < text.Length && text[index] == '@')
            index++;

        if (index >= text.Length || !IsAsciiIdentifierStartChar(text[index]))
            return startIndex;

        index++;
        while (index < text.Length && IsIdentifierChar(text[index]))
            index++;

        return index;
    }

    private static bool IsIdentifierChar(char ch) =>
        char.IsLetterOrDigit(ch) || ch == '_';

    /// <summary>
    /// Classify a call-looking identifier as an attribute/annotation when it appears inside
    /// a C# `[...]` attribute list or is preceded by a Java-family `@` marker. Returns null
    /// for ordinary method calls so the caller emits the default `call` reference kind.
    /// 呼び出しに見える識別子を、C# の `[...]` 属性リスト内や Java 系 `@` 付き注釈に該当する
    /// 場合に専用の reference kind へ分類する。通常の呼び出しは null を返して既定の `call` を維持する。
    /// </summary>
    private static string? TryClassifyMetadataReference(
        string language,
        string preparedLine,
        int nameIndex,
        bool insideCSharpAttributeRange)
    {
        if (language == "csharp")
            return insideCSharpAttributeRange ? "attribute" : null;

        if (nameIndex >= 0
            && nameIndex < preparedLine.Length
            && preparedLine[nameIndex] == '@'
            && AnnotationLanguages.Contains(language))
        {
            return "annotation";
        }

        var probe = nameIndex - 1;
        while (probe >= 0 && char.IsWhiteSpace(preparedLine[probe]))
            probe--;
        if (probe < 0)
            return null;

        if (AnnotationLanguages.Contains(language))
            return IsAnnotationContext(preparedLine, probe) ? "annotation" : null;

        return null;
    }

    /// <summary>
    /// Build per-line column ranges that identify C# `[...]` attribute sections. Handles
    /// declaration-position detection (including parameter attributes preceded by `(` / `,`
    /// via forward look-ahead) and multi-line `[\n ... \n]` sections. Each inner list holds
    /// ordered `(startColumn, endColumnExclusive)` ranges that are inside an attribute section
    /// on that line. Call sites whose name column falls inside one of these ranges are
    /// reclassified as `attribute` instead of `call`.
    /// C# の `[...]` 属性セクションを行ごとの列範囲で表すテーブルを構築する。
    /// `(` / `,` の直後に置かれるパラメータ属性を forward lookahead で、複数行にわたる
    /// `[\n ... \n]` 属性を跨行トラッキングで検出する。各行のリストは属性セクションに含まれる
    /// `(開始列, 終端列 (exclusive))` のレンジを保持し、呼び出し名の列がどれかのレンジに含まれる場合に
    /// `call` ではなく `attribute` へ再分類する。
    /// </summary>
    private static (List<List<(int start, int end)>>, List<List<(int start, int end)>>) BuildCSharpAttributeRanges(string[] preparedLines)
    {
        var perLine = new List<List<(int, int)>>(preparedLines.Length);
        var perLineTopLevel = new List<List<(int, int)>>(preparedLines.Length);
        for (var i = 0; i < preparedLines.Length; i++)
        {
            perLine.Add(new List<(int, int)>());
            perLineTopLevel.Add(new List<(int, int)>());
        }

        // Stack entries capture the opening `[` position, whether that bracket was at
        // a C# declaration (attribute) position, and a snapshot of the global paren depth
        // at that moment. The snapshot lets us compute an attribute-section-local paren
        // depth (`parenDepth - parenDepthAtOpen`), which is what the top-level zone tracking
        // uses so that parameter attributes like `void M([Attr] int x)` still have their
        // attribute-list top level at section-local depth 0 even though the global depth
        // is inside the method's parameter list.
        // スタックは `[` の位置、その bracket が属性位置だったか、および開いた瞬間の
        // グローバル paren 深さのスナップショットを保持する。スナップショットを使うと
        // 属性セクション内ローカルの paren 深さ (`parenDepth - parenDepthAtOpen`) が
        // 得られるので、`void M([Attr] int x)` のように外側の method 引数リストの中で
        // 開く属性セクションでも、セクション内では top-level (local depth 0) として扱える。
        var bracketStack = new Stack<(int li, int ci, bool isAttr, int parenDepthAtOpen)>();
        char lastMeaningful = '\0';
        int parenDepth = 0;
        bool lastClosedBracketWasAttribute = false;

        // Top-level zone tracking: while we are inside an attribute section and the paren
        // depth is at the section's open snapshot (section-local depth 0), the current zone
        // span is open. When parens open inside the section we close it; when they fully
        // close again we reopen. When the attribute section itself closes, we emit the span.
        // top-level ゾーン追跡: 属性セクション内かつセクションローカルの paren 深さが 0 の
        // あいだだけゾーンを開いておき、セクション内の `(` で閉じ、`)` で再び開く。
        // セクションが閉じる `]` で確定させる。
        int topZoneStartLi = -1;
        int topZoneStartCi = 0;

        void EmitTopZone(int endLi, int endCi)
        {
            if (topZoneStartLi < 0)
                return;
            for (var l = topZoneStartLi; l <= endLi; l++)
            {
                int s = (l == topZoneStartLi) ? topZoneStartCi : 0;
                int e = (l == endLi) ? endCi : preparedLines[l].Length;
                if (e > s)
                    perLineTopLevel[l].Add((s, e));
            }
            topZoneStartLi = -1;
        }

        for (var li = 0; li < preparedLines.Length; li++)
        {
            var line = preparedLines[li];
            for (var ci = 0; ci < line.Length; ci++)
            {
                var c = line[ci];
                if (c == '/' && ci + 1 < line.Length && line[ci + 1] == '/')
                    break;

                if (char.IsWhiteSpace(c))
                    continue;

                if (c == '(')
                {
                    // If the innermost enclosing bracket is an attribute section and we are
                    // currently at that section's local top level, close the top-level zone
                    // just before the `(`. Use the stack top's `parenDepthAtOpen` snapshot so
                    // parameter attributes inside an outer `(...)` still get their top level
                    // tracked correctly.
                    // 直近の `[` が属性セクションで、かつその section-local 深さで top-level のとき、
                    // `(` 直前でゾーンを閉じる。外側の `(...)` の中で開く属性セクションにも対応するため、
                    // グローバル depth ではなくスタック top の開いたときの snapshot と比較する。
                    if (bracketStack.Count > 0)
                    {
                        var top = bracketStack.Peek();
                        if (top.isAttr && parenDepth == top.parenDepthAtOpen && topZoneStartLi >= 0)
                            EmitTopZone(li, ci);
                    }
                    parenDepth++;
                    lastMeaningful = c;
                    continue;
                }
                if (c == ')')
                {
                    if (parenDepth > 0)
                    {
                        parenDepth--;
                        // If the innermost `[` is an attribute section and we just returned
                        // to that section's local top level, reopen the top-level zone.
                        // 直近の `[` が属性セクションで、section-local top-level に戻ってきたら
                        // top-level ゾーンを再開する。
                        if (bracketStack.Count > 0)
                        {
                            var top = bracketStack.Peek();
                            if (top.isAttr && parenDepth == top.parenDepthAtOpen && topZoneStartLi < 0)
                            {
                                topZoneStartLi = li;
                                topZoneStartCi = ci + 1;
                            }
                        }
                    }
                    lastMeaningful = c;
                    continue;
                }

                if (c == '[')
                {
                    bool isAttr = EvaluateCSharpAttributePosition(
                        lastMeaningful, lastClosedBracketWasAttribute, preparedLines, li, ci);
                    bracketStack.Push((li, ci, isAttr, parenDepth));
                    if (isAttr && topZoneStartLi < 0)
                    {
                        // Start top-level zone just after the `[` so the `[` itself is not
                        // inside the zone. Section-local depth is 0 by construction at the
                        // open bracket.
                        // `[` 直後から top-level ゾーンを開始する。開いた瞬間は section-local 深さ 0。
                        topZoneStartLi = li;
                        topZoneStartCi = ci + 1;
                    }
                    lastMeaningful = c;
                    continue;
                }

                if (c == ']')
                {
                    if (bracketStack.Count > 0)
                    {
                        var opened = bracketStack.Pop();
                        lastClosedBracketWasAttribute = opened.isAttr;
                        if (opened.isAttr)
                        {
                            // Record the attribute section span for every line it covers so
                            // cross-line `[\n Foo("x")\n]` also classifies `Foo` as attribute.
                            // 属性セクションがまたぐ全ての行に対して範囲を記録し、
                            // `[\n Foo("x")\n]` のような跨行ケースでも `Foo` が属性として分類されるようにする。
                            for (var l = opened.li; l <= li; l++)
                            {
                                int s = (l == opened.li) ? opened.ci : 0;
                                int e = (l == li) ? ci + 1 : preparedLines[l].Length;
                                perLine[l].Add((s, e));
                            }
                            // Close the top-level zone at the `]`. Section-local depth should
                            // be 0 here (we are at the closing bracket of this section) — if
                            // it is not, we drop the open zone because paren balancing was
                            // malformed.
                            // `]` で top-level ゾーンを確定する。section-local 深さが 0 のはず。
                            // 不整合入力ならゾーンを捨てる。
                            if (parenDepth == opened.parenDepthAtOpen)
                            {
                                EmitTopZone(li, ci + 1);
                            }
                            else
                            {
                                topZoneStartLi = -1;
                            }
                        }
                    }
                    else
                    {
                        lastClosedBracketWasAttribute = false;
                    }
                    lastMeaningful = c;
                    continue;
                }

                lastMeaningful = c;
            }
        }

        return (perLine, perLineTopLevel);
    }

    /// <summary>
    /// Decide whether a `[` token sits at a C# attribute position based on the immediately
    /// preceding meaningful character. `(` / `,` (parameter attributes) are disambiguated via
    /// forward look-ahead because both attributes and C# 12 collection expressions can follow.
    /// `[` が C# の属性位置にあるかを、直前の非空白文字から判定する。`(` / `,` の直後は
    /// パラメータ属性にも collection expression にもなりうるため、forward lookahead で区別する。
    /// </summary>
    private static bool EvaluateCSharpAttributePosition(
        char lastMeaningful,
        bool lastClosedBracketWasAttribute,
        string[] preparedLines,
        int startLi,
        int startCi)
    {
        // Start of file or after a scope/statement boundary — attribute position.
        // ファイル先頭、あるいはスコープ・文境界の直後は属性位置。
        if (lastMeaningful is '\0' or '{' or '}' or ';')
            return true;

        // Chained attribute list `[A][B]`: the prior `]` must have closed an attribute section.
        // `arr[i][Compute()]` → the prior `]` closed an indexer, so stays `call`.
        // 連続した属性リスト `[A][B]` は、直前の `]` が属性セクションを閉じていたときのみ属性扱い。
        // `arr[i][Compute()]` の `]` は indexer を閉じているため `call` のまま。
        if (lastMeaningful == ']')
            return lastClosedBracketWasAttribute;

        // Parameter / type-parameter / lambda attribute candidates (`(`, `,`, `<`, `=`):
        // `void M([Attr] T x)`, `class C<[Attr] T>`, `var f = [Attr] () => body`, or
        // `Consume([Make()])`. Disambiguate by scanning forward to the matching `]` and
        // checking whether the next meaningful token begins a declaration (identifier /
        // `@` / `(` for tuple types or lambda parameter lists / `[` chained).
        // パラメータ / 型パラメータ / ラムダ属性候補 (`(`, `,`, `<`, `=`) は
        // `void M([Attr] T x)`・`class C<[Attr] T>`・`var f = [Attr] () => body`・
        // `Consume([Make()])` いずれにもなりうる。対応する `]` まで進んで次トークンが
        // 宣言やラムダを開始するか（識別子 / `@` / tuple・ラムダ仮引数の `(` / chained `[`）で区別する。
        if (lastMeaningful is '(' or ',' or '<' or '=')
            return IsCSharpAttributeFollowedByDeclaration(preparedLines, startLi, startCi);

        return false;
    }

    /// <summary>
    /// Keywords that indicate the preceding `[...]` is an expression (collection / pattern /
    /// switch target) rather than an attribute section when they appear after `]`.
    /// `]` の直後に現れると、直前の `[...]` が属性ではなく式（collection / pattern / switch 対象）
    /// であることを示す C# のキーワード集合。
    /// </summary>
    private static readonly HashSet<string> CSharpExpressionContinuationKeywords = new(StringComparer.Ordinal)
    {
        "is", "as", "switch", "with", "when",
    };

    /// <summary>
    /// Scan forward from a `[` to its matching `]` (skipping balanced parens) and return true
    /// when the next meaningful character begins an identifier-like token. Works across lines so
    /// `void M(\n    [Attr]\n    T x\n)` is recognized as a parameter attribute.
    /// `[` から対応する `]` まで進んで、`]` の次の非空白文字が識別子を始める場合に true を返す。
    /// 行を跨ぐ走査にも対応しているため `void M(\n    [Attr]\n    T x\n)` も属性として認識される。
    /// </summary>
    private static bool IsCSharpAttributeFollowedByDeclaration(string[] preparedLines, int startLi, int startCi)
    {
        var bracketDepth = 1;
        var parenDepth = 0;
        var li = startLi;
        var ci = startCi + 1;
        while (li < preparedLines.Length)
        {
            var line = preparedLines[li];
            while (ci < line.Length)
            {
                var c = line[ci];
                if (c == '/' && ci + 1 < line.Length && line[ci + 1] == '/' && parenDepth == 0)
                    break;

                if (c == '(')
                {
                    parenDepth++;
                    ci++;
                    continue;
                }
                if (c == ')')
                {
                    if (parenDepth > 0)
                        parenDepth--;
                    ci++;
                    continue;
                }
                if (parenDepth > 0)
                {
                    ci++;
                    continue;
                }
                if (c == '[')
                {
                    bracketDepth++;
                    ci++;
                    continue;
                }
                if (c == ']')
                {
                    bracketDepth--;
                    if (bracketDepth == 0)
                    {
                        ci++;
                        return NextTokenStartsDeclaration(preparedLines, li, ci);
                    }
                    ci++;
                    continue;
                }
                ci++;
            }
            li++;
            ci = 0;
        }
        return false;
    }

    /// <summary>
    /// After the closing `]` of a candidate `[...]`, inspect the next meaningful token to decide
    /// whether it begins a declaration. Accepts identifiers (except expression-continuation
    /// keywords like `is` / `as` / `switch` / `with` / `when`), leading `@` (verbatim identifier),
    /// `(` (tuple-typed parameter), and chained `[` (recurse for `[A][B]`).
    /// 閉じ `]` の直後のトークンで宣言が始まるかを判定する。識別子（式継続の `is` / `as` /
    /// `switch` / `with` / `when` は除外）、`@`（verbatim 識別子）、`(`（tuple パラメータ型）、
    /// `[`（`[A][B]` の連結）を受け入れる。
    /// </summary>
    private static bool NextTokenStartsDeclaration(string[] preparedLines, int li, int ci)
    {
        while (li < preparedLines.Length)
        {
            var line = preparedLines[li];
            while (ci < line.Length && char.IsWhiteSpace(line[ci]))
                ci++;
            if (ci < line.Length)
            {
                var first = line[ci];
                if (first == '@' || first == '(')
                    return true;
                if (first == '[')
                    return IsCSharpAttributeFollowedByDeclaration(preparedLines, li, ci);
                if (!IsIdentifierChar(first))
                    return false;
                var start = ci;
                while (ci < line.Length && IsIdentifierChar(line[ci]))
                    ci++;
                var token = line.Substring(start, ci - start);
                return !CSharpExpressionContinuationKeywords.Contains(token);
            }
            li++;
            ci = 0;
        }
        return false;
    }

    private static bool IsInsideCSharpAttributeRange(IReadOnlyList<(int start, int end)> ranges, int index)
    {
        foreach (var (start, end) in ranges)
        {
            if (index >= start && index < end)
                return true;
        }
        return false;
    }

    private static bool IsAnnotationContext(string line, int probe)
    {
        // `@Annotation(args)` — direct marker. 直接 `@Annotation(args)` の場合。
        if (line[probe] == '@')
            return true;

        // `@module.Annotation(args)` — walk past the dotted qualifier chain first so that
        // both `@module.Annotation` and `@field:com.example.Annotation` land the probe on
        // either `@` or the Kotlin use-site target `:`.
        // `@module.Annotation(args)` や `@field:com.example.Annotation(args)` のように修飾子が
        // 付く場合も対応するため、先にドット区切り修飾子チェーンを剥がしてから `@` または
        // Kotlin の use-site target `:` を判定する。
        while (probe >= 0 && line[probe] == '.')
        {
            probe--;
            while (probe >= 0 && IsIdentifierChar(line[probe]))
                probe--;
            while (probe >= 0 && char.IsWhiteSpace(line[probe]))
                probe--;
        }

        if (probe < 0)
            return false;

        if (line[probe] == '@')
            return true;

        // Kotlin use-site target: `@field:Deprecated("msg")` or
        // `@field:com.example.Deprecated("msg")`. After unwinding the dotted qualifier, the
        // probe lands on `:`; walk past the target identifier and confirm `@`.
        // Kotlin の use-site target `@field:Deprecated("msg")` や
        // `@field:com.example.Deprecated("msg")` では、ドット修飾子を剥がしたあと probe が `:`
        // に着地するため、target 識別子を読み飛ばして `@` を確認する。
        if (line[probe] == ':')
        {
            var j = probe - 1;
            var idEnd = j;
            while (j >= 0 && IsIdentifierChar(line[j]))
                j--;
            if (j + 1 <= idEnd)
            {
                var target = line[(j + 1)..(idEnd + 1)];
                if (KotlinAnnotationTargets.Contains(target))
                {
                    var k = j;
                    while (k >= 0 && char.IsWhiteSpace(line[k]))
                        k--;
                    if (k >= 0 && line[k] == '@')
                        return true;
                }
            }
        }

        return false;
    }

    private static bool UsesHashComments(string lang) =>
        lang is "python" or "ruby" or "perl" or "php" or "elixir" or "r" or "powershell"
            or "shell" or "makefile" or "terraform" or "dockerfile" or "protobuf";

    private static bool UsesSlashComments(string lang) =>
        lang is not "python" and not "ruby" and not "r" and not "haskell"
            and not "makefile" and not "terraform" and not "dockerfile"
            and not "css" and not "fortran";

    private static bool UsesDashDashComments(string lang) =>
        lang is "lua" or "sql" or "haskell";

    private static bool IsPythonStringPrefixChar(char c) =>
        c is 'r' or 'R' or 'u' or 'U' or 'b' or 'B' or 'f' or 'F';

    private static string MaskJavaTextBlocks(string content)
    {
        var chars = content.ToCharArray();

        for (var i = 0; i + 2 < chars.Length; i++)
        {
            if (!IsJavaTextBlockOpening(chars, i))
                continue;

            // Mask the body but keep line breaks so all existing line/column logic stays valid.
            i += 3;
            while (i < chars.Length)
            {
                if (i + 2 < chars.Length
                    && chars[i] == '"'
                    && chars[i + 1] == '"'
                    && chars[i + 2] == '"'
                    && !IsEscapedByBackslashes(chars, i))
                {
                    i += 2;
                    break;
                }

                if (chars[i] != '\r' && chars[i] != '\n')
                    chars[i] = ' ';
                i++;
            }
        }

        return new string(chars);
    }

    private static bool IsJavaTextBlockOpening(IReadOnlyList<char> chars, int index)
    {
        if (index + 2 >= chars.Count)
            return false;

        if (chars[index] != '"' || chars[index + 1] != '"' || chars[index + 2] != '"')
            return false;

        if (IsEscapedByBackslashes(chars, index))
            return false;

        for (var i = index + 3; i < chars.Count; i++)
        {
            var c = chars[i];
            if (c == '\r' || c == '\n')
                return true;
            if (!char.IsWhiteSpace(c))
                return false;
        }

        return true;
    }

    private static bool IsEscapedByBackslashes(IReadOnlyList<char> chars, int index)
    {
        var backslashCount = 0;
        for (var i = index - 1; i >= 0 && chars[i] == '\\'; i--)
            backslashCount++;

        return (backslashCount & 1) == 1;
    }

}
