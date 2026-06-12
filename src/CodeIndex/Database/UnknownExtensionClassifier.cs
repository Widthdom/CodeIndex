using System.Text;
using System.Text.Json;

namespace CodeIndex.Database;

internal sealed record UnknownExtensionClassification(
    Dictionary<string, long> ExtensionCounts,
    Dictionary<string, long> CategoryCounts,
    List<StatusUnknownExtensionGroup> Groups);

internal static class UnknownExtensionClassifier
{
    internal const int MaxPersistedGroups = 128;
    internal const int MaxSamplePathsPerGroup = 5;
    private const int MaxDictionaryEntries = 256;
    private const int MaxRawJsonCharacters = 512 * 1024;
    private const int MaxDecodedStringCharacters = 64 * 1024;
    private const int MaxJsonDepth = 8;

    private static readonly JsonReaderOptions ReaderOptions = new()
    {
        MaxDepth = MaxJsonDepth,
    };

    public static UnknownExtensionClassification Classify(IReadOnlyList<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);

        var extensionCounts = new Dictionary<string, long>(StringComparer.Ordinal);
        var categoryCounts = new Dictionary<string, long>(StringComparer.Ordinal);
        var groups = new Dictionary<string, StatusUnknownExtensionGroup>(StringComparer.Ordinal);

        foreach (var path in paths.OrderBy(p => p, StringComparer.Ordinal))
        {
            if (string.IsNullOrWhiteSpace(path))
                continue;

            var extension = GetExtensionKey(path);
            var (category, action) = ClassifyPath(path, extension);
            Increment(extensionCounts, extension);
            Increment(categoryCounts, category);

            var groupKey = extension + "\0" + category + "\0" + action;
            if (!groups.TryGetValue(groupKey, out var group))
            {
                group = new StatusUnknownExtensionGroup
                {
                    Extension = extension,
                    Category = category,
                    RecommendedAction = action,
                    SamplePaths = [],
                };
                groups[groupKey] = group;
            }

            group.Count++;
            if (group.SamplePaths.Count < MaxSamplePathsPerGroup)
                group.SamplePaths.Add(path);
            else
                group.SamplePathsTruncated = true;
        }

        var orderedGroups = groups.Values
            .OrderByDescending(group => group.Count)
            .ThenBy(group => group.Category, StringComparer.Ordinal)
            .ThenBy(group => group.Extension, StringComparer.Ordinal)
            .Take(MaxPersistedGroups)
            .ToList();

        return new UnknownExtensionClassification(
            OrderCounts(extensionCounts),
            OrderCounts(categoryCounts),
            orderedGroups);
    }

    public static string SerializeCounts(IReadOnlyDictionary<string, long> counts)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            foreach (var (key, count) in counts.OrderBy(kv => kv.Key, StringComparer.Ordinal).Take(MaxDictionaryEntries))
                writer.WriteNumber(key, count);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    public static Dictionary<string, long>? DeserializeCounts(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw) || raw.Length > MaxRawJsonCharacters)
            return null;

        try
        {
            var utf8 = Encoding.UTF8.GetBytes(raw);
            var reader = new Utf8JsonReader(utf8, ReaderOptions);
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
                return null;

            var counts = new Dictionary<string, long>(StringComparer.Ordinal);
            var entries = 0;
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndObject)
                    return counts;
                if (reader.TokenType != JsonTokenType.PropertyName)
                    return null;

                var key = reader.GetString();
                if (string.IsNullOrWhiteSpace(key) || key.Length > MaxDecodedStringCharacters || !reader.Read())
                    return null;
                if (reader.TokenType != JsonTokenType.Number || !reader.TryGetInt64(out var value) || value < 0)
                {
                    if (reader.TokenType is JsonTokenType.StartArray or JsonTokenType.StartObject)
                        reader.Skip();
                    continue;
                }

                entries++;
                if (entries > MaxDictionaryEntries)
                    return null;
                counts[key] = value;
            }
        }
        catch (JsonException)
        {
            return null;
        }

        return null;
    }

    public static string SerializeGroups(IReadOnlyList<StatusUnknownExtensionGroup> groups)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartArray();
            foreach (var group in groups.Take(MaxPersistedGroups))
            {
                writer.WriteStartObject();
                writer.WriteString("extension", group.Extension);
                writer.WriteString("category", group.Category);
                writer.WriteString("recommended_action", group.RecommendedAction);
                writer.WriteNumber("count", group.Count);
                writer.WriteStartArray("sample_paths");
                foreach (var samplePath in group.SamplePaths.Take(MaxSamplePathsPerGroup))
                    writer.WriteStringValue(samplePath);
                writer.WriteEndArray();
                writer.WriteBoolean("sample_paths_truncated", group.SamplePathsTruncated);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    public static List<StatusUnknownExtensionGroup>? DeserializeGroups(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw) || raw.Length > MaxRawJsonCharacters)
            return null;

        try
        {
            var utf8 = Encoding.UTF8.GetBytes(raw);
            var reader = new Utf8JsonReader(utf8, ReaderOptions);
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartArray)
                return null;

            var groups = new List<StatusUnknownExtensionGroup>();
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndArray)
                    return groups;
                if (reader.TokenType != JsonTokenType.StartObject)
                {
                    reader.Skip();
                    continue;
                }

                var group = ReadGroup(ref reader);
                if (group != null)
                {
                    groups.Add(group);
                    if (groups.Count > MaxPersistedGroups)
                        return null;
                }
            }
        }
        catch (JsonException)
        {
            return null;
        }

        return null;
    }

    private static StatusUnknownExtensionGroup? ReadGroup(ref Utf8JsonReader reader)
    {
        string? extension = null;
        string? category = null;
        string? recommendedAction = null;
        long? count = null;
        var samplePaths = new List<string>();
        var samplePathsTruncated = false;

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
            {
                if (extension == null || category == null || recommendedAction == null || count == null)
                    return null;
                return new StatusUnknownExtensionGroup
                {
                    Extension = extension,
                    Category = category,
                    RecommendedAction = recommendedAction,
                    Count = count.Value,
                    SamplePaths = samplePaths,
                    SamplePathsTruncated = samplePathsTruncated,
                };
            }

            if (reader.TokenType != JsonTokenType.PropertyName)
                return null;
            var propertyName = reader.GetString();
            if (!reader.Read())
                return null;

            switch (propertyName)
            {
                case "extension" when reader.TokenType == JsonTokenType.String:
                    extension = ReadBoundedString(ref reader);
                    break;
                case "category" when reader.TokenType == JsonTokenType.String:
                    category = ReadBoundedString(ref reader);
                    break;
                case "recommended_action" when reader.TokenType == JsonTokenType.String:
                    recommendedAction = ReadBoundedString(ref reader);
                    break;
                case "count" when reader.TokenType == JsonTokenType.Number && reader.TryGetInt64(out var parsedCount) && parsedCount >= 0:
                    count = parsedCount;
                    break;
                case "sample_paths" when reader.TokenType == JsonTokenType.StartArray:
                    samplePaths = ReadSamplePaths(ref reader);
                    break;
                case "sample_paths_truncated" when reader.TokenType is JsonTokenType.True or JsonTokenType.False:
                    samplePathsTruncated = reader.GetBoolean();
                    break;
                default:
                    if (reader.TokenType is JsonTokenType.StartArray or JsonTokenType.StartObject)
                        reader.Skip();
                    break;
            }
        }

        return null;
    }

    private static List<string> ReadSamplePaths(ref Utf8JsonReader reader)
    {
        var values = new List<string>();
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndArray)
                return values;
            if (reader.TokenType != JsonTokenType.String)
            {
                if (reader.TokenType is JsonTokenType.StartArray or JsonTokenType.StartObject)
                    reader.Skip();
                continue;
            }
            if (values.Count >= MaxSamplePathsPerGroup)
                continue;
            var value = ReadBoundedString(ref reader);
            if (!string.IsNullOrWhiteSpace(value))
                values.Add(value);
        }

        return values;
    }

    private static string? ReadBoundedString(ref Utf8JsonReader reader)
    {
        var value = reader.GetString();
        return value is { Length: <= MaxDecodedStringCharacters } ? value : null;
    }

    private static string GetExtensionKey(string path)
    {
        var fileName = Path.GetFileName(path.Replace('\\', '/'));
        if (string.IsNullOrWhiteSpace(fileName))
            return "<none>";
        var dotCount = fileName.Count(c => c == '.');
        if (fileName.StartsWith(".", StringComparison.Ordinal) && dotCount == 1)
            return fileName.ToLowerInvariant();
        var extension = Path.GetExtension(fileName);
        return string.IsNullOrWhiteSpace(extension)
            ? "<none>"
            : extension.ToLowerInvariant();
    }

    private static (string Category, string RecommendedAction) ClassifyPath(string path, string extension)
    {
        var normalizedPath = path.Replace('\\', '/');
        var fileName = Path.GetFileName(normalizedPath).ToLowerInvariant();
        var lowerPath = normalizedPath.ToLowerInvariant();

        if (fileName is ".git" or ".gitattributes" or ".gitkeep" || lowerPath.StartsWith(".git/", StringComparison.Ordinal))
            return ("repository_metadata", "ignore_configuration");
        if (lowerPath.StartsWith("licenses/", StringComparison.Ordinal) || fileName.Contains("license", StringComparison.Ordinal))
            return ("license", "ignore_configuration");
        if (extension is ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp" or ".ico" or ".svg" or ".pdf" or ".zip" or ".gz" or ".tar" or ".woff" or ".woff2" or ".ttf")
            return ("binary_asset", "ignore_configuration");
        if (extension is ".config" or ".runsettings" or ".rules" or ".props" or ".targets" || fileName.EndsWith(".config", StringComparison.Ordinal))
            return ("configuration", "ignore_configuration");
        if (extension is ".sln" or ".manifest" or ".lock" or ".assets")
            return ("structural_metadata", "first_class_structural_extraction");
        return ("language_support_candidate", "language_support");
    }

    private static void Increment(Dictionary<string, long> counts, string key)
        => counts[key] = counts.TryGetValue(key, out var current) ? current + 1 : 1;

    private static Dictionary<string, long> OrderCounts(Dictionary<string, long> counts)
        => counts
            .OrderByDescending(kv => kv.Value)
            .ThenBy(kv => kv.Key, StringComparer.Ordinal)
            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
}
