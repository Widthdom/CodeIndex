using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using CodeIndex.Database;
using CodeIndex.Indexer;
using CodeIndex.Models;

namespace CodeIndex.Cli;

public static partial class QueryCommandRunner
{
    // Reference kinds valid on `references --kind`. Includes the compile-time type-position
    // `type_reference` edge emitted by ReferenceExtractor for C#/Java base lists, declaration
    // types, generic constraints, `throws`, `is`/`as`/`instanceof`, and XML-doc `cref` targets.
    // C++ `friend` declarations are also accepted because they are extractor-owned dependency
    // edges and participate in graph queries.
    // `references --kind` で有効な reference kind。ReferenceExtractor が C#/Java の継承リスト、
    // 宣言型、generic 制約、`throws`、`is`/`as`/`instanceof`、XML-doc `cref` 対象向けに出力する
    // compile-time な `type_reference` エッジを含む。C++ の `friend` 宣言も extractor が出す
    // dependency edge として受け付け、graph query にも参加させる。
    private static readonly string[] AllValidReferenceKinds =
        ["annotation", "attribute", "augmentation", "bcl_regex_without_timeout", "call", "consumes_hook", "dependency", "friend", "import", "instantiate", "razor_event_binding", "subscribe", "type_reference", "unsubscribe"];

    // Reference kinds that `callers` / `callees` can legitimately return. Metadata kinds
    // (`attribute` / `annotation`) and type-position edges (`type_reference`) are structurally
    // not call-graph edges, so those queries are rejected at the CLI / MCP boundary. C++ `friend`
    // is a graph-visible coupling edge.
    // `callers` / `callees` が正しく返せる reference kind。metadata 種別 (`attribute` / `annotation`)
    // や型位置エッジ (`type_reference`) は構造的に call-graph エッジではないため、CLI / MCP 境界で弾く。
    // C++ の `friend` は graph に出す coupling edge。
    private static readonly string[] CallGraphOnlyReferenceKinds =
        ["augmentation", "call", "consumes_hook", "friend", "instantiate", "razor_event_binding", "subscribe", "unsubscribe"];

    private static void WriteGraphReferenceKindHint(string command, string? kind, bool json)
    {
        if (json || string.IsNullOrWhiteSpace(kind))
            return;

        // `references` accepts all reference kinds emitted by the extractor; `callers` / `callees`
        // are restricted to call-graph kinds. Pick the right acceptance set per command.
        // `references` は extractor が出す全 reference kind を受け付ける。`callers` / `callees` は
        // call-graph 種別のみ。コマンドごとに許容集合を使い分ける。
        var acceptedKinds = command == "references" ? AllValidReferenceKinds : CallGraphOnlyReferenceKinds;
        if (acceptedKinds.Contains(kind))
            return;

        if (AllValidKinds.Contains(kind))
        {
            CommandErrorWriter.WriteStderr($"WARN: '{ConsoleUi.FormatBoundedValue(kind)}' is a symbol kind, but --kind on '{command}' filters by reference kind ({string.Join(", ", acceptedKinds)}). Use symbols/definition/hotspots/unused to filter by symbol kind.");
            return;
        }

        CommandErrorWriter.WriteStderr($"Hint: '{ConsoleUi.FormatBoundedValue(kind)}' is not a known reference kind for '{command}'. Available reference kinds: {string.Join(", ", acceptedKinds)}");
        var suggestion = ConsoleUi.FindClosestMatch(kind, acceptedKinds);
        if (suggestion != null)
            CommandErrorWriter.WriteStderr($"Did you mean: --kind {suggestion}?");
    }

    // Reference kinds that are valid `references --kind` values but NOT valid
    // `callers --kind` / `callees --kind` values.
    // - `attribute` / `annotation`: metadata rows are attributed to the enclosing body-range
    //   symbol rather than the annotated target itself, so `callers Obsolete --kind attribute`
    //   and equivalent `callees` queries return structurally wrong answers (method-level
    //   metadata reported under the enclosing class; file-level targets such as
    //   `[assembly: ...]` drop entirely because `container_name` is null).
    // - `type_reference`: type-position edges are compile-time references, not runtime calls,
    //   so `callers Foo --kind type_reference` misreports type mentions as caller edges
    //   (declaration types, generic constraints, `is`/`as`, XML-doc `cref`, etc.).
    // Reject these kinds at the CLI boundary and redirect users to
    // `references --kind <kind>` (which IS correct).
    // `references --kind` では有効だが、`callers --kind` / `callees --kind` では
    // 使ってはいけない reference kind。
    // - `attribute` / `annotation`: metadata 行は注釈対象そのものではなく body-range 上の
    //   外側シンボルに帰属するため、`callers` / `callees` でこの kind を受け付けると
    //   構造的に誤答する（メソッドレベルは外側クラスに寄り、`[assembly: ...]` のような
    //   ファイルレベルは `container_name = null` で丸ごと消える）。
    // - `type_reference`: 型位置エッジは compile-time な参照であり実行時呼び出しではない。
    //   `callers Foo --kind type_reference` は宣言型や generic 制約、`is`/`as`、XML-doc `cref`
    //   などの型言及を caller edge として誤って返す。
    // - `import`: import/include dependency edges are structural, not call-graph edges.
    // CLI 境界で弾き、正しい列挙パスである `references --kind <kind>` に誘導する。
    private static readonly HashSet<string> NonCallGraphReferenceKinds = new(StringComparer.Ordinal)
    {
        "attribute", "annotation", "type_reference", "import",
    };

    /// <summary>
    /// Reject non-call-graph reference kinds (`attribute` / `annotation` / `type_reference` / `import`) on
    /// commands (`callers` / `callees`) whose data model cannot answer those queries correctly.
    /// Returns true if the kind was rejected; the caller should then return
    /// `CommandExitCodes.UsageError`.
    /// `callers` / `callees` のようにデータモデル的に metadata / 型位置参照に答えられない
    /// コマンドで `--kind attribute` / `--kind annotation` / `--kind type_reference` / `--kind import` を弾く。
    /// 弾いた場合 true を返すので、呼び出し側は `CommandExitCodes.UsageError` を返すこと。
    /// </summary>
    private static bool TryRejectNonCallGraphKindForGraphCommand(string command, string? kind)
    {
        if (string.IsNullOrWhiteSpace(kind) || !NonCallGraphReferenceKinds.Contains(kind))
            return false;

        if (kind == "type_reference")
            CommandErrorWriter.WriteStderr($"Error: '--kind type_reference' is not supported on '{command}'. Type-position references are compile-time edges (declaration types, generic constraints, `is`/`as`/`instanceof`, XML-doc `cref`), not runtime calls, so `{command} --kind type_reference` cannot return accurate call-graph rows.");
        else if (kind == "import")
            CommandErrorWriter.WriteStderr($"Error: '--kind import' is not supported on '{command}'. Import references are structural dependency edges, not runtime calls, so `{command} --kind import` cannot return accurate call-graph rows.");
        else
            CommandErrorWriter.WriteStderr($"Error: '--kind {kind}' is not supported on '{command}'. Metadata references are attributed to the enclosing body-range symbol rather than the annotated target, so `{command} --kind {kind}` cannot return accurate rows (file-level targets such as `[assembly: ...]` drop entirely).");
        CommandErrorWriter.WriteStderr($"Hint: use `cdidx references <name> --kind {kind}` instead.");
        return true;
    }

    private static void WriteGraphSupportHint(string? lang)
    {
        if (lang != null && !ReferenceExtractor.SupportsLanguage(lang))
            CommandErrorWriter.WriteStderr($"Note: call-graph queries are not indexed for '{lang}'. Use search, definition, excerpt, or files instead.");
    }

    private static void WriteImpactResolutionHint(ImpactAnalysisResult analysis)
    {
        if (analysis.DefinitionCount > 0)
        {
            var kinds = string.Join(", ", analysis.Definitions.Select(d => d.Kind).Distinct().OrderBy(k => k));
            var pathPreview = analysis.Definitions
                .Select(d => d.Path)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(3)
                .ToList();
            var extra = analysis.DefinitionFileCount > pathPreview.Count
                ? $" (+{analysis.DefinitionFileCount - pathPreview.Count} more)"
                : string.Empty;
            CommandErrorWriter.WriteStderr($"Note: '{analysis.Query}' resolved to '{analysis.ResolvedName}' ({kinds}) as {ConsoleUi.Counted(analysis.DefinitionCount, "definition")} across {ConsoleUi.Counted(analysis.DefinitionFileCount, "file")}: {string.Join(", ", pathPreview)}{extra}");
        }
        else if (analysis.ZeroResultReason == "no_matching_definition")
        {
            CommandErrorWriter.WriteStderr($"Note: no indexed definition matched '{analysis.Query}'.");
        }

        if (!string.IsNullOrWhiteSpace(analysis.Suggestion))
            CommandErrorWriter.WriteStderr($"Hint: {analysis.Suggestion}");
    }

    // Emit a zero-result payload that distinguishes "real 0 hits" from "graph table missing
    // (degraded)". Without this, AI agents and humans cannot tell the index from a legacy /
    // read-only DB apart from a DB that genuinely has no callers for the query.
    // graph テーブル欠損による 0 と本物の 0 を JSON で区別できるようにする。
    private static void WriteDegradedGraphZeroResult(DbReader reader, string resultsKey, bool json, bool graphAvailable, JsonSerializerOptions jsonOptions,
        ExactQuerySignal? exactSignal = null, QueryCommandOptions? queryOptions = null, Action<JsonObject>? extraFields = null)
    {
        if (graphAvailable) return;
        if (json)
        {
            var payload = BuildJsonZeroResultPayload(reader, jsonOptions, resultsKey: resultsKey, graphTableAvailable: false, degraded: true, exactSignal: exactSignal, queryOptions: queryOptions, extraFields: extraFields);
            payload["note"] = "symbol_references table is missing in this index (legacy or read-only DB). Zero result is degraded, not authoritative.";
            Console.WriteLine(payload.ToJsonString(jsonOptions));
        }
        else
        {
            CommandErrorWriter.WriteStderr("WARN: symbol_references table missing — this 0-result is degraded, not authoritative.");
        }
    }

    private static void WriteGraphCountResult(DbReader reader, int count, int files, QueryCommandOptions options, JsonSerializerOptions jsonOptions,
        bool graphAvailable, ExactQuerySignal exactSignal, ExactZeroHintResult? exactZeroHint = null, GraphSupportOverride? graphSupportOverride = null, Action<JsonObject>? extraFields = null)
    {
        if (!options.Json)
        {
            Console.WriteLine($"{count}");
            WriteGraphSupportOverrideHint(graphSupportOverride);
            if (!graphAvailable)
                CommandErrorWriter.WriteStderr("WARN: symbol_references table missing — this count result is degraded, not authoritative.");
            return;
        }

        var payload = BuildCountJsonPayload(
            reader,
            jsonOptions,
            count,
            files,
            query: options.Query,
            queryOptions: options,
            graphTableAvailable: graphAvailable,
            degraded: !graphAvailable,
            deferAuthority: true);
        AddGraphSupportOverrideFields(payload, graphSupportOverride);
        if (options.Exact || options.ExactName)
            AddExactGraphJsonFields(payload, exactSignal);
        if (exactZeroHint != null)
            payload["exact_zero_hint"] = JsonSerializer.SerializeToNode(exactZeroHint, CliJsonSerializerContextFactory.Create(jsonOptions).ExactZeroHintResult);
        extraFields?.Invoke(payload);
        AddCountAuthorityJsonFields(payload);
        Console.WriteLine(payload.ToJsonString(jsonOptions));
    }

    private static void WriteGraphZeroJsonResult(DbReader reader, string resultsKey, JsonSerializerOptions jsonOptions, bool graphAvailable,
        ExactQuerySignal? exactSignal, ExactZeroHintResult? exactZeroHint = null, GraphSupportOverride? graphSupportOverride = null, QueryCommandOptions? queryOptions = null, Action<JsonObject>? extraFields = null)
    {
        var payload = BuildJsonZeroResultPayload(reader, jsonOptions, resultsKey: resultsKey, graphTableAvailable: graphAvailable, queryOptions: queryOptions);
        if (!graphAvailable)
        {
            payload["degraded"] = true;
            payload["note"] = "symbol_references table is missing in this index (legacy or read-only DB). Zero result is degraded, not authoritative.";
        }
        AddGraphSupportOverrideFields(payload, graphSupportOverride);
        if (exactSignal != null)
            AddExactGraphJsonFields(payload, exactSignal.Value);
        if (exactZeroHint != null)
            payload["exact_zero_hint"] = JsonSerializer.SerializeToNode(exactZeroHint, CliJsonSerializerContextFactory.Create(jsonOptions).ExactZeroHintResult);
        extraFields?.Invoke(payload);
        Console.WriteLine(payload.ToJsonString(jsonOptions));
    }

    private static void WriteGraphJsonResult<T>(T result, JsonTypeInfo<T> jsonTypeInfo, ExactQuerySignal exactSignal, JsonSerializerOptions jsonOptions, GraphSupportOverride? graphSupportOverride = null, Action<JsonObject>? extraFields = null)
    {
        var payload = JsonSerializer.SerializeToNode(result, jsonTypeInfo)!.AsObject();
        AddExactGraphJsonFields(payload, exactSignal);
        AddGraphSupportOverrideFields(payload, graphSupportOverride);
        extraFields?.Invoke(payload);
        Console.WriteLine(payload.ToJsonString(jsonOptions));
    }

    private static void WriteJsonResult<T>(T result, JsonTypeInfo<T> jsonTypeInfo, JsonSerializerOptions jsonOptions, Action<JsonObject>? extraFields = null)
    {
        var payload = JsonSerializer.SerializeToNode(result, jsonTypeInfo)!.AsObject();
        extraFields?.Invoke(payload);
        Console.WriteLine(payload.ToJsonString(jsonOptions));
    }

    private static void WriteJsonResultWithExactSignal<T>(T result, JsonTypeInfo<T> jsonTypeInfo, ExactQuerySignal exactSignal, JsonSerializerOptions jsonOptions)
    {
        var payload = JsonSerializer.SerializeToNode(result, jsonTypeInfo)!.AsObject();
        AddExactJsonFields(payload, exactSignal);
        Console.WriteLine(payload.ToJsonString(jsonOptions));
    }

    private static void AddExactGraphJsonFields(JsonObject payload, ExactQuerySignal exactSignal)
    {
        AddExactJsonFields(payload, exactSignal);
    }

    private static void AddExactJsonFields(JsonObject payload, ExactQuerySignal exactSignal)
    {
        payload["exact_index_available"] = exactSignal.ExactIndexAvailable;
        if (exactSignal.DegradedReason != null)
            payload["degraded_reason"] = exactSignal.DegradedReason;
    }

    private static void AddGraphSupportOverrideFields(JsonObject payload, GraphSupportOverride? graphSupportOverride)
    {
        if (graphSupportOverride == null)
            return;

        if (graphSupportOverride.GraphLanguage != null)
            payload["graph_language"] = graphSupportOverride.GraphLanguage;
        if (graphSupportOverride.GraphSupported.HasValue)
            payload["graph_supported"] = graphSupportOverride.GraphSupported.Value;
        if (graphSupportOverride.GraphSupportReason != null)
            payload["graph_support_reason"] = graphSupportOverride.GraphSupportReason;
        if (graphSupportOverride.GraphDegraded)
            payload["graph_degraded"] = true;
        if (graphSupportOverride.UnsupportedSymbolKind != null)
            payload["unsupported_symbol_kind"] = graphSupportOverride.UnsupportedSymbolKind;
    }

    private static void AddImpactOptionWarnings(JsonObject payload, QueryCommandOptions options)
    {
        if (!options.ImpactDeprecatedDepthUsed)
            return;

        JsonArray warnings;
        if (payload["warnings"] is JsonArray existingWarnings)
        {
            warnings = existingWarnings;
        }
        else
        {
            warnings = [];
            payload["warnings"] = warnings;
        }

        warnings.Add("--depth is deprecated for impact; use --max-hops instead.");
    }

    private static void WriteGraphSupportOverrideHint(GraphSupportOverride? graphSupportOverride)
    {
        if (graphSupportOverride == null)
            return;

        CommandErrorWriter.WriteStderr($"Note: {graphSupportOverride.GraphSupportReason}");
    }

    private sealed record GraphSupportOverride(
        string? GraphLanguage,
        bool? GraphSupported,
        string GraphSupportReason,
        string? UnsupportedSymbolKind,
        bool GraphDegraded);
}
