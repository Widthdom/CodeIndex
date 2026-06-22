using CodeIndex.Models;

namespace CodeIndex.Indexer;

internal readonly record struct LoadedFileRecord(
    FileRecord Record,
    string Content,
    byte[] RawBytes,
    bool HasOversizeLine,
    string? Warning,
    FileContentInspection Inspection);
