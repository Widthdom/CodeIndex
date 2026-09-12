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
                if (rows.Count == 0 || !IsPartialTerminal(rows[^1]))
                    return null;
                var resultCount = 0;
                var emptyControls = 0;
                foreach (var row in rows.Take(rows.Count - 1).Cast<JsonObject>())
                {
                    if (IsDiagnosticControl(row))
                        continue;
                    if (IsEmptyControl(row))
                    {
                        emptyControls++;
                        continue;
                    }
                    if (!IsResultRow(row, command))
                        return null;
                    resultCount++;
                }
                return emptyControls <= 1 && (emptyControls == 0 || resultCount == 0)
                    && TerminalCount(rows[^1]!.AsObject()) == resultCount ? rows : null;
            }

            if (ParseNode(stdout) is not JsonObject root || !ValidRecord(root, command))
                return null;
            if (root["metadata"] is JsonObject metadata)
            {
                if (!ValidRecord(metadata, command)
                    || !IsExit11(metadata["exit_code"])
                    || root["results"] is not JsonArray results
                    || ReadCount(metadata["result_count"]) != results.Count
                    || results.Any(row => row is not JsonObject item || !ValidRecord(item, command)))
                    return null;
                if (metadata["stream_terminal"] is JsonObject terminal)
                {
                    if (!ValidRecord(terminal, command) || !IsPartialTerminal(terminal))
                        return null;
                    var logicalCount = command == "find" && results.Count == 1 && results[0] is JsonObject countRow
                        && !IsResultRow(countRow, command) && ReadCount(countRow["count"]).HasValue
                        ? ReadCount(countRow["count"]) : results.Count;
                    if (TerminalCount(terminal) == logicalCount)
                        return root;
                    // Bounded envelopes can project/trim rows. Their result_count
                    // describes the capture; stream_terminal retains the inner scan.
                    return IsTrue(metadata, "truncated")
                        && ReadCount(metadata["returned_count"]) == results.Count
                        && TerminalCount(terminal) > logicalCount ? root : null;
                }
                return results.Count == 1 && results[0] is JsonObject count
                    && IsPartialCount(count, command) ? root : null;
            }
            return IsPartialCount(root, command) ? root : null;
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
        => node is JsonObject terminal && IsTrue(node, "terminal_record")
            && (IsTrue(node, "partial_result") || IsTrue(node, "interrupted"))
            && TerminalCount(terminal).HasValue;

    private static long? TerminalCount(JsonObject terminal)
    {
        var count = ReadCount(terminal.ContainsKey("returned_count") ? terminal["returned_count"] : terminal["count"]);
        return terminal.ContainsKey("count") && ReadCount(terminal["count"]) != count ? null : count;
    }

    private static long? ReadCount(JsonNode? node)
        => node is JsonValue value && value.TryGetValue<long>(out var count) && count >= 0 ? count : null;

    private static bool IsPartialCount(JsonObject record, string command)
        => command == "find" && ReadCount(record["count"]).HasValue
            && (IsPartialTerminal(record) && TerminalCount(record) == ReadCount(record["count"])
                || record["terminal_record"] is null && IsTrue(record, "partial_result")
                    && record["authoritative_count"] is JsonValue authority
                    && authority.TryGetValue<bool>(out var authoritative) && !authoritative);

    private static bool IsEmptyControl(JsonObject record)
        => !IsPath(record["path"]) && !IsPath(record["file"])
            && ReadCount(record["count"]) == 0 && record["results"] is JsonArray { Count: 0 };

    private static bool IsDiagnosticControl(JsonObject record)
        => record.Count == 1 && (record["_debug"] is JsonObject || record["profile"] is JsonObject);

    private static bool IsResultRow(JsonObject record, string command)
        => (IsPath(record["path"]) || IsPath(record["file"]))
            && (command != "find" || ReadCount(record["line"]) > 0 && ReadCount(record["column"]) > 0);

    private static bool IsPath(JsonNode? node)
        => node is JsonValue value && value.TryGetValue<string>(out var path) && !string.IsNullOrWhiteSpace(path);

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
