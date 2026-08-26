using System.Text;
using CodeIndex.Models;

namespace CodeIndex.Indexer;

public static partial class ReferenceExtractor
{
    private const string CSharpLocalFunctionTargetQualifierPrefix = "\u001fcsharp_local:";
    private const string CSharpNonLocalFunctionTargetQualifier = "\u001fcsharp_nonlocal";
    private const string CSharpValueCallableTargetQualifier = "\u001fcsharp_value_callable";
    private const string CSharpUncertainLocalFunctionTargetQualifier = "\u001fcsharp_local_uncertain";
    private const int MaxCSharpLexicalLocalFunctionTargets = 64;

    private readonly record struct CSharpLocalFunctionScope(
        SymbolRecord Symbol,
        SymbolRecord? ParentCallable,
        CSharpBlockScope? DeclarationScope);

    private static void ApplyCSharpLocalFunctionTargetQualifiers(
        IReadOnlyList<string> structuralLines,
        IReadOnlyList<SymbolRecord> symbols,
        List<ReferenceRecord> references,
        CoreExtractionLookups lookups)
    {
        if (structuralLines.Count == 0 || symbols.Count == 0 || references.Count == 0)
            return;

        var topLevelScope = symbols.FirstOrDefault(symbol =>
            symbol.SubKind == SyntheticSymbolIdentity.CSharpTopLevelScopeSubKind);
        var localFunctions = symbols
            .Where(symbol =>
                IsCSharpLocalFunctionSymbol(symbol)
                || IsCSharpTopLevelLocalFunctionSymbol(symbol, topLevelScope))
            .ToArray();
        if (localFunctions.Length == 0)
            return;

        var blockScopes = BuildCSharpBlockScopes(structuralLines, 0, structuralLines.Count - 1);
        var callableSymbolsByName = symbols
            .Where(IsCSharpCallableScopeSymbol)
            .GroupBy(symbol => NormalizeCSharpIdentifier(symbol.Name), StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        var localScopesByName = localFunctions
            .Select(symbol => BuildCSharpLocalFunctionScope(
                symbol,
                topLevelScope,
                structuralLines.Count,
                callableSymbolsByName,
                blockScopes))
            .GroupBy(scope => NormalizeCSharpIdentifier(scope.Symbol.Name), StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        var valueNamesByFunctionStartLine = lookups.GetCSharpFunctionValueReceiverNames();
        var callableValueScopes = callableSymbolsByName.Values
            .SelectMany(static callables => callables)
            .Where(symbol => valueNamesByFunctionStartLine.ContainsKey(symbol.StartLine))
            .OrderBy(symbol => symbol.EndLine - symbol.StartLine)
            .ThenByDescending(symbol => symbol.StartLine)
            .ToArray();

        foreach (var reference in references)
        {
            if (reference.ReferenceKind != "call"
                || reference.TargetQualifier != null
                || string.IsNullOrWhiteSpace(reference.SymbolName)
                || !localScopesByName.TryGetValue(
                    NormalizeCSharpIdentifier(reference.SymbolName),
                    out var sameNameLocalScopes))
            {
                continue;
            }

            var referenceLineIndex = reference.Line - 1;
            var referenceColumn = Math.Max(reference.Column - 1, 0);
            if (HasCSharpCallableValueConflict(
                    callableValueScopes,
                    valueNamesByFunctionStartLine,
                    reference.SymbolName,
                    reference.Line,
                    referenceColumn))
            {
                reference.TargetQualifier = CSharpValueCallableTargetQualifier;
                continue;
            }

            var visibleScopes = new List<CSharpLocalFunctionScope>();
            var hasIncompleteRelatedScope = false;
            foreach (var localScope in sameNameLocalScopes)
            {
                if (localScope.ParentCallable == null)
                {
                    hasIncompleteRelatedScope = true;
                    continue;
                }

                if (!ContainsCSharpSymbolRange(localScope.ParentCallable, reference.Line))
                    continue;

                if (localScope.DeclarationScope is not { } declarationScope)
                {
                    hasIncompleteRelatedScope = true;
                    continue;
                }

                if (ContainsCSharpBlockScope(declarationScope, referenceLineIndex, referenceColumn))
                    visibleScopes.Add(localScope);
            }

            if (hasIncompleteRelatedScope)
            {
                reference.TargetQualifier = CSharpUncertainLocalFunctionTargetQualifier;
                continue;
            }

            if (visibleScopes.Count == 0)
            {
                reference.TargetQualifier = CSharpNonLocalFunctionTargetQualifier;
                continue;
            }

            var bestScope = visibleScopes[0].DeclarationScope!.Value;
            for (var index = 1; index < visibleScopes.Count; index++)
            {
                var candidateScope = visibleScopes[index].DeclarationScope!.Value;
                if (IsNarrowerCSharpBlockScope(candidateScope, bestScope))
                    bestScope = candidateScope;
            }

            var targets = visibleScopes
                .Where(scope => scope.DeclarationScope == bestScope)
                .Select(scope => (
                    Line: scope.Symbol.Line,
                    Column: scope.Symbol.IdentifierStartColumn
                        ?? scope.Symbol.StartColumn
                        ?? -1))
                .Distinct()
                .OrderBy(target => target.Line)
                .ThenBy(target => target.Column)
                .ToArray();
            if (targets.Length == 0 || targets.Length > MaxCSharpLexicalLocalFunctionTargets)
            {
                reference.TargetQualifier = CSharpUncertainLocalFunctionTargetQualifier;
                continue;
            }

            var qualifier = new StringBuilder(CSharpLocalFunctionTargetQualifierPrefix);
            foreach (var target in targets)
                qualifier.Append('|').Append(target.Line).Append(':').Append(target.Column).Append('|');
            reference.TargetQualifier = qualifier.ToString();
        }
    }

    private static bool IsCSharpLocalFunctionSymbol(SymbolRecord symbol) =>
        symbol.Kind == "function"
        && symbol.ContainerKind is "function" or "test.method" or "lambda" or "property"
        && !string.IsNullOrWhiteSpace(symbol.Name);

    private static bool IsCSharpTopLevelLocalFunctionSymbol(
        SymbolRecord symbol,
        SymbolRecord? topLevelScope) =>
        topLevelScope != null
        && !ReferenceEquals(symbol, topLevelScope)
        && symbol.Kind == "function"
        && symbol.SubKind != SyntheticSymbolIdentity.CSharpTopLevelScopeSubKind
        && string.IsNullOrWhiteSpace(symbol.ContainerKind)
        && string.IsNullOrWhiteSpace(symbol.ContainerName)
        && string.IsNullOrWhiteSpace(symbol.ContainerQualifiedName)
        && !string.IsNullOrWhiteSpace(symbol.Name);

    private static bool IsCSharpCallableScopeSymbol(SymbolRecord symbol) =>
        symbol.Kind is "function" or "property"
        && !string.IsNullOrWhiteSpace(symbol.Name);

    private static CSharpLocalFunctionScope BuildCSharpLocalFunctionScope(
        SymbolRecord symbol,
        SymbolRecord? topLevelScope,
        int structuralLineCount,
        IReadOnlyDictionary<string, SymbolRecord[]> callableSymbolsByName,
        IReadOnlyList<CSharpBlockScope> blockScopes)
    {
        var isTopLevelLocal = IsCSharpTopLevelLocalFunctionSymbol(symbol, topLevelScope);
        var parentCallable = isTopLevelLocal
            ? topLevelScope
            : FindCSharpLocalFunctionParentCallable(symbol, callableSymbolsByName);
        if (parentCallable == null)
            return new CSharpLocalFunctionScope(symbol, null, null);

        var declarationLineIndex = symbol.Line - 1;
        var declarationColumn = symbol.IdentifierStartColumn
            ?? symbol.StartColumn
            ?? -1;
        CSharpBlockScope? declarationScope = null;
        foreach (var scope in blockScopes)
        {
            if (!ContainsCSharpBlockScope(scope, declarationLineIndex, declarationColumn)
                || (!isTopLevelLocal && !IsCSharpBlockWithinCallable(scope, parentCallable)))
            {
                continue;
            }

            if (declarationScope == null || IsNarrowerCSharpBlockScope(scope, declarationScope.Value))
                declarationScope = scope;
        }

        if (isTopLevelLocal && declarationScope == null && structuralLineCount > 0)
        {
            declarationScope = new CSharpBlockScope(
                0,
                0,
                structuralLineCount - 1,
                int.MaxValue);
        }

        return new CSharpLocalFunctionScope(symbol, parentCallable, declarationScope);
    }

    private static SymbolRecord? FindCSharpLocalFunctionParentCallable(
        SymbolRecord localFunction,
        IReadOnlyDictionary<string, SymbolRecord[]> callableSymbolsByName)
    {
        if (string.IsNullOrWhiteSpace(localFunction.ContainerName)
            || !callableSymbolsByName.TryGetValue(
                NormalizeCSharpIdentifier(localFunction.ContainerName),
                out var candidates))
        {
            return null;
        }

        SymbolRecord? best = null;
        foreach (var candidate in candidates)
        {
            if (ReferenceEquals(candidate, localFunction)
                || !ContainsCSharpSymbolRange(candidate, localFunction.Line))
            {
                continue;
            }

            if (best == null || IsNarrowerCSharpSymbolRange(candidate, best))
                best = candidate;
        }

        return best;
    }

    private static bool HasCSharpCallableValueConflict(
        IReadOnlyList<SymbolRecord> callableValueScopes,
        IReadOnlyDictionary<int, List<CSharpFunctionValueReceiverNameRecord>> valueNamesByFunctionStartLine,
        string name,
        int lineNumber,
        int column)
    {
        if (valueNamesByFunctionStartLine.Count == 0)
            return false;

        var normalizedName = NormalizeCSharpIdentifier(name);
        foreach (var candidate in callableValueScopes)
        {
            if (ContainsCSharpSymbolRange(candidate, lineNumber)
                && valueNamesByFunctionStartLine.TryGetValue(candidate.StartLine, out var names)
                && HasCSharpFunctionValueReceiverName(names, normalizedName, lineNumber, column))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsCSharpBlockWithinCallable(CSharpBlockScope scope, SymbolRecord callable)
    {
        if (callable.BodyStartLine == null || callable.BodyEndLine == null)
            return false;

        var scopeStartLine = scope.StartLineIndex + 1;
        var scopeEndLine = scope.EndLineIndex + 1;
        return scopeStartLine >= callable.StartLine
            && scopeStartLine <= callable.BodyEndLine.Value
            && scopeEndLine >= callable.BodyStartLine.Value
            && scopeEndLine <= callable.EndLine;
    }

    private static bool ContainsCSharpSymbolRange(SymbolRecord symbol, int lineNumber) =>
        symbol.StartLine > 0
        && symbol.EndLine >= symbol.StartLine
        && lineNumber >= symbol.StartLine
        && lineNumber <= symbol.EndLine;

    private static bool IsNarrowerCSharpSymbolRange(SymbolRecord candidate, SymbolRecord current)
    {
        var candidateSpan = candidate.EndLine - candidate.StartLine;
        var currentSpan = current.EndLine - current.StartLine;
        if (candidateSpan != currentSpan)
            return candidateSpan < currentSpan;
        return candidate.StartLine > current.StartLine;
    }
}
