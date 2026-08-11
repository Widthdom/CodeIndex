using System.Text.Json;

namespace CodeIndex.Tests;

internal static class QueryCommandTestSupport
{
    internal static JsonSerializerOptions JsonOptions { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    internal static (int Result, string Stdout, string Stderr) CaptureConsole(Func<int> action)
        => ConsoleCapture.Capture(action);

    internal static JsonDocument ParseJsonOutput(string stdout)
    {
        var jsonLine = stdout
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Last(line =>
            {
                using var document = JsonDocument.Parse(line);
                return !IsJsonStreamDoneSentinel(document.RootElement);
            });
        return JsonDocument.Parse(jsonLine);
    }

    internal static List<JsonDocument> ParseJsonLines(string stdout)
        => stdout
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(static line => JsonDocument.Parse(line))
            .Where(document => !IsJsonStreamDoneSentinel(document.RootElement))
            .ToList();

    private static bool IsJsonStreamDoneSentinel(JsonElement element)
        => element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty("done", out var done)
            && done.ValueKind is JsonValueKind.True
            && element.TryGetProperty("interrupted", out _)
            && element.TryGetProperty("count", out _);
}
