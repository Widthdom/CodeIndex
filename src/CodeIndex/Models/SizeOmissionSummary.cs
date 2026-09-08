using System.Text.Json.Serialization;

namespace CodeIndex.Models;

public sealed record SizeOmissionFile(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("path_truncated")] bool PathTruncated,
    [property: JsonPropertyName("actual_bytes")] long? ActualBytes,
    [property: JsonPropertyName("limit_bytes")] long? LimitBytes);

public sealed class SizeOmissionSummary
{
    [JsonPropertyName("affected_file_count")]
    public long AffectedFileCount { get; init; }
    [JsonPropertyName("files")]
    public IReadOnlyList<SizeOmissionFile> Files { get; init; } = [];
    [JsonPropertyName("file_limit")]
    public int FileLimit { get; init; } = 20;
    [JsonPropertyName("files_truncated")]
    public bool FilesTruncated => AffectedFileCount > Files.Count;
    [JsonPropertyName("omitted_file_count")]
    public long OmittedFileCount => Math.Max(0, AffectedFileCount - Files.Count);
    [JsonPropertyName("recommended_action")]
    public string RecommendedAction { get; init; } =
        "Review the affected sources and explicitly set --max-file-bytes <bytes> (MCP: maxFileBytes) to an acceptable limit, then run normal indexing. Repeating the same limit cannot recover oversized input; no rebuild is required.";
    [JsonPropertyName("alternative_action")]
    public string AlternativeAction { get; init; } =
        "Deliberately exclude the affected paths in .cdidxignore and run a full workspace scan; excluded content will no longer be searchable. --allow-partial only accepts CLI exit 0 and does not restore completeness.";
}
