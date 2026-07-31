using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using CodeIndex.Cli;
using CodeIndex.Database;
using CodeIndex.Models;

namespace CodeIndex.Mcp;

/// <summary>
/// MCP tool definitions (partial class split from McpServer.cs).
/// MCPツール定義（McpServer.csからのpartial class分割）。
/// </summary>
public partial class McpServer
{
    /// <summary>
    /// Return the list of available tools.
    /// 利用可能なツール一覧を返す。
    /// </summary>
    private JsonNode HandleToolsList(JsonNode? id, JsonNode? listParams) =>
        CreateToolsListResponse(id, listParams, CreateToolCatalog());


    private JsonNode CreateToolsListResponse(JsonNode? id, JsonNode? listParams, JsonArray tools)
    {
        // Per-deployment enablement gate (#1561). Drop any tool the operator disabled via
        // `CDIDX_MCP_TOOLS_ALLOW` / `CDIDX_MCP_TOOLS_DENY` so AI clients never see destructive
        // or out-of-scope tools advertised in the first place.
        // デプロイ単位の有効化ゲート (#1561)。`CDIDX_MCP_TOOLS_ALLOW` /
        // `CDIDX_MCP_TOOLS_DENY` で除外されたツールは tools/list 段階で隠し、AI クライアント
        // が破壊的ツールや範囲外ツールを最初から見えないようにする。
        var filtered = new JsonArray();
        foreach (var tool in tools)
        {
            var name = tool?["name"]?.GetValue<string>();
            if (name == null || !_toolFilter.IsEnabled(name))
                continue;
            filtered.Add(tool!.DeepClone());
        }

        if (listParams is not null && listParams is not JsonObject)
            return CreateToolsListParamsError(id);

        var paramsObject = listParams as JsonObject;
        var catalogFormat = "full";
        if (paramsObject?.ContainsKey("format") == true
            && (paramsObject["format"] is not JsonValue formatValue
                || !formatValue.TryGetValue<string>(out catalogFormat)
                || catalogFormat is not ("full" or "compact")))
        {
            return CreateToolsListFormatError(id);
        }
        if (!TryReadToolsListNames(paramsObject, out var requestedNames, out var namesError))
            return CreateToolsListNamesError(id, namesError!);

        var pageSize = DefaultToolsListPageSize;
        if (paramsObject?["limit"] is JsonNode limitNode
            && (limitNode is not JsonValue limitValue
                || !limitValue.TryGetValue<int>(out pageSize)
                || pageSize < 1
                || pageSize > MaxToolsListPageSize))
        {
            return CreateToolsListLimitError(id);
        }

        var offset = 0;
        if (paramsObject?["cursor"] is JsonNode cursorNode)
        {
            if (cursorNode is not JsonValue cursorValue
                || !cursorValue.TryGetValue<string>(out var cursor)
                || !TryParseToolsListCursor(cursor, out offset, out var cursorFormat, out var cursorNames))
            {
                return CreateToolsListCursorError(id);
            }

            if (cursorFormat is not null)
            {
                if ((paramsObject.ContainsKey("format") && catalogFormat != cursorFormat)
                    || (paramsObject.ContainsKey("names")
                        && (requestedNames is null || cursorNames is null || !requestedNames.SetEquals(cursorNames))))
                {
                    return CreateToolsListCursorError(id);
                }

                catalogFormat = cursorFormat;
                requestedNames = cursorNames;
            }
        }

        var selected = filtered;
        if (requestedNames is not null)
        {
            selected = new JsonArray();
            foreach (var tool in filtered)
            {
                var name = tool?["name"]?.GetValue<string>();
                if (name is not null && requestedNames.Contains(name))
                    selected.Add(tool!.DeepClone());
            }
        }

        var page = new JsonArray();
        for (var i = offset; i < selected.Count && page.Count < pageSize; i++)
        {
            var tool = selected[i]!.AsObject();
            page.Add(catalogFormat == "compact"
                ? BuildCompactToolCatalogEntry(tool)
                : tool.DeepClone());
        }

        var catalogMeta = catalogFormat == "compact"
            ? BuildCompactToolsListCatalogMeta(filtered.Count, selected.Count, page.Count, offset, pageSize, requestedNames is not null)
            : BuildToolsListCatalogMeta(filtered, page.Count, offset, pageSize);
        if (catalogFormat == "full" && requestedNames is not null)
            MarkToolsListCatalogMetaNameScoped(catalogMeta, filtered.Count, selected.Count);

        var result = new JsonObject
        {
            ["tools"] = page,
            ["_meta"] = catalogMeta,
        };
        var nextOffset = offset + pageSize;
        if (nextOffset <= MaxMcpPaginationOffset && nextOffset < selected.Count)
            result["nextCursor"] = CreateToolsListCursor(nextOffset, catalogFormat, requestedNames);
        return CreateSuccessResponse(id, result);
    }

    private static JsonObject CreateToolsListCursorError(JsonNode? id)
        => CreateErrorResponse(hasId: true, id: id, code: -32602,
            message: $"tools/list cursor must be a valid continuation token no longer than {MaxToolsListCursorCharacters} characters with an offset no greater than {MaxMcpPaginationOffset}.",
            category: McpErrorEnvelope.CategoryInvalidArgument,
            suggestion: "Use the `nextCursor` value returned by the previous tools/list response without changing its format or names controls, or omit params.cursor to start from the first page.",
            retrySafe: false,
            extraData: new JsonObject
            {
                ["max_pagination_offset"] = MaxMcpPaginationOffset,
                ["max_tools_list_cursor_characters"] = MaxToolsListCursorCharacters,
            });

    private static JsonObject CreateToolsListParamsError(JsonNode? id)
        => CreateErrorResponse(hasId: true, id: id, code: -32602,
            message: "tools/list params must be an object when present.",
            category: McpErrorEnvelope.CategoryInvalidArgument,
            suggestion: "Omit params for the default tools/list response, or pass an object such as {\"limit\": 3}.",
            retrySafe: false);

    private static JsonObject CreateToolsListLimitError(JsonNode? id)
        => CreateErrorResponse(hasId: true, id: id, code: -32602,
            message: $"tools/list limit must be between 1 and {MaxToolsListPageSize}.",
            category: McpErrorEnvelope.CategoryInvalidArgument,
            suggestion: "Use params.limit only when you need a smaller discovery page, or omit it for the default tools/list page.",
            retrySafe: false,
            extraData: new JsonObject
            {
                ["default_tools_list_page_size"] = DefaultToolsListPageSize,
                ["max_tools_list_page_size"] = MaxToolsListPageSize,
            });

    private static JsonObject CreateToolsListFormatError(JsonNode? id)
        => CreateErrorResponse(hasId: true, id: id, code: -32602,
            message: "tools/list format must be one of full, compact.",
            category: McpErrorEnvelope.CategoryInvalidArgument,
            suggestion: "Use params.format=\"compact\" for lightweight discovery or params.format=\"full\" for complete tool definitions.",
            retrySafe: false);

    private static JsonObject CreateToolsListNamesError(JsonNode? id, string message)
        => CreateErrorResponse(hasId: true, id: id, code: -32602,
            message: message,
            category: McpErrorEnvelope.CategoryInvalidArgument,
            suggestion: $"Pass one exact tool name or an array of up to {MaxToolsListNameFilters} names, then use format=\"full\" to retrieve complete definitions.",
            retrySafe: false,
            extraData: new JsonObject
            {
                ["max_tool_name_filters"] = MaxToolsListNameFilters,
                ["max_tool_name_characters"] = MaxToolsListNameCharacters,
            });

    private static bool TryReadToolsListNames(
        JsonObject? paramsObject,
        out HashSet<string>? requestedNames,
        out string? error)
    {
        requestedNames = null;
        error = null;
        if (paramsObject?.ContainsKey("names") != true)
            return true;

        var node = paramsObject["names"];
        if (node is null)
        {
            error = "tools/list names must be a non-empty string or string array.";
            return false;
        }
        IEnumerable<JsonNode?> values = node is JsonArray array ? array : new JsonNode?[] { node };
        if (node is JsonArray namesArray && (namesArray.Count == 0 || namesArray.Count > MaxToolsListNameFilters))
        {
            error = $"tools/list names must contain between 1 and {MaxToolsListNameFilters} entries.";
            return false;
        }

        requestedNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in values)
        {
            if (value is not JsonValue jsonValue
                || !jsonValue.TryGetValue<string>(out var name)
                || string.IsNullOrWhiteSpace(name))
            {
                error = "tools/list names entries must be non-empty strings.";
                return false;
            }

            name = name.Trim();
            if (name.Length > MaxToolsListNameCharacters)
            {
                error = $"tools/list names entries must be no longer than {MaxToolsListNameCharacters} characters.";
                return false;
            }
            requestedNames.Add(name);
        }
        return true;
    }

    private static string CreateToolsListCursor(
        int offset,
        string catalogFormat,
        HashSet<string>? requestedNames)
    {
        if (catalogFormat == "full" && requestedNames is null)
            return offset.ToString(CultureInfo.InvariantCulture);

        var payload = new JsonObject
        {
            ["offset"] = offset,
            ["format"] = catalogFormat,
        };
        if (requestedNames is not null)
        {
            payload["names"] = new JsonArray(requestedNames
                .OrderBy(name => name, StringComparer.Ordinal)
                .Select(name => (JsonNode?)name)
                .ToArray());
        }

        return "v1." + Convert.ToBase64String(Encoding.UTF8.GetBytes(payload.ToJsonString()))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private static bool TryParseToolsListCursor(
        string cursor,
        out int offset,
        out string? catalogFormat,
        out HashSet<string>? requestedNames)
    {
        offset = 0;
        catalogFormat = null;
        requestedNames = null;
        if (cursor.Length == 0 || cursor.Length > MaxToolsListCursorCharacters)
            return false;

        if (int.TryParse(cursor, NumberStyles.None, CultureInfo.InvariantCulture, out offset))
            return offset >= 0 && offset <= MaxMcpPaginationOffset;

        const string prefix = "v1.";
        if (!cursor.StartsWith(prefix, StringComparison.Ordinal))
            return false;

        try
        {
            var encoded = cursor[prefix.Length..].Replace('-', '+').Replace('_', '/');
            encoded = (encoded.Length % 4) switch
            {
                0 => encoded,
                2 => encoded + "==",
                3 => encoded + "=",
                _ => string.Empty,
            };
            if (encoded.Length == 0)
                return false;

            var payload = JsonNode.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(encoded))) as JsonObject;
            if (payload is null
                || payload.Count is < 2 or > 3
                || payload.Any(property => property.Key is not ("offset" or "format" or "names"))
                || payload["offset"] is not JsonValue offsetValue
                || !offsetValue.TryGetValue<int>(out offset)
                || offset < 0
                || offset > MaxMcpPaginationOffset
                || payload["format"] is not JsonValue formatValue
                || !formatValue.TryGetValue<string>(out catalogFormat)
                || catalogFormat is not ("full" or "compact")
                || !TryReadToolsListNames(payload, out requestedNames, out _))
            {
                return false;
            }

            return true;
        }
        catch (Exception ex) when (ex is FormatException or System.Text.Json.JsonException)
        {
            return false;
        }
    }

    private static JsonObject BuildCompactToolCatalogEntry(JsonObject tool)
    {
        var compact = new JsonObject
        {
            ["name"] = tool["name"]?.DeepClone(),
            ["description"] = BuildCompactToolDescription(tool["description"]?.GetValue<string>()),
            ["inputSchema"] = new JsonObject { ["type"] = "object" },
        };
        if (tool["annotations"] is not null)
            compact["annotations"] = tool["annotations"]!.DeepClone();
        if (tool["x-stability"] is not null)
            compact["x-stability"] = tool["x-stability"]!.DeepClone();
        return compact;
    }

    private static string BuildCompactToolDescription(string? description)
    {
        if (string.IsNullOrWhiteSpace(description))
            return string.Empty;

        var english = description.Split(" / ", 2, StringSplitOptions.None)[0];
        var sentenceEnd = english.IndexOf(". ", StringComparison.Ordinal);
        if (sentenceEnd >= 0)
            english = english[..(sentenceEnd + 1)];
        const int maxDescriptionCharacters = 240;
        return english.Length <= maxDescriptionCharacters
            ? english
            : $"{english[..(maxDescriptionCharacters - 3)].TrimEnd()}...";
    }

    private static JsonObject BuildCompactToolsListCatalogMeta(
        int enabledToolCount,
        int selectedToolCount,
        int returnedToolCount,
        int offset,
        int pageSize,
        bool namesFiltered) => new()
        {
            ["catalog_version"] = "cdidx.mcp.tools.v1",
            ["format"] = "compact",
            ["definitions_complete"] = false,
            ["full_definition_request"] = new JsonObject
            {
                ["method"] = "tools/list",
                ["params"] = new JsonObject
                {
                    ["format"] = "full",
                    ["names"] = new JsonArray { "<tool-name>" },
                },
            },
            ["discovery_contract"] = new JsonObject
            {
                ["disabled_tools_are_omitted"] = true,
                ["input_schemas_are_authoritative"] = false,
                ["full_definitions_available_on_demand"] = true,
                ["name_filter_param"] = "params.names",
                ["pagination_supported"] = true,
            },
            ["response_controls"] = new JsonObject
            {
                ["enabled_tools_total"] = enabledToolCount,
                ["tools_total"] = selectedToolCount,
                ["tools_returned"] = returnedToolCount,
                ["tools_offset"] = offset,
                ["tools_page_size"] = pageSize,
                ["names_filtered"] = namesFiltered,
                ["max_tool_name_filters"] = MaxToolsListNameFilters,
                ["max_pagination_offset"] = MaxMcpPaginationOffset,
            },
        };

    private static JsonObject BuildToolsListCatalogMeta(JsonArray tools, int returnedToolCount, int offset, int pageSize)
    {
        var enabledToolNames = GetAdvertisedToolNames(tools);
        return new JsonObject
        {
            ["catalog_version"] = "cdidx.mcp.tools.v1",
            ["purpose"] = "Help first-time AI clients discover cdidx capabilities from tools/list without guessing from a flat tool array.",
            ["first_time_ai_guide"] = new JsonArray
            {
                "Start with status to verify index freshness and graph readiness before trusting search or graph answers.",
                "Use map and languages to orient on repository shape and supported extraction depth.",
                "Use search for broad discovery, then definition or excerpt for focused source context.",
                "Use references, callers, callees, and impact_analysis when graph_supported or language readiness indicates graph data is available.",
                "Use batch_query to combine independent read-only lookups under one response budget.",
                "Use suggest_improvement when an extraction, ranking, or output gap is observed; never include source code in that report.",
            },
            ["capability_groups"] = new JsonObject
            {
                ["workspace_health"] = ToolNameArray(enabledToolNames, "status", "validate", "languages", "ping"),
                ["discovery"] = ToolNameArray(enabledToolNames, "search", "map", "files", "symbols", "outline", "deps"),
                ["symbol_navigation"] = ToolNameArray(enabledToolNames, "definition", "references", "callers", "callees", "analyze_symbol", "impact_analysis"),
                ["file_reading"] = ToolNameArray(enabledToolNames, "excerpt", "find_in_file", "read_resource"),
                ["batching"] = ToolNameArray(enabledToolNames, "batch_query"),
                ["analysis"] = ToolNameArray(enabledToolNames, "unused_symbols", "symbol_hotspots"),
                ["index_maintenance"] = ToolNameArray(enabledToolNames, "index", "backfill_fold"),
                ["feedback"] = ToolNameArray(enabledToolNames, "suggest_improvement"),
            },
            ["recommended_workflows"] = new JsonArray
            {
                WorkflowMeta(enabledToolNames, "first_pass_orientation", "Check whether the existing index can be trusted, then inspect repository shape.", "status", "map", "languages", "search"),
                WorkflowMeta(enabledToolNames, "go_to_implementation", "Find candidate code and retrieve the smallest useful implementation context.", "search", "definition", "excerpt"),
                WorkflowMeta(enabledToolNames, "trace_call_graph", "Move from a symbol to usage, callers/callees, and blast-radius analysis.", "references", "callers", "callees", "impact_analysis"),
                WorkflowMeta(enabledToolNames, "safe_file_review", "Locate files and read constrained excerpts or bounded resources without dumping whole large files.", "files", "find_in_file", "excerpt", "read_resource"),
                WorkflowMeta(enabledToolNames, "large_question_batch", "Bundle independent read-only lookups while respecting response budgets.", "batch_query"),
                WorkflowMeta(enabledToolNames, "index_freshness_repair", "Diagnose stale or partial indexes and refresh only when needed.", "status", "index", "backfill_fold", "validate"),
                WorkflowMeta(enabledToolNames, "report_capability_gap", "Report missing or poor extraction/ranking behavior in natural language.", "suggest_improvement"),
            },
            ["discovery_contract"] = new JsonObject
            {
                ["tools_list_is_authoritative"] = true,
                ["disabled_tools_are_omitted"] = true,
                ["input_schemas_are_authoritative"] = true,
                ["annotations_describe_read_only_or_mutating_behavior"] = true,
                ["respect_tool_filtering"] = true,
                ["pagination_supported"] = true,
                ["cursor_param"] = "params.cursor",
                ["limit_param"] = "params.limit",
                ["next_cursor_field"] = "result.nextCursor",
            },
            ["response_controls"] = new JsonObject
            {
                ["tools_total"] = tools.Count,
                ["tools_returned"] = returnedToolCount,
                ["tools_offset"] = offset,
                ["tools_page_size"] = pageSize,
                ["default_tools_list_page_size"] = DefaultToolsListPageSize,
                ["max_tools_list_page_size"] = MaxToolsListPageSize,
                ["max_pagination_offset"] = MaxMcpPaginationOffset,
            },
        };
    }

    private static void MarkToolsListCatalogMetaNameScoped(
        JsonObject catalogMeta,
        int enabledToolCount,
        int selectedToolCount)
    {
        catalogMeta["catalog_scope"] = "name_filtered";
        var discoveryContract = catalogMeta["discovery_contract"]!.AsObject();
        discoveryContract["tools_list_is_authoritative"] = false;
        discoveryContract["catalog_metadata_scope"] = "enabled_tools";
        discoveryContract["returned_tools_scope"] = "requested_names";

        var responseControls = catalogMeta["response_controls"]!.AsObject();
        responseControls["enabled_tools_total"] = enabledToolCount;
        responseControls["tools_total"] = selectedToolCount;
        responseControls["names_filtered"] = true;
    }

    private static HashSet<string> GetAdvertisedToolNames(JsonArray tools)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var tool in tools)
        {
            var name = tool?["name"]?.GetValue<string>();
            if (!string.IsNullOrEmpty(name))
                names.Add(name);
        }

        return names;
    }

    private static JsonArray ToolNameArray(HashSet<string> enabledToolNames, params string[] toolNames)
    {
        var result = new JsonArray();
        foreach (var toolName in toolNames)
        {
            if (enabledToolNames.Contains(toolName))
                result.Add(toolName);
        }

        return result;
    }

    private static JsonObject WorkflowMeta(HashSet<string> enabledToolNames, string name, string description, params string[] toolNames) => new()
    {
        ["name"] = name,
        ["description"] = description,
        ["tools"] = ToolNameArray(enabledToolNames, toolNames),
    };

    private static JsonObject StringOrArraySchema(string description) => new()
    {
        ["oneOf"] = new JsonArray
        {
            new JsonObject { ["type"] = "string" },
            new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" } },
        },
        ["description"] = description,
    };

    private static void AddProjectScopeProperties(JsonArray tools)
    {
        var scopedTools = new HashSet<string>(StringComparer.Ordinal)
        {
            "search",
            "definition",
            "references",
            "callers",
            "callees",
            "symbols",
            "files",
            "map",
            "analyze_symbol",
            "impact_analysis",
            "deps",
            "validate",
            "unused_symbols",
            "symbol_hotspots",
        };

        foreach (var tool in tools.OfType<JsonObject>())
        {
            var name = tool["name"]?.GetValue<string>();
            if (name == null || !scopedTools.Contains(name))
                continue;

            var properties = tool["inputSchema"]?["properties"] as JsonObject;
            if (properties == null || !properties.ContainsKey("path") || properties.ContainsKey("project"))
                continue;

            properties["project"] = new JsonObject
            {
                ["oneOf"] = new JsonArray { new JsonObject { ["type"] = "string" }, new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" } } },
                ["description"] = "Restrict to .sln/.csproj project name or project path. Accepts a single string or array; combines with path filters.",
            };
            properties["solution"] = new JsonObject
            {
                ["type"] = "string",
                ["description"] = "Solution file used to resolve project filters when the workspace has multiple .sln files.",
            };
        }
    }

    private static void AddCommonSchemaConstraints(JsonArray tools)
    {
        foreach (var tool in tools.OfType<JsonObject>())
        {
            var inputSchema = tool["inputSchema"] as JsonObject;
            inputSchema?.TryAdd("additionalProperties", false);
            var toolName = tool["name"]?.GetValue<string>() ?? string.Empty;
            var stability = GetToolStability(toolName);
            tool["x-stability"] = stability;
            if (stability != "stable" && tool["description"]?.GetValue<string>() is { } description
                && !description.StartsWith($"[{stability}]", StringComparison.Ordinal))
            {
                tool["description"] = $"[{stability}] {description}";
            }

            var properties = inputSchema?["properties"] as JsonObject;
            if (properties == null)
                continue;

            foreach (var (name, schema) in properties)
            {
                ApplyCommonSchemaConstraint(toolName, name, schema);
                if (schema is JsonObject obj)
                    ApplyCommonSchemaMetadata(toolName, name, obj);
            }
        }
    }

    private static string GetToolStability(string toolName) => toolName switch
    {
        "validate" or "impact_analysis" or "backfill_fold" or "suggest_improvement" => "experimental",
        _ => "stable",
    };

    private static void ApplyCommonSchemaConstraint(string toolName, string name, JsonNode? schema)
    {
        if (schema is not JsonObject obj)
            return;

        if (obj["oneOf"] is JsonArray oneOf)
        {
            foreach (var option in oneOf)
                ApplyCommonSchemaConstraint(toolName, name, option);
        }

        if (obj["type"]?.GetValue<string>() == "array" && obj["items"] is JsonObject items)
            ApplyCommonSchemaConstraint(toolName, name, items);

        switch (name)
        {
            case "query":
            case "description":
            case "context":
            case "toolInvocationContext":
                obj.TryAdd("minLength", 1);
                obj.TryAdd("maxLength", 1024);
                break;
            case "path":
            case "project":
            case "solution":
                if (obj["type"]?.GetValue<string>() == "array")
                {
                    obj.TryAdd("maxItems", MaxMcpArrayFilterCount);
                }
                else if (name == "path" && toolName == "index")
                {
                    obj.TryAdd("minLength", 1);
                    obj.TryAdd("maxLength", MaxMcpArrayFilterStringLength);
                    obj.TryAdd("pattern", @"^(?!.*\u0000).+$");
                    AppendConstraintDescription(obj, "May be absolute or relative, but must be non-empty and must not contain NUL bytes.");
                }
                else
                {
                    obj.TryAdd("minLength", 1);
                    obj.TryAdd("maxLength", MaxMcpArrayFilterStringLength);
                    obj.TryAdd("pattern", @"^(?!/)(?![A-Za-z]:)(?!.*(^|/)\.\.(/|$))(?!.*\u0000).*$");
                    AppendConstraintDescription(obj, "Must be workspace-relative, non-empty, and must not contain NUL bytes or `..` path traversal segments.");
                }
                break;
            case "excludePaths":
                if (obj["type"]?.GetValue<string>() == "array")
                {
                    obj.TryAdd("maxItems", MaxMcpArrayFilterCount);
                }
                else
                {
                    obj.TryAdd("minLength", 1);
                    obj.TryAdd("maxLength", MaxMcpArrayFilterStringLength);
                    obj.TryAdd("pattern", @"^(?!/)(?![A-Za-z]:)(?!.*(^|/)\.\.(/|$))(?!.*\u0000).*$");
                    AppendConstraintDescription(obj, "Must be workspace-relative, non-empty, and must not contain NUL bytes or `..` path traversal segments.");
                }
                break;
            case "sections":
                if (obj["type"]?.GetValue<string>() == "array")
                {
                    obj.TryAdd("maxItems", MaxMcpArrayFilterCount);
                }
                else
                {
                    obj.TryAdd("minLength", 1);
                    obj.TryAdd("maxLength", MaxMcpArrayFilterStringLength);
                }
                break;
            case "limit":
                obj.TryAdd("minimum", 1);
                obj.TryAdd("maximum", MaxLimit);
                break;
            case "offset":
                obj.TryAdd("minimum", 0);
                obj.TryAdd("maximum", MaxMcpPaginationOffset);
                break;
            case "startLine":
            case "endLine":
                obj.TryAdd("minimum", 1);
                break;
            case "before":
            case "after":
                obj.TryAdd("maximum", MaxContextLines);
                break;
            case "kind":
                if (toolName is "references")
                    obj.TryAdd("enum", new JsonArray { "call", "instantiate", "subscribe", "unsubscribe", "friend", "attribute", "annotation", "type_reference" });
                else if (toolName is "callers" or "callees")
                    obj.TryAdd("enum", new JsonArray { "call", "instantiate", "subscribe", "unsubscribe", "friend" });
                break;
            case "lang":
            case "language":
                obj.TryAdd("pattern", "^[A-Za-z0-9_+.#-]{1,64}$");
                obj.TryAdd("maxLength", 64);
                break;
        }
    }

    private static void ApplyCommonSchemaMetadata(string toolName, string name, JsonObject obj)
    {
        if (TryGetExpectedJsonType(toolName, name, out var expected))
            obj["x-expectedType"] = expected;

        switch (toolName, name)
        {
            case ("definition", "lsp_compatible"):
            case ("references", "lsp_compatible"):
                obj["x-aliases"] = new JsonArray { "lspCompatible" };
                break;
            case ("definition", "lspCompatible"):
            case ("references", "lspCompatible"):
                obj["x-aliasOf"] = "lsp_compatible";
                break;
            case ("search", "exact"):
                MarkDeprecatedAlias(obj, "exactSubstring", "Use `exactSubstring` for search exact substring matching.");
                break;
            case ("definition", "exact"):
            case ("references", "exact"):
            case ("callers", "exact"):
            case ("callees", "exact"):
            case ("symbols", "exact"):
            case ("analyze_symbol", "exact"):
                MarkDeprecatedAlias(obj, "exactName", "Use `exactName` for exact symbol-name matching.");
                break;
            case ("impact_analysis", "maxDepth"):
                MarkDeprecatedAlias(obj, "maxHops", "Use `maxHops`; `maxDepth` is retained for compatibility.");
                break;
        }

        switch (name)
        {
            case "query":
                AppendConstraintDescription(obj, "Use identifiers, symbol names, error messages, config keys, or short code/text fragments; add exactName/exactSubstring/tokenBoundary when identity matters.");
                break;
            case "exactName":
                AppendConstraintDescription(obj, "Use this when the symbol name must match exactly, e.g. `Run` should not also match `RunAsync`.");
                break;
            case "path" when toolName != "index":
                AppendConstraintDescription(obj, "Use this after broad results are noisy to narrow by module, directory, file name, project area, or tests.");
                break;
            case "excludeTests":
                AppendConstraintDescription(obj, "Set true for production-code investigation; leave false when finding tests, examples, or coverage.");
                break;
            case "includeGenerated":
                AppendConstraintDescription(obj, "Keep false by default unless generated code is explicitly part of the investigation.");
                break;
            case "format":
                AppendConstraintDescription(obj, "Use `compact` or `count` while exploring large result sets; use `full` when snippets or complete rows are needed.");
                break;
        }

        switch (toolName, name)
        {
            case ("search", "exactSubstring"):
                AppendConstraintDescription(obj, "Use this for case-sensitive exact text identity when tokenization, punctuation, emoji, or prefix matching would be misleading.");
                break;
            case ("search", "tokenBoundary"):
                AppendConstraintDescription(obj, "Use this for exact code phrases that should stop at identifier/token boundaries, such as matching `new HttpClient` without `new HttpClientHandler`.");
                break;
            case ("search", "exact"):
                AppendConstraintDescription(obj, "Alias of `exactSubstring`; use `exactSubstring` in new calls for search text identity.");
                break;
            case ("search", "prefix"):
                AppendConstraintDescription(obj, "Use this for partial tokens, Japanese terms, or identifier prefixes when a broader token-prefix search is desired.");
                break;
            case ("definition", "exact"):
            case ("references", "exact"):
            case ("callers", "exact"):
            case ("callees", "exact"):
            case ("symbols", "exact"):
            case ("analyze_symbol", "exact"):
                AppendConstraintDescription(obj, "Alias of `exactName`; use `exactName` in new calls for exact symbol identity.");
                break;
        }
    }

    private static void MarkDeprecatedAlias(JsonObject obj, string aliasOf, string reason)
    {
        obj["x-aliasOf"] = aliasOf;
        obj["deprecated"] = true;
        obj["x-deprecationReason"] = reason;
    }

    private static void AppendConstraintDescription(JsonObject obj, string sentence)
    {
        var description = obj["description"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(description) || description.Contains(sentence, StringComparison.Ordinal))
            return;
        obj["description"] = $"{description} {sentence}";
    }
}
