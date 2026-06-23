using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;

namespace CodeIndex.Cli;

internal static class CommandOutputWriter
{
    internal static void WriteLine(string message = "")
        => Console.WriteLine(message);

    internal static void WriteJson<T>(T value, JsonTypeInfo<T> jsonTypeInfo)
        => Console.WriteLine(JsonSerializer.Serialize(value, jsonTypeInfo));

    internal static void WriteJsonNode(JsonNode node, JsonSerializerOptions jsonOptions)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions
        {
            Encoder = jsonOptions.Encoder,
            Indented = jsonOptions.WriteIndented,
        }))
        {
            node.WriteTo(writer);
        }

        Console.WriteLine(Encoding.UTF8.GetString(stream.ToArray()));
    }

    internal static void WriteRawJson(string json)
        => Console.WriteLine(json);
}
