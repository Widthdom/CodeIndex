using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using CodeIndex.Database;

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

    private static readonly IReadOnlyList<string> StatusExplainCompactFields =
    [
        "api_version",
        "field",
        "meaning",
        "interpretation",
        "remediation",
    ];

    // Inspect keeps its existing single-bundle JSON contract instead of entering the
    // shared row-envelope path, but its selectors still need one typed source of truth.
    // inspect は既存の単一 bundle JSON 契約を維持するため shared row envelope には
    // 登録しないが、selector は同じ型由来レジストリで一元管理する。Issue #5098.
    private static readonly ProjectionCommandFieldSchema InspectSchema = CreateInspectSchema();

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

    internal static IReadOnlyList<string> GetStatusExplainCompactFields()
        => StatusExplainCompactFields;

    internal static JsonObject ProjectCompactStatusWorkspaceCheck(
        JsonObject workspaceCheck)
    {
        const string prefix = "workspace_check.";
        var projected = new JsonObject();
        foreach (var field in GetCompactFields("status")!)
        {
            if (!field.StartsWith(prefix, StringComparison.Ordinal))
                continue;

            var propertyName = field[prefix.Length..];
            if (workspaceCheck.TryGetPropertyValue(propertyName, out var value))
                projected[propertyName] = value?.DeepClone();
        }
        return projected;
    }

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

    internal static string GetInspectHelpDescription()
        => "Project inspect JSON groups or collection.field leaves; parent groups keep full rows, and --fields list prints the machine-readable catalog.";

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
        return CreateDiscoveryDocument(schema, caseSensitive: true);
    }

    internal static bool IsInspectDiscoveryRequest(IReadOnlyList<string>? fields)
        => fields is { Count: 1 }
           && string.Equals(fields[0], DiscoveryValue, StringComparison.Ordinal);

    internal static bool TryResolveInspectSelector(
        string rawSelector,
        out string canonicalSelector,
        out bool includeBody,
        out IReadOnlyList<string>? expansion,
        out ProjectionFieldValidationError? error)
    {
        canonicalSelector = string.Empty;
        includeBody = false;
        expansion = null;
        error = null;

        var normalized = rawSelector.Trim().ToLowerInvariant().Replace('-', '_');
        if (string.Equals(normalized, DiscoveryValue, StringComparison.Ordinal))
        {
            canonicalSelector = DiscoveryValue;
            return true;
        }
        var definition = InspectSchema.Fields.FirstOrDefault(field =>
            string.Equals(field.Name, normalized, StringComparison.Ordinal));
        if (definition is null)
        {
            var nearby = ConsoleUi.FindClosestMatches(normalized, InspectSchema.ValidFieldNames);
            var candidateHint = nearby.Count > 0
                ? $" Nearby valid fields: {string.Join(", ", nearby)}."
                : $" Valid fields include: {string.Join(", ", InspectSchema.ValidFieldNames.Take(8))}.";
            error = new ProjectionFieldValidationError(
                $"Unknown --fields value '{ConsoleUi.FormatBoundedValue(rawSelector)}' for command 'inspect'.",
                $"{candidateHint.TrimStart()} Run `cdidx inspect --fields {DiscoveryValue}` for the complete catalog.");
            return false;
        }

        canonicalSelector = definition.AliasFor ?? definition.Name;
        expansion = definition.ExpandsTo;
        includeBody = string.Equals(normalized, "body", StringComparison.Ordinal)
                      || canonicalSelector.StartsWith("definitions.", StringComparison.Ordinal)
                      && IsInspectDefinitionBodyContentField(canonicalSelector["definitions.".Length..]);
        return true;
    }

    internal static bool IsInspectDefinitionBodyContentField(string field)
        => string.Equals(field, "body_content", StringComparison.Ordinal)
           || field.StartsWith("body_", StringComparison.Ordinal)
           && !string.Equals(field, "body_start_line", StringComparison.Ordinal)
           && !string.Equals(field, "body_end_line", StringComparison.Ordinal);

    internal static JsonObject CreateInspectDiscoveryDocument()
    {
        var document = CreateDiscoveryDocument(InspectSchema, caseSensitive: false);
        document["normalization"] = "lowercase_and_hyphen_to_underscore";
        document["parent_child_behavior"] = "parent_selector_returns_full_rows";
        document["duplicate_behavior"] = "first_canonical_selector_wins";
        document["ordering"] = "canonical_request_order";
        return document;
    }

    private static JsonObject CreateDiscoveryDocument(
        ProjectionCommandFieldSchema schema,
        bool caseSensitive)
    {
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
            if (definition.ExpandsTo is not null)
            {
                item["expands_to"] = new JsonArray(
                    definition.ExpandsTo.Select(field => (JsonNode?)field).ToArray());
            }
            fields.Add(item);
        }

        return new JsonObject
        {
            ["api_version"] = "1",
            ["command"] = schema.Command,
            ["case_sensitive"] = caseSensitive,
            ["discovery_value"] = DiscoveryValue,
            ["valid_fields"] = new JsonArray(
                schema.ValidFieldNames.Select(field => (JsonNode?)field).ToArray()),
            ["fields"] = fields,
        };
    }

    private static ProjectionCommandFieldSchema CreateInspectSchema()
    {
        var definitionFields = GetJsonFieldNames<DefinitionResult>()
            .Where(field => !string.Equals(field, "content", StringComparison.Ordinal)
                            && !IsSymbolsOnlyPartialFamilyContinuationField(field))
            .Concat(["content_omitted", "content_omitted_reason"])
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var nearbyFields = GetJsonFieldNames<SymbolResult>()
            .Where(field => !IsSymbolsOnlyPartialFamilyContinuationField(field))
            .ToArray();
        var referenceFields = GetJsonFieldNames<ReferenceResult>()
            .Where(field => !IsInspectGraphBodyField(field))
            .ToArray();
        var callerFields = GetJsonFieldNames<CallerResult>()
            .Where(field => !IsInspectGraphBodyField(field))
            .ToArray();
        var calleeFields = GetJsonFieldNames<CalleeResult>()
            .Where(field => !IsInspectGraphBodyField(field))
            .ToArray();

        return Create(
            "inspect",
            [],
            builder => builder
                .Fields("file", "workspace", "graph", "source_excerpt", "candidates")
                .Collection("definitions", definitionFields, pathAlias: true)
                .Collection("nearby_symbols", nearbyFields, pathAlias: true)
                .Collection("references", referenceFields, pathAlias: true)
                .Collection("callers", callerFields, pathAlias: true)
                .Collection("callees", calleeFields, pathAlias: true)
                .Alias("metadata", "workspace")
                .Alias("trust", "graph")
                .Alias("definition", "definitions")
                .Alias("defs", "definitions")
                .Alias("body", "definitions")
                .Alias("source", "source_excerpt")
                .Alias("excerpt", "source_excerpt")
                .Alias("nearby", "nearby_symbols")
                .Alias("nearbysymbols", "nearby_symbols")
                .Alias("reference", "references")
                .Alias("refs", "references")
                .Alias("caller", "callers")
                .Alias("callee", "callees")
                .Alias("candidate", "candidates")
                .Alias("candidate_bundles", "candidates")
                .Alias("definitions.body", "definitions.body_content")
                .Alias("callers.line", "callers.first_line")
                .Alias("callers.column", "callers.first_column")
                .Alias("callees.line", "callees.first_line")
                .Alias("callees.column", "callees.first_column")
                .Expansion("outline", ["file", "definitions", "nearby_symbols"])
                .Expansion("outline_only", ["file", "definitions", "nearby_symbols"])
                .Expansion("outlineonly", ["file", "definitions", "nearby_symbols"]));
    }

    private static bool IsInspectGraphBodyField(string field)
        => string.Equals(field, "body_content", StringComparison.Ordinal)
           || field.StartsWith("body_", StringComparison.Ordinal)
           || field.StartsWith("callsite_", StringComparison.Ordinal);

    private static bool IsSymbolsOnlyPartialFamilyContinuationField(string field)
        => field is "family_member_total_count"
            or "family_member_total_count_authoritative"
            or "family_member_returned_count"
            or "family_member_omitted_count"
            or "family_member_remaining_count"
            or "family_members_recovery_cursor"
            or "family_members_next_cursor";

    private static ProjectionCommandFieldSchema CreateSearchSchema()
        => Create(
            "search",
            ["file", "line"],
            builder => builder
                .Fields(GetJsonFieldNames<CompactSearchResult>())
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
                    "body_end_line", "signature", "container_kind", "container_name", "visibility",
                    "return_type", "definition_sites", "partial_family_id", "representative_reason",
                    "family_members", "family_members_truncated", "exact_index_available", "degraded_reason",
                    "content_omitted", "content_omitted_reason", "body_content", "body_content_start_line",
                    "body_content_end_line", "body_requested_start_line", "body_requested_end_line",
                    "body_effective_start_line", "body_effective_end_line", "body_content_truncated",
                    "body_content_truncation_reasons", "body_content_recovery",
                    "body_content_next_start_line", "complexity")
                .Alias("file", "path")
                .Alias("column", "start_column")
                .Alias("body", "body_content"));

    private static ProjectionCommandFieldSchema CreateFindSchema()
        => Create(
            "find",
            ["file", "line", "column"],
            builder => builder
                .Fields(GetJsonFieldNames<FileFindResult>())
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
                "workspace_check.checked", "workspace_check.matches_workspace", "workspace_check.reason",
                "workspace_check.changed_file_count", "workspace_check.changed_files_truncated",
                "workspace_check.changed_files_path_limit", "workspace_check.changed_files_omitted_count",
                "workspace_check.missing_file_count", "workspace_check.missing_files_truncated",
                "workspace_check.missing_files_path_limit", "workspace_check.missing_files_omitted_count",
                "workspace_check.outside_sparse_cone_file_count", "workspace_check.outside_sparse_cone_files_truncated",
                "workspace_check.outside_sparse_cone_files_path_limit", "workspace_check.outside_sparse_cone_files_omitted_count",
                "workspace_check.unindexed_file_count", "workspace_check.unindexed_files_truncated",
                "workspace_check.unindexed_files_path_limit", "workspace_check.unindexed_files_omitted_count",
                "workspace_check.unverifiable_file_count", "workspace_check.unverifiable_files_truncated",
                "workspace_check.unverifiable_files_path_limit", "workspace_check.unverifiable_files_omitted_count",
                "workspace_check.scan_error_count", "workspace_check.scan_errors_truncated",
                "workspace_check.scan_errors_path_limit", "workspace_check.scan_errors_omitted_count",
            ],
            builder => builder
                .Fields(GetJsonFieldNames<StatusResult>())
                .Fields(GetJsonFieldNames<IndexFreshnessCheckResult>()
                    .Select(field => $"workspace_check.{field}"))
                .Fields(
                    "effective_config", "log_path", "field", "label", "ready", "degraded",
                    "remediation", "known_fields", "scope", "meaning", "source", "dependencies",
                    "dependencies_truncated", "interpretation", "repair_guidance", "redaction",
                    "known_field_limit", "known_fields_truncated"));

    private static ProjectionCommandFieldSchema CreateHotspotsSchema()
        => Create(
            "hotspots",
            ["name", "kind", "path", "line", "reference_count", "reference_score", "ranking_score"],
            builder => builder
                .Fields(GetJsonFieldNames<SymbolHotspotJsonResult>())
                .Fields(GetJsonFieldNames<GroupedSymbolHotspotJsonResult>())
                .Fields(
                    "name", "kind", "path", "line", "reference_count", "reference_score",
                    "ranking_score", "generic_name_penalty", "structural_rank_penalty",
                    "symbol_count", "lang", "visibility", "container")
                .Alias("file", "path"));

    private static ProjectionCommandFieldSchema CreateReferencesSchema()
        => Create(
            "references",
            ["file", "line", "column"],
            builder => builder
                .Fields(GetJsonFieldNames<ReferenceResult>())
                .Fields(
                    "api_version", "path", "lang", "symbol_name", "target_symbol_id",
                    "target_symbol_key", "reference_kind", "line", "column", "context",
                    "context_truncated", "container_kind", "container_name", "is_self_reference",
                    "is_mutual_recursion", "resolution_state", "resolution_candidate_count")
                .Alias("file", "path"));

    private static ProjectionCommandFieldSchema CreateCallGraphSchema(string command)
    {
        var isCallerCommand = string.Equals(command, "callers", StringComparison.Ordinal);
        var resultFields = isCallerCommand
            ? GetJsonFieldNames<CallerResult>()
            : GetJsonFieldNames<CalleeResult>();
        string[] compactFields = ["file", "line", "column"];
        return Create(
            command,
            compactFields,
            builder =>
            {
                builder
                    .Fields(resultFields)
                    .Fields(
                    "reference_extraction_limits", "reference_graph_complete",
                    "reference_extraction_cap_hits")
                    .Alias("file", "path")
                    .Alias("line", "first_line")
                    .Alias("column", "first_column");
            });
    }

    private static ProjectionCommandFieldSchema CreateSymbolsSchema()
        => Create(
            "symbols",
            [
                "path", "line", "kind", "name", "definition_sites", "partial_family_id",
                "representative_reason", "family_members_truncated", "family_member_total_count",
                "family_member_total_count_authoritative", "family_member_returned_count",
                "family_member_omitted_count", "family_member_remaining_count",
                "family_members_recovery_cursor", "family_members_next_cursor",
            ],
            builder => builder
                .Fields(GetJsonFieldNames<SymbolResult>())
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
            builder => builder.Fields(GetJsonFieldNames<LanguageEntryJsonResult>()));

    private static ProjectionCommandFieldSchema CreateImpactSchema()
    {
        var callerFields = GetJsonFieldNames<ImpactResult>().ToArray();
        var fileImpactFields = new[]
        {
            "result_kind", "path", "lang", "depth", "reference_count", "reference_kind",
            "reference_kinds", "reference_kind_counts",
        };
        var definitionFields = new[]
        {
            "api_version", "path", "symbol_id", "lang", "kind", "sub_kind", "name", "line",
            "start_line", "start_column", "end_line", "body_start_line", "body_end_line",
            "signature", "container_kind", "container_name", "visibility", "return_type",
            "definition_sites", "partial_family_id", "representative_reason", "family_members",
            "family_members_truncated",
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
                "languages", "modules", "entrypoints",
            ],
            builder => builder
                .Fields(
                    "api_version", "file_count", "total_lines", "total_symbols", "total_references",
                    "indexed_at", "latest_modified", "workspace_indexed_at", "workspace_latest_modified",
                    "project_root", "git_head", "git_is_dirty", "indexed_head_commit", "workspace_verified_head_sha", "indexed_head_sha",
                    "indexed_head_branch", "indexed_head_timestamp", "commits_ahead_of_indexed_head",
                    "worktree_head_changed", "head_freshness", "language_count", "module_count",
                    "entrypoint_count", "graph_table_available", "generated_code_policy",
                    "generated_file_count_excluded", "generated_file_count_excluded_authoritative",
                    "generated_file_filter_available", "decomposition_plan", "summary_only", "sections",
                    "section_properties", "depth", "output_byte_limit", "compact", "compact_limit",
                    "next_commands", "truncation")
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

    private static IEnumerable<string> GetJsonFieldNames<T>()
        => typeof(T)
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .OrderBy(property => property.MetadataToken)
            .Where(property =>
                property.GetCustomAttribute<JsonIgnoreAttribute>()?.Condition
                is not JsonIgnoreCondition.Always)
            .Select(property =>
                property.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name
                ?? JsonNamingPolicy.SnakeCaseLower.ConvertName(property.Name));

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

        internal ProjectionFieldSchemaBuilder Expansion(string name, IReadOnlyList<string> fields)
        {
            Add(new ProjectionFieldDefinition(name, "shorthand", null, false, null, fields));
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
    string? Collection,
    IReadOnlyList<string>? ExpandsTo = null);

internal sealed record ProjectionCommandFieldSchema(
    string Command,
    IReadOnlyList<ProjectionFieldDefinition> Fields,
    IReadOnlyList<string> ValidFieldNames,
    IReadOnlyList<string> CompactFields);
