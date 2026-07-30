using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using CodeIndex.Mcp;

namespace CodeIndex.Tests;

public partial class McpServerTests
{
    [Fact]
    public void ToolsList_OutputSchemasValidateActualSuccessEmptyPartialAndTypedError_Issue4898()
    {
        InsertIndexedFile(
            "src/output-schema-a.cs",
            "csharp",
            "public class OutputSchemaA { public void Issue4898OutputSchemaMarker() { } }");
        InsertIndexedFile(
            "src/output-schema-b.cs",
            "csharp",
            "public class OutputSchemaB { public void Issue4898OutputSchemaMarker() { } }");

        var listResponse = _server.HandleMessage(JsonNode.Parse(
            """{"jsonrpc":"2.0","id":1,"method":"tools/list"}""")!)!;
        var schemas = listResponse["result"]!["tools"]!.AsArray()
            .ToDictionary(
                tool => tool!["name"]!.GetValue<string>(),
                tool => tool!["outputSchema"]!.AsObject(),
                StringComparer.Ordinal);

        var success = CallToolForStructuredContent("ping", new JsonObject());
        var empty = CallToolForStructuredContent(
            "definition",
            new JsonObject { ["query"] = "Issue4898DefinitelyMissingSymbol" });
        var partial = CallToolForStructuredContent(
            "search",
            new JsonObject
            {
                ["query"] = "Issue4898OutputSchemaMarker",
                ["limit"] = 1,
            });
        var typedError = CallToolForStructuredContent("search", new JsonObject());

        Assert.Empty(empty["results"]!.AsArray());
        Assert.True(partial["truncated"]!.GetValue<bool>());
        Assert.Equal(McpErrorEnvelope.CategoryInvalidArgument, typedError["category"]!.GetValue<string>());

        Assert.True(MatchesSchema(success, schemas["ping"], schemas["ping"]), success.ToJsonString());
        Assert.True(MatchesSchema(empty, schemas["definition"], schemas["definition"]), empty.ToJsonString());
        Assert.True(MatchesSchema(partial, schemas["search"], schemas["search"]), partial.ToJsonString());
        Assert.True(MatchesSchema(typedError, schemas["search"], schemas["search"]), typedError.ToJsonString());
    }

    private JsonObject CallToolForStructuredContent(string toolName, JsonObject arguments)
    {
        var request = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 1,
            ["method"] = "tools/call",
            ["params"] = new JsonObject
            {
                ["name"] = toolName,
                ["arguments"] = arguments,
            },
        };

        var response = _server.HandleMessage(request)!;
        return response["result"]!["structuredContent"]!.AsObject();
    }

    private static bool MatchesSchema(JsonNode? instance, JsonObject schema, JsonObject root)
    {
        if (schema["$ref"] is JsonValue reference)
        {
            var prefix = "#/$defs/";
            var referenceText = reference.GetValue<string>();
            if (!referenceText.StartsWith(prefix, StringComparison.Ordinal))
                return false;
            var definitionName = referenceText[prefix.Length..];
            return root["$defs"]?[definitionName] is JsonObject definition
                && MatchesSchema(instance, definition, root);
        }

        if (schema["oneOf"] is JsonArray alternatives
            && alternatives.Count(alternative => MatchesSchema(instance, alternative!.AsObject(), root)) != 1)
        {
            return false;
        }

        if (schema["allOf"] is JsonArray requirements
            && requirements.Any(requirement => !MatchesSchema(instance, requirement!.AsObject(), root)))
        {
            return false;
        }

        if (schema["not"] is JsonObject exclusion && MatchesSchema(instance, exclusion, root))
            return false;

        if (schema["type"] is JsonValue type
            && !MatchesSchemaType(instance, type.GetValue<string>()))
        {
            return false;
        }

        if (schema["const"] is JsonNode constant && !JsonNode.DeepEquals(instance, constant))
            return false;

        if (schema["required"] is JsonArray required)
        {
            if (instance is not JsonObject requiredObject
                || required.Any(property => !requiredObject.ContainsKey(property!.GetValue<string>())))
            {
                return false;
            }
        }

        if (schema["properties"] is JsonObject properties && instance is JsonObject instanceObject)
        {
            foreach (var property in properties)
            {
                if (instanceObject.TryGetPropertyValue(property.Key, out var value)
                    && !MatchesSchema(value, property.Value!.AsObject(), root))
                {
                    return false;
                }
            }
        }

        if (schema["items"] is JsonObject items && instance is JsonArray array
            && array.Any(item => !MatchesSchema(item, items, root)))
        {
            return false;
        }

        if (schema["minimum"] is JsonValue minimum
            && (!TryGetSchemaNumber(instance, out var actual)
                || !TryGetSchemaNumber(minimum, out var lowerBound)
                || actual < lowerBound))
        {
            return false;
        }

        return true;
    }

    private static bool MatchesSchemaType(JsonNode? instance, string type)
        => type switch
        {
            "null" => instance is null,
            "object" => instance is JsonObject,
            "array" => instance is JsonArray,
            "string" => instance is JsonValue stringValue
                && stringValue.GetValueKind() == JsonValueKind.String,
            "boolean" => instance is JsonValue booleanValue
                && booleanValue.GetValueKind() is JsonValueKind.True or JsonValueKind.False,
            "number" => TryGetSchemaNumber(instance, out _),
            "integer" => TryGetSchemaNumber(instance, out var number)
                && decimal.Truncate(number) == number,
            _ => false,
        };

    private static bool TryGetSchemaNumber(JsonNode? node, out decimal number)
    {
        number = default;
        return node is JsonValue value
            && value.GetValueKind() == JsonValueKind.Number
            && decimal.TryParse(
                value.ToJsonString(),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out number);
    }
}
