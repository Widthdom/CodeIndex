using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CodeIndex.Tests;

/// <summary>
/// Snapshot harness for JSON contract regression fixtures (issue #1548).
/// Normalizes volatile fields (timestamps, absolute paths, commit SHAs, versions, FTS5 scores)
/// so a snapshot diff catches shape drift (renamed fields, reordered arrays, new keys,
/// removed keys) without flagging legitimate per-run variance.
///
/// Update procedure: set the environment variable <c>UPDATE_SNAPSHOTS=1</c> and re-run the
/// target tests once; the helper rewrites the matching golden file in the source tree.
/// Review the diff before committing.
/// </summary>
internal static class JsonOutputSnapshotHelper
{
    public const string UpdateEnvironmentVariable = "UPDATE_SNAPSHOTS";

    private static readonly string GoldenDirectory = ResolveGoldenDirectory();

    private static readonly HashSet<string> TimestampKeys = new(StringComparer.Ordinal)
    {
        "indexed_at",
        "latest_modified",
        "indexed_head_timestamp",
        "workspace_latest_modified",
    };

    private static readonly HashSet<string> CommitShaKeys = new(StringComparer.Ordinal)
    {
        "git_head",
        "indexed_head_commit",
        "indexed_head_sha",
        "workspace_head_commit",
        "prior_indexed_head_commit",
        "current_head_commit",
    };

    private static readonly HashSet<string> VersionKeys = new(StringComparer.Ordinal)
    {
        "version",
        "index_writer_version",
    };

    private static readonly HashSet<string> ProjectRootKeys = new(StringComparer.Ordinal)
    {
        "project_root",
    };

    private static readonly HashSet<string> ScoreKeys = new(StringComparer.Ordinal)
    {
        "score",
    };

    public static void AssertMatches(
        string goldenName,
        string actualJson,
        IReadOnlyList<(string Original, string Placeholder)>? extraReplacements = null)
    {
        var canonical = Canonicalize(actualJson, extraReplacements);
        var goldenPath = Path.Combine(GoldenDirectory, goldenName);

        if (string.Equals(Environment.GetEnvironmentVariable(UpdateEnvironmentVariable), "1", StringComparison.Ordinal))
        {
            Directory.CreateDirectory(GoldenDirectory);
            File.WriteAllText(goldenPath, canonical);
            return;
        }

        if (!File.Exists(goldenPath))
        {
            throw new InvalidOperationException(
                $"Golden file not found: {goldenPath}. " +
                $"Audit the JSON shape, then re-run the test with {UpdateEnvironmentVariable}=1 to create it.");
        }

        var expected = File.ReadAllText(goldenPath).Replace("\r\n", "\n");
        Assert.Equal(expected, canonical);
    }

    public static string Canonicalize(
        string actualJson,
        IReadOnlyList<(string Original, string Placeholder)>? extraReplacements = null)
    {
        if (string.IsNullOrWhiteSpace(actualJson))
            throw new InvalidOperationException("JSON snapshot input was empty.");

        var lines = actualJson
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.TrimEnd('\r', ' ', '\t'))
            .Where(l => l.Length > 0 && (l[0] == '{' || l[0] == '['))
            .ToList();
        if (lines.Count == 0)
            throw new InvalidOperationException("JSON snapshot input contained no JSON-looking line.");

        var jsonNodes = lines.Select(line => JsonNode.Parse(line)
            ?? throw new InvalidOperationException("Failed to parse JSON snapshot line.")).ToList();

        foreach (var node in jsonNodes)
            Normalize(node);

        var pretty = jsonNodes.Count == 1
            ? jsonNodes[0]!.ToJsonString(new JsonSerializerOptions { WriteIndented = true })
            : string.Join("\n", jsonNodes.Select(n => n!.ToJsonString(new JsonSerializerOptions { WriteIndented = true })));

        if (extraReplacements != null)
        {
            foreach (var (original, placeholder) in extraReplacements)
            {
                if (!string.IsNullOrEmpty(original))
                    pretty = pretty.Replace(original, placeholder, StringComparison.Ordinal);
            }
        }

        return pretty.Replace("\r\n", "\n").TrimEnd() + "\n";
    }

    private static void Normalize(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var key in obj.Select(kv => kv.Key).ToList())
                {
                    var value = obj[key];
                    if (value is null)
                        continue;

                    if (TimestampKeys.Contains(key))
                        obj[key] = "<TIMESTAMP>";
                    else if (CommitShaKeys.Contains(key))
                        obj[key] = "<COMMIT_SHA>";
                    else if (VersionKeys.Contains(key))
                        obj[key] = "<VERSION>";
                    else if (ProjectRootKeys.Contains(key))
                        obj[key] = "<PROJECT_ROOT>";
                    else if (ScoreKeys.Contains(key) && value is JsonValue scoreValue && scoreValue.TryGetValue(out double score))
                        obj[key] = "<SCORE>"; // BM25 scores are SQLite-FTS5-implementation-sensitive; pin only the field's presence.
                    else
                        Normalize(value);
                }
                break;
            case JsonArray arr:
                foreach (var item in arr)
                    Normalize(item);
                break;
        }
    }

    private static string ResolveGoldenDirectory()
        => Path.Combine(GetThisDirectory(), "golden");

    private static string GetThisDirectory([CallerFilePath] string path = "")
        => Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException("Failed to resolve caller file path for golden directory.");
}
