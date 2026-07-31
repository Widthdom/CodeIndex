using System.Text;
using System.Text.RegularExpressions;
using Regex = CodeIndex.Indexer.BoundedRegex;
using CodeIndex.Models;

namespace CodeIndex.Indexer;

public static partial class ReferenceExtractor
{
    private readonly record struct CSharpLineColumn(int Line, int Column);
    private readonly record struct CSharpRecursivePatternValueNameRecord(string Name, int Offset, bool IsCasePattern, int ArrowIndex = -1);
    internal sealed record CSharpUsingAliasRecord(string AliasName, string TargetQualifiedName, int Line, int ScopeStartLine, int ScopeEndLine, bool TargetsType);
    internal sealed record CSharpUsingNamespaceRecord(string TargetQualifiedName, int Line, int ScopeStartLine, int ScopeEndLine);
    internal sealed record CSharpUsingStaticRecord(string TargetQualifiedName, int Line, int ScopeStartLine, int ScopeEndLine);
    private sealed record CSharpCastTypeShape(IReadOnlyList<string> IdentifierSegments, string? SimpleQualifiedName, bool HasTypeOnlySyntax, bool AllIdentifiersTypeLike);
    internal sealed record CSharpContainingTypeValueReceiverNames(HashSet<string> InstanceNames, HashSet<string> StaticNames);
    internal sealed record CSharpFunctionValueReceiverNameRecord(string Name, int ScopeStartLine, int ScopeStartColumn, int ScopeEndLine, int ScopeEndColumn);
    private static readonly IReadOnlySet<string> EmptyCSharpStringSet = new HashSet<string>(StringComparer.Ordinal);
    private static readonly IReadOnlyDictionary<string, CSharpContainingTypeValueReceiverNames> EmptyCSharpValueReceiverNamesByContainingType =
        new Dictionary<string, CSharpContainingTypeValueReceiverNames>(StringComparer.Ordinal);
    private static readonly IReadOnlyDictionary<int, List<CSharpFunctionValueReceiverNameRecord>> EmptyCSharpValueReceiverNamesByFunctionStartLine =
        new Dictionary<int, List<CSharpFunctionValueReceiverNameRecord>>();
    private static readonly IReadOnlyDictionary<string, List<(string EnumName, string? QualifiedEnumName, bool AllowShortNameFallback)>> EmptyCSharpQualifiedEnumMemberLookup =
        new Dictionary<string, List<(string EnumName, string? QualifiedEnumName, bool AllowShortNameFallback)>>(StringComparer.Ordinal);
    private static readonly IReadOnlyDictionary<string, List<(string ContainerName, string? QualifiedContainerName, bool AllowShortNameFallback)>> EmptyCSharpQualifiedPatternLookup =
        new Dictionary<string, List<(string ContainerName, string? QualifiedContainerName, bool AllowShortNameFallback)>>(StringComparer.Ordinal);
    internal sealed record CSharpQualifiedPatternLookups(
        IReadOnlyDictionary<string, List<(string EnumName, string? QualifiedEnumName, bool AllowShortNameFallback)>> EnumMemberLookup,
        IReadOnlyDictionary<string, List<(string ContainerName, string? QualifiedContainerName, bool AllowShortNameFallback)>> ConstantPatternMemberLookup,
        IReadOnlyDictionary<string, List<(string ContainerName, string? QualifiedContainerName, bool AllowShortNameFallback)>> TypePatternLookup);

    private static (
        IReadOnlyList<CSharpUsingAliasRecord> Aliases,
        IReadOnlyList<CSharpUsingNamespaceRecord> Namespaces,
        IReadOnlyList<CSharpUsingStaticRecord> Statics) BuildCSharpUsingImports(
        string language,
        IReadOnlyList<SymbolRecord> symbols,
        IReadOnlySet<string> csharpKnownTypeNames,
        IReadOnlyList<(int StartLine, int EndLine)> namespaceScopes,
        IReadOnlyList<string>? lines = null,
        IReadOnlyList<string>? aliasScanLines = null)
    {
        if (language != "csharp")
            return ([], [], []);

        List<CSharpUsingAliasRecord>? aliases = null;
        List<CSharpUsingNamespaceRecord>? namespaces = null;
        List<CSharpUsingStaticRecord>? statics = null;

        foreach (var symbol in symbols)
        {
            if (symbol.Kind != "import" || string.IsNullOrWhiteSpace(symbol.Signature))
                continue;

            var signature = symbol.Signature!;
            if (signature.IndexOf("using", StringComparison.Ordinal) < 0)
                continue;

            if (signature.IndexOf('=') >= 0)
            {
                var aliasMatch = CSharpUsingAliasRegex.Match(signature);
                if (aliasMatch.Success)
                    AddCSharpUsingAliasRecord(aliases ??= [], namespaceScopes, symbol.Line, aliasMatch, csharpKnownTypeNames);
            }

            if (signature.IndexOf('=') < 0
                && signature.IndexOf("static", StringComparison.Ordinal) < 0)
            {
                var namespaceMatch = CSharpUsingNamespaceRegex.Match(signature);
                if (namespaceMatch.Success)
                    AddCSharpUsingNamespaceRecord(namespaces ??= [], namespaceScopes, symbol.Line, namespaceMatch);
            }

            if (signature.IndexOf("static", StringComparison.Ordinal) >= 0)
            {
                var staticMatch = CSharpUsingStaticRegex.Match(signature);
                if (staticMatch.Success)
                    AddCSharpUsingStaticRecord(statics ??= [], namespaceScopes, symbol.Line, staticMatch);
            }
        }

        if (lines != null)
        {
            for (var i = 0; i < lines.Count; i++)
            {
                var scanLine = aliasScanLines != null && i < aliasScanLines.Count
                    ? aliasScanLines[i]
                    : lines[i];
                if (scanLine.IndexOf("using", StringComparison.Ordinal) < 0
                    || scanLine.IndexOf('=') < 0)
                {
                    continue;
                }

                var match = CSharpUsingAliasRegex.Match(scanLine);
                if (!match.Success)
                    continue;

                var lineNumber = i + 1;
                var aliasName = NormalizeCSharpIdentifier(match.Groups["alias"].Value);
                if (aliases != null && HasCSharpUsingAliasRecord(aliases, lineNumber, aliasName))
                    continue;

                AddCSharpUsingAliasRecord(aliases ??= [], namespaceScopes, lineNumber, match, csharpKnownTypeNames);
            }
        }

        aliases?.Sort(static (left, right) => left.Line.CompareTo(right.Line));
        namespaces?.Sort(static (left, right) => left.Line.CompareTo(right.Line));
        statics?.Sort(static (left, right) => left.Line.CompareTo(right.Line));
        return (aliases ?? [], namespaces ?? [], statics ?? []);
    }

    private static IReadOnlyList<(int StartLine, int EndLine)> BuildCSharpNamespaceScopes(IReadOnlyList<SymbolRecord> symbols)
    {
        List<(int StartLine, int EndLine)>? scopes = null;
        foreach (var symbol in symbols)
        {
            if (symbol.Kind != "namespace")
                continue;

            var startLine = symbol.BodyStartLine ?? symbol.StartLine;
            var endLine = symbol.BodyEndLine ?? symbol.EndLine;
            if (startLine > 0 && endLine >= startLine)
                (scopes ??= []).Add((startLine, endLine));
        }

        return scopes ?? [];
    }

    private static bool HasCSharpUsingAliasRecord(
        IReadOnlyList<CSharpUsingAliasRecord> aliases,
        int lineNumber,
        string aliasName)
    {
        foreach (var existing in aliases)
        {
            if (existing.Line == lineNumber
                && string.Equals(existing.AliasName, aliasName, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    private static void AddCSharpUsingAliasRecord(
        List<CSharpUsingAliasRecord> aliases,
        IReadOnlyList<(int StartLine, int EndLine)> namespaceScopes,
        int lineNumber,
        Match match,
        IReadOnlySet<string> csharpKnownTypeNames)
    {
        var alias = NormalizeCSharpIdentifier(match.Groups["alias"].Value);
        var target = TryNormalizeCSharpQualifiedName(match.Groups["target"].Value)
            ?? NormalizeCSharpUsingAliasRawTarget(match.Groups["target"].Value);
        if (string.IsNullOrWhiteSpace(alias) || string.IsNullOrWhiteSpace(target))
            return;

        var (scopeStartLine, scopeEndLine) = ResolveCSharpUsingScope(namespaceScopes, lineNumber);

        aliases.Add(new CSharpUsingAliasRecord(
            alias,
            target,
            lineNumber,
            scopeStartLine,
            scopeEndLine,
            IsCSharpUsingAliasTypeTarget(target, csharpKnownTypeNames)));
    }

    private static void AddCSharpUsingNamespaceRecord(
        List<CSharpUsingNamespaceRecord> imports,
        IReadOnlyList<(int StartLine, int EndLine)> namespaceScopes,
        int lineNumber,
        Match match)
    {
        var target = TryNormalizeCSharpQualifiedName(match.Groups["target"].Value)
            ?? NormalizeCSharpUsingAliasRawTarget(match.Groups["target"].Value);
        if (string.IsNullOrWhiteSpace(target))
            return;

        var (scopeStartLine, scopeEndLine) = ResolveCSharpUsingScope(namespaceScopes, lineNumber);
        imports.Add(new CSharpUsingNamespaceRecord(target, lineNumber, scopeStartLine, scopeEndLine));
    }

    private static void AddCSharpUsingStaticRecord(
        List<CSharpUsingStaticRecord> imports,
        IReadOnlyList<(int StartLine, int EndLine)> namespaceScopes,
        int lineNumber,
        Match match)
    {
        var target = TryNormalizeCSharpQualifiedName(match.Groups["target"].Value);
        if (string.IsNullOrWhiteSpace(target))
            return;

        var (scopeStartLine, scopeEndLine) = ResolveCSharpUsingScope(namespaceScopes, lineNumber);
        imports.Add(new CSharpUsingStaticRecord(target, lineNumber, scopeStartLine, scopeEndLine));
    }

    private static (int ScopeStartLine, int ScopeEndLine) ResolveCSharpUsingScope(
        IReadOnlyList<(int StartLine, int EndLine)> namespaceScopes,
        int lineNumber)
    {
        var scopeStartLine = 1;
        var scopeEndLine = int.MaxValue;
        var scopeWidth = int.MaxValue;
        foreach (var (startLine, endLine) in namespaceScopes)
        {
            if (lineNumber < startLine || lineNumber > endLine)
                continue;

            var width = endLine - startLine;
            if (width > scopeWidth)
                continue;

            scopeStartLine = startLine;
            scopeEndLine = endLine;
            scopeWidth = width;
        }

        return (scopeStartLine, scopeEndLine);
    }

    private static string NormalizeCSharpUsingAliasRawTarget(string target)
    {
        var trimmed = target.Trim();
        var genericStart = trimmed.IndexOf('<');
        if (genericStart >= 0)
            trimmed = trimmed[..genericStart].TrimEnd();
        return trimmed;
    }

    private static (IReadOnlySet<string> KnownTypeNames, IReadOnlySet<string> NonEnumTypeNames) BuildCSharpTypeNameSets(
        string language,
        IReadOnlyList<SymbolRecord> symbols)
    {
        if (language != "csharp")
            return (EmptyCSharpStringSet, EmptyCSharpStringSet);

        HashSet<string>? knownTypeNames = null;
        HashSet<string>? nonEnumTypeNames = null;

        foreach (var symbol in symbols)
        {
            if (symbol.Kind is not ("class" or "struct" or "interface" or "enum" or "delegate"))
                continue;

            var normalizedName = NormalizeCSharpIdentifier(symbol.Name);
            if (!string.IsNullOrWhiteSpace(normalizedName))
                (knownTypeNames ??= new HashSet<string>(StringComparer.Ordinal)).Add(normalizedName);

            var qualifiedContainer = !string.IsNullOrWhiteSpace(symbol.ContainerQualifiedName)
                ? symbol.ContainerQualifiedName
                : symbol.ContainerKind == "namespace" && !string.IsNullOrWhiteSpace(symbol.ContainerName)
                    ? symbol.ContainerName
                    : null;
            if (!string.IsNullOrWhiteSpace(qualifiedContainer) && !string.IsNullOrWhiteSpace(normalizedName))
                (knownTypeNames ??= new HashSet<string>(StringComparer.Ordinal)).Add(qualifiedContainer + "." + normalizedName);

            if (symbol.Kind != "enum" && !string.IsNullOrWhiteSpace(symbol.Name))
                (nonEnumTypeNames ??= new HashSet<string>(StringComparer.Ordinal)).Add(symbol.Name);
        }

        return (knownTypeNames ?? EmptyCSharpStringSet, nonEnumTypeNames ?? EmptyCSharpStringSet);
    }

    private static HashSet<string>? BuildCallableDefinitionNames(string language, IReadOnlyList<SymbolRecord> symbols)
    {
        if (language != "csharp")
            return null;

        HashSet<string>? names = null;
        foreach (var symbol in symbols)
        {
            if (symbol.Kind != "function" || string.IsNullOrWhiteSpace(symbol.Name))
                continue;

            var name = NormalizeCSharpIdentifier(symbol.Name);
            if (!string.IsNullOrWhiteSpace(name))
                (names ??= new HashSet<string>(StringComparer.Ordinal)).Add(name);
        }

        return names;
    }

    private static bool IsCSharpUsingAliasTypeTarget(string targetQualifiedName, IReadOnlySet<string> csharpKnownTypeNames)
    {
        var normalizedTarget = NormalizeCSharpAliasTargetForTypeLookup(targetQualifiedName);
        return normalizedTarget.Length > 0 && csharpKnownTypeNames.Contains(normalizedTarget);
    }

    private static string NormalizeCSharpAliasTargetForTypeLookup(string targetQualifiedName)
    {
        if (string.IsNullOrWhiteSpace(targetQualifiedName))
            return string.Empty;

        var trimmed = targetQualifiedName.Trim();
        if (trimmed.IndexOf('<') < 0
            && trimmed.IndexOf('>') < 0
            && !trimmed.EndsWith("?", StringComparison.Ordinal)
            && !trimmed.EndsWith("[]", StringComparison.Ordinal))
        {
            return trimmed;
        }

        var builder = new System.Text.StringBuilder(trimmed.Length);
        var genericDepth = 0;
        for (var i = 0; i < trimmed.Length; i++)
        {
            var ch = trimmed[i];
            if (ch == '<')
            {
                genericDepth++;
                continue;
            }

            if (ch == '>')
            {
                if (genericDepth > 0)
                    genericDepth--;
                continue;
            }

            if (genericDepth == 0)
                builder.Append(ch);
        }

        var normalized = builder.ToString().AsSpan().Trim();
        while (normalized.EndsWith("?", StringComparison.Ordinal))
            normalized = normalized[..^1].TrimEnd();
        while (normalized.EndsWith("[]", StringComparison.Ordinal))
            normalized = normalized[..^2].TrimEnd();

        return normalized.ToString();
    }

    private static (
        IReadOnlyDictionary<string, CSharpContainingTypeValueReceiverNames> ByContainingType,
        IReadOnlyDictionary<int, List<CSharpFunctionValueReceiverNameRecord>> ByFunctionStartLine) BuildCSharpValueReceiverNameLookups(
        string language,
        IReadOnlyList<SymbolRecord> symbols,
        IReadOnlyList<string> structuralLines,
        IReadOnlySet<string> csharpKnownTypeNames,
        IReadOnlyList<CSharpUsingAliasRecord> csharpUsingAliases)
    {
        if (language != "csharp")
            return (EmptyCSharpValueReceiverNamesByContainingType, EmptyCSharpValueReceiverNamesByFunctionStartLine);

        Dictionary<string, CSharpContainingTypeValueReceiverNames>? byContainingType = null;
        Dictionary<int, List<CSharpFunctionValueReceiverNameRecord>>? byFunctionStartLine = null;

        foreach (var symbol in symbols)
        {
            AddCSharpContainingTypeValueReceiverName(ref byContainingType, symbol);

            if (symbol.Kind is not ("function" or "property") || symbol.StartLine <= 0)
                continue;

            List<CSharpFunctionValueReceiverNameRecord>? names = null;
            if (symbol.BodyStartLine != null && symbol.BodyEndLine != null)
            {
                names = [];
                var seenNames = new HashSet<CSharpFunctionValueReceiverNameRecord>();
                var start = Math.Max(symbol.BodyStartLine.Value - 1, 0);
                var end = Math.Min(symbol.BodyEndLine.Value - 1, structuralLines.Count - 1);
                var blockScopes = BuildCSharpBlockScopes(structuralLines, start, end);
                var bodyText = LineRangeText.Join(structuralLines, start, end);
                if (symbol.Kind == "function")
                    AddCSharpParameterNames(names, symbol.Signature, symbol.BodyStartLine.Value, 0, symbol.BodyEndLine.Value, int.MaxValue, seenNames);
                for (var i = start; i <= end; i++)
                {
                    foreach (Match match in BoundedRegex.EnumerateMatches(CSharpLocalValueNameRegex, structuralLines[i]))
                        AddCSharpFunctionValueReceiverName(
                            names,
                            NormalizeCSharpIdentifier(match.Groups["name"].Value),
                            i + 1,
                            match.Index,
                            FindInnermostCSharpBlockEndLine(blockScopes, end + 1, i, match.Index),
                            int.MaxValue,
                            seenNames);
                    foreach (Match match in BoundedRegex.EnumerateMatches(CSharpForeachValueNameRegex, structuralLines[i]))
                    {
                        var scopeEnd = FindFollowingCSharpEmbeddedStatementEndPosition(structuralLines, end, i, match.Index);
                        AddCSharpFunctionValueReceiverName(
                            names,
                            NormalizeCSharpIdentifier(match.Groups["name"].Value),
                            i + 1,
                            match.Index,
                            scopeEnd.Line,
                            scopeEnd.Column,
                            seenNames);
                    }
                    foreach (Match match in BoundedRegex.EnumerateMatches(CSharpQueryRangeValueNameRegex, structuralLines[i]))
                    {
                        var scopeEnd = FindCSharpQueryExpressionEndPosition(
                            structuralLines,
                            end,
                            i,
                            match.Index,
                            csharpKnownTypeNames,
                            csharpUsingAliases,
                            names);
                        AddCSharpFunctionValueReceiverName(
                            names,
                            NormalizeCSharpIdentifier(match.Groups["name"].Value),
                            i + 1,
                            match.Index,
                            scopeEnd.Line,
                            scopeEnd.Column,
                            seenNames);
                    }
                    foreach (Match match in BoundedRegex.EnumerateMatches(CSharpDeclarationPatternValueNameRegex, structuralLines[i]))
                    {
                        if (!TryFindCSharpDeclarationPatternScopeEndPosition(structuralLines, start, end, i, match.Index, out var scopeEnd))
                            continue;

                        AddCSharpFunctionValueReceiverName(
                            names,
                            NormalizeCSharpIdentifier(match.Groups["name"].Value),
                            i + 1,
                            match.Index,
                            scopeEnd.Line,
                            scopeEnd.Column,
                            seenNames);
                    }
                    foreach (Match match in BoundedRegex.EnumerateMatches(CSharpCaseDeclarationPatternValueNameRegex, structuralLines[i]))
                    {
                        if (!TryFindCSharpSwitchCaseScopeEndPosition(structuralLines, end, i, match.Index, out var scopeEnd))
                            continue;

                        AddCSharpFunctionValueReceiverName(
                            names,
                            NormalizeCSharpIdentifier(match.Groups["name"].Value),
                            i + 1,
                            match.Index,
                            scopeEnd.Line,
                            scopeEnd.Column,
                            seenNames);
                    }
                    foreach (Match match in BoundedRegex.EnumerateMatches(CSharpOutValueNameRegex, structuralLines[i]))
                        AddCSharpFunctionValueReceiverName(names, NormalizeCSharpIdentifier(match.Groups["name"].Value), i + 1, match.Index, symbol.BodyEndLine.Value, int.MaxValue, seenNames);
                    foreach (Match match in BoundedRegex.EnumerateMatches(CSharpCatchValueNameRegex, structuralLines[i]))
                    {
                        var scopeEnd = FindFollowingCSharpEmbeddedStatementEndPosition(structuralLines, end, i, match.Index);
                        AddCSharpFunctionValueReceiverName(
                            names,
                            NormalizeCSharpIdentifier(match.Groups["name"].Value),
                            i + 1,
                            match.Index,
                            scopeEnd.Line,
                            scopeEnd.Column,
                            seenNames);
                    }
                    foreach (Match match in BoundedRegex.EnumerateMatches(CSharpUsingStatementValueNameRegex, structuralLines[i]))
                    {
                        var scopeEnd = FindFollowingCSharpEmbeddedStatementEndPosition(structuralLines, end, i, match.Index);
                        AddCSharpFunctionValueReceiverName(
                            names,
                            NormalizeCSharpIdentifier(match.Groups["name"].Value),
                            i + 1,
                            match.Index,
                            scopeEnd.Line,
                            scopeEnd.Column,
                            seenNames);
                    }
                    foreach (Match match in BoundedRegex.EnumerateMatches(CSharpFixedValueNameRegex, structuralLines[i]))
                    {
                        var scopeEnd = FindFollowingCSharpEmbeddedStatementEndPosition(structuralLines, end, i, match.Index);
                        AddCSharpFunctionValueReceiverName(
                            names,
                            NormalizeCSharpIdentifier(match.Groups["name"].Value),
                            i + 1,
                            match.Index,
                            scopeEnd.Line,
                            scopeEnd.Column,
                            seenNames);
                    }
                }

                AddCSharpRecursivePatternValueReceiverNames(names, bodyText, structuralLines, start, end, seenNames);
                AddCSharpLambdaParameterNames(
                    names,
                    bodyText,
                    start + 1,
                    symbol.BodyEndLine.Value,
                    seenNames);
            }

            if (names is { Count: > 0 })
            {
                byFunctionStartLine ??= new Dictionary<int, List<CSharpFunctionValueReceiverNameRecord>>();
                byFunctionStartLine[symbol.StartLine] = names;
            }
        }

        return (
            byContainingType ?? EmptyCSharpValueReceiverNamesByContainingType,
            byFunctionStartLine ?? EmptyCSharpValueReceiverNamesByFunctionStartLine);
    }

    private static void AddCSharpContainingTypeValueReceiverName(
        ref Dictionary<string, CSharpContainingTypeValueReceiverNames>? lookup,
        SymbolRecord symbol)
    {
        if (symbol.Kind is not ("field" or "property") || string.IsNullOrWhiteSpace(symbol.Name))
            return;

        var containingType = GetContainingTypeQualifiedName(symbol);
        if (string.IsNullOrWhiteSpace(containingType))
            return;

        lookup ??= new Dictionary<string, CSharpContainingTypeValueReceiverNames>(StringComparer.Ordinal);
        if (!lookup.TryGetValue(containingType!, out var names))
        {
            names = new CSharpContainingTypeValueReceiverNames(
                new HashSet<string>(StringComparer.Ordinal),
                new HashSet<string>(StringComparer.Ordinal));
            lookup[containingType!] = names;
        }

        if (IsStaticCSharpSymbol(symbol))
            names.StaticNames.Add(symbol.Name);
        else
            names.InstanceNames.Add(symbol.Name);
    }

    internal static CSharpQualifiedPatternLookups BuildCSharpQualifiedPatternLookups(
        IReadOnlyList<SymbolRecord> symbols)
    {
        var typeNameSets = BuildCSharpTypeNameSets("csharp", symbols);
        return BuildCSharpQualifiedPatternLookups(symbols, typeNameSets.NonEnumTypeNames);
    }

    private static CSharpQualifiedPatternLookups BuildCSharpQualifiedPatternLookups(
        IReadOnlyList<SymbolRecord> symbols,
        IReadOnlySet<string> conflictingNonEnumTypeNames)
    {
        Dictionary<string, List<(string EnumName, string? QualifiedEnumName, bool AllowShortNameFallback)>>? enumMemberLookup = null;
        Dictionary<string, List<(string ContainerName, string? QualifiedContainerName, bool AllowShortNameFallback)>>? constantPatternMemberLookup = null;
        Dictionary<string, List<(string ContainerName, string? QualifiedContainerName, bool AllowShortNameFallback)>>? typePatternLookup = null;

        foreach (var symbol in symbols)
        {
            if (symbol.Kind is "class" or "struct" or "interface" or "enum" or "delegate"
                && !string.IsNullOrWhiteSpace(symbol.Name)
                && !string.IsNullOrWhiteSpace(symbol.ContainerName))
            {
                AddCSharpQualifiedPatternTarget(
                    ref typePatternLookup,
                    symbol.Name,
                    symbol.ContainerName!,
                    symbol.ContainerQualifiedName,
                    allowShortNameFallback: true);
            }

            if (string.IsNullOrWhiteSpace(symbol.Name) || string.IsNullOrWhiteSpace(symbol.ContainerName))
                continue;

            if (symbol.Kind == "enum" && symbol.ContainerKind == "enum")
            {
                var allowShortNameFallback = !conflictingNonEnumTypeNames.Contains(symbol.ContainerName!);
                AddCSharpQualifiedEnumMemberTarget(
                    ref enumMemberLookup,
                    symbol.Name,
                    symbol.ContainerName!,
                    symbol.ContainerQualifiedName,
                    allowShortNameFallback);
                AddCSharpQualifiedPatternTarget(
                    ref constantPatternMemberLookup,
                    symbol.Name,
                    symbol.ContainerName!,
                    symbol.ContainerQualifiedName,
                    allowShortNameFallback);
                continue;
            }

            if (IsCSharpConstMemberSymbol(symbol))
            {
                AddCSharpQualifiedPatternTarget(
                    ref constantPatternMemberLookup,
                    symbol.Name,
                    symbol.ContainerName!,
                    symbol.ContainerQualifiedName,
                    allowShortNameFallback: true);
                AddCSharpQualifiedEnumMemberTarget(
                    ref enumMemberLookup,
                    symbol.Name,
                    symbol.ContainerName!,
                    symbol.ContainerQualifiedName,
                    allowShortNameFallback: true);
                continue;
            }

            if (symbol.Kind is "field" or "property" && IsStaticCSharpSymbol(symbol))
            {
                AddCSharpQualifiedEnumMemberTarget(
                    ref enumMemberLookup,
                    symbol.Name,
                    symbol.ContainerName!,
                    symbol.ContainerQualifiedName,
                    allowShortNameFallback: true);
            }
        }

        return new CSharpQualifiedPatternLookups(
            enumMemberLookup ?? EmptyCSharpQualifiedEnumMemberLookup,
            constantPatternMemberLookup ?? EmptyCSharpQualifiedPatternLookup,
            typePatternLookup ?? EmptyCSharpQualifiedPatternLookup);
    }

    private static void AddCSharpQualifiedEnumMemberTarget(
        ref Dictionary<string, List<(string EnumName, string? QualifiedEnumName, bool AllowShortNameFallback)>>? lookup,
        string name,
        string enumName,
        string? qualifiedEnumName,
        bool allowShortNameFallback)
    {
        lookup ??= new Dictionary<string, List<(string EnumName, string? QualifiedEnumName, bool AllowShortNameFallback)>>(StringComparer.Ordinal);
        if (!lookup.TryGetValue(name, out var targets))
        {
            targets = [];
            lookup[name] = targets;
        }

        foreach (var target in targets)
        {
            if (string.Equals(target.EnumName, enumName, StringComparison.Ordinal)
                && string.Equals(target.QualifiedEnumName, qualifiedEnumName, StringComparison.Ordinal))
            {
                return;
            }
        }

        targets.Add((enumName, qualifiedEnumName, allowShortNameFallback));
    }

    private static void AddCSharpQualifiedPatternTarget(
        ref Dictionary<string, List<(string ContainerName, string? QualifiedContainerName, bool AllowShortNameFallback)>>? lookup,
        string name,
        string containerName,
        string? qualifiedContainerName,
        bool allowShortNameFallback)
    {
        lookup ??= new Dictionary<string, List<(string ContainerName, string? QualifiedContainerName, bool AllowShortNameFallback)>>(StringComparer.Ordinal);
        if (!lookup.TryGetValue(name, out var targets))
        {
            targets = [];
            lookup[name] = targets;
        }

        foreach (var existing in targets)
        {
            if (string.Equals(existing.ContainerName, containerName, StringComparison.Ordinal)
                && string.Equals(existing.QualifiedContainerName, qualifiedContainerName, StringComparison.Ordinal))
            {
                return;
            }
        }

        targets.Add((containerName, qualifiedContainerName, allowShortNameFallback));
    }

    internal static bool IsCSharpQualifiedMemberReadTargetSymbol(SymbolRecord symbol)
        => symbol.Kind == "enum" && symbol.ContainerKind == "enum"
            || IsCSharpConstMemberSymbol(symbol)
            || symbol.ContainerKind is "class" or "struct" or "interface"
                && symbol.Kind is "field" or "property"
                && IsStaticCSharpSymbol(symbol);

    private static bool IsCSharpConstMemberSymbol(SymbolRecord symbol)
    {
        if (symbol.ContainerKind is not ("class" or "struct" or "interface"))
            return false;
        if (string.IsNullOrWhiteSpace(symbol.Signature))
            return false;

        return symbol.Signature!.Contains(" const ", StringComparison.Ordinal)
            || symbol.Signature.StartsWith("const ", StringComparison.Ordinal);
    }

}
