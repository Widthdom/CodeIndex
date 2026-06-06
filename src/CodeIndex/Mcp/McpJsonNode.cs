using System.Text.Json.Nodes;

namespace CodeIndex.Mcp;

internal static class McpJsonNode
{
    public static JsonNode? Clone(JsonNode? node)
        => node?.DeepClone();
}
