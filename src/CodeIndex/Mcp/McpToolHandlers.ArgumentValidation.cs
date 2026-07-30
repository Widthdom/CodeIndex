using System.Text.Json;
using System.Text.Json.Nodes;
using CodeIndex.Cli;
using CodeIndex.Database;
using CodeIndex.Indexer;

namespace CodeIndex.Mcp;

public partial class McpServer
{
    private static JsonObject? ValidateCommonListArguments(JsonNode? args)
    {
        foreach (var propertyName in new[] { "path", "project", "excludePaths", "names", "sections", "capability", "scopes", "visibility", "excludeVisibility", "includeSymbolKind", "excludeSymbolKind", "commits", "changedBetween", "files" })
        {
            if (ValidateStringListArgument(args, propertyName) is JsonObject error)
                return error;
        }

        return null;
    }

    private static JsonObject? ValidateToolArguments(string toolName, JsonNode? args)
    {
        if (!IsKnownToolName(toolName))
            return null;

        if (args is null)
            return null;
        if (args is not JsonObject obj)
            return new JsonObject
            {
                ["message"] = "Tool arguments must be a JSON object.",
                ["tool"] = toolName,
            };

        var allowed = GetAllowedToolArguments(toolName);
        if (allowed.Count == 0)
            return obj.Count == 0 ? null : AddUnknownArgumentData(
                new JsonObject
                {
                    ["message"] = $"Tool '{toolName}' does not accept arguments.",
                    ["tool"] = toolName,
                },
                toolName,
                obj.First().Key);

        foreach (var property in obj)
        {
            if (!allowed.Contains(property.Key))
            {
                return AddUnknownArgumentData(
                    new JsonObject
                    {
                        ["message"] = $"Unknown argument '{McpBoundedText.ForDisplay(property.Key).Text}' for tool '{toolName}'.",
                        ["tool"] = toolName,
                    },
                    toolName,
                    property.Key);
            }

        }

        if (ValidateToolArgumentTypes(toolName, obj) is JsonObject typeError)
            return typeError;

        if (ValidateToolArgumentRanges(toolName, obj) is JsonObject rangeError)
            return rangeError;

        if (ValidateBoundedEnumLikeScalarArguments(toolName, obj) is JsonObject scalarError)
            return scalarError;

        return null;
    }

    private static JsonObject? ValidateToolArgumentRanges(string toolName, JsonObject args)
    {
        if (args["limit"] is JsonValue limitValue
            && limitValue.TryGetValue<int>(out var limit)
            && limit <= 0)
            return CreateIntegerMinimumArgumentError(toolName, "limit", minimum: 1, actual: limit);

        if (args["offset"] is JsonValue offsetValue
            && offsetValue.TryGetValue<int>(out var offset)
            && offset < 0)
            return CreateIntegerMinimumArgumentError(toolName, "offset", minimum: 0, actual: offset);

        if (args["maxSymbolsPerFile"] is JsonValue maxSymbolsValue
            && maxSymbolsValue.TryGetValue<int>(out var maxSymbolsPerFile)
            && (maxSymbolsPerFile <= 0 || maxSymbolsPerFile > IndexCommandRunner.MaxSymbolsPerFileLimit))
            return CreateIntegerRangeArgumentError(toolName, "maxSymbolsPerFile", 1, IndexCommandRunner.MaxSymbolsPerFileLimit, maxSymbolsPerFile);

        if (args["parallelism"] is JsonValue parallelismValue
            && parallelismValue.TryGetValue<int>(out var parallelism)
            && (parallelism <= 0 || parallelism > IndexCommandRunner.MaxIndexParallelism))
            return CreateIntegerRangeArgumentError(toolName, "parallelism", 1, IndexCommandRunner.MaxIndexParallelism, parallelism);

        if (args["debounce"] is JsonValue debounceValue
            && debounceValue.TryGetValue<int>(out var debounce)
            && (debounce < 0 || debounce > IndexWatchRunner.MaxDebounceMs))
            return CreateIntegerRangeArgumentError(toolName, "debounce", 0, IndexWatchRunner.MaxDebounceMs, debounce);

        if (args["maxResponseBytes"] is JsonValue maxResponseBytesValue
            && maxResponseBytesValue.TryGetValue<int>(out var maxResponseBytes)
            && maxResponseBytes <= 0)
            return CreateIntegerMinimumArgumentError(toolName, "maxResponseBytes", minimum: 1, actual: maxResponseBytes);

        return null;
    }

    private static JsonObject CreateIntegerMinimumArgumentError(string toolName, string argumentName, int minimum, int actual) => new()
    {
        ["message"] = $"Argument '{argumentName}' on tool '{toolName}' must be greater than or equal to {minimum}; got {actual}.",
        ["tool"] = toolName,
        ["parameter"] = argumentName,
        ["minimum"] = minimum,
        ["actual"] = actual,
        ["jsonrpc_invalid_params"] = true,
    };

    private static JsonObject CreateIntegerRangeArgumentError(string toolName, string argumentName, int minimum, int maximum, int actual) => new()
    {
        ["message"] = $"Argument '{argumentName}' on tool '{toolName}' must be between {minimum} and {maximum}; got {actual}.",
        ["tool"] = toolName,
        ["parameter"] = argumentName,
        ["minimum"] = minimum,
        ["maximum"] = maximum,
        ["actual"] = actual,
        ["jsonrpc_invalid_params"] = true,
    };

    private static JsonObject? ValidateBoundedEnumLikeScalarArguments(string toolName, JsonObject args)
    {
        foreach (var property in args)
        {
            if (!BoundedEnumLikeScalarArguments.Contains(property.Key))
                continue;
            if (property.Value is not JsonValue value || !value.TryGetValue<string>(out var scalar))
                continue;
            if (scalar.Length <= McpBoundedText.MaxScalarArgumentChars)
                continue;

            var display = McpBoundedText.ForDisplay(scalar);
            var error = new JsonObject
            {
                ["message"] = $"Argument '{property.Key}' on tool '{toolName}' is too long (max {McpBoundedText.MaxScalarArgumentChars} characters): '{display.Text}'.",
                ["tool"] = toolName,
                ["parameter"] = property.Key,
                ["value"] = display.Text,
                ["max_length"] = McpBoundedText.MaxScalarArgumentChars,
                ["actual_length"] = display.OriginalLength,
            };
            display.AddMetadata(error, "value");
            return error;
        }

        return null;
    }

    private static JsonObject AddUnknownArgumentData(JsonObject error, string toolName, string argumentName)
    {
        var display = McpBoundedText.ForDisplay(argumentName);
        error["unknown_argument"] = display.Text;
        display.AddMetadata(error, "unknown_argument");
        return AddArgumentCompatibilityData(error, toolName, argumentName);
    }

    private static JsonObject AddArgumentCompatibilityData(JsonObject error, string toolName, string argumentName)
    {
        switch (toolName, argumentName)
        {
            case ("definition", "lspCompatible"):
            case ("references", "lspCompatible"):
                error["alias_of"] = "lsp_compatible";
                break;
            case ("search", "exact"):
                error["alias_of"] = "exactSubstring";
                error["deprecated"] = true;
                error["deprecation_reason"] = "Use `exactSubstring` for search exact substring matching.";
                break;
            case ("definition", "exact"):
            case ("references", "exact"):
            case ("callers", "exact"):
            case ("callees", "exact"):
            case ("symbols", "exact"):
            case ("analyze_symbol", "exact"):
                error["alias_of"] = "exactName";
                error["deprecated"] = true;
                error["deprecation_reason"] = "Use `exactName` for exact symbol-name matching.";
                break;
            case ("impact_analysis", "maxDepth"):
                error["alias_of"] = "maxHops";
                error["deprecated"] = true;
                error["deprecation_reason"] = "Use `maxHops`; `maxDepth` is retained for compatibility.";
                break;
        }

        return error;
    }

    private static JsonObject? ValidateToolArgumentTypes(string toolName, JsonObject args)
    {
        foreach (var property in args)
        {
            if (TryGetExpectedJsonType(toolName, property.Key, out var expected)
                && !MatchesExpectedJsonType(property.Value, expected))
            {
                return AddArgumentCompatibilityData(new JsonObject
                {
                    ["message"] = $"Invalid type for argument '{property.Key}' on tool '{toolName}'. Expected {expected}.",
                    ["tool"] = toolName,
                    ["parameter"] = property.Key,
                    ["expected"] = expected,
                    ["actual"] = DescribeJsonType(property.Value),
                    ["jsonrpc_invalid_params"] = true,
                }, toolName, property.Key);
            }
        }

        return null;
    }

    private static bool TryGetExpectedJsonType(string toolName, string argumentName, out string expected)
    {
        if (argumentName is "names" or "sections")
        {
            expected = "array";
            return true;
        }

        if (argumentName == "excludePaths")
        {
            expected = "string_or_array";
            return true;
        }

        if (argumentName == "path")
        {
            expected = ToolAllowsStringOrArrayPath(toolName) ? "string_or_array" : "string";
            return true;
        }

        expected = argumentName switch
        {
            "limit" or "offset" or "snippetLines" or "maxLineWidth" or "before" or "after" or
                "focusLine" or "focusColumn" or "focusLength" or "startLine" or "endLine" or
                "maxHops" or "maxDepth" or "depth" or "parallelism" or "maxFileBytes" or "maxSymbolsPerFile" or "maxReferencesPerFile" or "debounce" or
                "staleAfterSeconds" or
                "guardWindow" or "maxOutputBytes" or "maxResponseBytes" or "graphBudget" => "integer",
            "check" or "excludeTests" or "includeGenerated" or "indexedOnly" or "rawQuery" or "noDedup" or "exactSubstring" or "tokenBoundary" or
                "exactName" or "exact" or "prefix" or "countOnly" or "includeBody" or "lsp_compatible" or
                "lspCompatible" or
                "regex" or "withPaths" or "rebuild" or "dryRun" or "dry_run" or "force" or
                "optimize" or "reverse" or "cycles" or "config" or "logPath" or "updateCheck" or
                "rawKinds" or "includeQualifiedCommonCalls" or "includeMemberReads" or "orderBySize" or "rawBytes" or "byBucket" or "memoryTrace" or "watch" or
                "estimateOnly" or "listRecipes" => "boolean",
            "project" or "capability" or "scopes" or "fields" or "visibility" or "excludeVisibility" or "includeSymbolKind" or "excludeSymbolKind" or
                "commits" or "changedBetween" or "files" or
                "requireBefore" or "requireAfter" or "rejectBefore" or "rejectAfter" => "string_or_array",
            "query" or "lang" or "kind" or "format" or "rankBy" or "since" or "cursor" or "guardScope" or
                "solution" or "symbol" or "groupBy" or "category" or "language" or "severity" or "explain" or "snippetFocus" or
                "bucket" or "minConfidence" or "extension" or "alias" or "description" or "context" or "toolInvocationContext" or "db" or
                "followSymlinks" or "recipe" or "auditScope" => "string",
            "minEntrypointConfidence" => "number",
            "queries" or "evidencePaths" or "evidence_paths" => "array",
            _ => string.Empty,
        };

        if (expected.Length == 0)
            return false;

        return true;
    }

    private static bool ToolAllowsStringOrArrayPath(string toolName) => toolName switch
    {
        "search" or "definition" or "references" or "callers" or "callees" or "symbols" or
        "files" or "find_in_file" or "map" or "analyze_symbol" or "deps" or "impact_analysis" or
        "validate" or "unused_symbols" or "symbol_hotspots" => true,
        _ => false,
    };

    private static bool MatchesExpectedJsonType(JsonNode? node, string expected) => expected switch
    {
        "integer" => node is JsonValue value && value.TryGetValue<int>(out _),
        "boolean" => node is JsonValue value && value.TryGetValue<bool>(out _),
        "string" => node is JsonValue value && value.TryGetValue<string>(out _),
        "string_or_array" => node is JsonArray || node is JsonValue value && value.TryGetValue<string>(out _),
        "array" => node is JsonArray,
        "number" => node is JsonValue value && value.TryGetValue<double>(out _),
        _ => true,
    };

    private static string DescribeJsonType(JsonNode? node)
    {
        if (node is null)
            return "null";
        return node.GetValueKind() switch
        {
            JsonValueKind.String => "string",
            JsonValueKind.Number => "number",
            JsonValueKind.True or JsonValueKind.False => "boolean",
            JsonValueKind.Array => "array",
            JsonValueKind.Object => "object",
            JsonValueKind.Null => "null",
            _ => "unknown",
        };
    }

    private static JsonObject? ValidateStringListArgument(JsonNode? args, string propertyName)
    {
        var node = args?[propertyName];
        if (node is null)
            return null;

        if (node is JsonArray array)
        {
            if (array.Count > MaxMcpArrayFilterCount)
                return new JsonObject
                {
                    ["message"] = $"{propertyName} must contain at most {MaxMcpArrayFilterCount} entries.",
                    ["invalid_count"] = array.Count - MaxMcpArrayFilterCount,
                    ["max_count"] = MaxMcpArrayFilterCount,
                    ["actual_count"] = array.Count,
                };

            var invalidCount = 0;
            var invalidSamples = new JsonArray();
            var hasTooLongEntry = false;
            for (var i = 0; i < array.Count; i++)
            {
                var element = array[i];
                if (element is not JsonValue value || !value.TryGetValue<string>(out var text) || string.IsNullOrWhiteSpace(text))
                {
                    invalidCount++;
                    if (invalidSamples.Count < 3)
                        invalidSamples.Add($"[{i}]");
                    continue;
                }

                if (text.Length > MaxMcpArrayFilterStringLength)
                {
                    invalidCount++;
                    hasTooLongEntry = true;
                    if (invalidSamples.Count < 3)
                        invalidSamples.Add($"[{i}] length {text.Length}");
                }
            }

            if (invalidCount > 0 && (propertyName != "names" || invalidCount != array.Count || hasTooLongEntry))
                return new JsonObject
                {
                    ["message"] = $"{propertyName} contains {invalidCount} invalid entr{(invalidCount == 1 ? "y" : "ies")}. Entries must be non-empty strings no longer than {MaxMcpArrayFilterStringLength} characters.",
                    ["invalid_count"] = invalidCount,
                    ["invalid_samples"] = invalidSamples,
                    ["max_length"] = MaxMcpArrayFilterStringLength,
                };
            return null;
        }

        if (node is JsonValue scalar && scalar.TryGetValue<string>(out var scalarText))
        {
            if (propertyName is "names" or "sections")
                return new JsonObject
                {
                    ["message"] = $"{propertyName} must be an array of strings.",
                    ["invalid_count"] = 1,
                };
            if (propertyName == "path" && string.IsNullOrWhiteSpace(scalarText))
                return null;
            if (string.IsNullOrWhiteSpace(scalarText))
                return new JsonObject
                {
                    ["message"] = $"{propertyName} cannot be empty or whitespace-only.",
                    ["invalid_count"] = 1,
                };
            if (scalarText.Length > MaxMcpArrayFilterStringLength)
                return new JsonObject
                {
                    ["message"] = $"{propertyName} must be no longer than {MaxMcpArrayFilterStringLength} characters.",
                    ["invalid_count"] = 1,
                    ["invalid_samples"] = new JsonArray { $"length {scalarText.Length}" },
                    ["max_length"] = MaxMcpArrayFilterStringLength,
                    ["actual_length"] = scalarText.Length,
                };
            return null;
        }

        return new JsonObject
        {
            ["message"] = $"{propertyName} must be a string or an array of strings.",
            ["invalid_count"] = 1,
        };
    }

    private static bool TryResolveSearchExactArgument(JsonNode? args, out bool exact, out string? error)
    {
        var legacyExact = args?["exact"]?.GetValue<bool>() ?? false;
        var exactSubstring = args?["exactSubstring"]?.GetValue<bool>() ?? false;
        var tokenBoundary = args?["tokenBoundary"]?.GetValue<bool>() ?? false;
        var exactName = args?["exactName"]?.GetValue<bool>() ?? false;

        if (CountTrue(legacyExact, exactSubstring, tokenBoundary, exactName) > 1)
        {
            exact = false;
            error = "Pass only one of 'exact', 'exactSubstring', 'tokenBoundary', 'exactName'.";
            return false;
        }

        if (exactName)
        {
            exact = false;
            error = "Search does not accept 'exactName'. Use 'exactSubstring' for search, or keep 'exact' for backward compatibility.";
            return false;
        }

        exact = legacyExact || exactSubstring;
        error = null;
        return true;
    }

    private static bool TryResolveNameExactArgument(JsonNode? args, string toolName, out bool exact, out string? error)
    {
        var legacyExact = args?["exact"]?.GetValue<bool>() ?? false;
        var exactSubstring = args?["exactSubstring"]?.GetValue<bool>() ?? false;
        var exactName = args?["exactName"]?.GetValue<bool>() ?? false;

        if (CountTrue(legacyExact, exactSubstring, exactName) > 1)
        {
            exact = false;
            error = "Pass only one of 'exact', 'exactSubstring', 'exactName'.";
            return false;
        }

        if (exactSubstring)
        {
            exact = false;
            error = $"Tool '{toolName}' does not accept 'exactSubstring'. Use 'exactName', or keep 'exact' for backward compatibility.";
            return false;
        }

        exact = legacyExact || exactName;
        error = null;
        return true;
    }

    private static bool TryReadLspCompatibleArgument(JsonNode? args, out bool lspCompatible, out string? error)
    {
        var snakeNode = args?["lsp_compatible"];
        var camelNode = args?["lspCompatible"];
        var snakeProvided = snakeNode is not null;
        var camelProvided = camelNode is not null;
        var snakeValue = snakeNode?.GetValue<bool>() ?? false;
        var camelValue = camelNode?.GetValue<bool>() ?? false;

        if (snakeProvided && camelProvided && snakeValue != camelValue)
        {
            lspCompatible = false;
            error = "Pass only one of 'lsp_compatible' or 'lspCompatible', or give both aliases the same value.";
            return false;
        }

        lspCompatible = snakeProvided ? snakeValue : camelValue;
        error = null;
        return true;
    }

    private static int CountTrue(params bool[] values)
    {
        return values.Count(value => value);
    }


}
