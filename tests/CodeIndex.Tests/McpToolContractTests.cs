using System.Reflection;
using System.Text.Json.Nodes;
using CodeIndex.Mcp;

namespace CodeIndex.Tests;

public class McpToolContractTests
{
    private static readonly MethodInfo GetAllowedToolArgumentsMethod =
        RequiredPrivateStaticMethod("GetAllowedToolArguments");

    private static readonly MethodInfo TryGetExpectedJsonTypeMethod =
        RequiredPrivateStaticMethod("TryGetExpectedJsonType");

    private static readonly HashSet<(string Tool, string Argument)> HiddenCompatibilityAliases =
    [
        ("backfill_fold", "dryRun"),
        ("suggest_improvement", "evidence_paths"),
    ];

    private static readonly HashSet<string> SpecializedListValidatedArguments = new(StringComparer.Ordinal)
    {
        "excludePaths",
        "names",
        "sections",
    };

    [Fact]
    public void ToolsList_AdvertisedInputPropertiesMatchArgumentAllowlist_Issue3199()
    {
        var advertisedSchemas = GetAdvertisedToolSchemas();
        var failures = new List<string>();

        foreach (var (toolName, properties) in advertisedSchemas.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            var advertised = properties.Keys.ToHashSet(StringComparer.Ordinal);
            var allowed = GetAllowedToolArguments(toolName);

            var advertisedButRejected = advertised.Except(allowed, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            var allowedButHidden = allowed
                .Except(advertised, StringComparer.Ordinal)
                .Where(argument => !HiddenCompatibilityAliases.Contains((toolName, argument)))
                .Order(StringComparer.Ordinal)
                .ToArray();

            if (advertisedButRejected.Length > 0 || allowedButHidden.Length > 0)
            {
                failures.Add(
                    $"{toolName}: advertised_but_rejected=[{string.Join(", ", advertisedButRejected)}]; "
                    + $"allowed_but_hidden=[{string.Join(", ", allowedButHidden)}]");
            }
        }

        Assert.True(
            failures.Count == 0,
            "MCP tools/list schema and argument allowlist drift detected:\n" + string.Join('\n', failures));
    }

    [Fact]
    public void ToolsList_AdvertisedInputPropertiesHaveMatchingTypeValidation_Issue3199()
    {
        var advertisedSchemas = GetAdvertisedToolSchemas();
        var failures = new List<string>();

        foreach (var (toolName, properties) in advertisedSchemas.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            foreach (var (argumentName, schema) in properties.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                var schemaType = ExpectedTypeFromSchema(schema);
                if (schemaType == null)
                {
                    failures.Add($"{toolName}.{argumentName}: unsupported schema shape");
                    continue;
                }

                if (SpecializedListValidatedArguments.Contains(argumentName))
                {
                    if (schemaType != "array")
                    {
                        failures.Add(
                            $"{toolName}.{argumentName}: schema={schemaType}; "
                            + "specialized_list_validator=array");
                    }

                    continue;
                }

                var (hasValidator, validatorType) = TryGetExpectedJsonType(toolName, argumentName);
                if (!hasValidator || validatorType != schemaType)
                {
                    failures.Add(
                        $"{toolName}.{argumentName}: schema={schemaType}; "
                        + $"validator={(hasValidator ? validatorType : "<missing>")}");
                }
            }
        }

        Assert.True(
            failures.Count == 0,
            "MCP tools/list schema and argument type validator drift detected:\n" + string.Join('\n', failures));
    }

    private static Dictionary<string, Dictionary<string, JsonObject>> GetAdvertisedToolSchemas()
    {
        using var server = new McpServer("unused.db", "test", dbPathExplicit: false, McpToolFilter.AllowAll());
        var request = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 1,
            ["method"] = "tools/list",
        };
        var response = server.HandleMessage(request)
            ?? throw new InvalidOperationException("tools/list returned no response.");

        var tools = response["result"]?["tools"]?.AsArray()
            ?? throw new InvalidOperationException("tools/list response did not contain result.tools.");
        var result = new Dictionary<string, Dictionary<string, JsonObject>>(StringComparer.Ordinal);

        foreach (var tool in tools)
        {
            var toolObject = tool?.AsObject()
                ?? throw new InvalidOperationException("tools/list returned a non-object tool entry.");
            var toolName = toolObject["name"]?.GetValue<string>()
                ?? throw new InvalidOperationException("tools/list returned a tool without a name.");
            var properties = toolObject["inputSchema"]?["properties"]?.AsObject()
                ?? throw new InvalidOperationException($"Tool '{toolName}' did not expose inputSchema.properties.");

            var propertySchemas = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
            foreach (var (propertyName, propertySchema) in properties)
            {
                propertySchemas[propertyName] = propertySchema?.AsObject()
                    ?? throw new InvalidOperationException($"Tool '{toolName}' property '{propertyName}' did not expose an object schema.");
            }

            result.Add(toolName, propertySchemas);
        }

        return result;
    }

    private static HashSet<string> GetAllowedToolArguments(string toolName)
    {
        var result = GetAllowedToolArgumentsMethod.Invoke(null, [toolName]);
        if (result is not IReadOnlySet<string> allowed)
            throw new InvalidOperationException("GetAllowedToolArguments did not return IReadOnlySet<string>.");
        return allowed.ToHashSet(StringComparer.Ordinal);
    }

    private static (bool HasValidator, string ValidatorType) TryGetExpectedJsonType(string toolName, string argumentName)
    {
        object?[] args = [toolName, argumentName, string.Empty];
        var hasValidator = (bool)(TryGetExpectedJsonTypeMethod.Invoke(null, args)
            ?? throw new InvalidOperationException("TryGetExpectedJsonType returned null."));
        return (hasValidator, (string)args[2]!);
    }

    private static string? ExpectedTypeFromSchema(JsonObject schema)
    {
        if (schema["oneOf"] is JsonArray oneOf)
        {
            var optionTypes = oneOf
                .Select(option => (option as JsonObject)?["type"]?.GetValue<string>())
                .Where(type => type != null)
                .Cast<string>()
                .ToHashSet(StringComparer.Ordinal);

            return optionTypes.SetEquals(["string", "array"]) ? "string_or_array" : null;
        }

        return schema["type"]?.GetValue<string>() switch
        {
            "array" => "array",
            "boolean" => "boolean",
            "integer" => "integer",
            "string" => "string",
            _ => null,
        };
    }

    private static MethodInfo RequiredPrivateStaticMethod(string name)
        => typeof(McpServer).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException($"McpServer.{name} was not found.");
}
