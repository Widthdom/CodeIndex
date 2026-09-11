using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CodeIndex.Database;

namespace CodeIndex.Cli;

// Declaration navigation is independent of graph/SCC admission. The catalogue is a
// superset of graph nodes: every indexed C# type and every explicit file fallback.
internal static class DependencyCycleNavigation
{
    internal const int NodeLimit = 40;
    internal const int FileLimit = 20;
    internal const int DefaultMaxBytes = 65536;
    internal const int MaxNodeLength = 16384;
    private const string CursorPrefix = "deps-mapping:v1:";

    internal static string Generation(DbReader reader)
        => Hash("csharp-partial-type-v1\n" + reader.GetSymbolSelectorGenerationIdentity()
            + "\n" + reader.DependencyCycleGroupingReady);

    private static string Hash(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static string Cursor(string generation, string? node, int offset)
    {
        var binding = Hash(generation + "\n" + (node == null ? "nodes" : "files\n" + node));
        var body = offset.ToString(CultureInfo.InvariantCulture) + ":" + binding;
        return CursorPrefix + body + ":" + Hash(body)[..16];
    }

    private static int Offset(string? cursor, string generation, string? node)
    {
        if (cursor == null)
            return 0;
        if (cursor.Length > 256 || !cursor.StartsWith(CursorPrefix, StringComparison.Ordinal))
            throw new ArgumentException("Invalid mapping cursor; restart declaration navigation.");
        var parts = cursor[CursorPrefix.Length..].Split(':');
        if (parts.Length != 3 || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var offset)
            || offset <= 0 || !string.Equals(cursor, Cursor(generation, node, offset), StringComparison.Ordinal))
            throw new ArgumentException("Mapping cursor is invalid or stale, or belongs to another node/grouping/index generation. Restart declaration navigation.");
        return offset;
    }

    internal static JsonObject Describe(DbReader reader, string node, string generation, int offset = 0, int limit = FileLimit)
    {
        var mapping = reader.DescribeDependencyCycleNode(node, offset, limit);
        UpdateFiles(mapping, generation, offset);
        return mapping;
    }

    private static void UpdateFiles(JsonObject mapping, string generation, int offset)
    {
        var paths = mapping["files"]!.AsArray();
        var count = mapping["file_count"]!.GetValue<int>();
        var remaining = Math.Max(0, count - offset - paths.Count);
        mapping["file_offset"] = offset;
        mapping["files_returned"] = paths.Count;
        mapping["files_truncated"] = remaining > 0;
        mapping["files_omitted_count"] = remaining;
        mapping["next_file_cursor"] = remaining > 0
            ? Cursor(generation, mapping["id"]!.GetValue<string>(), checked(offset + paths.Count)) : null;
    }

    internal static JsonObject BuildPage(DbReader reader, string? node, string? generationToken,
        string? cursor, int limit, int? maxBytes, JsonSerializerOptions jsonOptions)
        => reader.RunInReadSnapshot(() => BuildPageInSnapshot(reader, node, generationToken, cursor, limit, maxBytes, jsonOptions));

    private static JsonObject BuildPageInSnapshot(DbReader reader, string? node, string? generationToken,
        string? cursor, int limit, int? maxBytes, JsonSerializerOptions jsonOptions)
    {
        jsonOptions = QueryCommandRunner.EnsureJsonNodeSerializerOptions(jsonOptions);
        if (!reader.DependencyCycleGroupingReady)
            throw new ArgumentException("Declaration navigation requires current C# family/reference metadata. Refresh the index and restart the grouped-cycle query.");
        var generation = Generation(reader);
        if (node != null && (node.Length is 0 or > MaxNodeLength
            || !(node.StartsWith("csharp-type:", StringComparison.Ordinal) || node.StartsWith("file:", StringComparison.Ordinal))))
            throw new ArgumentException("cycle-node must be a node ID emitted by grouped dependency cycles or node mappings.");
        if ((node != null && generationToken == null)
            || (generationToken != null && !string.Equals(generationToken, generation, StringComparison.Ordinal)))
            throw new ArgumentException("Missing or stale node-generation. Use the node_generation from the same grouped-cycle or mapping response as the node ID.");
        if (limit <= 0)
            throw new ArgumentException("Declaration navigation requires a positive limit to make pagination progress.");
        if (maxBytes is <= 0)
            throw new ArgumentException("Declaration navigation requires a positive byte budget.");
        var offset = Offset(cursor, generation, node);
        var pageLimit = Math.Min(limit, node == null ? NodeLimit : FileLimit);
        var mappings = new JsonArray();
        long count;
        if (node == null)
        {
            var page = reader.ListDependencyCycleMappingNodes(offset, pageLimit);
            count = page.Count;
            foreach (var id in page.Nodes)
                mappings.Add(Describe(reader, id, generation));
        }
        else
        {
            var mapping = Describe(reader, node, generation, offset, pageLimit);
            if (mapping["files"]!.AsArray().Count == 0)
                throw new ArgumentException("Node or declaration page does not exist in this generation. Restart declaration navigation.");
            count = mapping["file_count"]!.GetValue<int>();
            mappings.Add(mapping);
        }
        if (offset > 0 && mappings.Count == 0)
            throw new ArgumentException("Mapping cursor points beyond the available nodes. Restart declaration navigation.");
        var payload = new JsonObject
        {
            ["api_version"] = "1",
            ["node_generation"] = generation,
            ["grouping_mode"] = "csharp_partial_type",
            ["mapping_scope"] = "all_indexed_declarations_not_cycle_membership",
            ["pagination_dimension"] = node == null ? "nodes" : "declaration_files",
            ["node_mappings"] = mappings,
            ["total_count"] = count,
            ["page_offset"] = offset,
            ["page_limit"] = pageLimit,
            ["node_limit"] = NodeLimit,
            ["file_limit"] = FileLimit,
            ["byte_limited"] = false,
        };
        void UpdatePage()
        {
            var returned = node == null ? mappings.Count : mappings[0]!["files"]!.AsArray().Count;
            var remaining = Math.Max(0, count - offset - returned);
            payload["count"] = mappings.Count;
            payload["returned_count"] = returned;
            payload["remaining_count"] = remaining;
            payload["has_more"] = remaining > 0;
            payload["next_mapping_cursor"] = remaining > 0 ? Cursor(generation, node, checked(offset + returned)) : null;
        }
        UpdatePage();
        // Include the CLI newline; MCP applies the same budget to structuredContent.
        var budget = Math.Min(maxBytes ?? DefaultMaxBytes, DefaultMaxBytes);
        while (Encoding.UTF8.GetByteCount(payload.ToJsonString(jsonOptions)) + Encoding.UTF8.GetByteCount(Environment.NewLine) > budget)
        {
            reader.Cancellation.ThrowIfCancellationRequested();
            payload["byte_limited"] = true;
            if (mappings.Count > 1)
                mappings.RemoveAt(mappings.Count - 1);
            else if (mappings.Count == 1 && mappings[0]!["files"]!.AsArray().Count > 1)
            {
                var mapping = mappings[0]!.AsObject();
                var files = mapping["files"]!.AsArray();
                files.RemoveAt(files.Count - 1);
                UpdateFiles(mapping, generation, node == null ? 0 : offset);
            }
            else
            {
                var minimum = Encoding.UTF8.GetByteCount(payload.ToJsonString(jsonOptions)) + Encoding.UTF8.GetByteCount(Environment.NewLine);
                return CommandErrorWriter.BuildJsonPayload(jsonOptions,
                    "The declaration navigation budget cannot fit one mapping/path and its continuation metadata.",
                    CommandExitCodes.UsageError,
                    hint: "Increase --max-json-bytes (MCP: maxBytes), up to 65536. No page was consumed.",
                    errorCode: CommandErrorCodes.ResponseBudgetTooSmall, command: "deps",
                    additionalJsonProperties: new JsonObject { ["minimum_required_bytes"] = minimum, ["max_bytes"] = budget });
            }
            UpdatePage();
        }
        return payload;
    }
}
