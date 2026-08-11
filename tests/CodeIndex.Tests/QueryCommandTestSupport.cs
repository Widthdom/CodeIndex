using System.Text.Json;
using CodeIndex.Database;

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

    internal static string CreateHotspotFamilyFixtureDb(string projectRoot, bool markHotspotFamilyReady)
    {
        var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
        TestProjectHelper.InsertIndexedFile(
            dbPath,
            "src/Api.Part1.cs",
            "csharp",
            """
            public partial class Api
            {
                public void Run() { }
            }
            """);
        TestProjectHelper.InsertIndexedFile(
            dbPath,
            "src/Api.Part2.cs",
            "csharp",
            """
            public partial class Api
            {
                public void Run(int value) { }
            }
            """);
        TestProjectHelper.InsertIndexedFile(
            dbPath,
            "src/Caller.cs",
            "csharp",
            """
            public class Caller
            {
                public void Call(Api api)
                {
                    api.Run();
                    api.Run(1);
                }
            }
            """);

        using var db = new DbContext(DbOpenIntent.WriteIndex, dbPath);
        var writer = new DbWriter(db.Connection);
        writer.MarkGraphReady();
        if (markHotspotFamilyReady)
            writer.MarkHotspotFamilyReady("csharp", "fixture-fingerprint");
        return dbPath;
    }

    private static bool IsJsonStreamDoneSentinel(JsonElement element)
        => element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty("done", out var done)
            && done.ValueKind is JsonValueKind.True
            && element.TryGetProperty("interrupted", out _)
            && element.TryGetProperty("count", out _);
}
