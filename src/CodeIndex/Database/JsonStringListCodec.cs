using System.Text;
using System.Text.Json;

namespace CodeIndex.Database;

internal static class JsonStringListCodec
{
    internal const int MaxRawJsonCharacters = 512 * 1024;
    internal const int MaxJsonDepth = 4;
    internal const int MaxArrayItems = 1024;
    internal const int MaxDecodedStringCharacters = 64 * 1024;

    private const int MaxJsonStringBytesPerDecodedChar = 6;

    private static readonly JsonReaderOptions ReaderOptions = new()
    {
        MaxDepth = MaxJsonDepth,
    };

    public static string Serialize(IReadOnlyList<string> values)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartArray();
            foreach (var value in values)
                writer.WriteStringValue(value);
            writer.WriteEndArray();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    internal static List<string> TakeSerializableSample(IReadOnlyList<string> values, int maxItems)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (maxItems <= 0)
            return [];

        var itemLimit = Math.Min(maxItems, MaxArrayItems);
        var sample = new List<string>(Math.Min(values.Count, itemLimit));
        var decodedCharacters = 0;
        foreach (var value in values)
        {
            if (sample.Count >= itemLimit)
                break;

            if (value.Length > MaxDecodedStringCharacters - decodedCharacters)
                break;

            decodedCharacters += value.Length;
            sample.Add(value);
        }

        return sample;
    }

    public static List<string>? Deserialize(string? raw)
        => Deserialize(raw, out _);

    internal static List<string>? Deserialize(string? raw, out string? diagnostic)
    {
        diagnostic = null;
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        if (raw.Length > MaxRawJsonCharacters)
            return Reject("json_string_list_raw_too_large", out diagnostic);

        try
        {
            var utf8 = Encoding.UTF8.GetBytes(raw);
            var reader = new Utf8JsonReader(utf8, ReaderOptions);
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartArray)
                return Reject("json_string_list_not_array", out diagnostic);

            var values = new List<string>();
            var itemCount = 0;
            var decodedCharacters = 0;
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndArray)
                {
                    if (reader.Read())
                        return Reject("json_string_list_trailing_json", out diagnostic);
                    return values;
                }

                itemCount++;
                if (itemCount > MaxArrayItems)
                    return Reject("json_string_list_too_many_items", out diagnostic);

                if (reader.TokenType != JsonTokenType.String)
                {
                    if (reader.TokenType is JsonTokenType.StartArray or JsonTokenType.StartObject)
                        reader.Skip();
                    continue;
                }

                var remainingCharacters = MaxDecodedStringCharacters - decodedCharacters;
                if (ExceedsStringByteBudget(ref reader, remainingCharacters))
                    return Reject("json_string_list_too_many_characters", out diagnostic);

                var value = reader.GetString();
                if (value == null)
                    continue;
                if (value.Length > remainingCharacters)
                    return Reject("json_string_list_too_many_characters", out diagnostic);

                decodedCharacters += value.Length;
                if (!string.IsNullOrWhiteSpace(value))
                    values.Add(value);
            }

            return Reject("json_string_list_incomplete", out diagnostic);
        }
        catch (JsonException)
        {
            diagnostic = "json_string_list_malformed";
            return null;
        }
    }

    private static bool ExceedsStringByteBudget(ref Utf8JsonReader reader, int remainingCharacters)
    {
        if (remainingCharacters < 0)
            return true;

        var byteBudget = (long)remainingCharacters * MaxJsonStringBytesPerDecodedChar;
        return reader.HasValueSequence
            ? reader.ValueSequence.Length > byteBudget
            : reader.ValueSpan.Length > byteBudget;
    }

    private static List<string>? Reject(string reason, out string? diagnostic)
    {
        diagnostic = reason;
        return null;
    }
}
