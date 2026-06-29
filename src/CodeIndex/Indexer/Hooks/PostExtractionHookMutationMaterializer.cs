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

    private static SymbolRecord CloneSymbol(SymbolRecord symbol)
        => new()
        {
            Id = symbol.Id,
            FileId = symbol.FileId,
            Kind = symbol.Kind,
            SubKind = symbol.SubKind,
            Name = symbol.Name,
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
            ReferenceKind = reference.ReferenceKind,
            Line = reference.Line,
            Column = reference.Column,
            Context = reference.Context,
            ContainerKind = reference.ContainerKind,
            ContainerName = reference.ContainerName,
            IsSelfReference = reference.IsSelfReference,
            IsMutualRecursion = reference.IsMutualRecursion,
        };
}
