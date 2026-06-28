using System.Text;
using System.Text.Json;

namespace CodeIndex;

internal static class WorkerProtocolJsonValidator
{
    internal const int DefaultMaxJsonDepth = 32;
    private const int DefaultMaxJsonProperties = 1_000_000;
    internal static int? MaxJsonDepthForTesting { get; set; }
    internal static int? MaxJsonPropertiesForTesting { get; set; }
    internal static int? MaxStringCharactersForTesting { get; set; }

    internal static JsonSerializerOptions CreateSerializerOptions(JsonSerializerOptions options)
        => new(options) { MaxDepth = DefaultMaxJsonDepth };

    internal static bool TryValidate(string json, int maxStringCharacters, out string error)
    {
        var maxDepth = ResolveMaxJsonDepth();
        var maxProperties = MaxJsonPropertiesForTesting ?? DefaultMaxJsonProperties;
        var effectiveMaxStringCharacters = MaxStringCharactersForTesting ?? maxStringCharacters;
        var propertyCount = 0;
        if (IsPayloadOverLimit(json, maxStringCharacters))
        {
            error = SafeDiagnosticFormatter.FormatCategoryType("worker_protocol_error", "json_payload_length_exceeded");
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = maxDepth });
            ValidateElement(document.RootElement, maxProperties, effectiveMaxStringCharacters, ref propertyCount, out error);
            return error.Length == 0;
        }
        catch (JsonException)
        {
            error = SafeDiagnosticFormatter.FormatCategoryType("worker_protocol_error", nameof(JsonException));
            return false;
        }
    }

    private static int ResolveMaxJsonDepth()
    {
        var maxDepth = MaxJsonDepthForTesting ?? DefaultMaxJsonDepth;
        return maxDepth > 0 ? maxDepth : DefaultMaxJsonDepth;
    }

    private static bool IsPayloadOverLimit(string json, int maxCharactersAndUtf8Bytes)
    {
        if (maxCharactersAndUtf8Bytes <= 0)
            return true;
        if (json.Length > maxCharactersAndUtf8Bytes)
            return true;

        return Encoding.UTF8.GetByteCount(json) > maxCharactersAndUtf8Bytes;
    }

    private static void ValidateElement(
        JsonElement element,
        int maxProperties,
        int maxStringCharacters,
        ref int propertyCount,
        out string error)
    {
        error = string.Empty;
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    propertyCount++;
                    if (propertyCount > maxProperties)
                    {
                        error = SafeDiagnosticFormatter.FormatCategoryType("worker_protocol_error", "json_property_limit_exceeded");
                        return;
                    }

                    ValidateElement(property.Value, maxProperties, maxStringCharacters, ref propertyCount, out error);
                    if (error.Length != 0)
                        return;
                }

                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    ValidateElement(item, maxProperties, maxStringCharacters, ref propertyCount, out error);
                    if (error.Length != 0)
                        return;
                }

                break;
            case JsonValueKind.String:
                if ((element.GetString()?.Length ?? 0) > maxStringCharacters)
                    error = SafeDiagnosticFormatter.FormatCategoryType("worker_protocol_error", "json_string_length_exceeded");
                break;
        }
    }
}
