using CodeIndex.Models;

namespace CodeIndex.Indexer;

internal readonly record struct LoadedFileRecord(
    FileRecord Record,
    string Content,
    byte[] RawBytes,
    NormalizedContentFacts Facts,
    string? Warning,
    FileContentInspection Inspection,
    FileIndexer.LanguageDetectionResult LanguageDetection)
{
    internal bool HasOversizeLine => Facts.HasOversizeLine;
    internal int ConflictMarkerLine => Facts.ConflictMarkerLine;
}
