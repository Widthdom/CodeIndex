using System.Text.Json;
using System.Text.Json.Nodes;
using CodeIndex.Diagnostics;

namespace CodeIndex.Cli;

// Exit 11 alone is not proof that captured stdout contains a complete result contract.
internal static class BatchChildPartialResultParser
{
    internal static JsonNode? Parse(string stdout, string command, bool ndjson)
    {
        if (stdout.Length > JsonEnvelopeWrapper.MaxCapturedOutputChars || string.IsNullOrWhiteSpace(stdout))
            return null;

        try
        {
            JsonNode? ParseNode(string json) => BoundedJson.ParseNode(
                json, JsonEnvelopeWrapper.MaxCapturedOutputChars * 4, QueryCommandRunner.BatchMaxJsonDepth);

            if (ndjson)
            {
                var rows = new JsonArray();
                using var reader = new StringReader(stdout);
                while (reader.ReadLine() is { } line)
                {
                    if (string.IsNullOrWhiteSpace(line))
                        continue;
                    if (ParseNode(line) is not JsonObject row || !ValidRecord(row, command))
                        return null;
                    // A terminal followed by more output is an incomplete or mixed capture.
                    if (rows.Count > 0 && IsTrue(rows[^1]!, "terminal_record"))
                        return null;
                    rows.Add(row);
                }
                return rows.Count > 0 && IsPartialTerminal(rows[^1]) ? rows : null;
            }

            if (ParseNode(stdout) is not JsonObject root || !ValidRecord(root, command))
                return null;
            if (root["metadata"] is JsonObject metadata)
            {
                if (!ValidRecord(metadata, command)
                    || !IsExit11(metadata["exit_code"])
                    || root["results"] is not JsonArray results
                    || results.Any(row => row is not JsonObject item || !ValidRecord(item, command)))
                    return null;
                return metadata["stream_terminal"] is JsonObject terminal
                    && ValidRecord(terminal, command) && IsPartialTerminal(terminal) ? root : null;
            }
            return IsPartialTerminal(root) ? root : null;
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or InvalidOperationException or ArgumentException)
        {
            // Node materialization also detects duplicate keys and invalid Unicode escapes.
            return null;
        }
    }

    private static bool ValidRecord(JsonObject record, string command)
    {
        ValidateTree(record);
        return record["error"] is null && record["error_code"] is null
            && (record["status"] is null || record["status"]!.GetValue<string>() != "error")
            && (record["command"] is null
                || record["command"]!.GetValue<string>() == JsonEnvelopeWrapper.CanonicalizeCommandName(command))
            && (record["exit_code"] is null || IsExit11(record["exit_code"]));
    }

    private static bool IsExit11(JsonNode? value)
        => value is JsonValue number && number.TryGetValue<int>(out var exitCode)
            && exitCode == CommandExitCodes.PartialResult;

    private static bool IsPartialTerminal(JsonNode? node)
        => node is JsonObject && IsTrue(node, "terminal_record")
            && (IsTrue(node, "partial_result") || IsTrue(node, "interrupted"))
            && (IsCount(node["returned_count"]) || IsCount(node["count"]));

    private static bool IsCount(JsonNode? node)
        => node is JsonValue value && value.TryGetValue<long>(out var count) && count >= 0;

    private static bool IsTrue(JsonNode node, string name)
        => node[name] is JsonValue value && value.TryGetValue<bool>(out var flag) && flag;

    private static void ValidateTree(JsonNode? node)
    {
        if (node is JsonObject obj)
        {
            foreach (var property in obj)
                ValidateTree(property.Value);
        }
        else if (node is JsonArray array)
        {
            foreach (var item in array)
                ValidateTree(item);
        }
        else if (node is JsonValue value)
        {
            // Force lazy string decoding before retaining any part of the capture.
            value.TryGetValue<string>(out _);
        }
    }
}
