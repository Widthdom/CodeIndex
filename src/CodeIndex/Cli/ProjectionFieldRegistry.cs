using System.Text.Json.Nodes;

namespace CodeIndex.Cli;

/// <summary>
/// Canonical, command-specific registry for bounded-response projection fields.
/// Runtime validation, machine discovery, compact defaults, and command help all
/// consume this registry so their contracts cannot drift independently. Issue #4836.
/// bounded-response の投影フィールドをコマンド別に管理する正規レジストリ。
/// 実行時検証・機械向け発見・compact 既定値・コマンドヘルプはすべてこの
/// レジストリを参照し、契約の独立したずれを防ぐ。Issue #4836。
/// </summary>
internal static class ProjectionFieldRegistry
{
    private const string DiscoveryValue = "list";

    private static readonly IReadOnlyDictionary<string, ProjectionCommandFieldSchema> Schemas =
        new Dictionary<string, ProjectionCommandFieldSchema>(StringComparer.Ordinal)
        {
            ["search"] = CreateSearchSchema(),
            ["definition"] = CreateDefinitionSchema(),
            ["find"] = CreateFindSchema(),
            ["status"] = CreateStatusSchema(),
            ["hotspots"] = CreateHotspotsSchema(),
            ["references"] = CreateReferencesSchema(),
            ["callers"] = CreateCallGraphSchema("callers"),
            ["callees"] = CreateCallGraphSchema("callees"),
            ["symbols"] = CreateSymbolsSchema(),
            ["files"] = CreateFilesSchema(),
            ["languages"] = CreateLanguagesSchema(),
            ["impact"] = CreateImpactSchema(),
            ["map"] = CreateMapSchema(),
        };

    internal static IReadOnlyList<string> SupportedCommands { get; } =
        Schemas.Keys.OrderBy(command => command, StringComparer.Ordinal).ToArray();

    internal static bool SupportsCommand(string command) => Schemas.ContainsKey(command);

    internal static bool IsDiscoveryRequest(IReadOnlyList<string>? fields)
        => fields is { Count: 1 }
           && string.Equals(fields[0], DiscoveryValue, StringComparison.Ordinal);

    internal static IReadOnlyList<string>? GetCompactFields(string command)
        => Schemas.TryGetValue(command, out var schema) ? schema.CompactFields : null;

    internal static bool TryResolveAlias(
        string command,
        string? collection,
        string requestedField,
        out string sourceField)
    {
        sourceField = string.Empty;
        if (!Schemas.TryGetValue(command, out var schema))
            return false;
        var registryName = collection is null ? requestedField : $"{collection}.{requestedField}";
        var definition = schema.Fields.FirstOrDefault(field =>
            string.Equals(field.Name, registryName, StringComparison.Ordinal));
        if (definition?.AliasFor is null)
            return false;
        sourceField = collection is not null
                      && definition.AliasFor.StartsWith(collection + ".", StringComparison.Ordinal)
            ? definition.AliasFor[(collection.Length + 1)..]
            : definition.AliasFor;
        return true;
    }

    internal static string GetHelpValuePlaceholder(string command)
        => SupportsCommand(command) ? "<csv|list>" : "<csv>";

    internal static string GetHelpDescription(string command)
        => $"Project validated {command} response fields (case-sensitive); use --fields list for the machine-readable catalog.";

    internal static bool TryValidate(
        string command,
        IReadOnlyList<string>? requestedFields,
        out ProjectionFieldValidationError? error)
    {
        error = null;
        if (requestedFields is null || !Schemas.TryGetValue(command, out var schema))
            return true;

        if (requestedFields.Contains(DiscoveryValue, StringComparer.Ordinal))
        {
            error = new ProjectionFieldValidationError(
                $"The --fields discovery value '{DiscoveryValue}' must be used by itself for command '{command}'.",
                $"Run `cdidx {command} --fields {DiscoveryValue}` without other field names.");
            return false;
        }

        foreach (var requestedField in requestedFields)
        {
            if (schema.ValidFieldNames.Contains(requestedField))
                continue;

            var nearby = ConsoleUi.FindClosestMatches(
                requestedField,
                schema.ValidFieldNames.Where(field => !string.Equals(field, "all", StringComparison.Ordinal)));
            var candidateHint = nearby.Count > 0
                ? $" Nearby valid fields: {string.Join(", ", nearby)}."
                : $" Valid fields include: {string.Join(", ", schema.ValidFieldNames.Take(8))}.";
            error = new ProjectionFieldValidationError(
                $"Unknown --fields value '{requestedField}' for command '{command}'.",
                $"{candidateHint.TrimStart()} Run `cdidx {command} --fields {DiscoveryValue}` for the complete catalog.");
            return false;
        }

        return true;
    }

    internal static JsonObject CreateDiscoveryDocument(string command)
    {
        var schema = Schemas[command];
        var fields = new JsonArray();
        foreach (var definition in schema.Fields)
        {
            var item = new JsonObject
            {
                ["name"] = definition.Name,
                ["kind"] = definition.Kind,
                ["deprecated"] = definition.Deprecated,
            };
            if (definition.AliasFor is not null)
                item["alias_for"] = definition.AliasFor;
            if (definition.Collection is not null)
                item["collection"] = definition.Collection;
            fields.Add(item);
        }

        return new JsonObject
        {
            ["api_version"] = "1",
            ["command"] = command,
            ["case_sensitive"] = true,
            ["discovery_value"] = DiscoveryValue,
            ["valid_fields"] = new JsonArray(
                schema.ValidFieldNames.Select(field => (JsonNode?)field).ToArray()),
            ["fields"] = fields,
        };
    }

    private static ProjectionCommandFieldSchema CreateSearchSchema()
        => Create(
            "search",
            ["file", "line"],
            builder => builder
                .Fields(
                    "api_version", "query", "path", "lang", "visibility", "chunk_start_line",
                    "chunk_end_line", "snippet_start_line", "snippet_end_line", "snippet",
                    "match_lines", "highlights", "match_origins", "match_facets", "result_kinds",
                    "test_file", "test_symbol", "test_fixture", "context_before", "context_after",
                    "truncated_line_count", "dropped_match_line_count", "snippet_lines", "max_line_width",
                    "exact", "raw_fts", "literal_highlights_available", "focus_mode", "focus_line",
                    "focus_column", "focus_reason", "next_match", "truncation_context", "score",
                    "enclosing_symbol_name", "enclosing_symbol_kind", "enclosing_symbol_start_line",
                    "enclosing_symbol_end_line", "enclosing_container_name")
                .Alias("file", "path")
                .Alias("line", "snippet_start_line"));

    private static ProjectionCommandFieldSchema CreateDefinitionSchema()
        => Create(
            "definition",
            ["file", "line", "column"],
            builder => builder
                .Fields(
                    "disambiguator", "api_version", "path", "symbol_id", "lang", "kind", "sub_kind",
                    "name", "line", "start_line", "start_column", "end_line", "body_start_line",
                    "body_end_line", "signature", "container_kind", "container_name",
                    "container_qualified_name", "visibility", "family_key", "return_type",
                    "is_metadata_target", "metadata_target_source", "same_line_signature_occurrence_index",
                    "content_omitted", "content_omitted_reason", "body_content", "body_content_start_line",
                    "body_content_end_line", "body_requested_start_line", "body_requested_end_line",
                    "body_effective_start_line", "body_effective_end_line", "complexity")
                .Alias("file", "path")
                .Alias("column", "start_column")
                .Alias("body", "body_content"));

    private static ProjectionCommandFieldSchema CreateFindSchema()
        => Create(
            "find",
            ["file", "line", "column"],
            builder => builder
                .Fields(
                    "api_version", "path", "lang", "line", "column", "length", "original_line_length",
                    "start_line", "end_line", "snippet", "snippet_truncated",
                    "snippet_truncation_context")
                .Alias("file", "path"));

    private static ProjectionCommandFieldSchema CreateStatusSchema()
        => Create(
            "status",
            [
                "api_version", "files", "chunks", "symbols", "references", "indexed_at", "git_head",
                "git_is_dirty", "head_freshness", "version", "graph_table_available",
                "hotspot_family_ready", "summary",
            ],
            builder => builder.Fields(
                "api_version", "files", "chunks", "symbols", "references", "unknown_extension_file_count",
                "unknown_extension_files", "unknown_extension_files_truncated",
                "unknown_extension_file_path_limit", "unknown_extension_extension_counts",
                "unknown_extension_category_counts", "unknown_extension_groups", "indexed_at",
                "last_workspace_freshened_at", "latest_modified", "project_root", "data_dir",
                "data_dir_source", "data_dir_mode", "db_file_mode", "database_permission_policy",
                "database_permission_diagnostics", "read_only_fallback", "wal_checkpoint_attempted",
                "wal_checkpoint_succeeded", "read_only_immutable_fallback", "wal_stale_snapshot_risk",
                "sqlite_connection_policy", "git_head", "git_is_dirty", "indexed_head_commit",
                "worktree_head_changed", "indexed_head_sha", "indexed_head_branch",
                "indexed_head_timestamp", "commits_ahead_of_indexed_head", "head_freshness", "languages",
                "symbol_kinds", "symbols_by_language", "graph_supported_languages", "git_executable",
                "extractors", "version", "summary", "graph_table_available", "graph_data_current",
                "reference_extraction_limits", "reference_graph_complete",
                "reference_extraction_cap_hits", "issues_table_available", "file_issues_data_current",
                "migration_in_progress", "index_complete", "hotspot_family_ready",
                "language_readiness", "csharp_symbol_name_ready", "csharp_metadata_target_ready",
                "sql_graph_contract_ready", "fold_ready", "index_writer_version",
                "index_newer_than_reader", "path_case_sensitive", "db_pragma_settings",
                "prepared_command_cache", "maintenance_guidance", "db_size_bytes", "wal_size_bytes",
                "process", "last_index_run", "workspace_check", "failed_checks", "repair_commands",
                "query_context", "stale_after_seconds", "index_age_seconds",
                "last_failed_or_partial_index_run", "mac_profile", "mac_profile_diagnostics",
                "trust_overrides", "hooks", "hook_diagnostics", "index_newer_than_reader_reason",
                "csharp_metadata_target_degraded_reason", "fold_ready_reason",
                "sql_graph_contract_degraded_reason", "hotspot_family_degraded_reason",
                "degraded_root_cause", "degraded_reason", "recommended_action", "alternative_action",
                "readiness_degradations"));

    private static ProjectionCommandFieldSchema CreateHotspotsSchema()
        => Create(
            "hotspots",
            ["name", "kind", "path", "line", "reference_count", "reference_score", "ranking_score"],
            builder => builder
                .Fields(
                    "name", "kind", "path", "line", "reference_count", "reference_score",
                    "ranking_score", "generic_name_penalty", "visibility", "container")
                .Alias("file", "path"));

    private static ProjectionCommandFieldSchema CreateReferencesSchema()
        => Create(
            "references",
            ["file", "line", "column"],
            builder => builder
                .Fields(
                    "api_version", "path", "lang", "symbol_name", "target_symbol_id",
                    "target_symbol_key", "reference_kind", "line", "column", "context",
                    "context_truncated", "container_kind", "container_name", "is_self_reference",
                    "is_mutual_recursion", "resolution_state", "resolution_candidate_count")
                .Alias("file", "path"));

    private static ProjectionCommandFieldSchema CreateCallGraphSchema(string command)
        => Create(
            command,
            ["file", "line", "column"],
            builder => builder
                .Fields(
                    "api_version", "path", "lang", "caller_kind", "caller_name", "callee_name",
                    "reference_kind", "reference_kinds", "has_mixed_reference_kinds",
                    "reference_kind_counts", "reference_weight_score", "first_line",
                    "first_column", "reference_count", "has_self_reference", "has_mutual_recursion",
                    "reference_extraction_limits", "reference_graph_complete",
                    "reference_extraction_cap_hits")
                .Alias("file", "path")
                .Alias("line", "first_line")
                .Alias("column", "first_column"));

    private static ProjectionCommandFieldSchema CreateSymbolsSchema()
        => Create(
            "symbols",
            ["path", "line", "kind", "name"],
            builder => builder
                .Fields(
                    "api_version", "path", "symbol_id", "lang", "kind", "sub_kind", "name", "line",
                    "start_line", "start_column", "end_line", "body_start_line", "body_end_line",
                    "signature", "container_kind", "container_name", "container_qualified_name",
                    "visibility", "family_key", "return_type", "is_metadata_target",
                    "metadata_target_source", "same_line_signature_occurrence_index")
                .Alias("file", "path"));

    private static ProjectionCommandFieldSchema CreateFilesSchema()
        => Create(
            "files",
            ["path", "lang", "lines"],
            builder => builder
                .Fields(
                    "api_version", "path", "lang", "size", "lines", "symbol_count",
                    "reference_count", "checksum", "modified", "indexed_at", "generated")
                .Alias("file", "path"));

    private static ProjectionCommandFieldSchema CreateLanguagesSchema()
        => Create(
            "languages",
            ["lang", "extensions", "symbol_extraction", "reference_extraction", "graph_queries"],
            builder => builder.Fields(
                "lang", "extensions", "exact_filenames", "filename_prefix_patterns", "legacy_patterns",
                "pattern_provenance", "aliases", "symbol_extraction", "reference_extraction",
                "graph_queries", "capability_gaps", "unsupported_guidance"));

    private static ProjectionCommandFieldSchema CreateImpactSchema()
    {
        var callerFields = new[]
        {
            "result_kind", "path", "lang", "caller_kind", "caller_name", "callee_name", "depth",
            "first_line", "first_column", "reference_count", "reference_kind", "reference_kinds",
            "reference_kind_counts",
        };
        var fileImpactFields = new[]
        {
            "result_kind", "path", "lang", "depth", "reference_count", "reference_kind",
            "reference_kinds", "reference_kind_counts",
        };
        var definitionFields = new[]
        {
            "api_version", "path", "symbol_id", "lang", "kind", "name", "line", "start_line",
            "start_column", "end_line", "body_start_line", "body_end_line", "signature",
            "container_kind", "container_name", "visibility", "return_type",
        };
        return Create(
            "impact",
            ["path", "caller_name", "callee_name", "depth", "first_line", "reference_count", "result_kind"],
            builder => builder
                .Fields(callerFields.Concat(fileImpactFields).Concat(definitionFields).Distinct(StringComparer.Ordinal))
                .Alias("file", "path")
                .Collection("callers", callerFields, pathAlias: true)
                .Collection("file_impacts", fileImpactFields, pathAlias: true)
                .Collection("definitions", definitionFields, pathAlias: true));
    }

    private static ProjectionCommandFieldSchema CreateMapSchema()
    {
        var fileFields = new[] { "path", "lang", "lines", "size", "symbol_count", "reference_count" };
        return Create(
            "map",
            [
                "api_version", "file_count", "total_lines", "total_symbols", "total_references",
                "indexed_at", "git_head", "git_is_dirty", "head_freshness", "graph_table_available",
                "sections",
            ],
            builder => builder
                .Fields(
                    "api_version", "file_count", "total_lines", "total_symbols", "total_references",
                    "indexed_at", "latest_modified", "workspace_indexed_at", "workspace_latest_modified",
                    "project_root", "git_head", "git_is_dirty", "indexed_head_commit", "indexed_head_sha",
                    "indexed_head_branch", "indexed_head_timestamp", "commits_ahead_of_indexed_head",
                    "worktree_head_changed", "head_freshness", "language_count", "module_count",
                    "entrypoint_count", "graph_table_available", "generated_code_policy",
                    "generated_file_count_excluded", "generated_file_count_excluded_authoritative",
                    "generated_file_filter_available", "decomposition_plan", "sections")
                .Collection("languages", ["lang", "files", "lines", "symbols", "references"])
                .Collection("modules", ["module", "files", "lines", "symbols", "references"])
                .Collection(
                    "top_files",
                    ["path", "lang", "lines", "size", "symbol_count", "reference_count", "score"],
                    pathAlias: true)
                .Collection("largest_files", fileFields, pathAlias: true)
                .Collection("symbol_rich_files", fileFields, pathAlias: true)
                .Collection("reference_rich_files", fileFields, pathAlias: true)
                .Collection(
                    "entrypoints",
                    ["path", "lang", "kind", "name", "line", "score", "match_type", "confidence", "hint_rank"],
                    pathAlias: true));
    }

    private static ProjectionCommandFieldSchema Create(
        string command,
        IReadOnlyList<string> compactFields,
        Action<ProjectionFieldSchemaBuilder> configure)
    {
        var builder = new ProjectionFieldSchemaBuilder();
        configure(builder);
        return builder.Build(command, compactFields);
    }

    private sealed class ProjectionFieldSchemaBuilder
    {
        private readonly List<ProjectionFieldDefinition> _fields =
        [
            new("all", "selector", AliasFor: null, Deprecated: false, Collection: null),
        ];

        private readonly HashSet<string> _names = new(StringComparer.Ordinal) { "all" };

        internal ProjectionFieldSchemaBuilder Fields(params string[] names)
            => Fields((IEnumerable<string>)names);

        internal ProjectionFieldSchemaBuilder Fields(IEnumerable<string> names)
        {
            foreach (var name in names)
                Add(new ProjectionFieldDefinition(name, "field", null, false, null));
            return this;
        }

        internal ProjectionFieldSchemaBuilder Alias(string name, string target)
        {
            Add(new ProjectionFieldDefinition(name, "alias", target, false, null));
            return this;
        }

        internal ProjectionFieldSchemaBuilder Collection(
            string name,
            IEnumerable<string> fields,
            bool pathAlias = false)
        {
            Add(new ProjectionFieldDefinition(name, "collection", null, false, name));
            foreach (var field in fields)
                Add(new ProjectionFieldDefinition($"{name}.{field}", "field", null, false, name));
            if (pathAlias)
                Add(new ProjectionFieldDefinition($"{name}.file", "alias", $"{name}.path", false, name));
            return this;
        }

        internal ProjectionCommandFieldSchema Build(string command, IReadOnlyList<string> compactFields)
        {
            var missingCompactField = compactFields.FirstOrDefault(field => !_names.Contains(field));
            if (missingCompactField is not null)
            {
                throw new InvalidOperationException(
                    $"Compact projection field '{missingCompactField}' is not registered for command '{command}'.");
            }
            return new ProjectionCommandFieldSchema(
                command,
                _fields.ToArray(),
                _fields.Select(field => field.Name).ToArray(),
                compactFields.ToArray());
        }

        private void Add(ProjectionFieldDefinition definition)
        {
            if (!_names.Add(definition.Name))
                return;
            _fields.Add(definition);
        }
    }
}

internal sealed record ProjectionFieldValidationError(string Message, string Hint);

internal sealed record ProjectionFieldDefinition(
    string Name,
    string Kind,
    string? AliasFor,
    bool Deprecated,
    string? Collection);

internal sealed record ProjectionCommandFieldSchema(
    string Command,
    IReadOnlyList<ProjectionFieldDefinition> Fields,
    IReadOnlyList<string> ValidFieldNames,
    IReadOnlyList<string> CompactFields);
