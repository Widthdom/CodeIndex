using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;

namespace CodeIndex.Cli;

internal static class CommandOutputWriter
{
    private static readonly AsyncLocal<TextWriter?> ScopedOutput = new();

    private static TextWriter Output => ScopedOutput.Value ?? Console.Out;

    internal static IDisposable Push(TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(output);
        var previous = ScopedOutput.Value;
        ScopedOutput.Value = output;
        return new OutputScope(previous);
    }

    internal static void WriteLine(string message = "")
        => Output.WriteLine(message);

    internal static void WriteJson<T>(T value, JsonTypeInfo<T> jsonTypeInfo)
        => Output.WriteLine(JsonSerializer.Serialize(value, jsonTypeInfo));

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

        Output.WriteLine(Encoding.UTF8.GetString(stream.ToArray()));
    }

    internal static void WriteRawJson(string json)
        => Output.WriteLine(json);

    private sealed class OutputScope(TextWriter? previous) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
                return;

            ScopedOutput.Value = previous;
            _disposed = true;
        }
    }
}
