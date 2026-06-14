using System.Text.Json.Serialization;

namespace CodeIndex.Models;

public sealed record UpdateCheckResult(
    [property: JsonPropertyName("current_version")] string CurrentVersion,
    [property: JsonPropertyName("latest_version")] string? LatestVersion,
    [property: JsonPropertyName("update_available")] bool UpdateAvailable,
    [property: JsonPropertyName("from_cache")] bool FromCache,
    [property: JsonPropertyName("error")] string? Error,
    [property: JsonPropertyName("error_category")] string? ErrorCategory = null,
    [property: JsonPropertyName("error_hint")] string? ErrorHint = null);
