using System.Text;
using System.Text.RegularExpressions;
using Regex = CodeIndex.Indexer.BoundedRegex;
using CodeIndex.Models;

namespace CodeIndex.Indexer;

public static partial class ReferenceExtractor
{
    private readonly record struct CSharpLineColumn(int Line, int Column);
    private readonly record struct CSharpRecursivePatternValueNameRecord(string Name, int Offset, bool IsCasePattern, int ArrowIndex = -1);
    private sealed record CSharpNamespaceScope(string QualifiedName, int ScopeStartLine, int ScopeEndLine);
    private sealed record CSharpUsingNamespaceScope(string TargetQualifiedName, int Line, int ScopeStartLine, int ScopeEndLine);
    private sealed record CSharpContainingTypeScope(string QualifiedName, int ScopeStartLine, int ScopeEndLine);
    internal sealed record CSharpUsingAliasRecord(string AliasName, string TargetQualifiedName, int Line, int ScopeStartLine, int ScopeEndLine, bool TargetsType);
    internal sealed record CSharpUsingStaticRecord(string TargetQualifiedName, int Line, int ScopeStartLine, int ScopeEndLine);
    private sealed record CSharpCastTypeShape(IReadOnlyList<string> IdentifierSegments, string? SimpleQualifiedName, bool HasTypeOnlySyntax, bool AllIdentifiersTypeLike);
    internal sealed record CSharpContainingTypeValueReceiverNames(HashSet<string> InstanceNames, HashSet<string> StaticNames);
    internal sealed record CSharpFunctionValueReceiverNameRecord(string Name, int ScopeStartLine, int ScopeStartColumn, int ScopeEndLine, int ScopeEndColumn);

    private static List<CSharpUsingAliasRecord> BuildCSharpUsingAliases(string language, IReadOnlyList<SymbolRecord> symbols, IReadOnlySet<string> csharpKnownTypeNames)
    {
        var aliases = new List<CSharpUsingAliasRecord>();
        if (language != "csharp")
            return aliases;

        var namespaceScopes = symbols
            .Where(symbol => symbol.Kind == "namespace")
            .Select(symbol => (
                StartLine: symbol.BodyStartLine ?? symbol.StartLine,
                EndLine: symbol.BodyEndLine ?? symbol.EndLine))
            .Where(scope => scope.StartLine > 0 && scope.EndLine >= scope.StartLine)
            .ToList();

        foreach (var symbol in symbols)
        {
            if (symbol.Kind != "import" || string.IsNullOrWhiteSpace(symbol.Signature))
                continue;

            var match = CSharpUsingAliasRegex.Match(symbol.Signature!);
            if (!match.Success)
                continue;

            var alias = NormalizeCSharpIdentifier(match.Groups["alias"].Value);
            var target = TryNormalizeCSharpQualifiedName(match.Groups["target"].Value);
            if (string.IsNullOrWhiteSpace(alias) || string.IsNullOrWhiteSpace(target))
                continue;

            var scopeStartLine = 1;
            var scopeEndLine = int.MaxValue;
            var scopeWidth = int.MaxValue;
            foreach (var (startLine, endLine) in namespaceScopes)
            {
                if (symbol.Line < startLine || symbol.Line > endLine)
                    continue;

                var width = endLine - startLine;
                if (width > scopeWidth)
                    continue;

                scopeStartLine = startLine;
                scopeEndLine = endLine;
                scopeWidth = width;
            }

            aliases.Add(new CSharpUsingAliasRecord(
                alias,
                target,
                symbol.Line,
                scopeStartLine,
                scopeEndLine,
                IsCSharpUsingAliasTypeTarget(target, csharpKnownTypeNames)));
        }

        aliases.Sort(static (left, right) => left.Line.CompareTo(right.Line));
        return aliases;
    }

    private static List<CSharpUsingStaticRecord> BuildCSharpUsingStatics(string language, IReadOnlyList<SymbolRecord> symbols)
    {
        var imports = new List<CSharpUsingStaticRecord>();
        if (language != "csharp")
            return imports;

        var namespaceScopes = symbols
            .Where(symbol => symbol.Kind == "namespace")
            .Select(symbol => (
                StartLine: symbol.BodyStartLine ?? symbol.StartLine,
                EndLine: symbol.BodyEndLine ?? symbol.EndLine))
            .Where(scope => scope.StartLine > 0 && scope.EndLine >= scope.StartLine)
            .ToList();

        foreach (var symbol in symbols)
        {
            if (symbol.Kind != "import" || string.IsNullOrWhiteSpace(symbol.Signature))
                continue;

            var match = CSharpUsingStaticRegex.Match(symbol.Signature!);
            if (!match.Success)
                continue;

            var target = TryNormalizeCSharpQualifiedName(match.Groups["target"].Value);
            if (string.IsNullOrWhiteSpace(target))
                continue;

            var scopeStartLine = 1;
            var scopeEndLine = int.MaxValue;
            var scopeWidth = int.MaxValue;
            foreach (var (startLine, endLine) in namespaceScopes)
            {
                if (symbol.Line < startLine || symbol.Line > endLine)
                    continue;

                var width = endLine - startLine;
                if (width > scopeWidth)
                    continue;

                scopeStartLine = startLine;
                scopeEndLine = endLine;
                scopeWidth = width;
            }

            imports.Add(new CSharpUsingStaticRecord(target, symbol.Line, scopeStartLine, scopeEndLine));
        }

        imports.Sort(static (left, right) => left.Line.CompareTo(right.Line));
        return imports;
    }

    private static List<CSharpUsingNamespaceScope> BuildCSharpUsingNamespaceScopes(string language, IReadOnlyList<SymbolRecord> symbols)
    {
        var scopes = new List<CSharpUsingNamespaceScope>();
        if (language != "csharp")
            return scopes;

        var namespaceScopes = symbols
            .Where(symbol => symbol.Kind == "namespace")
            .Select(symbol => (
                StartLine: symbol.BodyStartLine ?? symbol.StartLine,
                EndLine: symbol.BodyEndLine ?? symbol.EndLine))
            .Where(scope => scope.StartLine > 0 && scope.EndLine >= scope.StartLine)
            .ToList();

        foreach (var symbol in symbols)
        {
            if (symbol.Kind != "import" || string.IsNullOrWhiteSpace(symbol.Signature))
                continue;

            if (!TryParseCSharpUsingNamespaceImport(symbol.Signature!, out var target, out _))
                continue;

            var scopeStartLine = 1;
            var scopeEndLine = int.MaxValue;
            var scopeWidth = int.MaxValue;
            foreach (var (startLine, endLine) in namespaceScopes)
            {
                if (symbol.Line < startLine || symbol.Line > endLine)
                    continue;

                var width = endLine - startLine;
                if (width > scopeWidth)
                    continue;

                scopeStartLine = startLine;
                scopeEndLine = endLine;
                scopeWidth = width;
            }

            scopes.Add(new CSharpUsingNamespaceScope(target!, symbol.Line, scopeStartLine, scopeEndLine));
        }

        scopes.Sort(static (left, right) => left.Line.CompareTo(right.Line));
        return scopes;
    }

    private static List<CSharpNamespaceScope> BuildCSharpNamespaceScopes(string language, IReadOnlyList<SymbolRecord> symbols, int totalLineCount)
    {
        var scopes = new List<CSharpNamespaceScope>();
        if (language != "csharp")
            return scopes;

        foreach (var symbol in symbols)
        {
            if (symbol.Kind != "namespace" || string.IsNullOrWhiteSpace(symbol.Name))
                continue;

            var startLine = symbol.BodyStartLine ?? symbol.StartLine;
            var endLine = symbol.BodyEndLine ?? symbol.EndLine;
            if (!string.IsNullOrWhiteSpace(symbol.Signature)
                && symbol.Signature.TrimEnd().EndsWith(';'))
            {
                endLine = Math.Max(endLine, totalLineCount);
            }

            if (startLine <= 0 || endLine < startLine)
                continue;

            var qualifiedName = TryNormalizeCSharpQualifiedName(symbol.Name) ?? string.Empty;
            scopes.Add(new CSharpNamespaceScope(qualifiedName, startLine, endLine));
        }

        return scopes;
    }

    private static bool TryParseCSharpUsingNamespaceImport(string signature, out string? target, out bool isGlobal)
    {
        target = null;
        isGlobal = false;
        if (string.IsNullOrWhiteSpace(signature) || signature.IndexOf('=') >= 0)
            return false;

        var match = CSharpUsingNamespaceRegex.Match(signature);
        if (!match.Success)
            return false;

        target = TryNormalizeCSharpQualifiedName(match.Groups["target"].Value);
        if (string.IsNullOrWhiteSpace(target))
            return false;

        isGlobal = signature.TrimStart().StartsWith("global using ", StringComparison.Ordinal);
        return true;
    }

    private static List<CSharpContainingTypeScope> BuildCSharpContainingTypeScopes(string language, IReadOnlyList<SymbolRecord> symbols)
    {
        var scopes = new List<CSharpContainingTypeScope>();
        if (language != "csharp")
            return scopes;

        foreach (var symbol in symbols)
        {
            if (symbol.Kind is not ("class" or "struct" or "interface")
                || string.IsNullOrWhiteSpace(symbol.Name))
            {
                continue;
            }

            var startLine = symbol.BodyStartLine ?? symbol.StartLine;
            var endLine = symbol.BodyEndLine ?? symbol.EndLine;
            if (startLine <= 0 || endLine < startLine)
                continue;

            var qualifiedName = CombineQualifiedName(symbol.ContainerQualifiedName, NormalizeCSharpIdentifier(symbol.Name));
            if (string.IsNullOrWhiteSpace(qualifiedName))
                qualifiedName = NormalizeCSharpIdentifier(symbol.Name);
            if (string.IsNullOrWhiteSpace(qualifiedName))
                continue;

            scopes.Add(new CSharpContainingTypeScope(qualifiedName!, startLine, endLine));
        }

        return scopes;
    }

    private static Dictionary<string, HashSet<string>> BuildCSharpTopLevelTypeNamespacesByName(string language, IReadOnlyList<SymbolRecord> symbols)
    {
        var lookup = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        if (language != "csharp")
            return lookup;

        foreach (var symbol in symbols)
        {
            if (symbol.Kind is not ("class" or "struct" or "interface" or "enum" or "delegate")
                || string.IsNullOrWhiteSpace(symbol.Name))
            {
                continue;
            }

            if (symbol.ContainerKind is not (null or "namespace"))
                continue;

            var name = NormalizeCSharpIdentifier(symbol.Name);
            if (string.IsNullOrWhiteSpace(name))
                continue;

            if (!lookup.TryGetValue(name, out var namespaces))
            {
                namespaces = new HashSet<string>(StringComparer.Ordinal);
                lookup[name] = namespaces;
            }

            var qualifiedNamespace = symbol.ContainerQualifiedName;
            if (string.IsNullOrWhiteSpace(qualifiedNamespace) && symbol.ContainerKind == "namespace")
                qualifiedNamespace = symbol.ContainerName;
            namespaces.Add(TryNormalizeCSharpQualifiedName(qualifiedNamespace ?? string.Empty) ?? string.Empty);
        }

        return lookup;
    }

    private static Dictionary<string, HashSet<string>> BuildCSharpNestedTypeContainersByName(string language, IReadOnlyList<SymbolRecord> symbols)
    {
        var lookup = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        if (language != "csharp")
            return lookup;

        foreach (var symbol in symbols)
        {
            if (symbol.Kind is not ("class" or "struct" or "interface" or "enum" or "delegate")
                || string.IsNullOrWhiteSpace(symbol.Name))
            {
                continue;
            }

            if (symbol.ContainerKind is not ("class" or "struct" or "interface"))
                continue;

            var name = NormalizeCSharpIdentifier(symbol.Name);
            if (string.IsNullOrWhiteSpace(name))
                continue;

            if (!lookup.TryGetValue(name, out var containingTypes))
            {
                containingTypes = new HashSet<string>(StringComparer.Ordinal);
                lookup[name] = containingTypes;
            }

            var qualifiedContainer = !string.IsNullOrWhiteSpace(symbol.ContainerQualifiedName)
                ? symbol.ContainerQualifiedName
                : NormalizeCSharpIdentifier(symbol.ContainerName ?? string.Empty);
            if (!string.IsNullOrWhiteSpace(qualifiedContainer))
                containingTypes.Add(qualifiedContainer!);
        }

        return lookup;
    }

    private static HashSet<string> BuildCSharpKnownTypeNames(string language, IReadOnlyList<SymbolRecord> symbols)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        if (language != "csharp")
            return names;

        foreach (var symbol in symbols)
        {
            if (symbol.Kind is not ("class" or "struct" or "interface" or "enum" or "delegate"))
                continue;

            var normalizedName = NormalizeCSharpIdentifier(symbol.Name);
            if (!string.IsNullOrWhiteSpace(normalizedName))
                names.Add(normalizedName);

            var qualifiedContainer = !string.IsNullOrWhiteSpace(symbol.ContainerQualifiedName)
                ? symbol.ContainerQualifiedName
                : symbol.ContainerKind == "namespace" && !string.IsNullOrWhiteSpace(symbol.ContainerName)
                    ? symbol.ContainerName
                    : null;
            if (!string.IsNullOrWhiteSpace(qualifiedContainer) && !string.IsNullOrWhiteSpace(normalizedName))
                names.Add(qualifiedContainer + "." + normalizedName);
        }

        return names;
    }

    private static HashSet<string>? BuildCallableDefinitionNames(string language, IReadOnlyList<SymbolRecord> symbols)
    {
        if (language != "csharp")
            return null;

        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var symbol in symbols)
        {
            if (symbol.Kind != "function" || string.IsNullOrWhiteSpace(symbol.Name))
                continue;

            var name = language == "csharp"
                ? NormalizeCSharpIdentifier(symbol.Name)
                : symbol.Name;
            if (!string.IsNullOrWhiteSpace(name))
                names.Add(name);
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

        var normalized = builder.ToString().Trim();
        while (normalized.EndsWith("?", StringComparison.Ordinal))
            normalized = normalized[..^1].TrimEnd();
        while (normalized.EndsWith("[]", StringComparison.Ordinal))
            normalized = normalized[..^2].TrimEnd();

        return normalized;
    }

    private static Dictionary<string, CSharpContainingTypeValueReceiverNames> BuildCSharpValueReceiverNamesByContainingType(string language, IReadOnlyList<SymbolRecord> symbols)
    {
        var lookup = new Dictionary<string, CSharpContainingTypeValueReceiverNames>(StringComparer.Ordinal);
        if (language != "csharp")
            return lookup;

        foreach (var symbol in symbols)
        {
            if (symbol.Kind != "property" || string.IsNullOrWhiteSpace(symbol.Name))
                continue;

            var containingType = GetContainingTypeQualifiedName(symbol);
            if (string.IsNullOrWhiteSpace(containingType))
                continue;

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

        return lookup;
    }

    private static Dictionary<int, List<CSharpFunctionValueReceiverNameRecord>> BuildCSharpValueReceiverNamesByFunctionStartLine(
        string language,
        IReadOnlyList<SymbolRecord> symbols,
        IReadOnlyList<string> structuralLines,
        IReadOnlySet<string> csharpKnownTypeNames,
        IReadOnlyList<CSharpUsingAliasRecord> csharpUsingAliases)
    {
        var lookup = new Dictionary<int, List<CSharpFunctionValueReceiverNameRecord>>();
        if (language != "csharp")
            return lookup;

        foreach (var symbol in symbols)
        {
            if (symbol.Kind is not ("function" or "property") || symbol.StartLine <= 0)
                continue;

            var names = new List<CSharpFunctionValueReceiverNameRecord>();
            if (symbol.BodyStartLine != null && symbol.BodyEndLine != null)
            {
                var start = Math.Max(symbol.BodyStartLine.Value - 1, 0);
                var end = Math.Min(symbol.BodyEndLine.Value - 1, structuralLines.Count - 1);
                var bodyText = string.Join("\n", structuralLines.Skip(start).Take(end - start + 1));
                if (symbol.Kind == "function")
                    AddCSharpParameterNames(names, symbol.Signature, symbol.BodyStartLine.Value, 0, symbol.BodyEndLine.Value, int.MaxValue);
                for (var i = start; i <= end; i++)
                {
                    foreach (Match match in CSharpLocalValueNameRegex.Matches(structuralLines[i]))
                        AddCSharpFunctionValueReceiverName(
                            names,
                            NormalizeCSharpIdentifier(match.Groups["name"].Value),
                            i + 1,
                            match.Index,
                            FindInnermostCSharpBlockEndLine(structuralLines, start, end, i, match.Index),
                            int.MaxValue);
                    foreach (Match match in CSharpForeachValueNameRegex.Matches(structuralLines[i]))
                    {
                        var scopeEnd = FindFollowingCSharpEmbeddedStatementEndPosition(structuralLines, end, i, match.Index);
                        AddCSharpFunctionValueReceiverName(
                            names,
                            NormalizeCSharpIdentifier(match.Groups["name"].Value),
                            i + 1,
                            match.Index,
                            scopeEnd.Line,
                            scopeEnd.Column);
                    }
                    foreach (Match match in CSharpQueryRangeValueNameRegex.Matches(structuralLines[i]))
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
                            scopeEnd.Column);
                    }
                    foreach (Match match in CSharpDeclarationPatternValueNameRegex.Matches(structuralLines[i]))
                    {
                        if (!TryFindCSharpDeclarationPatternScopeEndPosition(structuralLines, start, end, i, match.Index, out var scopeEnd))
                            continue;

                        AddCSharpFunctionValueReceiverName(
                            names,
                            NormalizeCSharpIdentifier(match.Groups["name"].Value),
                            i + 1,
                            match.Index,
                            scopeEnd.Line,
                            scopeEnd.Column);
                    }
                    foreach (Match match in CSharpCaseDeclarationPatternValueNameRegex.Matches(structuralLines[i]))
                    {
                        if (!TryFindCSharpSwitchCaseScopeEndPosition(structuralLines, end, i, match.Index, out var scopeEnd))
                            continue;

                        AddCSharpFunctionValueReceiverName(
                            names,
                            NormalizeCSharpIdentifier(match.Groups["name"].Value),
                            i + 1,
                            match.Index,
                            scopeEnd.Line,
                            scopeEnd.Column);
                    }
                    foreach (Match match in CSharpOutValueNameRegex.Matches(structuralLines[i]))
                        AddCSharpFunctionValueReceiverName(names, NormalizeCSharpIdentifier(match.Groups["name"].Value), i + 1, match.Index, symbol.BodyEndLine.Value, int.MaxValue);
                    foreach (Match match in CSharpCatchValueNameRegex.Matches(structuralLines[i]))
                    {
                        var scopeEnd = FindFollowingCSharpEmbeddedStatementEndPosition(structuralLines, end, i, match.Index);
                        AddCSharpFunctionValueReceiverName(
                            names,
                            NormalizeCSharpIdentifier(match.Groups["name"].Value),
                            i + 1,
                            match.Index,
                            scopeEnd.Line,
                            scopeEnd.Column);
                    }
                    foreach (Match match in CSharpUsingStatementValueNameRegex.Matches(structuralLines[i]))
                    {
                        var scopeEnd = FindFollowingCSharpEmbeddedStatementEndPosition(structuralLines, end, i, match.Index);
                        AddCSharpFunctionValueReceiverName(
                            names,
                            NormalizeCSharpIdentifier(match.Groups["name"].Value),
                            i + 1,
                            match.Index,
                            scopeEnd.Line,
                            scopeEnd.Column);
                    }
                    foreach (Match match in CSharpFixedValueNameRegex.Matches(structuralLines[i]))
                    {
                        var scopeEnd = FindFollowingCSharpEmbeddedStatementEndPosition(structuralLines, end, i, match.Index);
                        AddCSharpFunctionValueReceiverName(
                            names,
                            NormalizeCSharpIdentifier(match.Groups["name"].Value),
                            i + 1,
                            match.Index,
                            scopeEnd.Line,
                            scopeEnd.Column);
                    }
                }

                AddCSharpRecursivePatternValueReceiverNames(names, bodyText, structuralLines, start, end);
                AddCSharpLambdaParameterNames(
                    names,
                    bodyText,
                    start + 1,
                    symbol.BodyEndLine.Value);
            }

            if (names.Count > 0)
                lookup[symbol.StartLine] = names;
        }

        return lookup;
    }

    private static Dictionary<string, List<(string EnumName, string? QualifiedEnumName, bool AllowShortNameFallback)>> BuildCSharpQualifiedEnumMemberLookup(
        string language,
        IReadOnlyList<SymbolRecord> symbols)
    {
        var lookup = new Dictionary<string, List<(string EnumName, string? QualifiedEnumName, bool AllowShortNameFallback)>>(StringComparer.Ordinal);
        if (language != "csharp")
            return lookup;

        var conflictingNonEnumTypeNames = new HashSet<string>(
            symbols
                .Where(symbol => symbol.Kind is "class" or "struct" or "interface" or "delegate")
                .Select(symbol => symbol.Name)
                .Where(name => !string.IsNullOrWhiteSpace(name))!,
            StringComparer.Ordinal);

        foreach (var symbol in symbols)
        {
            if (symbol.Kind != "enum" || symbol.ContainerKind != "enum")
                continue;
            if (string.IsNullOrWhiteSpace(symbol.Name) || string.IsNullOrWhiteSpace(symbol.ContainerName))
                continue;

            if (!lookup.TryGetValue(symbol.Name, out var targets))
            {
                targets = [];
                lookup[symbol.Name] = targets;
            }

            bool exists = false;
            foreach (var target in targets)
            {
                if (string.Equals(target.EnumName, symbol.ContainerName, StringComparison.Ordinal)
                    && string.Equals(target.QualifiedEnumName, symbol.ContainerQualifiedName, StringComparison.Ordinal))
                {
                    exists = true;
                    break;
                }
            }

            if (!exists)
                targets.Add((
                    symbol.ContainerName!,
                    symbol.ContainerQualifiedName,
                    AllowShortNameFallback: !conflictingNonEnumTypeNames.Contains(symbol.ContainerName!)));
        }

        return lookup;
    }

    private static Dictionary<string, List<(string ContainerName, string? QualifiedContainerName, bool AllowShortNameFallback)>> BuildCSharpQualifiedConstantPatternMemberLookup(
        string language,
        IReadOnlyList<SymbolRecord> symbols)
    {
        var lookup = new Dictionary<string, List<(string ContainerName, string? QualifiedContainerName, bool AllowShortNameFallback)>>(StringComparer.Ordinal);
        if (language != "csharp")
            return lookup;

        var conflictingNonEnumTypeNames = new HashSet<string>(
            symbols
                .Where(symbol => symbol.Kind is "class" or "struct" or "interface" or "delegate")
                .Select(symbol => symbol.Name)
                .Where(name => !string.IsNullOrWhiteSpace(name))!,
            StringComparer.Ordinal);

        foreach (var symbol in symbols)
        {
            if (string.IsNullOrWhiteSpace(symbol.Name) || string.IsNullOrWhiteSpace(symbol.ContainerName))
                continue;

            var target = symbol switch
            {
                { Kind: "enum", ContainerKind: "enum" } => (
                    Included: true,
                    AllowShortNameFallback: !conflictingNonEnumTypeNames.Contains(symbol.ContainerName!)),
                _ when IsCSharpConstMemberSymbol(symbol) => (
                    Included: true,
                    AllowShortNameFallback: true),
                _ => (Included: false, AllowShortNameFallback: false)
            };

            if (!target.Included)
                continue;

            if (!lookup.TryGetValue(symbol.Name, out var targets))
            {
                targets = [];
                lookup[symbol.Name] = targets;
            }

            bool exists = false;
            foreach (var existing in targets)
            {
                if (string.Equals(existing.ContainerName, symbol.ContainerName, StringComparison.Ordinal)
                    && string.Equals(existing.QualifiedContainerName, symbol.ContainerQualifiedName, StringComparison.Ordinal))
                {
                    exists = true;
                    break;
                }
            }

            if (!exists)
                targets.Add((
                    symbol.ContainerName!,
                    symbol.ContainerQualifiedName,
                    target.AllowShortNameFallback));
        }

        return lookup;
    }

    private static Dictionary<string, List<(string ContainerName, string? QualifiedContainerName, bool AllowShortNameFallback)>> BuildCSharpQualifiedTypePatternLookup(
        string language,
        IReadOnlyList<SymbolRecord> symbols)
    {
        var lookup = new Dictionary<string, List<(string ContainerName, string? QualifiedContainerName, bool AllowShortNameFallback)>>(StringComparer.Ordinal);
        if (language != "csharp")
            return lookup;

        foreach (var symbol in symbols)
        {
            if (symbol.Kind is not ("class" or "struct" or "interface" or "enum" or "delegate")
                || string.IsNullOrWhiteSpace(symbol.Name)
                || string.IsNullOrWhiteSpace(symbol.ContainerName))
            {
                continue;
            }

            if (!lookup.TryGetValue(symbol.Name, out var targets))
            {
                targets = [];
                lookup[symbol.Name] = targets;
            }

            bool exists = false;
            foreach (var existing in targets)
            {
                if (string.Equals(existing.ContainerName, symbol.ContainerName, StringComparison.Ordinal)
                    && string.Equals(existing.QualifiedContainerName, symbol.ContainerQualifiedName, StringComparison.Ordinal))
                {
                    exists = true;
                    break;
                }
            }

            if (!exists)
                targets.Add((symbol.ContainerName!, symbol.ContainerQualifiedName, AllowShortNameFallback: true));
        }

        return lookup;
    }

    private static bool IsCSharpConstMemberSymbol(SymbolRecord symbol)
    {
        if (symbol.ContainerKind is not ("class" or "struct"))
            return false;
        if (string.IsNullOrWhiteSpace(symbol.Signature))
            return false;

        return symbol.Signature!.Contains(" const ", StringComparison.Ordinal)
            || symbol.Signature.StartsWith("const ", StringComparison.Ordinal);
    }

    internal static void EmitCSharpQualifiedEnumMemberReferences(
        string preparedLine,
        IReadOnlyDictionary<string, List<(string EnumName, string? QualifiedEnumName, bool AllowShortNameFallback)>> enumMemberLookup,
        IReadOnlyList<(int start, int end)>? csharpAttrRangesOnLine,
        IReadOnlyList<CSharpUsingAliasRecord> usingAliases,
        IReadOnlyDictionary<string, CSharpContainingTypeValueReceiverNames> valueReceiverNamesByContainingType,
        IReadOnlyDictionary<int, List<CSharpFunctionValueReceiverNameRecord>> valueReceiverNamesByFunctionStartLine,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForCall)
    {
        var scan = 0;
        while (scan < preparedLine.Length)
        {
            if (!TryReadCSharpQualifiedAccess(preparedLine, scan, out var parsed))
            {
                scan++;
                continue;
            }

            scan = Math.Max(scan + 1, parsed.NextIndex);
            if (!parsed.LastSeparatorWasDot || parsed.Segments.Count < 2)
                continue;

            var member = parsed.Segments[^1];
            var memberName = preparedLine.Substring(member.Start, member.End - member.Start);
            if (!enumMemberLookup.TryGetValue(memberName, out var targets))
                continue;

            var callContainer = resolveContainerForCall(member.Start);
            var qualifier = TrimLeadingCSharpGlobalQualifier(NormalizeCSharpQualifiedSegments(preparedLine, parsed.Segments, parsed.Segments.Count - 1));
            var resolvedQualifier = parsed.HasLeadingGlobalQualifier
                ? qualifier
                : ResolveCSharpQualifiedAliasTarget(qualifier, lineNumber, usingAliases);
            if (!parsed.HasLeadingGlobalQualifier
                && HasCSharpValueReceiverConflict(qualifier, resolvedQualifier, lineNumber, member.Start, callContainer, valueReceiverNamesByContainingType, valueReceiverNamesByFunctionStartLine))
                continue;
            if (!MatchesQualifiedConstantContainer(
                    resolvedQualifier,
                    targets,
                    allowShortNameFallback: !parsed.HasLeadingGlobalQualifier,
                    allowSingleSegmentQualifiedMatch: parsed.HasLeadingGlobalQualifier))
                continue;

            if (IsCSharpQualifiedConstantPatternReferenceSite(preparedLine, parsed))
                continue;

            var nextTokenIndex = SkipWhitespace(preparedLine, member.End);
            if (nextTokenIndex < preparedLine.Length && preparedLine[nextTokenIndex] == '(')
                continue;

            var insideCSharpAttributeRange = csharpAttrRangesOnLine != null
                && IsInsideCSharpAttributeRange(csharpAttrRangesOnLine, member.Start);
            var referenceKind = TryClassifyMetadataReference("csharp", preparedLine, member.Start, insideCSharpAttributeRange) ?? "call";

            AddReference(
                references,
                seen,
                fileId,
                memberName,
                member.Start,
                referenceKind,
                context,
                lineNumber,
                callContainer);
        }
    }

    private static bool IsCSharpQualifiedConstantPatternReferenceSite(
        string preparedLine,
        (List<(int Start, int End)> Segments, int NextIndex, bool LastSeparatorWasDot, bool HasLeadingGlobalQualifier) parsed)
    {
        if (!parsed.LastSeparatorWasDot || parsed.Segments.Count < 2)
            return false;

        var headCursor = parsed.Segments[0].Start;
        if (parsed.HasLeadingGlobalQualifier
            && headCursor >= "global::".Length
            && preparedLine.AsSpan(headCursor - "global::".Length, "global::".Length).Equals("global::", StringComparison.Ordinal))
        {
            headCursor -= "global::".Length;
        }

        return IsCSharpConstantPatternAnchor(preparedLine, ref headCursor);
    }

    private static bool IsCSharpConstantPatternAnchor(string text, ref int cursor)
    {
        cursor = SkipCSharpTriviaBackward(text, cursor);
        if (TryConsumeTrailingCSharpToken(text, ref cursor, "not"))
            cursor = SkipCSharpTriviaBackward(text, cursor);

        while (true)
        {
            if (TryConsumeTrailingCSharpToken(text, ref cursor, "case"))
                return true;

            if (TryConsumeTrailingCSharpToken(text, ref cursor, "is"))
                return false;

            if (!TryConsumeTrailingCSharpToken(text, ref cursor, "or")
                && !TryConsumeTrailingCSharpToken(text, ref cursor, "and"))
            {
                return false;
            }

            cursor = SkipCSharpTriviaBackward(text, cursor);
            if (!SkipCSharpPatternHeadBackward(text, ref cursor))
                return false;
            cursor = SkipCSharpTriviaBackward(text, cursor);
            if (TryConsumeTrailingCSharpToken(text, ref cursor, "not"))
                cursor = SkipCSharpTriviaBackward(text, cursor);
        }
    }

    private static int SkipCSharpTriviaBackward(string text, int cursor)
    {
        while (cursor > 0)
        {
            if (char.IsWhiteSpace(text[cursor - 1]))
            {
                cursor--;
                continue;
            }

            if (cursor >= 2
                && text[cursor - 1] == '/'
                && text[cursor - 2] == '*')
            {
                var commentStart = text.LastIndexOf("/*", cursor - 2, StringComparison.Ordinal);
                if (commentStart >= 0)
                {
                    cursor = commentStart;
                    continue;
                }
            }

            break;
        }

        return cursor;
    }

    internal static bool IsCSharpPatternHeadCallSite(string[] preparedLines, int lineIndex, string preparedLine, int nameIndex)
    {
        var whenOffset = FindTopLevelCSharpWhenKeywordOffset(preparedLine);
        if (whenOffset >= 0 && nameIndex > whenOffset)
            return false;

        var cursor = nameIndex;
        if (IsCSharpConstantPatternAnchor(preparedLine, ref cursor))
            return true;

        cursor = nameIndex;
        cursor = SkipCSharpTriviaBackward(preparedLine, cursor);
        if (TryConsumeTrailingCSharpToken(preparedLine, ref cursor, "not"))
            cursor = SkipCSharpTriviaBackward(preparedLine, cursor);

        if (TryConsumeTrailingCSharpToken(preparedLine, ref cursor, "is"))
            return true;

        for (var previous = lineIndex - 1; previous >= 0; previous--)
        {
            var previousLine = preparedLines[previous];
            if (string.IsNullOrWhiteSpace(previousLine))
                continue;

            if (LineEndsWithCSharpToken(previousLine, "case")
                || LineEndsWithCSharpToken(previousLine, "is")
                || LineEndsWithCSharpToken(previousLine, "not"))
            {
                return true;
            }

            break;
        }

        // Switch-expression arms (`Point(...) => ...`) do not have a `case` / `is` anchor,
        // so the same positional pattern suppression has to look for the trailing arrow.
        if (IsCSharpSwitchExpressionPatternHead(preparedLines, lineIndex, preparedLine, nameIndex))
            return true;

        return false;
    }

    private static bool IsCSharpSwitchExpressionPatternHead(string[] preparedLines, int lineIndex, string preparedLine, int nameIndex)
    {
        var cursor = nameIndex;
        while (cursor < preparedLine.Length && IsCSharpIdentifierPart(preparedLine[cursor]))
            cursor++;

        cursor = SkipCSharpTriviaForward(preparedLine, cursor);

        var openParenIndex = preparedLine.IndexOf('(', cursor);
        if (openParenIndex < 0)
            return false;

        var parenDepth = 0;
        for (var i = openParenIndex; i < preparedLine.Length; i++)
        {
            switch (preparedLine[i])
            {
                case '(':
                    parenDepth++;
                    break;
                case ')':
                    parenDepth--;
                    if (parenDepth == 0)
                    {
                        var afterClose = SkipCSharpTriviaForward(preparedLine, i + 1);
                        if (afterClose + 1 < preparedLine.Length
                            && preparedLine[afterClose] == '='
                            && preparedLine[afterClose + 1] == '>')
                        {
                            return true;
                        }

                        for (var next = lineIndex + 1; next < preparedLines.Length; next++)
                        {
                            var nextLine = preparedLines[next];
                            if (string.IsNullOrWhiteSpace(nextLine))
                                continue;

                            var nextCursor = SkipCSharpTriviaForward(nextLine, 0);
                            return nextCursor + 1 < nextLine.Length
                                && nextLine[nextCursor] == '='
                                && nextLine[nextCursor + 1] == '>';
                        }

                        return false;
                    }
                    break;
            }
        }

        return false;
    }

    private static bool LineEndsWithCSharpToken(string text, string token)
    {
        var cursor = text.Length;
        return TryConsumeTrailingCSharpToken(text, ref cursor, token);
    }

    private static bool TryConsumeTrailingCSharpToken(string text, ref int cursor, string token)
    {
        if (string.IsNullOrEmpty(token))
            return false;

        cursor = SkipCSharpTriviaBackward(text, cursor);
        if (cursor < token.Length)
            return false;

        var tokenStart = cursor - token.Length;
        if (!text.AsSpan(tokenStart, token.Length).Equals(token, StringComparison.Ordinal))
            return false;

        if ((tokenStart > 0 && IsCSharpIdentifierPart(text[tokenStart - 1]))
            || (cursor < text.Length && IsCSharpIdentifierPart(text[cursor])))
        {
            return false;
        }

        cursor = tokenStart;
        return true;
    }

    private static bool SkipCSharpPatternHeadBackward(string text, ref int cursor)
    {
        if (!TryConsumeTrailingCSharpIdentifier(text, ref cursor))
            return false;

        while (true)
        {
            cursor = SkipCSharpTriviaBackward(text, cursor);
            if (cursor >= 2
                && text[cursor - 2] == ':'
                && text[cursor - 1] == ':')
            {
                cursor -= 2;
            }
            else if (cursor > 0 && text[cursor - 1] == '.')
            {
                cursor--;
            }
            else
            {
                break;
            }

            cursor = SkipCSharpTriviaBackward(text, cursor);
            if (!TryConsumeTrailingCSharpIdentifier(text, ref cursor))
                return false;
        }

        return true;
    }

    private static bool TryConsumeTrailingCSharpIdentifier(string text, ref int cursor)
    {
        var end = cursor;
        while (cursor > 0 && IsCSharpIdentifierPart(text[cursor - 1]))
            cursor--;

        if (cursor == end)
            return false;

        if (cursor > 0 && text[cursor - 1] == '@')
            cursor--;

        return true;
    }

    private static bool TryReadCSharpQualifiedAccess(
        string preparedLine,
        int start,
        out (List<(int Start, int End)> Segments, int NextIndex, bool LastSeparatorWasDot, bool HasLeadingGlobalQualifier) parsed)
    {
        parsed = (new List<(int Start, int End)>(), start, false, false);

        if (start > 0 && IsCSharpIdentifierPart(preparedLine[start - 1]))
            return false;
        if (start >= preparedLine.Length || !IsCSharpIdentifierStart(preparedLine[start]))
            return false;

        var segments = new List<(int Start, int End)>();
        var cursor = start;
        var lastSeparatorWasDot = false;
        var hasLeadingGlobalQualifier = false;
        while (true)
        {
            if (!TryConsumeCSharpIdentifier(preparedLine, ref cursor, out var segmentStart, out var segmentEnd))
                return false;

            segments.Add((segmentStart, segmentEnd));

            var separatorStart = SkipWhitespace(preparedLine, cursor);
            if (separatorStart + 1 < preparedLine.Length
                && preparedLine[separatorStart] == ':'
                && preparedLine[separatorStart + 1] == ':')
            {
                if (segments.Count == 1
                    && segmentEnd - segmentStart == "global".Length
                    && string.CompareOrdinal(preparedLine, segmentStart, "global", 0, "global".Length) == 0)
                {
                    hasLeadingGlobalQualifier = true;
                }

                cursor = SkipWhitespace(preparedLine, separatorStart + 2);
                lastSeparatorWasDot = false;
                continue;
            }

            if (separatorStart < preparedLine.Length && preparedLine[separatorStart] == '.')
            {
                cursor = SkipWhitespace(preparedLine, separatorStart + 1);
                lastSeparatorWasDot = true;
                continue;
            }

            parsed = (segments, cursor, lastSeparatorWasDot, hasLeadingGlobalQualifier);
            return true;
        }
    }

    private static bool TryConsumeCSharpIdentifier(
        string preparedLine,
        ref int cursor,
        out int start,
        out int end)
    {
        start = cursor;
        if (cursor >= preparedLine.Length || !IsCSharpIdentifierStart(preparedLine[cursor]))
        {
            end = cursor;
            return false;
        }

        cursor++;
        while (cursor < preparedLine.Length && IsCSharpIdentifierPart(preparedLine[cursor]))
            cursor++;

        end = cursor;
        return true;
    }

    private static bool TryConsumeCSharpPatternKeyword(string preparedLine, ref int cursor, string keyword)
    {
        if (!preparedLine.AsSpan(cursor).StartsWith(keyword, StringComparison.Ordinal))
            return false;

        int afterKeyword = cursor + keyword.Length;
        if (afterKeyword < preparedLine.Length && !char.IsWhiteSpace(preparedLine[afterKeyword]))
            return false;

        cursor = afterKeyword;
        return true;
    }

    private static bool IsCSharpCaseTypePatternContinuation(
        string preparedLine,
        string typeExpression,
        int cursor,
        IReadOnlyDictionary<string, List<(string ContainerName, string? QualifiedContainerName, bool AllowShortNameFallback)>> csharpQualifiedConstantPatternMemberLookup,
        IReadOnlyDictionary<string, List<(string ContainerName, string? QualifiedContainerName, bool AllowShortNameFallback)>> csharpQualifiedTypePatternLookup,
        IReadOnlyList<CSharpUsingAliasRecord> csharpUsingAliases,
        IReadOnlyList<CSharpUsingStaticRecord> csharpUsingStatics,
        Func<string, int, bool> hasActiveSameFileCSharpTypeCandidate,
        int lineNumber)
    {
        if (IsCSharpNonTypePatternExpression(typeExpression))
            return false;

        if (cursor >= preparedLine.Length)
            return false;

        return preparedLine[cursor] switch
        {
            ':' => !IsCSharpConstantPatternMemberHead(
                    typeExpression,
                    lineNumber,
                    csharpQualifiedConstantPatternMemberLookup,
                    csharpUsingAliases,
                    csharpUsingStatics,
                    hasActiveSameFileCSharpTypeCandidate),
            '{' or '(' or '[' => true,
            _ => IsCSharpCaseTypePatternIdentifier(
                preparedLine,
                typeExpression,
                cursor,
                csharpQualifiedConstantPatternMemberLookup,
                csharpQualifiedTypePatternLookup,
                csharpUsingAliases,
                csharpUsingStatics,
                hasActiveSameFileCSharpTypeCandidate,
                lineNumber)
        };
    }

    private static bool IsCSharpCaseTypePatternIdentifier(
        string preparedLine,
        string typeExpression,
        int cursor,
        IReadOnlyDictionary<string, List<(string ContainerName, string? QualifiedContainerName, bool AllowShortNameFallback)>> csharpQualifiedConstantPatternMemberLookup,
        IReadOnlyDictionary<string, List<(string ContainerName, string? QualifiedContainerName, bool AllowShortNameFallback)>> csharpQualifiedTypePatternLookup,
        IReadOnlyList<CSharpUsingAliasRecord> csharpUsingAliases,
        IReadOnlyList<CSharpUsingStaticRecord> csharpUsingStatics,
        Func<string, int, bool> hasActiveSameFileCSharpTypeCandidate,
        int lineNumber)
    {
        int tokenCursor = cursor;
        if (!TryConsumeCSharpIdentifier(preparedLine, ref tokenCursor, out var start, out var end))
            return false;

        var rawToken = preparedLine[start..end];
        if (rawToken.Length > 0 && rawToken[0] == '@')
            return true;

        return rawToken switch
        {
            "when" => !IsCSharpConstantPatternMemberHead(
                    typeExpression,
                    lineNumber,
                    csharpQualifiedConstantPatternMemberLookup,
                    csharpUsingAliases,
                    csharpUsingStatics,
                    hasActiveSameFileCSharpTypeCandidate),
            "or" or "and" => !IsCSharpLogicalConstantPatternHead(
                preparedLine,
                typeExpression,
                tokenCursor,
                lineNumber,
                csharpQualifiedConstantPatternMemberLookup,
                csharpQualifiedTypePatternLookup,
                csharpUsingAliases,
                csharpUsingStatics,
                hasActiveSameFileCSharpTypeCandidate),
            _ => true,
        };
    }

    private static bool TryEmitCSharpLogicalTypePatternHeads(
        string preparedLine,
        string initialTypeExpression,
        int initialTypeIndex,
        int continuationIndex,
        int lineNumber,
        IReadOnlyDictionary<string, List<(string ContainerName, string? QualifiedContainerName, bool AllowShortNameFallback)>> csharpQualifiedConstantPatternMemberLookup,
        IReadOnlyDictionary<string, List<(string ContainerName, string? QualifiedContainerName, bool AllowShortNameFallback)>> csharpQualifiedTypePatternLookup,
        IReadOnlyList<CSharpUsingAliasRecord> csharpUsingAliases,
        IReadOnlyList<CSharpUsingStaticRecord> csharpUsingStatics,
        Func<string, int, bool> hasActiveSameFileCSharpTypeCandidate,
        Action<string, int> emitTypeExpression)
    {
        var currentTypeExpression = initialTypeExpression;
        var currentTypeIndex = initialTypeIndex;
        var currentContinuationIndex = continuationIndex;
        var sawLogicalKeyword = false;
        var emittedAny = false;
        while (TryConsumeCSharpLogicalPatternKeyword(preparedLine, currentContinuationIndex, out var nextHeadCursor))
        {
            sawLogicalKeyword = true;
            if (!IsCSharpLogicalConstantPatternHead(
                    preparedLine,
                    currentTypeExpression,
                    nextHeadCursor,
                    lineNumber,
                    csharpQualifiedConstantPatternMemberLookup,
                    csharpQualifiedTypePatternLookup,
                    csharpUsingAliases,
                    csharpUsingStatics,
                    hasActiveSameFileCSharpTypeCandidate))
            {
                emitTypeExpression(currentTypeExpression, currentTypeIndex);
                emittedAny = true;
            }

            int nextTypeCursor = nextHeadCursor;
            if (TryConsumeCSharpPatternKeyword(preparedLine, ref nextTypeCursor, "not"))
                nextTypeCursor = SkipWhitespace(preparedLine, nextTypeCursor);

            var nextMatch = CSharpTypeExpressionAtCursorRegex.Match(preparedLine, nextTypeCursor);
            if (!nextMatch.Success)
                return false;

            var nextTypeGroup = nextMatch.Groups["type"];
            currentTypeExpression = nextTypeGroup.Value;
            currentTypeIndex = nextTypeGroup.Index;
            currentContinuationIndex = SkipWhitespace(preparedLine, nextTypeGroup.Index + nextTypeGroup.Length);
        }

        if (sawLogicalKeyword
            && !IsCSharpNonTypePatternExpression(currentTypeExpression)
            && !IsCSharpConstantPatternMemberHead(
                currentTypeExpression,
                lineNumber,
                csharpQualifiedConstantPatternMemberLookup,
                csharpUsingAliases,
                csharpUsingStatics,
                hasActiveSameFileCSharpTypeCandidate))
        {
            emitTypeExpression(currentTypeExpression, currentTypeIndex);
            emittedAny = true;
        }

        return emittedAny;
    }

    private static bool IsCSharpLogicalConstantPatternAtCursor(
        string preparedLine,
        string typeExpression,
        int cursor,
        int lineNumber,
        IReadOnlyDictionary<string, List<(string ContainerName, string? QualifiedContainerName, bool AllowShortNameFallback)>> csharpQualifiedConstantPatternMemberLookup,
        IReadOnlyDictionary<string, List<(string ContainerName, string? QualifiedContainerName, bool AllowShortNameFallback)>> csharpQualifiedTypePatternLookup,
        IReadOnlyList<CSharpUsingAliasRecord> csharpUsingAliases,
        IReadOnlyList<CSharpUsingStaticRecord> csharpUsingStatics,
        Func<string, int, bool> hasActiveSameFileCSharpTypeCandidate)
    {
        int tokenCursor = cursor;
        if (!TryConsumeCSharpIdentifier(preparedLine, ref tokenCursor, out var start, out var end))
            return false;

        var rawToken = preparedLine[start..end];
        if (rawToken is not ("or" or "and"))
            return false;

        return IsCSharpLogicalConstantPatternHead(
            preparedLine,
            typeExpression,
            tokenCursor,
            lineNumber,
            csharpQualifiedConstantPatternMemberLookup,
            csharpQualifiedTypePatternLookup,
            csharpUsingAliases,
            csharpUsingStatics,
            hasActiveSameFileCSharpTypeCandidate);
    }

    private static bool TryConsumeCSharpLogicalPatternKeyword(
        string preparedLine,
        int cursor,
        out int nextHeadCursor)
    {
        nextHeadCursor = cursor;
        int tokenCursor = cursor;
        if (!TryConsumeCSharpIdentifier(preparedLine, ref tokenCursor, out var start, out var end))
            return false;

        var rawToken = preparedLine[start..end];
        if (rawToken is not ("or" or "and"))
            return false;

        nextHeadCursor = SkipWhitespace(preparedLine, tokenCursor);
        return true;
    }

    private static bool IsCSharpLogicalConstantPatternHead(
        string preparedLine,
        string typeExpression,
        int cursor,
        int lineNumber,
        IReadOnlyDictionary<string, List<(string ContainerName, string? QualifiedContainerName, bool AllowShortNameFallback)>> csharpQualifiedConstantPatternMemberLookup,
        IReadOnlyDictionary<string, List<(string ContainerName, string? QualifiedContainerName, bool AllowShortNameFallback)>> csharpQualifiedTypePatternLookup,
        IReadOnlyList<CSharpUsingAliasRecord> csharpUsingAliases,
        IReadOnlyList<CSharpUsingStaticRecord> csharpUsingStatics,
        Func<string, int, bool> hasActiveSameFileCSharpTypeCandidate)
    {
        if (IsCSharpConstantPatternMemberHead(
                typeExpression,
                lineNumber,
                csharpQualifiedConstantPatternMemberLookup,
                csharpUsingAliases,
                csharpUsingStatics,
                hasActiveSameFileCSharpTypeCandidate))
        {
            return true;
        }

        if (IsCSharpQualifiedTypePatternHead(
                typeExpression,
                lineNumber,
                csharpQualifiedTypePatternLookup,
                csharpUsingAliases))
        {
            return false;
        }

        if (!TryReadCSharpQualifiedAccess(typeExpression, 0, out var currentParsed)
            || !currentParsed.LastSeparatorWasDot
            || currentParsed.Segments.Count < 2)
        {
            return false;
        }

        var currentQualifier = ResolveCSharpQualifiedConstantPatternQualifier(typeExpression, currentParsed, lineNumber, csharpUsingAliases);
        if (string.IsNullOrWhiteSpace(currentQualifier))
            return false;

        int nextCursor = SkipWhitespace(preparedLine, cursor);
        if (TryConsumeCSharpPatternKeyword(preparedLine, ref nextCursor, "not"))
            nextCursor = SkipWhitespace(preparedLine, nextCursor);

        var nextMatch = CSharpTypeExpressionAtCursorRegex.Match(preparedLine, nextCursor);
        if (!nextMatch.Success)
            return false;

        var nextTypeExpression = nextMatch.Groups["type"].Value;
        if (IsCSharpQualifiedTypePatternHead(
                nextTypeExpression,
                lineNumber,
                csharpQualifiedTypePatternLookup,
                csharpUsingAliases))
        {
            return false;
        }

        if (!TryReadCSharpQualifiedAccess(nextTypeExpression, 0, out var nextParsed)
            || !nextParsed.LastSeparatorWasDot
            || nextParsed.Segments.Count < 2)
        {
            return false;
        }

        var nextQualifier = ResolveCSharpQualifiedConstantPatternQualifier(nextTypeExpression, nextParsed, lineNumber, csharpUsingAliases);
        return string.Equals(currentQualifier, nextQualifier, StringComparison.Ordinal);
    }

    private static bool IsCSharpQualifiedTypePatternHead(
        string typeExpression,
        int lineNumber,
        IReadOnlyDictionary<string, List<(string ContainerName, string? QualifiedContainerName, bool AllowShortNameFallback)>> csharpQualifiedTypePatternLookup,
        IReadOnlyList<CSharpUsingAliasRecord> csharpUsingAliases)
    {
        if (!TryReadCSharpQualifiedAccess(typeExpression, 0, out var parsed)
            || !parsed.LastSeparatorWasDot
            || parsed.Segments.Count < 2)
        {
            return false;
        }

        var member = parsed.Segments[^1];
        var memberName = typeExpression.Substring(member.Start, member.End - member.Start);
        if (!csharpQualifiedTypePatternLookup.TryGetValue(memberName, out var targets))
            return false;

        var resolvedQualifier = ResolveCSharpQualifiedConstantPatternQualifier(typeExpression, parsed, lineNumber, csharpUsingAliases);
        bool qualifierHasMultipleSegments = resolvedQualifier.Contains('.') || resolvedQualifier.Contains("::", StringComparison.Ordinal);
        return MatchesQualifiedConstantContainer(
            resolvedQualifier,
            targets,
            allowShortNameFallback: !parsed.HasLeadingGlobalQualifier && !qualifierHasMultipleSegments,
            allowSingleSegmentQualifiedMatch: parsed.HasLeadingGlobalQualifier);
    }

    private static string ResolveCSharpQualifiedConstantPatternQualifier(
        string typeExpression,
        (List<(int Start, int End)> Segments, int NextIndex, bool LastSeparatorWasDot, bool HasLeadingGlobalQualifier) parsed,
        int lineNumber,
        IReadOnlyList<CSharpUsingAliasRecord> csharpUsingAliases)
    {
        var qualifier = TrimLeadingCSharpGlobalQualifier(NormalizeCSharpQualifiedSegments(typeExpression, parsed.Segments, parsed.Segments.Count - 1));
        return parsed.HasLeadingGlobalQualifier
            ? qualifier
            : ResolveCSharpQualifiedAliasTarget(qualifier, lineNumber, csharpUsingAliases);
    }

    private static bool IsCSharpQualifiedConstantPatternMemberHead(
        string typeExpression,
        int lineNumber,
        IReadOnlyDictionary<string, List<(string ContainerName, string? QualifiedContainerName, bool AllowShortNameFallback)>> csharpQualifiedConstantPatternMemberLookup,
        IReadOnlyList<CSharpUsingAliasRecord> csharpUsingAliases)
    {
        if (!TryReadCSharpQualifiedAccess(typeExpression, 0, out var parsed)
            || !parsed.LastSeparatorWasDot
            || parsed.Segments.Count < 2)
        {
            return false;
        }

        var member = parsed.Segments[^1];
        var memberName = typeExpression.Substring(member.Start, member.End - member.Start);
        if (!csharpQualifiedConstantPatternMemberLookup.TryGetValue(memberName, out var targets))
            return false;

        var resolvedQualifier = ResolveCSharpQualifiedConstantPatternQualifier(typeExpression, parsed, lineNumber, csharpUsingAliases);
        bool qualifierHasMultipleSegments = resolvedQualifier.Contains('.') || resolvedQualifier.Contains("::", StringComparison.Ordinal);
        return MatchesQualifiedConstantContainer(
            resolvedQualifier,
            targets,
            allowShortNameFallback: !parsed.HasLeadingGlobalQualifier && !qualifierHasMultipleSegments,
            allowSingleSegmentQualifiedMatch: parsed.HasLeadingGlobalQualifier);
    }

    private static bool IsCSharpConstantPatternMemberHead(
        string typeExpression,
        int lineNumber,
        IReadOnlyDictionary<string, List<(string ContainerName, string? QualifiedContainerName, bool AllowShortNameFallback)>> csharpQualifiedConstantPatternMemberLookup,
        IReadOnlyList<CSharpUsingAliasRecord> csharpUsingAliases,
        IReadOnlyList<CSharpUsingStaticRecord> csharpUsingStatics,
        Func<string, int, bool> hasActiveSameFileCSharpTypeCandidate)
    {
        return IsCSharpQualifiedConstantPatternMemberHead(
            typeExpression,
            lineNumber,
            csharpQualifiedConstantPatternMemberLookup,
            csharpUsingAliases);
    }

    private static bool IsCSharpNonTypePatternExpression(string typeExpression)
    {
        var trimmed = typeExpression.Trim();
        if (trimmed.Length == 0)
            return false;

        if (trimmed[0] == '@')
            return false;

        return trimmed.IndexOf('.') < 0
            && trimmed.IndexOf(':') < 0
            && trimmed.IndexOf('<') < 0
            && trimmed.IndexOf('[') < 0
            && trimmed.IndexOf('?') < 0
            && trimmed.IndexOf(' ') < 0
            && CSharpNonTypePatternTokens.Contains(trimmed);
    }

    private static int SkipWhitespace(string text, int index)
    {
        while (index < text.Length && char.IsWhiteSpace(text[index]))
            index++;
        return index;
    }

    private static string NormalizeCSharpIdentifier(string identifier) =>
        !string.IsNullOrEmpty(identifier) && identifier[0] == '@'
            ? identifier[1..]
            : identifier;

    private static string NormalizeAtPrefixedIdentifier(string identifier) =>
        !string.IsNullOrEmpty(identifier) && identifier[0] == '@'
            ? identifier[1..]
            : identifier;

    private static string NormalizeCSharpQualifiedSegments(
        string preparedLine,
        IReadOnlyList<(int Start, int End)> segments,
        int count)
    {
        var builder = new StringBuilder();
        for (var i = 0; i < count; i++)
        {
            if (i > 0)
                builder.Append('.');
            var (start, end) = segments[i];
            var segment = preparedLine.Substring(start, end - start);
            builder.Append(segment[0] == '@' ? segment[1..] : segment);
        }
        return builder.ToString();
    }

    private static string TrimLeadingCSharpGlobalQualifier(string qualifiedName) =>
        qualifiedName.StartsWith("global.", StringComparison.Ordinal)
            ? qualifiedName["global.".Length..]
            : qualifiedName;

    private static string? TryNormalizeCSharpQualifiedName(string candidate)
    {
        var trimmed = candidate.Trim();
        if (trimmed.StartsWith("global::", StringComparison.Ordinal))
            trimmed = trimmed["global::".Length..];
        if (string.IsNullOrWhiteSpace(trimmed))
            return null;
        if (!TryReadCSharpQualifiedAccess(trimmed, 0, out var parsed))
            return null;
        if (SkipWhitespace(trimmed, parsed.NextIndex) != trimmed.Length)
            return null;
        return NormalizeCSharpQualifiedSegments(trimmed, parsed.Segments, parsed.Segments.Count);
    }

    private static string ResolveCSharpQualifiedAliasTarget(string qualifier, int lineNumber, IReadOnlyList<CSharpUsingAliasRecord> usingAliases)
    {
        if (string.IsNullOrWhiteSpace(qualifier) || usingAliases.Count == 0)
            return qualifier;

        var firstSegment = GetFirstQualifiedSegment(qualifier);
        string? aliasTarget = null;
        for (var i = usingAliases.Count - 1; i >= 0; i--)
        {
            var alias = usingAliases[i];
            if (alias.Line > lineNumber)
                continue;
            if (lineNumber < alias.ScopeStartLine || lineNumber > alias.ScopeEndLine)
                continue;
            if (!string.Equals(alias.AliasName, firstSegment, StringComparison.Ordinal))
                continue;

            aliasTarget = alias.TargetQualifiedName;
            break;
        }

        if (aliasTarget == null)
            return qualifier;

        return qualifier.Length == firstSegment.Length
            ? aliasTarget
            : aliasTarget + qualifier[firstSegment.Length..];
    }

    private static bool TryGetCSharpXmlDocCommentSpan(
        string line,
        bool inDelimitedDocComment,
        bool inOrdinaryBlockComment,
        out int commentStartIndex,
        out int commentEndExclusive,
        out bool nextDelimitedDocComment)
    {
        commentStartIndex = 0;
        commentEndExclusive = 0;
        nextDelimitedDocComment = inDelimitedDocComment;
        if (string.IsNullOrWhiteSpace(line))
        {
            commentEndExclusive = inDelimitedDocComment ? line.Length : 0;
            return inDelimitedDocComment;
        }

        var firstNonWhitespaceIndex = 0;
        while (firstNonWhitespaceIndex < line.Length && char.IsWhiteSpace(line[firstNonWhitespaceIndex]))
            firstNonWhitespaceIndex++;

        if (inDelimitedDocComment)
        {
            var closeIndex = line.IndexOf("*/", StringComparison.Ordinal);
            nextDelimitedDocComment = closeIndex < 0;
            commentStartIndex = 0;
            commentEndExclusive = closeIndex < 0 ? line.Length : closeIndex;
            return true;
        }

        if (inOrdinaryBlockComment)
            return false;

        if (line.AsSpan(firstNonWhitespaceIndex).StartsWith("///", StringComparison.Ordinal))
        {
            if (line.Length != firstNonWhitespaceIndex + 3 && line[firstNonWhitespaceIndex + 3] == '/')
                return false;

            commentStartIndex = firstNonWhitespaceIndex;
            commentEndExclusive = line.Length;
            return true;
        }

        if (!line.AsSpan(firstNonWhitespaceIndex).StartsWith("/**", StringComparison.Ordinal))
            return false;

        var closeAfterOpenIndex = line.IndexOf("*/", firstNonWhitespaceIndex + 3, StringComparison.Ordinal);
        nextDelimitedDocComment = closeAfterOpenIndex < 0;
        commentStartIndex = firstNonWhitespaceIndex;
        commentEndExclusive = closeAfterOpenIndex < 0 ? line.Length : closeAfterOpenIndex;
        return true;
    }

    private static bool HasActiveCSharpUsingStaticTarget(
        string targetQualifiedName,
        int lineNumber,
        IReadOnlyList<CSharpUsingStaticRecord> usingStatics)
    {
        if (string.IsNullOrWhiteSpace(targetQualifiedName))
            return false;

        for (var i = usingStatics.Count - 1; i >= 0; i--)
        {
            var import = usingStatics[i];
            if (import.Line > lineNumber)
                continue;
            if (lineNumber < import.ScopeStartLine || lineNumber > import.ScopeEndLine)
                continue;
            if (string.Equals(import.TargetQualifiedName, targetQualifiedName, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    private static bool HasCSharpValueReceiverConflict(
        string qualifier,
        string resolvedQualifier,
        int lineNumber,
        int column,
        SymbolRecord? callContainer,
        IReadOnlyDictionary<string, CSharpContainingTypeValueReceiverNames> valueReceiverNamesByContainingType,
        IReadOnlyDictionary<int, List<CSharpFunctionValueReceiverNameRecord>> valueReceiverNamesByFunctionStartLine)
    {
        if (string.IsNullOrWhiteSpace(qualifier)
            || (valueReceiverNamesByContainingType.Count == 0 && valueReceiverNamesByFunctionStartLine.Count == 0))
            return false;
        if (!string.Equals(qualifier, resolvedQualifier, StringComparison.Ordinal))
            return false;

        var receiverName = GetFirstQualifiedSegment(qualifier);
        if (string.IsNullOrWhiteSpace(receiverName))
            return false;

        if (callContainer != null
            && (callContainer.Kind == "function" || callContainer.Kind == "property")
            && valueReceiverNamesByFunctionStartLine.TryGetValue(callContainer.StartLine, out var functionNames)
            && functionNames.Any(record => IsWithinCSharpScope(record, lineNumber, column)
                && string.Equals(record.Name, receiverName, StringComparison.Ordinal)))
        {
            return true;
        }

        var containingType = GetContainingTypeQualifiedName(callContainer);
        return containingType != null
            && valueReceiverNamesByContainingType.TryGetValue(containingType, out var names)
            && (IsStaticCSharpSymbol(callContainer)
                ? names.StaticNames.Contains(receiverName)
                : names.StaticNames.Contains(receiverName) || names.InstanceNames.Contains(receiverName));
    }

    private static string? GetContainingTypeQualifiedName(SymbolRecord? symbol)
    {
        if (symbol == null)
            return null;
        if (IsTypeLikeSymbolKind(symbol.Kind))
            return CombineQualifiedName(symbol.ContainerQualifiedName, symbol.Name);
        return symbol.ContainerQualifiedName;
    }

    private static bool IsTypeLikeSymbolKind(string? kind) =>
        kind is "class" or "struct" or "interface";

    private static string? CombineQualifiedName(string? parentQualifiedName, string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;
        if (string.IsNullOrWhiteSpace(parentQualifiedName))
            return name;
        return $"{parentQualifiedName}.{name}";
    }

    private static bool IsWithinCSharpScope(CSharpFunctionValueReceiverNameRecord record, int lineNumber, int column)
    {
        var startsBefore = lineNumber > record.ScopeStartLine
            || (lineNumber == record.ScopeStartLine && column >= record.ScopeStartColumn);
        if (!startsBefore)
            return false;

        return lineNumber < record.ScopeEndLine
            || (lineNumber == record.ScopeEndLine && column < record.ScopeEndColumn);
    }

    private static void AddCSharpParameterNames(List<CSharpFunctionValueReceiverNameRecord> names, string? signature, int scopeStartLine, int scopeStartColumn, int scopeEndLine, int scopeEndColumn)
    {
        if (string.IsNullOrWhiteSpace(signature))
            return;

        var openParen = signature.IndexOf('(');
        var closeParen = signature.LastIndexOf(')');
        if (openParen < 0 || closeParen <= openParen)
            return;

        var parameters = signature[(openParen + 1)..closeParen];
        foreach (var segment in SplitTopLevelCSharpParameterSegments(parameters))
        {
            if (TryExtractTrailingCSharpParameterName(segment, out var name))
                AddCSharpFunctionValueReceiverName(names, name, scopeStartLine, scopeStartColumn, scopeEndLine, scopeEndColumn);
        }
    }

    private static List<string> SplitTopLevelCSharpParameterSegments(string parameters)
    {
        var segments = new List<string>();
        var depthAngle = 0;
        var depthParen = 0;
        var depthBracket = 0;
        var depthBrace = 0;
        var segmentStart = 0;

        for (var i = 0; i < parameters.Length; i++)
        {
            var ch = parameters[i];
            switch (ch)
            {
                case '<':
                    depthAngle++;
                    break;
                case '>':
                    if (depthAngle > 0)
                        depthAngle--;
                    break;
                case '(':
                    depthParen++;
                    break;
                case ')':
                    if (depthParen > 0)
                        depthParen--;
                    break;
                case '[':
                    depthBracket++;
                    break;
                case ']':
                    if (depthBracket > 0)
                        depthBracket--;
                    break;
                case '{':
                    depthBrace++;
                    break;
                case '}':
                    if (depthBrace > 0)
                        depthBrace--;
                    break;
                case ',':
                    if (depthAngle == 0 && depthParen == 0 && depthBracket == 0 && depthBrace == 0)
                    {
                        segments.Add(parameters[segmentStart..i]);
                        segmentStart = i + 1;
                    }
                    break;
            }
        }

        if (segmentStart <= parameters.Length)
            segments.Add(parameters[segmentStart..]);

        return segments;
    }

    private static bool TryExtractTrailingCSharpParameterName(string segment, out string name)
    {
        name = string.Empty;
        var trimmed = segment.Trim();
        if (trimmed.Length == 0 || trimmed == "this")
            return false;

        var end = trimmed.Length - 1;
        while (end >= 0 && char.IsWhiteSpace(trimmed[end]))
            end--;
        while (end >= 0 && (trimmed[end] == '?' || trimmed[end] == '!'))
            end--;
        var start = end;
        while (start >= 0 && IsCSharpIdentifierPart(trimmed[start]))
            start--;
        if (end < 0 || start >= end)
            return false;

        name = NormalizeCSharpIdentifier(trimmed[(start + 1)..(end + 1)]);
        return !string.IsNullOrWhiteSpace(name);
    }

    private static void AddCSharpLambdaParameterNames(List<CSharpFunctionValueReceiverNameRecord> names, string bodyText, int startLineNumber, int scopeEndLine)
    {
        if (string.IsNullOrWhiteSpace(bodyText))
            return;

        var searchIndex = 0;
        while (searchIndex < bodyText.Length)
        {
            var arrowIndex = bodyText.IndexOf("=>", searchIndex, StringComparison.Ordinal);
            if (arrowIndex < 0)
                break;

            var lambdaScopeEnd = FindCSharpArrowExpressionScopeEndPosition(bodyText, arrowIndex, startLineNumber, scopeEndLine);
            AddCSharpLambdaParametersBeforeArrow(names, bodyText, arrowIndex, startLineNumber, lambdaScopeEnd);
            searchIndex = arrowIndex + 2;
        }
    }

    private static void AddCSharpRecursivePatternValueReceiverNames(
        List<CSharpFunctionValueReceiverNameRecord> names,
        string bodyText,
        IReadOnlyList<string> structuralLines,
        int bodyStartIndex,
        int bodyEndIndex)
    {
        if (string.IsNullOrWhiteSpace(bodyText))
            return;

        var startLineNumber = bodyStartIndex + 1;
        foreach (var pattern in FindCSharpRecursivePatternValueNames(bodyText))
        {
            var position = GetLineColumnFromOffset(bodyText, pattern.Offset, startLineNumber);
            var declarationLineIndex = position.Line - 1;
            if (pattern.ArrowIndex >= 0)
            {
                var scopeEnd = FindCSharpArrowExpressionScopeEndPosition(bodyText, pattern.ArrowIndex, startLineNumber, bodyEndIndex + 1);
                AddCSharpFunctionValueReceiverName(names, pattern.Name, position.Line, position.Column, scopeEnd.Line, scopeEnd.Column);
                continue;
            }

            if (pattern.IsCasePattern)
            {
                if (!TryFindCSharpSwitchCaseScopeEndPosition(structuralLines, bodyEndIndex, declarationLineIndex, position.Column, out var scopeEnd))
                    continue;

                AddCSharpFunctionValueReceiverName(names, pattern.Name, position.Line, position.Column, scopeEnd.Line, scopeEnd.Column);
                continue;
            }

            if (!TryFindCSharpDeclarationPatternScopeEndPosition(structuralLines, bodyStartIndex, bodyEndIndex, declarationLineIndex, position.Column, out var declarationScopeEnd))
                continue;

            AddCSharpFunctionValueReceiverName(names, pattern.Name, position.Line, position.Column, declarationScopeEnd.Line, declarationScopeEnd.Column);
        }
    }

    private static IEnumerable<CSharpRecursivePatternValueNameRecord> FindCSharpRecursivePatternValueNames(string bodyText)
    {
        for (var index = 0; index < bodyText.Length; index++)
        {
            if (!IsCSharpIdentifierStart(bodyText[index]))
                continue;

            var tokenStart = index;
            index++;
            while (index < bodyText.Length && IsCSharpIdentifierPart(bodyText[index]))
                index++;

            var token = bodyText[tokenStart..index];
            if ((string.Equals(token, "is", StringComparison.Ordinal) || string.Equals(token, "case", StringComparison.Ordinal))
                && TryParseCSharpRecursivePatternDesignation(bodyText, index, string.Equals(token, "case", StringComparison.Ordinal), out var name, out var designationOffset))
            {
                yield return new CSharpRecursivePatternValueNameRecord(name, designationOffset, string.Equals(token, "case", StringComparison.Ordinal));
            }

            index--;
        }

        foreach (var pattern in FindCSharpSwitchExpressionPatternValueNames(bodyText))
            yield return pattern;
    }

    private static IEnumerable<CSharpRecursivePatternValueNameRecord> FindCSharpSwitchExpressionPatternValueNames(string bodyText)
    {
        if (string.IsNullOrWhiteSpace(bodyText))
            yield break;

        for (var searchIndex = 0; searchIndex < bodyText.Length;)
        {
            var arrowIndex = bodyText.IndexOf("=>", searchIndex, StringComparison.Ordinal);
            if (arrowIndex < 0)
                yield break;

            searchIndex = arrowIndex + 2;
            if (IsPotentialCSharpLambdaArrow(bodyText, arrowIndex))
                continue;

            if (!TryFindCSharpSwitchExpressionArmStartOffset(bodyText, arrowIndex, out var armStartOffset))
                continue;

            if (!TryParseCSharpSwitchExpressionArmPatternDesignation(bodyText, armStartOffset, arrowIndex, out var name, out var designationOffset))
                continue;

            yield return new CSharpRecursivePatternValueNameRecord(name, designationOffset, false, arrowIndex);
        }
    }

    private static bool TryFindCSharpSwitchExpressionArmStartOffset(string bodyText, int arrowIndex, out int armStartOffset)
    {
        armStartOffset = 0;
        if (arrowIndex <= 0 || arrowIndex > bodyText.Length)
            return false;

        var parenDepth = 0;
        var bracketDepth = 0;
        var braceDepth = 0;
        for (var index = arrowIndex - 1; index >= 0; index--)
        {
            var current = bodyText[index];
            switch (current)
            {
                case ')':
                    parenDepth++;
                    break;
                case '(':
                    if (parenDepth > 0)
                        parenDepth--;
                    break;
                case ']':
                    bracketDepth++;
                    break;
                case '[':
                    if (bracketDepth > 0)
                        bracketDepth--;
                    break;
                case '}':
                    braceDepth++;
                    break;
                case '{':
                    if (braceDepth > 0)
                    {
                        braceDepth--;
                        break;
                    }

                    if (parenDepth == 0 && bracketDepth == 0)
                    {
                        armStartOffset = SkipWhitespaceForward(bodyText, index + 1);
                        return armStartOffset < arrowIndex;
                    }

                    break;
                case ',':
                    if (parenDepth == 0 && bracketDepth == 0 && braceDepth == 0)
                    {
                        armStartOffset = SkipWhitespaceForward(bodyText, index + 1);
                        return armStartOffset < arrowIndex;
                    }

                    break;
                case ';':
                    if (parenDepth == 0 && bracketDepth == 0 && braceDepth == 0)
                        return false;
                    break;
            }
        }

        return false;
    }

    private static bool TryGetCSharpSwitchExpressionArmTypePatternRange(
        string bodyText,
        int arrowIndex,
        out int bodyStartOffset,
        out int armStartOffset,
        out int armPatternEndOffset)
    {
        bodyStartOffset = 0;
        armStartOffset = 0;
        armPatternEndOffset = 0;
        if (!TryFindCSharpSwitchExpressionBodyStartOffset(bodyText, arrowIndex, out bodyStartOffset))
            return false;

        var segmentStartOffset = bodyStartOffset + 1;
        if (segmentStartOffset >= arrowIndex)
            return false;

        var segmentText = bodyText[segmentStartOffset..arrowIndex];
        var lastCommaOffset = FindLastTopLevelCSharpComma(segmentText);
        var relativeArmStart = lastCommaOffset >= 0
            ? SkipWhitespaceForward(segmentText, lastCommaOffset + 1)
            : SkipWhitespaceForward(segmentText, 0);
        if (relativeArmStart >= segmentText.Length)
            return false;

        var armSegment = segmentText[relativeArmStart..];
        var whenOffset = FindTopLevelCSharpWhenKeywordOffset(armSegment);
        var relativePatternEnd = whenOffset >= 0
            ? relativeArmStart + whenOffset
            : segmentText.Length;
        while (relativePatternEnd > relativeArmStart && char.IsWhiteSpace(segmentText[relativePatternEnd - 1]))
            relativePatternEnd--;
        if (relativePatternEnd <= relativeArmStart)
            return false;

        armStartOffset = segmentStartOffset + relativeArmStart;
        armPatternEndOffset = segmentStartOffset + relativePatternEnd;
        return armStartOffset < armPatternEndOffset;
    }

    private static bool TryFindCSharpSwitchExpressionBodyStartOffset(string bodyText, int arrowIndex, out int bodyStartOffset)
    {
        bodyStartOffset = -1;
        if (arrowIndex <= 0 || arrowIndex > bodyText.Length)
            return false;

        var parenDepth = 0;
        var bracketDepth = 0;
        var braceDepth = 0;
        for (var index = arrowIndex - 1; index >= 0; index--)
        {
            var current = bodyText[index];
            switch (current)
            {
                case ')':
                    parenDepth++;
                    break;
                case '(':
                    if (parenDepth > 0)
                        parenDepth--;
                    break;
                case ']':
                    bracketDepth++;
                    break;
                case '[':
                    if (bracketDepth > 0)
                        bracketDepth--;
                    break;
                case '}':
                    braceDepth++;
                    break;
                case '{':
                    if (braceDepth > 0)
                    {
                        braceDepth--;
                        break;
                    }

                    if (parenDepth == 0 && bracketDepth == 0)
                    {
                        bodyStartOffset = index;
                        return true;
                    }

                    break;
                case ';':
                    if (parenDepth == 0 && bracketDepth == 0 && braceDepth == 0)
                        return false;
                    break;
            }
        }

        return false;
    }

    private static int FindLastTopLevelCSharpComma(string text)
    {
        var angleDepth = 0;
        var parenDepth = 0;
        var bracketDepth = 0;
        var braceDepth = 0;
        var lastComma = -1;
        for (var i = 0; i < text.Length; i++)
        {
            switch (text[i])
            {
                case '<':
                    angleDepth++;
                    break;
                case '>':
                    if (angleDepth > 0)
                        angleDepth--;
                    break;
                case '(':
                    parenDepth++;
                    break;
                case ')':
                    if (parenDepth > 0)
                        parenDepth--;
                    break;
                case '[':
                    bracketDepth++;
                    break;
                case ']':
                    if (bracketDepth > 0)
                        bracketDepth--;
                    break;
                case '{':
                    braceDepth++;
                    break;
                case '}':
                    if (braceDepth > 0)
                        braceDepth--;
                    break;
                case ',':
                    if (angleDepth == 0 && parenDepth == 0 && bracketDepth == 0 && braceDepth == 0)
                        lastComma = i;
                    break;
            }
        }

        return lastComma;
    }

    private static bool TryParseCSharpSwitchExpressionArmPatternDesignation(
        string bodyText,
        int armStartOffset,
        int arrowIndex,
        out string name,
        out int designationOffset)
    {
        name = string.Empty;
        designationOffset = -1;
        if (armStartOffset < 0 || armStartOffset >= arrowIndex || arrowIndex > bodyText.Length)
            return false;

        var armText = bodyText[armStartOffset..arrowIndex];
        var preparedArmLines = StructuralLineMasker.MaskLines("csharp", armText.Split('\n'));
        for (var i = 0; i < preparedArmLines.Length; i++)
            preparedArmLines[i] = PrepareLine("csharp", preparedArmLines[i]);

        var preparedArmText = string.Join("\n", preparedArmLines);
        if (!TryParseCSharpRecursivePatternDesignation(preparedArmText, 0, false, out name, out var relativeOffset)
            && !TryParseCSharpSwitchExpressionArmDeclarationPatternDesignation(preparedArmText, out name, out relativeOffset))
        {
            return false;
        }

        designationOffset = armStartOffset + relativeOffset;
        return designationOffset < arrowIndex;
    }

    private static bool TryParseCSharpSwitchExpressionArmDeclarationPatternDesignation(
        string armText,
        out string name,
        out int designationOffset)
    {
        name = string.Empty;
        designationOffset = -1;
        if (string.IsNullOrWhiteSpace(armText))
            return false;

        var whenOffset = FindTopLevelCSharpWhenKeywordOffset(armText);
        var patternText = whenOffset >= 0 ? armText[..whenOffset] : armText;
        var match = CSharpSwitchExpressionDeclarationPatternValueNameRegex.Match(patternText);
        if (!match.Success)
            return false;

        name = NormalizeCSharpIdentifier(match.Groups["name"].Value);
        designationOffset = match.Groups["name"].Index;
        return designationOffset >= 0;
    }

    private static bool TryParseCSharpRecursivePatternDesignation(
        string bodyText,
        int index,
        bool isCasePattern,
        out string name,
        out int designationOffset)
    {
        name = string.Empty;
        designationOffset = -1;
        var parenDepth = 0;
        var bracketDepth = 0;
        var braceDepth = 0;
        var sawRecursiveClause = false;
        var previousTopLevelNonWhitespaceChar = '\0';
        for (var i = index; i < bodyText.Length; i++)
        {
            var current = bodyText[i];
            if (char.IsWhiteSpace(current))
                continue;

            if (braceDepth == 0 && parenDepth == 0 && bracketDepth == 0 && IsCSharpIdentifierStart(current))
            {
                var tokenStart = i;
                i++;
                while (i < bodyText.Length && IsCSharpIdentifierPart(bodyText[i]))
                    i++;

                var token = bodyText[tokenStart..i];
                i--;
                if (sawRecursiveClause
                    && previousTopLevelNonWhitespaceChar is not '.' and not ':' and not '<' and not '[' and not '?'
                    && !IsCSharpPatternControlKeyword(token))
                {
                    name = NormalizeCSharpIdentifier(token);
                    designationOffset = tokenStart;
                    return true;
                }

                previousTopLevelNonWhitespaceChar = token[^1];
                continue;
            }

            switch (current)
            {
                case '(':
                    parenDepth++;
                    break;
                case ')':
                    if (parenDepth > 0)
                        parenDepth--;
                    break;
                case '[':
                    bracketDepth++;
                    break;
                case ']':
                    if (bracketDepth > 0)
                        bracketDepth--;
                    break;
                case '{':
                    braceDepth++;
                    sawRecursiveClause = true;
                    break;
                case '}':
                    if (braceDepth > 0)
                        braceDepth--;
                    break;
            }

            if (braceDepth == 0 && parenDepth == 0 && bracketDepth == 0)
                previousTopLevelNonWhitespaceChar = current;
        }

        return false;
    }

    private static bool IsCSharpPatternControlKeyword(string token) =>
        token is "and" or "or" or "not" or "when" or "null" or "true" or "false";

    private static int FindTopLevelCSharpWhenKeywordOffset(string text)
    {
        var parenDepth = 0;
        var bracketDepth = 0;
        var braceDepth = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var current = text[i];
            switch (current)
            {
                case '(':
                    parenDepth++;
                    break;
                case ')':
                    if (parenDepth > 0)
                        parenDepth--;
                    break;
                case '[':
                    bracketDepth++;
                    break;
                case ']':
                    if (bracketDepth > 0)
                        bracketDepth--;
                    break;
                case '{':
                    braceDepth++;
                    break;
                case '}':
                    if (braceDepth > 0)
                        braceDepth--;
                    break;
            }

            if (parenDepth == 0
                && bracketDepth == 0
                && braceDepth == 0
                && TryConsumeCSharpKeyword(text, i, "when", out _))
            {
                return i;
            }
        }

        return -1;
    }

}
