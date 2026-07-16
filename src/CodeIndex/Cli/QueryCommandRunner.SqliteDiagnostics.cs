using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using CodeIndex.Database;

namespace CodeIndex.Cli;

public static partial class QueryCommandRunner
{
    private static readonly AsyncLocal<DbReader?> ActiveSqliteDiagnosticsReader = new();

    private static void AddActiveSqliteDiagnostics(JsonObject payload)
    {
        var reader = ActiveSqliteDiagnosticsReader.Value;
        if (reader != null)
            AddReadOnlyFallbackDiagnostics(payload, reader);
    }

    private static void WriteActiveSqliteDiagnosticsProperties(TextWriter writer, JsonSerializerOptions jsonOptions)
    {
        var payload = new JsonObject();
        AddActiveSqliteDiagnostics(payload);
        if (payload.Count == 0)
            return;

        var json = payload.ToJsonString(jsonOptions);
        writer.Write(',');
        writer.Write(json.AsSpan(1, json.Length - 2));
    }

    private static string SerializeQueryJson<T>(T value, JsonTypeInfo<T> jsonTypeInfo, JsonSerializerOptions jsonOptions)
    {
        var reader = ActiveSqliteDiagnosticsReader.Value;
        if (reader == null || !reader.WalStaleSnapshotRisk)
            return JsonSerializer.Serialize(value, jsonTypeInfo);

        var node = JsonSerializer.SerializeToNode(value, jsonTypeInfo);
        AddSqliteDiagnostics(node, reader);
        return node?.ToJsonString(jsonOptions) ?? "null";
    }

    private static string AddActiveSqliteDiagnostics(string json)
    {
        var reader = ActiveSqliteDiagnosticsReader.Value;
        if (reader == null || !reader.WalStaleSnapshotRisk)
            return json;

        var node = JsonNode.Parse(json);
        AddSqliteDiagnostics(node, reader);
        return node?.ToJsonString() ?? json;
    }

    private static void AddSqliteDiagnostics(JsonNode? node, DbReader reader)
    {
        if (node is JsonObject payload)
        {
            AddReadOnlyFallbackDiagnostics(payload, reader);
            return;
        }

        if (node is JsonArray array)
        {
            foreach (var item in array)
            {
                if (item is JsonObject row)
                    AddReadOnlyFallbackDiagnostics(row, reader);
            }
        }
    }
}
