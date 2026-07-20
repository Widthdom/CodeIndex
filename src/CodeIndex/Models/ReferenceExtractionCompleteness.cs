using System.Text.Json.Serialization;

namespace CodeIndex.Models;

public sealed class ReferenceExtractionSafetyLimits
{
    [JsonPropertyName("max_lookup_symbols")]
    public int MaxLookupSymbols { get; init; }
    [JsonPropertyName("max_lookup_lines")]
    public int MaxLookupLines { get; init; }
    [JsonPropertyName("max_names_per_line")]
    public int MaxNamesPerLine { get; init; }
    [JsonPropertyName("max_container_candidates")]
    public int MaxContainerCandidates { get; init; }
}

public sealed class ReferenceExtractionCapHitSummary
{
    [JsonPropertyName("state_available")]
    public bool StateAvailable { get; init; } = true;
    [JsonPropertyName("hit_count")]
    public long HitCount { get; init; }
    [JsonPropertyName("affected_file_count")]
    public long AffectedFileCount { get; init; }
    [JsonPropertyName("reasons")]
    public List<string> Reasons { get; init; } = [];
    [JsonPropertyName("files")]
    public List<ReferenceExtractionFileCapHits> Files { get; init; } = [];
    [JsonPropertyName("files_truncated")]
    public bool FilesTruncated { get; init; }
    [JsonPropertyName("file_limit")]
    public int FileLimit { get; init; }
}

public sealed class ReferenceExtractionFileCapHits
{
    [JsonPropertyName("file")]
    public string File { get; init; } = string.Empty;
    [JsonPropertyName("hit_count")]
    public long HitCount { get; init; }
    [JsonPropertyName("reasons")]
    public List<string> Reasons { get; init; } = [];
}
