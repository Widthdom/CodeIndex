using CodeIndex.Models;

namespace CodeIndex.Indexer;

public static partial class ReferenceExtractor
{
    private static List<ReferenceRecord> ExtractAmbiguousMReferences(ReferenceExtractionContext request)
    {
        if (string.IsNullOrEmpty(request.Content)
            || (request.HasOversizeLine ?? ChunkSplitter.HasOversizeLine(request.Content))
            || (request.ConflictMarkerLine ?? FileIndexer.GetConflictMarkerLine(request.Content)) > 0)
        {
            return [];
        }

        var normalizedContent = request.ContentIsNormalized
            ? request.Content
            : FileIndexer.NormalizeContentForPrepass(request.Content);
        var originalLines = SplitContentLines(normalizedContent);
        var matlabContent = AmbiguousMContentMasker.MaskComments(
            normalizedContent,
            maskMatlabComments: true,
            maskObjectiveCComments: true);
        var objectiveCContent = AmbiguousMContentMasker.MaskComments(
            normalizedContent,
            maskMatlabComments: true,
            maskObjectiveCComments: true,
            preserveObjectiveCModuloExpressions: true);
        var matlabReferences = ExtractCore(request with
        {
            Language = "matlab",
            Content = matlabContent,
            RequestedLanguage = "ambiguous_m",
            ContentIsNormalized = true,
            HasOversizeLine = false,
            ConflictMarkerLine = 0,
        });
        var objectiveCReferences = ExtractCore(request with
        {
            Language = "objc",
            Content = objectiveCContent,
            RequestedLanguage = "ambiguous_m",
            ContentIsNormalized = true,
            HasOversizeLine = false,
            ConflictMarkerLine = 0,
        });
        var merged = CreateReferenceList(
            request.MaxReferenceCount,
            Math.Min(matlabReferences.Count + objectiveCReferences.Count, ReferenceListInitialCapacityMax));
        var seen = new ReferenceDedupeSet(merged.Capacity);

        AddUnique(matlabReferences);
        AddUnique(objectiveCReferences);
        return merged;

        void AddUnique(IReadOnlyList<ReferenceRecord> candidates)
        {
            for (var index = 0; index < candidates.Count && !ReferenceLimitReached(merged); index++)
            {
                var candidate = candidates[index];
                if (candidate.Line > 0 && candidate.Line <= originalLines.Length)
                    candidate.Context = originalLines[candidate.Line - 1].Trim();
                var key = CreateReferenceDedupeKey(
                    candidate.FileId,
                    "ambiguous_m",
                    candidate.Line,
                    candidate.Column,
                    candidate.ReferenceKind,
                    candidate.SymbolName,
                    candidate.ContainerKind,
                    candidate.ContainerName);
                if (seen.Add(key))
                    TryAddReference(merged, candidate);
            }
        }
    }

}
