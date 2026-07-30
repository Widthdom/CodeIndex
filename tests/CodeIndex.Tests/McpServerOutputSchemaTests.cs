using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using CodeIndex.Cli;
using CodeIndex.Database;
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
        InsertIndexedFile(
            "src/app.cs",
            "csharp",
            "public class App { public void Run() { } }");

        var listResponse = _server.HandleMessage(JsonNode.Parse(
            """{"jsonrpc":"2.0","id":1,"method":"tools/list"}""")!)!;
        var toolDefinitions = listResponse["result"]!["tools"]!.AsArray();
        var schemas = toolDefinitions
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

        foreach (var (toolName, schema) in schemas)
        {
            var actualError = CallToolForStructuredContent(
                toolName,
                new JsonObject { ["__issue4898_unknown_argument"] = true });
            Assert.Equal(JsonOutputContract.ApiVersion, actualError["api_version"]!.GetValue<string>());
            Assert.True(MatchesSchema(actualError, schema, schema), $"{toolName}: {actualError.ToJsonString()}");
            Assert.False(
                MatchesSchema(
                    new JsonObject { ["api_version"] = JsonOutputContract.ApiVersion },
                    schema,
                    schema),
                $"{toolName} accepted an incomplete success payload.");
        }

        foreach (var tool in toolDefinitions)
        {
            var toolName = tool!["name"]!.GetValue<string>();
            if (toolName == "index")
                continue; // The shared seeded server is intentionally not authorized to mutate its fixture root.
            var arguments = toolName switch
            {
                "backfill_fold" => new JsonObject { ["dry_run"] = true, ["force"] = false },
                "suggest_improvement" => new JsonObject
                {
                    ["category"] = "output_format",
                    ["description"] = "The response contract should remain easy for typed clients to consume.",
                    ["evidencePaths"] = new JsonArray { "src/app.cs" },
                },
                _ => tool["examples"]![0]!["request"]!["params"]!["arguments"]!.DeepClone().AsObject(),
            };
            var actualResult = CallToolForResult(toolName, arguments);
            Assert.True(
                actualResult["isError"]?.GetValue<bool>() != true,
                $"{toolName} returned an error: {actualResult.ToJsonString()}");
            var actualSuccess = actualResult["structuredContent"]!.AsObject();
            Assert.True(
                MatchesSchema(actualSuccess, schemas[toolName], schemas[toolName]),
                $"{toolName}: {actualSuccess.ToJsonString()}");
        }

        var indexRoot = Path.Combine(
            Environment.CurrentDirectory,
            "tests",
            "CodeIndex.Tests",
            "bin",
            $"cdidx-output-schema-{Guid.NewGuid():N}");
        Directory.CreateDirectory(indexRoot);
        try
        {
            File.WriteAllText(Path.Combine(indexRoot, "app.cs"), "public class IndexedApp { }");
            var indexDbPath = Path.Combine(indexRoot, ".cdidx", "codeindex.db");
            Directory.CreateDirectory(Path.GetDirectoryName(indexDbPath)!);
            using var indexServer = new McpServer(
                indexDbPath,
                ConsoleUi.LoadVersion(),
                dbPathExplicit: true);
            var indexResponse = CallIndex(
                indexServer,
                indexRoot,
                arguments => arguments["dryRun"] = true);
            Assert.True(
                indexResponse["result"]?["isError"]?.GetValue<bool>() != true,
                indexResponse.ToJsonString());
            var indexSuccess = indexResponse["result"]!["structuredContent"]!.AsObject();
            Assert.True(
                MatchesSchema(indexSuccess, schemas["index"], schemas["index"]),
                indexSuccess.ToJsonString());
        }
        finally
        {
            Directory.Delete(indexRoot, recursive: true);
        }

        var versionlessError = typedError.DeepClone().AsObject();
        versionlessError.Remove("api_version");
        Assert.False(
            MatchesSchema(versionlessError, schemas["search"], schemas["search"]),
            versionlessError.ToJsonString());
        Assert.False(
            MatchesSchema(success, schemas["search"], schemas["search"]),
            "The search schema accepted a ping result.");

        var analyzeProperties = schemas["analyze_symbol"]["$defs"]!["tool_result"]!["properties"]!.AsObject();
        Assert.NotNull(analyzeProperties["nearby_symbols"]);
        Assert.NotNull(analyzeProperties["graph_sections"]);
        Assert.Null(analyzeProperties["nearbySymbols"]);
        Assert.Null(analyzeProperties["graphSections"]);
        var batchProperties = schemas["batch_query"]["$defs"]!["tool_result"]!["properties"]!.AsObject();
        Assert.Null(batchProperties["estimated_response_bytes"]);
        Assert.NotNull(batchProperties["metadata"]!["properties"]!["estimated_response_bytes"]);

        var sharedDefinitions = schemas["search"]["$defs"]!.AsObject();
        Assert.Equal(10_000, sharedDefinitions["rows"]!["maxItems"]!.GetValue<int>());
        Assert.Equal(512, sharedDefinitions["row"]!["maxProperties"]!.GetValue<int>());
        Assert.Equal(
            McpServer.MaxConfiguredResponseBytes,
            sharedDefinitions["row"]!["properties"]!["path"]!["maxLength"]!.GetValue<int>());
    }

    private JsonObject CallToolForStructuredContent(string toolName, JsonObject arguments)
        => CallToolForResult(toolName, arguments)["structuredContent"]!.AsObject();

    private JsonObject CallToolForResult(string toolName, JsonObject arguments)
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
        return response["result"]!.AsObject();
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

        if (schema["anyOf"] is JsonArray choices
            && !choices.Any(choice => MatchesSchema(instance, choice!.AsObject(), root)))
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

        if (schema["maxItems"] is JsonValue maxItems
            && (instance is not JsonArray boundedArray
                || boundedArray.Count > maxItems.GetValue<int>()))
        {
            return false;
        }

        if (schema["maxLength"] is JsonValue maxLength
            && (instance is not JsonValue boundedString
                || boundedString.GetValueKind() != JsonValueKind.String
                || boundedString.GetValue<string>().Length > maxLength.GetValue<int>()))
        {
            return false;
        }

        if (schema["maxProperties"] is JsonValue maxProperties
            && (instance is not JsonObject boundedObject
                || boundedObject.Count > maxProperties.GetValue<int>()))
        {
            return false;
        }

        if (schema["propertyNames"] is JsonObject propertyNameSchema
            && instance is JsonObject namedObject
            && namedObject.Any(property =>
                !MatchesSchema(JsonValue.Create(property.Key), propertyNameSchema, root)))
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
