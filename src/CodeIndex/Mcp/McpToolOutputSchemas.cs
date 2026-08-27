using System.Text.Json.Nodes;
using CodeIndex.Cli;
using CodeIndex.Database;

namespace CodeIndex.Mcp;

/// <summary>
/// Builds the JSON Schema advertised for each MCP tool's structured result.
/// Keep the explicit tool-name switch exhaustive so adding a tool without an
/// output contract fails while the catalog is being constructed.
/// </summary>
internal static class McpToolOutputSchemas
{
    private const string SchemaDialect = "https://json-schema.org/draft/2020-12/schema";
    private const int MaxSchemaArrayItems = McpServer.MaxMcpPaginationOffset;
    private const int MaxSchemaObjectProperties = 512;
    private const int MaxSchemaStringCharacters = McpServer.MaxConfiguredResponseBytes;
    private const int MaxOpenValueDepth = 8;

    public static JsonObject Create(string toolName)
    {
        var toolProperties = toolName switch
        {
            "search" => SearchProperties(),
            "definition" => QueryRowsProperties(),
            "references" => GraphQueryRowsProperties(),
            "callers" => GraphQueryRowsProperties(),
            "callees" => GraphQueryRowsProperties(),
            "symbols" => QueryRowsProperties(),
            "files" => RowsProperties(),
            "excerpt" => ExcerptProperties(),
            "read_resource" => ReadResourceProperties(),
            "find_in_file" => QueryRowsProperties(),
            "map" => MapProperties(),
            "analyze_symbol" => AnalyzeSymbolProperties(),
            "impact_analysis" => ImpactAnalysisProperties(),
            "status" => StatusProperties(),
            "outline" => OutlineProperties(),
            "deps" => DependencyProperties(),
            "languages" => LanguagesProperties(),
            "validate" => ValidateProperties(),
            "ping" => PingProperties(),
            "batch_query" => BatchProperties(),
            "index" => IndexProperties(),
            "backfill_fold" => BackfillFoldProperties(),
            "symbol_hotspots" => SymbolHotspotsProperties(),
            "unused_symbols" => UnusedSymbolsProperties(),
            "suggest_improvement" => SuggestImprovementProperties(),
            _ => throw new InvalidOperationException(
                $"MCP tool '{toolName}' must define a structured output schema."),
        };
        toolProperties["tool"] = ConstantStringSchema(toolName);
        var toolResult = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = toolProperties,
            ["required"] = RequiredToolProperties(toolName),
            ["not"] = new JsonObject
            {
                ["required"] = StringArray("category", "suggestion", "retry_safe"),
            },
            ["maxProperties"] = MaxSchemaObjectProperties,
            ["propertyNames"] = StringSchema(),
            ["additionalProperties"] = Reference("open_value_0"),
        };
        if (RequiredToolPropertyAlternatives(toolName) is JsonArray alternatives)
            toolResult["anyOf"] = alternatives;

        var definitions = new JsonObject
        {
            ["row"] = RowSchema(),
            ["rows"] = ArraySchema(Reference("row")),
            ["dependency_cycle"] = DependencyCycleSchema(),
            ["dependency_cycles"] = ArraySchema(Reference("dependency_cycle")),
            ["warning"] = new JsonObject
            {
                ["oneOf"] = new JsonArray
                {
                    StringSchema(),
                    ObjectSchema(),
                },
            },
            ["warnings"] = ArraySchema(Reference("warning")),
            ["readiness"] = ReadinessSchema(),
            ["versioned"] = new JsonObject
            {
                ["type"] = "object",
                ["required"] = StringArray("api_version"),
                ["properties"] = new JsonObject
                {
                    ["api_version"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["const"] = JsonOutputContract.ApiVersion,
                    },
                },
                ["maxProperties"] = MaxSchemaObjectProperties,
                ["propertyNames"] = StringSchema(),
            },
            ["result_envelope"] = ResultEnvelopeSchema(),
            ["tool_result"] = toolResult,
            ["success"] = new JsonObject
            {
                ["allOf"] = new JsonArray
                {
                    Reference("versioned"),
                    Reference("result_envelope"),
                    Reference("tool_result"),
                },
            },
            ["error_result"] = ErrorSchema(toolName),
            ["error"] = new JsonObject
            {
                ["allOf"] = new JsonArray
                {
                    Reference("versioned"),
                    Reference("error_result"),
                },
            },
        };
        for (var depth = 0; depth <= MaxOpenValueDepth; depth++)
            definitions[$"open_value_{depth}"] = OpenValueSchema(depth);

        return new JsonObject
        {
            ["$schema"] = SchemaDialect,
            ["title"] = $"{toolName} structured result",
            ["type"] = "object",
            ["oneOf"] = new JsonArray
            {
                Reference("success"),
                Reference("error"),
            },
            ["$defs"] = definitions,
        };
    }

    private static JsonArray RequiredToolProperties(string toolName)
    {
        var required = toolName switch
        {
            "search" => StringArray(),
            "definition" or "references" or "callers" or "callees"
                or "symbols" or "files" or "find_in_file" => StringArray("count", "results"),
            "excerpt" => StringArray("path", "totalLines"),
            "read_resource" => StringArray("resource", "_meta"),
            "map" => StringArray("fileCount"),
            "analyze_symbol" => StringArray("query", "graph_sections"),
            "impact_analysis" => StringArray("query", "impact_mode"),
            "status" => StringArray("version", "summary"),
            "outline" => StringArray("path"),
            "deps" => StringArray("count", "format"),
            "languages" => StringArray("languages"),
            "validate" => StringArray("count", "summary"),
            "ping" => StringArray("version", "timestamp", "db_path", "db_exists"),
            "batch_query" => StringArray("results", "metadata", "total_count"),
            "index" => StringArray("summary", "dry_run"),
            "backfill_fold" => StringArray("dry_run", "progress"),
            "symbol_hotspots" => StringArray("count", "grouped_by"),
            "unused_symbols" => StringArray("count", "summary", "symbols"),
            "suggest_improvement" => StringArray("status"),
            _ => throw new InvalidOperationException(
                $"MCP tool '{toolName}' must define required structured output fields."),
        };
        required.Insert(0, "tool");
        return required;
    }

    private static JsonArray? RequiredToolPropertyAlternatives(string toolName)
        => toolName switch
        {
            "search" => new JsonArray
            {
                RequiredSchema("results"),
                RequiredSchema("recipes"),
                RequiredSchema("recipe", "queries"),
            },
            "deps" => new JsonArray
            {
                RequiredSchema("edges"),
                RequiredSchema("graph"),
                RequiredSchema("cycles"),
                RequiredSchema("cycle_summaries"),
            },
            _ => null,
        };

    private static JsonObject RequiredSchema(params string[] propertyNames)
        => new() { ["required"] = StringArray(propertyNames) };

    private static JsonObject ResultEnvelopeSchema()
        => new()
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["results"] = Reference("rows"),
                ["count"] = NonNegativeIntegerSchema(),
                ["total"] = Nullable(NonNegativeIntegerSchema()),
                ["total_count"] = NonNegativeIntegerSchema(),
                ["returned_count"] = NonNegativeIntegerSchema(),
                ["truncated"] = BooleanSchema(),
                ["more_available"] = BooleanSchema(),
                ["next_offset"] = Nullable(NonNegativeIntegerSchema()),
                ["next_cursor"] = Nullable(StringSchema()),
                ["result_stable_at"] = Nullable(StringSchema()),
                ["warnings"] = Reference("warnings"),
                ["readiness"] = Reference("readiness"),
                ["next_step_suggestion"] = GuidanceSchema(),
                ["recovery_hint"] = GuidanceSchema(),
            },
            ["maxProperties"] = MaxSchemaObjectProperties,
            ["propertyNames"] = StringSchema(),
        };

    private static JsonObject ErrorSchema(string toolName)
        => new()
        {
            ["type"] = "object",
            ["required"] = StringArray("tool", "category", "suggestion", "retry_safe"),
            ["properties"] = new JsonObject
            {
                ["tool"] = ConstantStringSchema(toolName),
                ["category"] = StringSchema(),
                ["suggestion"] = StringSchema(),
                ["retry_safe"] = BooleanSchema(),
                ["correlation_id"] = StringSchema(),
                ["request_id"] = new JsonObject
                {
                    ["oneOf"] = new JsonArray
                    {
                        StringSchema(),
                        NumberSchema(),
                        NullSchema(),
                    },
                },
                ["warnings"] = Reference("warnings"),
            },
            ["maxProperties"] = MaxSchemaObjectProperties,
            ["propertyNames"] = StringSchema(),
            ["additionalProperties"] = Reference("open_value_0"),
        };

    private static JsonObject ReadinessSchema()
        => new()
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["ready"] = BooleanSchema(),
                ["is_ready"] = BooleanSchema(),
                ["degraded"] = BooleanSchema(),
                ["degraded_reason"] = Nullable(StringSchema()),
                ["failed_checks"] = ArraySchema(StringSchema()),
            },
            ["maxProperties"] = MaxSchemaObjectProperties,
            ["propertyNames"] = StringSchema(),
            ["additionalProperties"] = Reference("open_value_0"),
        };

    private static JsonObject SearchProperties()
    {
        var properties = QueryRowsProperties();
        properties["query"] = Nullable(StringSchema());
        properties["top_files"] = Reference("rows");
        properties["recipes"] = Reference("rows");
        properties["recipe"] = ObjectSchema();
        properties["query_count"] = NonNegativeIntegerSchema();
        properties["result_count"] = NonNegativeIntegerSchema();
        properties["limit_per_query"] = NonNegativeIntegerSchema();
        properties["queries"] = Reference("rows");
        return properties;
    }

    private static JsonObject QueryRowsProperties()
    {
        var properties = RowsProperties();
        properties["query"] = Nullable(StringSchema());
        return properties;
    }

    private static JsonObject GraphQueryRowsProperties()
    {
        var properties = QueryRowsProperties();
        AddGraphIdentityProperties(properties);
        return properties;
    }

    private static void AddGraphIdentityProperties(JsonObject properties)
    {
        properties["identity_scoped"] = BooleanSchema();
        properties["identity_scope_reason"] = StringSchema();
        properties["selected_symbol"] = ObjectSchema();
        properties["candidate_count"] = NonNegativeIntegerSchema();
        properties["candidates"] = Reference("rows");
        properties["candidates_truncated"] = BooleanSchema();
        properties["identity_warning"] = StringSchema();
    }

    private static JsonObject RowsProperties()
        => new()
        {
            ["results"] = Reference("rows"),
        };

    private static JsonObject ExcerptProperties()
        => new()
        {
            ["path"] = StringSchema(),
            ["content"] = Nullable(StringSchema()),
            ["requestedStartLine"] = IntegerSchema(),
            ["requestedEndLine"] = IntegerSchema(),
            ["effectiveStartLine"] = Nullable(IntegerSchema()),
            ["effectiveEndLine"] = Nullable(IntegerSchema()),
            ["totalLines"] = Nullable(NonNegativeIntegerSchema()),
            ["contentTruncated"] = BooleanSchema(),
        };

    private static JsonObject ReadResourceProperties()
        => new()
        {
            ["resource"] = new JsonObject
            {
                ["type"] = "object",
                ["required"] = StringArray("uri", "mimeType"),
                ["properties"] = new JsonObject
                {
                    ["uri"] = StringSchema(),
                    ["mimeType"] = StringSchema(),
                },
                ["maxProperties"] = 2,
                ["propertyNames"] = StringSchema(),
                ["additionalProperties"] = false,
            },
            ["_meta"] = ObjectSchema(),
        };

    private static JsonObject MapProperties()
        => new()
        {
            ["fileCount"] = NonNegativeIntegerSchema(),
            ["topFiles"] = Reference("rows"),
            ["entrypoints"] = Reference("rows"),
            ["languages"] = Reference("rows"),
            ["modules"] = Reference("rows"),
            ["indexedAt"] = Nullable(StringSchema()),
            ["workspaceIndexedAt"] = Nullable(StringSchema()),
            ["workspaceLatestModified"] = Nullable(StringSchema()),
            ["projectRoot"] = Nullable(StringSchema()),
        };

    private static JsonObject AnalyzeSymbolProperties()
        => new()
        {
            ["query"] = StringSchema(),
            ["definitions"] = Reference("rows"),
            ["nearby_symbols"] = Reference("rows"),
            ["references"] = Reference("rows"),
            ["callers"] = Reference("rows"),
            ["callees"] = Reference("rows"),
            ["graph_sections"] = ObjectSchema(),
        };

    private static JsonObject ImpactAnalysisProperties()
    {
        var properties = new JsonObject
        {
            ["query"] = StringSchema(),
            ["results"] = Reference("rows"),
            ["impact_mode"] = StringSchema(),
            ["heuristic"] = BooleanSchema(),
            ["has_multiple_definitions"] = BooleanSchema(),
        };
        AddGraphIdentityProperties(properties);
        return properties;
    }

    private static JsonObject StatusProperties()
        => new()
        {
            ["summary"] = StringSchema(),
            ["files"] = NonNegativeIntegerSchema(),
            ["chunks"] = NonNegativeIntegerSchema(),
            ["symbols"] = NonNegativeIntegerSchema(),
            ["references"] = NonNegativeIntegerSchema(),
            ["version"] = StringSchema(),
            ["index_matches_workspace"] = BooleanSchema(),
            ["readiness"] = Reference("readiness"),
        };

    private static JsonObject OutlineProperties()
        => new()
        {
            ["path"] = StringSchema(),
            ["symbols"] = Reference("rows"),
        };

    private static JsonObject DependencyProperties()
        => new()
        {
            ["edges"] = Reference("rows"),
            ["cycles"] = Reference("dependency_cycles"),
            ["cycle_summaries"] = Reference("dependency_cycles"),
            ["largest_component"] = Nullable(Reference("dependency_cycle")),
            ["graph"] = new JsonObject
            {
                ["type"] = "object",
                ["required"] = StringArray("nodes", "edges"),
                ["properties"] = new JsonObject
                {
                    ["nodes"] = ArraySchema(
                        Reference("row"),
                        QueryCommandRunner.MaxDependencyCycleGraphBudget),
                    ["edges"] = ArraySchema(
                        Reference("row"),
                        QueryCommandRunner.MaxDependencyCycleGraphBudget),
                },
                ["maxProperties"] = MaxSchemaObjectProperties,
                ["propertyNames"] = StringSchema(),
                ["additionalProperties"] = Reference("open_value_0"),
            },
            ["format"] = StringSchema(),
        };

    private static JsonObject DependencyCycleSchema()
        => new()
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["nodes"] = ArraySchema(
                    StringSchema(),
                    QueryCommandRunner.MaxDependencyCycleGraphBudget),
            },
            ["maxProperties"] = MaxSchemaObjectProperties,
            ["propertyNames"] = StringSchema(),
            ["additionalProperties"] = Reference("open_value_0"),
        };

    private static JsonObject LanguagesProperties()
        => new()
        {
            ["languages"] = Reference("rows"),
        };

    private static JsonObject PingProperties()
        => new()
        {
            ["version"] = StringSchema(),
            ["timestamp"] = StringSchema(),
            ["db_path"] = StringSchema(),
            ["db_exists"] = BooleanSchema(),
        };

    private static JsonObject BatchProperties()
        => new()
        {
            ["results"] = Reference("rows"),
            ["metadata"] = new JsonObject
            {
                ["type"] = "object",
                ["required"] = StringArray("submitted", "executed", "errors", "estimated_response_bytes"),
                ["properties"] = new JsonObject
                {
                    ["submitted"] = NonNegativeIntegerSchema(),
                    ["executed"] = NonNegativeIntegerSchema(),
                    ["errors"] = NonNegativeIntegerSchema(),
                    ["total_elapsed_ms"] = NonNegativeIntegerSchema(),
                    ["success_count"] = NonNegativeIntegerSchema(),
                    ["failure_count"] = NonNegativeIntegerSchema(),
                    ["response_byte_limit"] = NonNegativeIntegerSchema(),
                    ["estimated_response_bytes"] = NonNegativeIntegerSchema(),
                },
                ["maxProperties"] = MaxSchemaObjectProperties,
                ["propertyNames"] = StringSchema(),
                ["additionalProperties"] = Reference("open_value_0"),
            },
            ["success_count"] = NonNegativeIntegerSchema(),
            ["failure_count"] = NonNegativeIntegerSchema(),
            ["partial_failure"] = BooleanSchema(),
            ["failure_scope"] = StringSchema(),
        };

    private static JsonObject IndexProperties()
        => new()
        {
            ["mode"] = StringSchema(),
            ["summary"] = ObjectSchema(),
            ["dry_run"] = BooleanSchema(),
            ["rebuild_reclaim"] = ObjectSchema(),
            ["readiness"] = Reference("readiness"),
        };

    private static JsonObject BackfillFoldProperties()
        => new()
        {
            ["symbols"] = NonNegativeIntegerSchema(),
            ["symbol_references"] = NonNegativeIntegerSchema(),
            ["rewrite_all"] = BooleanSchema(),
            ["dry_run"] = BooleanSchema(),
            ["was_already_complete"] = BooleanSchema(),
            ["fold_ready_before"] = BooleanSchema(),
            ["fold_ready_after"] = BooleanSchema(),
            ["verified"] = BooleanSchema(),
            ["progress"] = ObjectSchema(),
            ["fold_ready"] = BooleanSchema(),
        };

    private static JsonObject ValidateProperties()
        => new()
        {
            ["count"] = NonNegativeIntegerSchema(),
            ["summary"] = ObjectSchema(),
            ["issues"] = Reference("rows"),
            ["top_files"] = Reference("rows"),
            ["issues_table_available"] = BooleanSchema(),
            ["file_issues_data_current"] = BooleanSchema(),
        };

    private static JsonObject SymbolHotspotsProperties()
        => new()
        {
            ["count"] = NonNegativeIntegerSchema(),
            ["grouped_by"] = StringSchema(),
            ["hotspots"] = Reference("rows"),
            ["files"] = NonNegativeIntegerSchema(),
            ["query_context"] = ObjectSchema(),
        };

    private static JsonObject UnusedSymbolsProperties()
        => new()
        {
            ["count"] = NonNegativeIntegerSchema(),
            ["graph_supported"] = Nullable(BooleanSchema()),
            ["graph_support_reason"] = Nullable(StringSchema()),
            ["summary"] = ObjectSchema(),
            ["symbols"] = Reference("rows"),
            ["symbols_by_bucket"] = ObjectSchema(),
            ["symbols_by_bucket_format"] = StringSchema(),
            ["returned_bucket_counts"] = ObjectSchema(),
            ["returned_contract_domain_counts"] = ObjectSchema(),
            ["bucket_taxonomy"] = ObjectSchema(),
        };

    private static JsonObject SuggestImprovementProperties()
        => new()
        {
            ["status"] = StringSchema(),
            ["id"] = StringSchema(),
            ["revision_hash"] = StringSchema(),
            ["hash"] = StringSchema(),
            ["category"] = StringSchema(),
            ["language"] = Nullable(StringSchema()),
            ["stored_locally"] = BooleanSchema(),
            ["submitted_to_github"] = BooleanSchema(),
            ["github_submission_reason"] = StringSchema(),
            ["lifecycle_status"] = StringSchema(),
            ["cdidx_dir"] = StringSchema(),
            ["duplicate_of"] = Nullable(StringSchema()),
            ["duplicate_score"] = NumberSchema(),
            ["upstream_url"] = StringSchema(),
            ["github_issue_url"] = StringSchema(),
        };

    private static JsonObject ObjectSchema()
        => new()
        {
            ["type"] = "object",
            ["maxProperties"] = MaxSchemaObjectProperties,
            ["propertyNames"] = StringSchema(),
            ["additionalProperties"] = Reference("open_value_0"),
        };

    private static JsonObject RowSchema()
        => new()
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["api_version"] = StringSchema(),
                ["path"] = StringSchema(),
                ["lang"] = StringSchema(),
                ["name"] = StringSchema(),
                ["kind"] = StringSchema(),
                ["query"] = StringSchema(),
                ["line"] = IntegerSchema(),
                ["column"] = IntegerSchema(),
                ["startLine"] = IntegerSchema(),
                ["endLine"] = IntegerSchema(),
                ["count"] = NonNegativeIntegerSchema(),
                ["score"] = NumberSchema(),
                ["snippet"] = StringSchema(),
                ["content"] = StringSchema(),
                ["result"] = ObjectSchema(),
                ["uri"] = StringSchema(),
                ["testFile"] = BooleanSchema(),
                ["generated"] = BooleanSchema(),
            },
            ["maxProperties"] = MaxSchemaObjectProperties,
            ["propertyNames"] = StringSchema(),
            ["additionalProperties"] = Reference("open_value_0"),
        };

    private static JsonObject StringSchema()
        => new()
        {
            ["type"] = "string",
            ["maxLength"] = MaxSchemaStringCharacters,
        };

    private static JsonObject ConstantStringSchema(string value)
        => new()
        {
            ["type"] = "string",
            ["const"] = value,
            ["maxLength"] = MaxSchemaStringCharacters,
        };

    private static JsonObject OpenValueSchema(int depth)
    {
        var alternatives = new JsonArray
        {
            NullSchema(),
            StringSchema(),
            BooleanSchema(),
            NumberSchema(),
        };
        if (depth < MaxOpenValueDepth)
        {
            alternatives.Add(ArraySchema(Reference($"open_value_{depth + 1}")));
            alternatives.Add(new JsonObject
            {
                ["type"] = "object",
                ["maxProperties"] = MaxSchemaObjectProperties,
                ["propertyNames"] = StringSchema(),
                ["additionalProperties"] = Reference($"open_value_{depth + 1}"),
            });
        }

        return new JsonObject { ["oneOf"] = alternatives };
    }

    private static JsonObject BooleanSchema()
        => new() { ["type"] = "boolean" };

    private static JsonObject IntegerSchema()
        => new() { ["type"] = "integer" };

    private static JsonObject NumberSchema()
        => new() { ["type"] = "number" };

    private static JsonObject NonNegativeIntegerSchema()
        => new()
        {
            ["type"] = "integer",
            ["minimum"] = 0,
        };

    private static JsonObject NullSchema()
        => new() { ["type"] = "null" };

    private static JsonObject ArraySchema(JsonObject itemSchema)
        => ArraySchema(itemSchema, MaxSchemaArrayItems);

    private static JsonObject ArraySchema(JsonObject itemSchema, int maxItems)
        => new()
        {
            ["type"] = "array",
            ["items"] = itemSchema,
            ["maxItems"] = maxItems,
        };

    private static JsonObject Nullable(JsonObject schema)
        => new()
        {
            ["oneOf"] = new JsonArray
            {
                schema,
                NullSchema(),
            },
        };

    private static JsonObject GuidanceSchema()
        => new()
        {
            ["oneOf"] = new JsonArray
            {
                StringSchema(),
                ObjectSchema(),
                NullSchema(),
            },
        };

    private static JsonObject Reference(string definition)
        => new() { ["$ref"] = $"#/$defs/{definition}" };

    private static JsonArray StringArray(params string[] values)
    {
        var array = new JsonArray();
        foreach (var value in values)
            array.Add(value);
        return array;
    }
}
