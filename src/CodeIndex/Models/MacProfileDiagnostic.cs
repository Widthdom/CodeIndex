using System.Text.Json.Serialization;

namespace CodeIndex.Models;

public sealed record MacProfileDiagnostic(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("category")] string Category,
    [property: JsonPropertyName("message")] string Message);
