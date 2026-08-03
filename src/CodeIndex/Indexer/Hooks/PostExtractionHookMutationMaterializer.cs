using CodeIndex.Database;
using CodeIndex.Models;

namespace CodeIndex.Indexer.Hooks;

internal static class PostExtractionHookMutationMaterializer
{
    internal static int? NormalizeLimit(int? value)
        => value is > 0 ? value : null;

    internal static List<SymbolRecord> CloneSymbols(IEnumerable<SymbolRecord> symbols, int? maxCount, out bool truncated)
    {
        var result = new List<SymbolRecord>();
        truncated = false;
        foreach (var symbol in symbols)
        {
            if (maxCount is { } limit && result.Count >= limit)
            {
                truncated = true;
                break;
            }

            result.Add(CloneSymbol(symbol));
        }

        return result;
    }

    internal static List<ReferenceRecord> CloneReferences(IEnumerable<ReferenceRecord> references, int? maxCount, out bool truncated)
    {
        var result = new List<ReferenceRecord>();
        truncated = false;
        foreach (var reference in references)
        {
            if (maxCount is { } limit && result.Count >= limit)
            {
                truncated = true;
                break;
            }

            result.Add(CloneReference(reference));
        }

        return result;
    }

    internal static void ReplaceList<T>(IList<T> target, IReadOnlyList<T> replacement)
    {
        target.Clear();
        foreach (var item in replacement)
            target.Add(item);
    }

    internal static bool TrimToLimit<T>(List<T>? items, int? maxCount)
    {
        if (items == null || maxCount is not { } limit || limit <= 0 || items.Count <= limit)
            return false;

        items.RemoveRange(limit, items.Count - limit);
        return true;
    }

    internal static void RefreshLanguageIdentity(string? language, IEnumerable<SymbolRecord> symbols)
    {
        if (string.Equals(language, "nim", StringComparison.Ordinal))
        {
            foreach (var symbol in symbols)
            {
                symbol.IdentityNameFolded = NimIdentifierIdentity.Fold(symbol.Name);
                symbol.DisplayNameFolded = null;
            }
            return;
        }

        if (string.Equals(language, "csharp", StringComparison.Ordinal))
        {
            foreach (var symbol in symbols)
            {
                var previousIdentityNameFolded = symbol.IdentityNameFolded;
                var previousDisplayNameFolded = symbol.DisplayNameFolded;
                symbol.IdentityNameFolded =
                    CSharpSymbolNameNormalizer.BuildExplicitInterfaceIdentityNameFolded(
                        symbol.Name,
                        symbol.Signature,
                        symbol.Kind)
                    ?? CSharpSymbolNameNormalizer.RebuildExplicitInterfaceIdentityAfterNameMutation(
                        symbol.Name,
                        symbol.Signature,
                        symbol.Kind,
                        previousIdentityNameFolded,
                        previousDisplayNameFolded);
                symbol.DisplayNameFolded = symbol.IdentityNameFolded != null
                    ? NameFold.Fold(symbol.Name)
                    : null;
            }
        }
    }

    internal static void RefreshCSharpDeclarationMetadataAfterHookMutation(
        string? language,
        IReadOnlyList<SymbolRecord> sourceSymbols,
        IReadOnlyList<SymbolRecord> mutatedSymbols)
    {
        if (!string.Equals(language, "csharp", StringComparison.Ordinal))
            return;

        var sourceFacts = new Dictionary<HookSymbolDeclarationState, Queue<SourceCSharpDeclarationFacts>>();
        var sourceFactsByLocation = new Dictionary<HookSymbolDeclarationLocation, Queue<SourceCSharpDeclarationFacts>>();
        foreach (var symbol in sourceSymbols)
        {
            var sourceFact = new SourceCSharpDeclarationFacts(CSharpDeclarationFacts.From(symbol));
            var state = HookSymbolDeclarationState.From(symbol);
            if (!sourceFacts.TryGetValue(state, out var facts))
            {
                facts = new Queue<SourceCSharpDeclarationFacts>();
                sourceFacts[state] = facts;
            }
            facts.Enqueue(sourceFact);

            var location = HookSymbolDeclarationLocation.From(symbol);
            if (!sourceFactsByLocation.TryGetValue(location, out var locationFacts))
            {
                locationFacts = new Queue<SourceCSharpDeclarationFacts>();
                sourceFactsByLocation[location] = locationFacts;
            }
            locationFacts.Enqueue(sourceFact);
        }

        foreach (var symbol in mutatedSymbols)
        {
            var state = HookSymbolDeclarationState.From(symbol);
            var exactSource = sourceFacts.TryGetValue(state, out var facts)
                ? DequeueUnmatched(facts)
                : null;
            if (exactSource != null)
            {
                exactSource.Matched = true;
                exactSource.Facts.Apply(symbol);
                continue;
            }

            SymbolExtractor.RefreshCSharpPartialDeclarationMetadataFromHookSignature(symbol);
            var location = HookSymbolDeclarationLocation.From(symbol);
            var positionalSource = sourceFactsByLocation.TryGetValue(location, out var locationFacts)
                ? DequeueUnmatched(locationFacts)
                : null;
            if (positionalSource != null)
            {
                positionalSource.Matched = true;
                positionalSource.Facts.PreserveLeadingSourceModifiers(symbol);
            }
            symbol.DeclarationStructureMutatedByHook = true;
        }
    }

    private static SourceCSharpDeclarationFacts? DequeueUnmatched(
        Queue<SourceCSharpDeclarationFacts> candidates)
    {
        while (candidates.Count > 0)
        {
            var candidate = candidates.Dequeue();
            if (!candidate.Matched)
                return candidate;
        }

        return null;
    }

    internal static void RefreshLanguageIdentity(string? language, IEnumerable<ReferenceRecord> references)
    {
        if (!string.Equals(language, "nim", StringComparison.Ordinal))
            return;

        foreach (var reference in references)
        {
            reference.IdentitySymbolNameFolded = NimIdentifierIdentity.Fold(reference.SymbolName);
            reference.IdentityContainerNameFolded = NimIdentifierIdentity.Fold(reference.ContainerName);
        }
    }

    private static SymbolRecord CloneSymbol(SymbolRecord symbol)
        => new()
        {
            Id = symbol.Id,
            FileId = symbol.FileId,
            Kind = symbol.Kind,
            SubKind = symbol.SubKind,
            Name = symbol.Name,
            IdentityNameFolded = symbol.IdentityNameFolded,
            DisplayNameFolded = symbol.DisplayNameFolded,
            Line = symbol.Line,
            StartLine = symbol.StartLine,
            StartColumn = symbol.StartColumn,
            EndLine = symbol.EndLine,
            BodyStartLine = symbol.BodyStartLine,
            BodyEndLine = symbol.BodyEndLine,
            Signature = symbol.Signature,
            ContainerKind = symbol.ContainerKind,
            ContainerName = symbol.ContainerName,
            ContainerQualifiedName = symbol.ContainerQualifiedName,
            FamilyKey = symbol.FamilyKey,
            Visibility = symbol.Visibility,
            ReturnType = symbol.ReturnType,
            IsPartialDeclaration = symbol.IsPartialDeclaration,
            IsFileLocalDeclaration = symbol.IsFileLocalDeclaration,
            IsExplicitFileLocalDeclaration = symbol.IsExplicitFileLocalDeclaration,
            DeclarationStructureMutatedByHook = symbol.DeclarationStructureMutatedByHook,
            DeclarationSemanticScore = symbol.DeclarationSemanticScore,
            IdentifierStartColumn = symbol.IdentifierStartColumn,
            IsMetadataTarget = symbol.IsMetadataTarget,
            MetadataTargetSource = symbol.MetadataTargetSource,
            SameLineSignatureOccurrenceIndex = symbol.SameLineSignatureOccurrenceIndex,
        };

    private static ReferenceRecord CloneReference(ReferenceRecord reference)
        => new()
        {
            Id = reference.Id,
            FileId = reference.FileId,
            SymbolName = reference.SymbolName,
            IdentitySymbolNameFolded = reference.IdentitySymbolNameFolded,
            ReferenceKind = reference.ReferenceKind,
            Line = reference.Line,
            Column = reference.Column,
            SpanLength = reference.SpanLength,
            Context = reference.Context,
            ContainerKind = reference.ContainerKind,
            ContainerName = reference.ContainerName,
            IdentityContainerNameFolded = reference.IdentityContainerNameFolded,
            TargetQualifier = reference.TargetQualifier,
            SuppressInferredTargetQualifier = reference.SuppressInferredTargetQualifier,
            IsSelfReference = reference.IsSelfReference,
            IsMutualRecursion = reference.IsMutualRecursion,
        };

    private readonly record struct HookSymbolDeclarationState(
        long Id,
        long FileId,
        string Kind,
        string? SubKind,
        string Name,
        int Line,
        int StartLine,
        int? StartColumn,
        int EndLine,
        int? BodyStartLine,
        int? BodyEndLine,
        string? Signature,
        string? ContainerKind,
        string? ContainerName,
        string? ContainerQualifiedName,
        int? SameLineSignatureOccurrenceIndex)
    {
        internal static HookSymbolDeclarationState From(SymbolRecord symbol)
            => new(
                symbol.Id,
                symbol.FileId,
                symbol.Kind,
                symbol.SubKind,
                symbol.Name,
                symbol.Line,
                symbol.StartLine,
                symbol.StartColumn,
                symbol.EndLine,
                symbol.BodyStartLine,
                symbol.BodyEndLine,
                symbol.Signature,
                symbol.ContainerKind,
                symbol.ContainerName,
                symbol.ContainerQualifiedName,
                symbol.SameLineSignatureOccurrenceIndex);
    }

    private readonly record struct HookSymbolDeclarationLocation(
        long Id,
        long FileId,
        string Kind,
        string? SubKind,
        int Line,
        int StartLine,
        int? StartColumn,
        int EndLine,
        int? BodyStartLine,
        int? BodyEndLine,
        int? SameLineSignatureOccurrenceIndex)
    {
        internal static HookSymbolDeclarationLocation From(SymbolRecord symbol)
            => new(
                symbol.Id,
                symbol.FileId,
                symbol.Kind,
                symbol.SubKind,
                symbol.Line,
                symbol.StartLine,
                symbol.StartColumn,
                symbol.EndLine,
                symbol.BodyStartLine,
                symbol.BodyEndLine,
                symbol.SameLineSignatureOccurrenceIndex);
    }

    private sealed class SourceCSharpDeclarationFacts(CSharpDeclarationFacts facts)
    {
        internal CSharpDeclarationFacts Facts { get; } = facts;
        internal bool Matched { get; set; }
    }

    private readonly record struct CSharpDeclarationFacts(
        bool? IsPartialDeclaration,
        bool IsFileLocalDeclaration,
        bool? IsExplicitFileLocalDeclaration,
        int? DeclarationSemanticScore,
        int? IdentifierStartColumn,
        bool SignatureDeclaresPartial,
        bool SignatureDeclaresFileLocal)
    {
        internal static CSharpDeclarationFacts From(SymbolRecord symbol)
        {
            var signatureFacts = new SymbolRecord
            {
                Kind = symbol.Kind,
                Name = symbol.Name,
                Signature = symbol.Signature,
            };
            SymbolExtractor.RefreshCSharpPartialDeclarationMetadataFromHookSignature(signatureFacts);
            return new(
                symbol.IsPartialDeclaration,
                symbol.IsFileLocalDeclaration,
                symbol.IsExplicitFileLocalDeclaration,
                symbol.DeclarationSemanticScore,
                symbol.IdentifierStartColumn,
                signatureFacts.IsPartialDeclaration == true,
                signatureFacts.IsExplicitFileLocalDeclaration == true);
        }

        internal void Apply(SymbolRecord symbol)
        {
            symbol.IsPartialDeclaration = IsPartialDeclaration;
            symbol.IsFileLocalDeclaration = IsFileLocalDeclaration;
            symbol.IsExplicitFileLocalDeclaration = IsExplicitFileLocalDeclaration;
            symbol.DeclarationSemanticScore = DeclarationSemanticScore;
            symbol.IdentifierStartColumn = IdentifierStartColumn;
        }

        internal void PreserveLeadingSourceModifiers(SymbolRecord symbol)
        {
            if (IsPartialDeclaration == true && !SignatureDeclaresPartial)
                symbol.IsPartialDeclaration = true;
            if (IsExplicitFileLocalDeclaration == true && !SignatureDeclaresFileLocal)
            {
                symbol.IsExplicitFileLocalDeclaration = true;
                symbol.IsFileLocalDeclaration = true;
            }
        }
    }
}
