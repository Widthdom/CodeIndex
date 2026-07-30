using System.Text.Json.Nodes;
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

    public static JsonObject Create(string toolName)
    {
        var toolProperties = toolName switch
        {
            "search" => SearchProperties(),
            "definition" => QueryRowsProperties(),
            "references" => QueryRowsProperties(),
            "callers" => QueryRowsProperties(),
            "callees" => QueryRowsProperties(),
            "symbols" => QueryRowsProperties(),
            "files" => RowsProperties(),
            "excerpt" => ExcerptProperties(),
            "find_in_file" => QueryRowsProperties(),
            "map" => MapProperties(),
            "analyze_symbol" => AnalyzeSymbolProperties(),
            "impact_analysis" => ImpactAnalysisProperties(),
            "status" => StatusProperties(),
            "outline" => OutlineProperties(),
            "deps" => DependencyProperties(),
            "languages" => LanguagesProperties(),
            "validate" => RowsProperties(),
            "ping" => PingProperties(),
            "batch_query" => BatchProperties(),
            "index" => MutationProperties(),
            "backfill_fold" => MutationProperties(),
            "symbol_hotspots" => RowsProperties(),
            "unused_symbols" => RowsProperties(),
            "suggest_improvement" => SuggestImprovementProperties(),
            _ => throw new InvalidOperationException(
                $"MCP tool '{toolName}' must define a structured output schema."),
        };

        var definitions = new JsonObject
        {
            ["row"] = RowSchema(),
            ["rows"] = ArraySchema(Reference("row")),
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
                ["additionalProperties"] = true,
            },
            ["result_envelope"] = ResultEnvelopeSchema(),
            ["tool_result"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = toolProperties,
                ["not"] = new JsonObject
                {
                    ["required"] = StringArray("category", "suggestion", "retry_safe"),
                },
                ["additionalProperties"] = true,
            },
            ["success"] = new JsonObject
            {
                ["allOf"] = new JsonArray
                {
                    Reference("versioned"),
                    Reference("result_envelope"),
                    Reference("tool_result"),
                },
            },
            ["error"] = ErrorSchema(),
        };

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
            ["additionalProperties"] = true,
        };

    private static JsonObject ErrorSchema()
        => new()
        {
            ["type"] = "object",
            ["required"] = StringArray("category", "suggestion", "retry_safe"),
            ["properties"] = new JsonObject
            {
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
            ["additionalProperties"] = true,
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
            ["additionalProperties"] = true,
        };

    private static JsonObject SearchProperties()
    {
        var properties = QueryRowsProperties();
        properties["query"] = Nullable(StringSchema());
        properties["top_files"] = Reference("rows");
        properties["recipes"] = Reference("rows");
        return properties;
    }

    private static JsonObject QueryRowsProperties()
    {
        var properties = RowsProperties();
        properties["query"] = Nullable(StringSchema());
        return properties;
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
            ["effectiveStartLine"] = IntegerSchema(),
            ["effectiveEndLine"] = IntegerSchema(),
            ["totalLines"] = NonNegativeIntegerSchema(),
            ["contentTruncated"] = BooleanSchema(),
        };

    private static JsonObject MapProperties()
        => new()
        {
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
            ["nearbySymbols"] = Reference("rows"),
            ["references"] = Reference("rows"),
            ["callers"] = Reference("rows"),
            ["callees"] = Reference("rows"),
            ["graphSections"] = ObjectSchema(),
        };

    private static JsonObject ImpactAnalysisProperties()
        => new()
        {
            ["query"] = StringSchema(),
            ["results"] = Reference("rows"),
            ["impact_mode"] = StringSchema(),
            ["heuristic"] = BooleanSchema(),
            ["has_multiple_definitions"] = BooleanSchema(),
        };

    private static JsonObject StatusProperties()
        => new()
        {
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
            ["results"] = Reference("rows"),
            ["edges"] = Reference("rows"),
            ["cycles"] = Reference("rows"),
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
            ["estimated_response_bytes"] = NonNegativeIntegerSchema(),
        };

    private static JsonObject MutationProperties()
        => new()
        {
            ["status"] = StringSchema(),
            ["mode"] = StringSchema(),
            ["summary"] = ObjectSchema(),
            ["dry_run"] = BooleanSchema(),
            ["readiness"] = Reference("readiness"),
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
            ["additionalProperties"] = true,
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
                ["uri"] = StringSchema(),
                ["testFile"] = BooleanSchema(),
                ["generated"] = BooleanSchema(),
            },
            ["additionalProperties"] = true,
        };

    private static JsonObject StringSchema()
        => new() { ["type"] = "string" };

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
        => new()
        {
            ["type"] = "array",
            ["items"] = itemSchema,
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
