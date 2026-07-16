using System.Text.Json.Serialization;
using CodeIndex.Database;

namespace CodeIndex.Models;

public sealed record UpdateCheckResult(
    [property: JsonPropertyName("current_version")] string CurrentVersion,
    [property: JsonPropertyName("latest_version")] string? LatestVersion,
    [property: JsonPropertyName("update_available")] bool UpdateAvailable,
    [property: JsonPropertyName("from_cache")] bool FromCache,
    [property: JsonPropertyName("error")] string? Error,
    [property: JsonPropertyName("error_category")] string? ErrorCategory = null,
    [property: JsonPropertyName("error_hint")] string? ErrorHint = null,
    [property: JsonPropertyName("api_version")] string ApiVersion = JsonOutputContract.ApiVersion) : IVersionedJsonResult;
