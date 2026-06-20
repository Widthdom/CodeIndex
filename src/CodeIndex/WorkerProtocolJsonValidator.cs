using System.Text.Json;

namespace CodeIndex;

internal static class WorkerProtocolJsonValidator
{
    private const int DefaultMaxJsonProperties = 1_000_000;
    internal static int? MaxJsonPropertiesForTesting { get; set; }
    internal static int? MaxStringCharactersForTesting { get; set; }

    internal static bool TryValidate(string json, int maxStringCharacters, out string error)
    {
        var maxProperties = MaxJsonPropertiesForTesting ?? DefaultMaxJsonProperties;
        var effectiveMaxStringCharacters = MaxStringCharactersForTesting ?? maxStringCharacters;
        var propertyCount = 0;
        try
        {
            using var document = JsonDocument.Parse(json);
            ValidateElement(document.RootElement, maxProperties, effectiveMaxStringCharacters, ref propertyCount, out error);
            return error.Length == 0;
        }
        catch (JsonException ex)
        {
            error = SafeDiagnosticFormatter.FormatExceptionCategory("worker_protocol_error", ex);
            return false;
        }
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
