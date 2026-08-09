namespace CodeIndex.Indexer;

internal readonly record struct NormalizedChunkSlice(int StartOffset, int Length);

internal readonly record struct NormalizedContentFacts(
    int LineCount,
    int FirstOversizeLine,
    int ConflictMarkerLine,
    int ReplacementCharacterCount,
    int[]? ReplacementCharacterLines,
    int FirstOversizeFtsTokenLine,
    NormalizedChunkSlice[]? ChunkSlices)
{
    internal bool HasOversizeLine => FirstOversizeLine > 0;

    internal static NormalizedContentFacts Empty => new(
        0,
        0,
        0,
        0,
        null,
        0,
        null);
}
