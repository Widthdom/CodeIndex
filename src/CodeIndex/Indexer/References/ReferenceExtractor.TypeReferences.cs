using System.Text;
using System.Text.RegularExpressions;
using Regex = CodeIndex.Indexer.BoundedRegex;
using CodeIndex.Models;

namespace CodeIndex.Indexer;

public static partial class ReferenceExtractor
{
    private static readonly char[] CSharpReferenceLinePreparationTriggerChars = ['"', '\'', '`', '/'];
    private static readonly string[] CSharpParameterRefModifiers = ["ref", "out", "in"];
    private static readonly string[] CSharpLeadingParameterModifiers = ["this", "scoped"];

    private const string CSharpImplicitImplementationReferenceKind = "implicit_implementation";
    internal sealed record CSharpStaticInterfaceMemberContract(string Name, string Kind, string? ParameterShape, string? ReturnTypeShape);
    private sealed record CSharpImplementedInterface(string Name, IReadOnlyDictionary<string, string> TypeArguments);
    internal sealed class CSharpStaticInterfaceMemberLookups
    {
        internal readonly Dictionary<string, List<CSharpStaticInterfaceMemberContract>> ContractsByType;
        internal readonly Dictionary<string, List<string>> InterfaceGenericParameters;

        internal CSharpStaticInterfaceMemberLookups(
            Dictionary<string, List<CSharpStaticInterfaceMemberContract>> contractsByType,
            Dictionary<string, List<string>> interfaceGenericParameters)
        {
            ContractsByType = contractsByType;
            InterfaceGenericParameters = interfaceGenericParameters;
        }
    }

    internal static Action<IReadOnlyList<SymbolRecord>>? CSharpStaticInterfaceMemberLookupsBuiltForTesting { get; set; }

    private static void EmitCSharpAsyncIteratorReferences(
        long fileId,
        string[] lines,
        string[] structuralLines,
        IReadOnlyList<SymbolRecord> symbols,
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen)
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
        CSharpStaticInterfaceMemberLookups? precomputedLookups,
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen)
    {
        var staticInterfaceMemberLookups = precomputedLookups
            ?? BuildCSharpStaticInterfaceMemberLookups(workspaceSymbols);
        var interfaceMembersByType = staticInterfaceMemberLookups.ContractsByType;
        if (interfaceMembersByType.Count == 0)
            return;

        var interfaceGenericParameters = staticInterfaceMemberLookups.InterfaceGenericParameters;
        var implementationLookups = BuildCSharpStaticInterfaceImplementationLookups(symbols);
        if (implementationLookups.TypeSymbols is not { Count: > 0 } typeSymbols
            || implementationLookups.StaticMembersByContainer is not { Count: > 0 } staticMembersByContainer)
        {
            return;
        }

        foreach (var typeSymbol in typeSymbols)
        {
            var implementedInterfaces = ExtractCSharpImplementedInterfaces(
                CollectCSharpRecordHeader(
                    structuralLines,
                    typeSymbol.StartLine,
                    skipCSharpPreprocessorDirectives: true).Text,
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

                    if (!AnyCSharpStaticInterfaceMemberContractMatches(interfaceMembers, implementation, implementedInterface.TypeArguments))
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

    internal static CSharpStaticInterfaceMemberLookups BuildCSharpStaticInterfaceMemberLookups(
        IReadOnlyList<SymbolRecord> workspaceSymbols)
    {
        CSharpStaticInterfaceMemberLookupsBuiltForTesting?.Invoke(workspaceSymbols);
        var contractsByType = new Dictionary<string, List<CSharpStaticInterfaceMemberContract>>(StringComparer.Ordinal);
        var interfaceGenericParameters = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var symbol in workspaceSymbols)
        {
            if (!IsCSharpStaticInterfaceMemberContract(symbol))
                continue;

            var containerName = symbol.ContainerName!;
            if (!contractsByType.TryGetValue(containerName, out var contracts))
            {
                contracts = new List<CSharpStaticInterfaceMemberContract>(1);
                contractsByType.Add(containerName, contracts);
            }

            contracts.Add(new CSharpStaticInterfaceMemberContract(
                symbol.Name,
                symbol.Kind,
                GetCSharpCallableParameterShape(symbol.Signature),
                NormalizeCSharpTypeArgumentShape(symbol.ReturnType ?? string.Empty)));
        }

        if (contractsByType.Count > 0)
        {
            // Generic declarations only matter for interfaces that own a static contract.
            // Scan after contract discovery so declaration/member ordering, partial types,
            // and duplicate-name last-write semantics remain unchanged without retaining
            // every unrelated interface signature in a large workspace.
            // generic宣言はstatic contractを持つinterfaceだけに必要。contract検出後に
            // 再走査し、宣言/member順・partial type・同名の後勝ちを維持したまま、巨大な
            // workspaceの無関係なinterface signatureを保持しない。
            foreach (var symbol in workspaceSymbols)
            {
                if (symbol.Kind != "interface"
                    || string.IsNullOrWhiteSpace(symbol.Name)
                    || string.IsNullOrWhiteSpace(symbol.Signature)
                    || !contractsByType.ContainsKey(symbol.Name))
                {
                    continue;
                }

                AddCSharpInterfaceGenericParameters(
                    interfaceGenericParameters,
                    symbol.Name,
                    symbol.Signature!);
            }
        }

        return new CSharpStaticInterfaceMemberLookups(contractsByType, interfaceGenericParameters);
    }

    private static void AddCSharpInterfaceGenericParameters(
        Dictionary<string, List<string>> lookup,
        string interfaceName,
        string signature)
    {
        var parameters = ExtractCSharpGenericArgumentList(signature, interfaceName);
        var parameterCount = 0;
        for (var index = 0; index < parameters.Count; index++)
        {
            var name = ExtractCSharpGenericParameterName(parameters[index]);
            if (string.IsNullOrWhiteSpace(name))
                continue;

            parameters[parameterCount] = name;
            parameterCount++;
        }

        if (parameterCount < parameters.Count)
            parameters.RemoveRange(parameterCount, parameters.Count - parameterCount);

        if (parameters.Count > 0)
            lookup[interfaceName] = parameters;
    }

    private static (
        IReadOnlyList<SymbolRecord>? TypeSymbols,
        IReadOnlyDictionary<string, List<SymbolRecord>>? StaticMembersByContainer) BuildCSharpStaticInterfaceImplementationLookups(IReadOnlyList<SymbolRecord> symbols)
    {
        List<SymbolRecord>? typeSymbols = null;
        Dictionary<string, List<SymbolRecord>>? staticMembersByContainer = null;
        foreach (var symbol in symbols)
        {
            if (symbol.Kind is ("class" or "struct")
                && symbol.BodyStartLine != null
                && symbol.BodyEndLine != null)
            {
                (typeSymbols ??= new List<SymbolRecord>(16)).Add(symbol);
            }

            if (symbol.Kind is not ("function" or "operator" or "property")
                || string.IsNullOrWhiteSpace(symbol.ContainerName)
                || string.IsNullOrWhiteSpace(symbol.Signature)
                || !ContainsCSharpWord(symbol.Signature!, "static"))
            {
                continue;
            }

            var containerName = symbol.ContainerName!;
            staticMembersByContainer ??= new Dictionary<string, List<SymbolRecord>>(16, StringComparer.Ordinal);
            if (!staticMembersByContainer.TryGetValue(containerName, out var staticMembers))
            {
                staticMembers = new List<SymbolRecord>(1);
                staticMembersByContainer.Add(containerName, staticMembers);
            }

            staticMembers.Add(symbol);
        }

        return (typeSymbols, staticMembersByContainer);
    }

    private static bool AnyCSharpStaticInterfaceMemberContractMatches(
        IReadOnlyList<CSharpStaticInterfaceMemberContract> interfaceMembers,
        SymbolRecord implementation,
        IReadOnlyDictionary<string, string> typeArguments)
    {
        foreach (var contract in interfaceMembers)
        {
            if (MatchesCSharpStaticInterfaceMemberContract(contract, implementation, typeArguments))
                return true;
        }

        return false;
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

        var parameterStart = paramStart;
        while (parameterStart < paramEnd && char.IsWhiteSpace(signature![parameterStart]))
            parameterStart++;
        var parameterEnd = paramEnd;
        while (parameterEnd > parameterStart && char.IsWhiteSpace(signature![parameterEnd - 1]))
            parameterEnd--;
        if (parameterEnd == parameterStart)
            return string.Empty;

        var parameterList = signature.AsSpan(parameterStart, parameterEnd - parameterStart);
        var parameterShape = new StringBuilder(parameterList.Length);
        foreach (var span in SplitTopLevelCommaSpans(parameterList))
        {
            if (parameterShape.Length > 0)
                parameterShape.Append(',');

            parameterShape.Append(NormalizeCSharpParameterTypeShape(parameterList.Slice(span.Start, span.Length).ToString()));
        }

        return parameterShape.ToString();
    }

    private static string NormalizeCSharpParameterTypeShape(string parameter)
    {
        var text = TrimCSharpParameterDefaultValue(parameter);
        var textStart = 0;
        while (textStart < text.Length && char.IsWhiteSpace(text[textStart]))
            textStart++;
        var textEnd = text.Length;
        while (textEnd > textStart && char.IsWhiteSpace(text[textEnd - 1]))
            textEnd--;
        if (textStart > 0 || textEnd < text.Length)
            text = text.Substring(textStart, textEnd - textStart);

        var refKind = string.Empty;
        foreach (var modifier in CSharpParameterRefModifiers)
        {
            if (StartsWithCSharpWord(text, modifier))
            {
                refKind = modifier + ":";
                var nextStart = modifier.Length;
                while (nextStart < text.Length && char.IsWhiteSpace(text[nextStart]))
                    nextStart++;
                text = text.Substring(nextStart);
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
        {
            var typeEnd = nameStart;
            while (typeEnd > 0 && char.IsWhiteSpace(text[typeEnd - 1]))
                typeEnd--;
            text = text.Substring(0, typeEnd);
        }

        var compactType = RemoveWhitespace(text);
        return refKind.Length == 0 ? compactType : refKind + compactType;
    }

    private static string StripLeadingCSharpParameterModifier(string text)
    {
        foreach (var modifier in CSharpLeadingParameterModifiers)
        {
            if (StartsWithCSharpWord(text, modifier))
            {
                var nextStart = modifier.Length;
                while (nextStart < text.Length && char.IsWhiteSpace(text[nextStart]))
                    nextStart++;
                return text.Substring(nextStart);
            }
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

    private static List<CSharpImplementedInterface> ExtractCSharpImplementedInterfaces(
        string headerText,
        IReadOnlyDictionary<string, List<string>> interfaceGenericParameters)
    {
        if (string.IsNullOrWhiteSpace(headerText))
            return [];

        var colonIndex = FindSignatureColonIndex(headerText);
        if (colonIndex < 0)
            return [];

        var baseList = headerText.Substring(colonIndex + 1);
        var whereMatch = CSharpWhereClauseRegex.Match(baseList);
        if (whereMatch.Success)
            baseList = baseList.Substring(0, whereMatch.Index);
        baseList = TrimTrailingTypeListTerminator(baseList);

        var interfaces = new List<CSharpImplementedInterface>(4);
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

        var arguments = ExtractCSharpGenericArgumentList(rawSegment, interfaceName);
        var count = Math.Min(parameters.Count, arguments.Count);
        for (var i = 0; i < count; i++)
            map[parameters[i]] = NormalizeCSharpTypeArgumentShape(arguments[i]);

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

        var list = text.AsSpan(genericStart + 1, genericEnd - genericStart - 1);
        var arguments = new List<string>();
        foreach (var span in SplitTopLevelCommaSpans(list))
        {
            var item = list.Slice(span.Start, span.Length).Trim().ToString();
            if (item.Length > 0)
                arguments.Add(item);
        }

        return arguments;
    }

    private static string ExtractCSharpGenericParameterName(string parameter)
    {
        var text = parameter.Trim();
        var nameStart = FindTrailingCSharpParameterNameStart(text);
        return nameStart >= 0 ? text.Substring(nameStart).Trim() : text;
    }

    private static string NormalizeCSharpTypeArgumentShape(string argument)
    {
        return RemoveWhitespace(argument);
    }

    private static string RemoveWhitespace(string value)
    {
        var firstWhitespace = -1;
        for (var i = 0; i < value.Length; i++)
        {
            if (char.IsWhiteSpace(value[i]))
            {
                firstWhitespace = i;
                break;
            }
        }

        if (firstWhitespace < 0)
            return value;

        var builder = new StringBuilder(value.Length);
        if (firstWhitespace > 0)
            builder.Append(value, 0, firstWhitespace);
        for (var i = firstWhitespace + 1; i < value.Length; i++)
        {
            var ch = value[i];
            if (!char.IsWhiteSpace(ch))
                builder.Append(ch);
        }

        return builder.ToString();
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

}
