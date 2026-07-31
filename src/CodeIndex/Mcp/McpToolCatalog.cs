using System.Text.Json.Nodes;
using CodeIndex.Cli;
using CodeIndex.Database;
using CodeIndex.Models;

namespace CodeIndex.Mcp;

public partial class McpServer
{
    private static JsonArray CreateLanguageCapabilityEnum()
    {
        var values = new JsonArray();
        foreach (var capability in LanguageCapabilityCatalog.SupportedCapabilities)
            values.Add(capability);
        return values;
    }

    private static JsonArray CreateToolCatalog()
    {
        var tools = new JsonArray
        {
            CreateToolDefinition(
                "search",
                "Use this when starting broad code discovery, checking error text, or running named search audit recipes. Prefer it before shell grep; common next step is `excerpt`, `definition`, or `references` on the best hit. Returns snippets plus `result_stable_at`, `next_cursor`, and `next_step_suggestion` or `recovery_hint`. Use `prefix`/trailing `*` to widen token matching, `rawQuery` for FTS5 syntax, `exactSubstring` for case-sensitive identity, and `tokenBoundary` when a code phrase must not match inside longer identifiers. Details and examples: USER_GUIDE.md#search. / 広いコード調査、エラー文言確認、search audit recipe 実行の起点に使う。shell grep より優先し、次は最有力ヒットに `excerpt` / `definition` / `references` を使う。`prefix` / 末尾 `*` / `rawQuery` / `exactSubstring` / `tokenBoundary` の詳細と例は USER_GUIDE.md#search を参照。",
                new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["query"] = new JsonObject { ["type"] = "string", ["description"] = "Search query text. Append `*` to a token to make that token a prefix phrase (`計算*` matches `計算する`)." },
                        ["recipe"] = new JsonObject { ["type"] = "string", ["description"] = "Run a named search audit recipe instead of a single query. Use `listRecipes:true` to discover available recipe names." },
                        ["listRecipes"] = new JsonObject { ["type"] = "boolean", ["description"] = "List built-in and configured search audit recipes without running a search.", ["default"] = false },
                        ["auditScope"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray { "source", "all" }, ["description"] = "Recipe runs only: source applies the recipe's production-code default path/exclusion scope; all searches every indexed path unless other filters exclude it." },
                        ["limit"] = new JsonObject { ["type"] = "integer", ["description"] = "Max results (default: 20). Responses include `truncated` and `more_available` when more rows exist.", ["default"] = QueryCommandRunner.DefaultQueryLimit },
                        ["lang"] = new JsonObject { ["type"] = "string", ["description"] = "Filter by language (e.g. csharp, python, javascript)" },
                        ["snippetLines"] = new JsonObject { ["type"] = "integer", ["description"] = "Max snippet lines per result (default: 8, max: 20)", ["default"] = 8, ["minimum"] = 1, ["maximum"] = SearchSnippetFormatter.MaxSnippetLines },
                        ["snippetFocus"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray { "quality", "leftmost", "proximity" }, ["description"] = "Snippet anchoring mode matching CLI `--snippet-focus`: quality (default), leftmost, or proximity.", ["default"] = "quality" },
                        ["maxLineWidth"] = new JsonObject { ["type"] = "integer", ["description"] = "Clamp very long single-line snippets per line (default: 512; 0 disables clamping). Match lines are clamped around the first match; non-match lines are clamped from the head. Each clamp inserts a `...(+N)...` marker showing how many chars were elided.", ["default"] = LineWidthFormatter.DefaultMaxLineWidth, ["minimum"] = 0, ["maximum"] = LineWidthFormatter.MaxAllowedLineWidth },
                        ["rawQuery"] = new JsonObject { ["type"] = "boolean", ["description"] = "Use raw FTS5 syntax instead of literal-safe quoting: content:term, NEAR(a b, 5), OR, NOT, parenthesized groups, prefix*, and quoted phrases.", ["default"] = false },
                        ["cursor"] = new JsonObject { ["type"] = "string", ["description"] = "Optional pagination cursor returned as `next_cursor` by a previous search response with the same query and filters. Compare `result_stable_at` across pages to detect index drift." },
                        ["path"] = new JsonObject { ["oneOf"] = new JsonArray { new JsonObject { ["type"] = "string" }, new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" } } }, ["description"] = "Prefer or restrict glob-style path patterns. `*` and `?` are wildcards. Accepts a single string or an array; multiple values are OR'd together." },
                        ["excludePaths"] = StringOrArraySchema("Exclude glob-style path patterns. `*` and `?` are wildcards."),
                        ["excludeTests"] = new JsonObject { ["type"] = "boolean", ["description"] = "Exclude likely test files", ["default"] = false },
                        ["includeGenerated"] = new JsonObject { ["type"] = "boolean", ["description"] = "Include files detected as generated code", ["default"] = false },
                        ["since"] = new JsonObject { ["type"] = "string", ["description"] = "Filter to files modified since this ISO 8601 timestamp" },
                        ["noDedup"] = new JsonObject { ["type"] = "boolean", ["description"] = "Disable overlapping-chunk deduplication and return every raw chunk hit; useful for debugging chunk boundaries or measuring raw match density.", ["default"] = false },
                        ["exactSubstring"] = new JsonObject { ["type"] = "boolean", ["description"] = "Preferred explicit name for search's exact mode: case-sensitive exact substring match (bypasses FTS5).", ["default"] = false },
                        ["tokenBoundary"] = new JsonObject { ["type"] = "boolean", ["description"] = "Case-sensitive exact code-phrase match that also requires identifier/token boundaries around the full query, so `new HttpClient` does not match `new HttpClientHandler`.", ["default"] = false },
                        ["exact"] = new JsonObject { ["type"] = "boolean", ["description"] = "Backward-compatible alias for `exactSubstring`.", ["default"] = false },
                        ["prefix"] = new JsonObject { ["type"] = "boolean", ["description"] = "Opt into FTS5 prefix expansion for every token in `query`. Cannot be combined with `exact`/`exactSubstring`/`tokenBoundary`.", ["default"] = false },
                        ["requireBefore"] = new JsonObject { ["oneOf"] = new JsonArray { new JsonObject { ["type"] = "string" }, new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" } } }, ["description"] = "Keep search matches only when this guard query appears within `guardWindow` lines before the primary match. Accepts a string or string array." },
                        ["requireAfter"] = new JsonObject { ["oneOf"] = new JsonArray { new JsonObject { ["type"] = "string" }, new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" } } }, ["description"] = "Keep search matches only when this guard query appears within `guardWindow` lines after the primary match. Accepts a string or string array." },
                        ["rejectBefore"] = new JsonObject { ["oneOf"] = new JsonArray { new JsonObject { ["type"] = "string" }, new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" } } }, ["description"] = "Drop search matches when this guard query appears within `guardWindow` lines before the primary match. Accepts a string or string array." },
                        ["rejectAfter"] = new JsonObject { ["oneOf"] = new JsonArray { new JsonObject { ["type"] = "string" }, new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" } } }, ["description"] = "Drop search matches when this guard query appears within `guardWindow` lines after the primary match. Accepts a string or string array." },
                        ["guardWindow"] = new JsonObject { ["type"] = "integer", ["description"] = $"Line window for guard queries (default: {DbReader.DefaultSearchGuardWindow}, max: {DbReader.MaxSearchGuardWindow}).", ["default"] = DbReader.DefaultSearchGuardWindow, ["minimum"] = 0, ["maximum"] = DbReader.MaxSearchGuardWindow },
                        ["guardScope"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray { "window", "same-line" }, ["description"] = "Evaluate guard queries in the line window or only on the same line before/after the primary match.", ["default"] = "window" },
                        ["countOnly"] = new JsonObject { ["type"] = "boolean", ["description"] = "Return only count metadata and a small top-file histogram; omit row payloads.", ["default"] = false },
                        ["format"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray { "full", "count", "compact" }, ["description"] = "Response shape: full rows, count-only metadata, or compact file/line rows without snippets.", ["default"] = "full" }
                    },
                    ["anyOf"] = new JsonArray
                    {
                        new JsonObject { ["required"] = new JsonArray { "query" } },
                        new JsonObject { ["required"] = new JsonArray { "recipe" } },
                        new JsonObject { ["required"] = new JsonArray { "listRecipes" } }
                    }
                },
                ReadOnlyAnnotations()),
            CreateToolDefinition(
                "definition",
                "Use this when you know or suspect a symbol name and need its declaration before editing. Prefer `exactName:true` for identity checks; common next step is `references` or `excerpt`. Resolve symbol definitions with ranges, signatures, and optional body content. Pass `lsp_compatible:true` to add `uri` and LSP `range` fields to each result. `exact` is the legacy alias documented in USER_GUIDE.md's flag compatibility table. Examples: `definition {\"query\":\"McpServer\"}`; `definition {\"query\":\"HandleMessage\",\"lang\":\"csharp\",\"includeBody\":true,\"exactName\":true}`. / シンボル名が分かる、または推測できるときに編集前の宣言確認に使う。identity 確認では `exactName:true` を優先し、次は `references` または `excerpt` を使う。定義範囲、シグネチャ、必要に応じて本体内容付きでシンボル定義を解決。例: `definition {\"query\":\"McpServer\"}`; `definition {\"query\":\"HandleMessage\",\"lang\":\"csharp\",\"includeBody\":true,\"exactName\":true}`。",
                new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["query"] = new JsonObject { ["type"] = "string", ["description"] = "Symbol name pattern to resolve" },
                        ["kind"] = new JsonObject { ["type"] = "string", ["description"] = "Filter by symbol kind" },
                        ["lang"] = new JsonObject { ["type"] = "string", ["description"] = "Filter by language" },
                        ["limit"] = new JsonObject { ["type"] = "integer", ["description"] = "Max results (default: 20)", ["default"] = QueryCommandRunner.DefaultQueryLimit },
                        ["visibility"] = new JsonObject { ["oneOf"] = new JsonArray { new JsonObject { ["type"] = "string" }, new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" } } }, ["description"] = "Filter symbol visibility. Accepts a value, comma-separated string, or array." },
                        ["excludeVisibility"] = new JsonObject { ["oneOf"] = new JsonArray { new JsonObject { ["type"] = "string" }, new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" } } }, ["description"] = "Exclude symbol visibility values. Accepts a value, comma-separated string, or array." },
                        ["includeBody"] = new JsonObject { ["type"] = "boolean", ["description"] = "Include body content when body ranges are available", ["default"] = false },
                        ["lsp_compatible"] = new JsonObject { ["type"] = "boolean", ["description"] = "Add file:// uri and LSP range fields to each result", ["default"] = false },
                        ["lspCompatible"] = new JsonObject { ["type"] = "boolean", ["description"] = "Alias for `lsp_compatible` for JSON-style clients.", ["default"] = false },
                        ["path"] = new JsonObject { ["oneOf"] = new JsonArray { new JsonObject { ["type"] = "string" }, new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" } } }, ["description"] = "Prefer or restrict matches to paths containing this text. Accepts a single string or an array; multiple values are OR'd together." },
                        ["excludePaths"] = StringOrArraySchema("Exclude any paths containing these texts"),
                        ["excludeTests"] = new JsonObject { ["type"] = "boolean", ["description"] = "Exclude likely test files", ["default"] = false },
                        ["includeGenerated"] = new JsonObject { ["type"] = "boolean", ["description"] = "Include files detected as generated code", ["default"] = false },
                        ["since"] = new JsonObject { ["type"] = "string", ["description"] = "Filter to symbols in files modified since this ISO 8601 timestamp" },
                        ["exactName"] = new JsonObject { ["type"] = "boolean", ["description"] = "Preferred explicit name for exact symbol-name equality: NFKC + Unicode CaseFold exact name match instead of substring, so `Run` no longer also returns `RunAsync`.", ["default"] = false },
                        ["exact"] = new JsonObject { ["type"] = "boolean", ["description"] = "Backward-compatible alias for `exactName`.", ["default"] = false },
                        ["format"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray { "full", "count", "compact" }, ["description"] = "Response shape: full rows, count-only metadata, or compact file/line rows without excerpts.", ["default"] = "full" }
                    },
                    ["required"] = new JsonArray { "query" }
                },
                ReadOnlyAnnotations()),
            CreateToolDefinition(
                "references",
                "Use this when you need usage sites, examples, tests, metadata references, or type-position references for a symbol. Prefer it after `definition`; common next step is `excerpt` on representative rows or `callers`/`callees` for runtime impact. Search indexed symbol references such as call sites. Non-empty responses include `next_step_suggestion`; empty responses include `recovery_hint`. Pass `lsp_compatible:true` to add `uri` and LSP `range` fields to each result. For exact matches, use `exactName`; `exact` is the legacy alias documented in USER_GUIDE.md's flag compatibility table. When `kind` is omitted, all indexed reference kinds including metadata uses (`attribute` / `annotation`), JavaScript/TypeScript discriminant tags (`type_tag`), C# BCL Regex timeout audit rows (`bcl_regex_without_timeout`), and compile-time type-position references (`type_reference`) stay visible, and identical constructor `call` + `instantiate` rows at one physical site are collapsed. Pass `kind: \"type_tag\"` to enumerate discriminant comparisons such as `shape.type === \"circle\"`. Pass `kind: \"type_reference\"` to enumerate declaration types, generic constraints, `is`/`as`/`instanceof`, and XML-doc `cref` targets. Pass `kind: \"bcl_regex_without_timeout\"` with query `Regex` to audit direct System.Text.RegularExpressions.Regex construction without a timeout argument. Examples: `references {\"query\":\"Run\"}`; `references {\"query\":\"Service\",\"kind\":\"type_reference\",\"lang\":\"csharp\"}`. / シンボルの利用箇所、例、テスト、metadata 参照、型位置参照を調べるときに使う。`definition` の後に優先し、次は代表行の `excerpt` または実行時影響の `callers` / `callees` を使う。`kind: \"type_tag\"` で JavaScript / TypeScript の discriminant 比較を列挙できる。`kind: \"bcl_regex_without_timeout\"` と query `Regex` で timeout 引数なしの直接 `System.Text.RegularExpressions.Regex` 生成を監査できる。例: `references {\"query\":\"Run\"}`; `references {\"query\":\"Service\",\"kind\":\"type_reference\",\"lang\":\"csharp\"}`。",
                new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["query"] = new JsonObject { ["type"] = "string", ["description"] = "Referenced symbol name pattern to search for" },
                        ["kind"] = new JsonObject { ["type"] = "string", ["description"] = "Filter by reference kind (call, instantiate, subscribe, friend, attribute, annotation, type_reference, type_tag, bcl_regex_without_timeout)" },
                        ["lang"] = new JsonObject { ["type"] = "string", ["description"] = "Filter by language" },
                        ["limit"] = new JsonObject { ["type"] = "integer", ["description"] = "Max results (default: 20). Responses include `truncated`, `more_available`, and `next_offset` when more rows exist.", ["default"] = QueryCommandRunner.DefaultQueryLimit },
                        ["offset"] = new JsonObject { ["type"] = "integer", ["description"] = "Zero-based result offset for pagination; use `next_offset` from a truncated response.", ["default"] = 0, ["minimum"] = 0 },
                        ["maxLineWidth"] = new JsonObject { ["type"] = "integer", ["description"] = "Clamp very long single-line context payloads per result (default: 512; 0 disables clamping)", ["default"] = LineWidthFormatter.DefaultMaxLineWidth, ["minimum"] = 0, ["maximum"] = LineWidthFormatter.MaxAllowedLineWidth },
                        ["lsp_compatible"] = new JsonObject { ["type"] = "boolean", ["description"] = "Add file:// uri and LSP range fields to each result", ["default"] = false },
                        ["lspCompatible"] = new JsonObject { ["type"] = "boolean", ["description"] = "Alias for `lsp_compatible` for JSON-style clients.", ["default"] = false },
                        ["path"] = new JsonObject { ["oneOf"] = new JsonArray { new JsonObject { ["type"] = "string" }, new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" } } }, ["description"] = "Prefer or restrict matches to paths containing this text. Accepts a single string or an array; multiple values are OR'd together." },
                        ["excludePaths"] = StringOrArraySchema("Exclude any paths containing these texts"),
                        ["excludeTests"] = new JsonObject { ["type"] = "boolean", ["description"] = "Exclude likely test files", ["default"] = false },
                        ["includeGenerated"] = new JsonObject { ["type"] = "boolean", ["description"] = "Include files detected as generated code", ["default"] = false },
                        ["includeQualifiedCommonCalls"] = new JsonObject { ["type"] = "boolean", ["description"] = "Include unresolved receiver/type-qualified C# calls with common member names. Resolved qualified calls are already included by default.", ["default"] = false },
                        ["exactName"] = new JsonObject { ["type"] = "boolean", ["description"] = "Preferred explicit name for exact referenced-symbol equality. Uses NFKC + Unicode CaseFold so `Run` no longer matches `RunAsync`.", ["default"] = false },
                        ["exact"] = new JsonObject { ["type"] = "boolean", ["description"] = "Backward-compatible alias for `exactName`.", ["default"] = false },
                        ["countOnly"] = new JsonObject { ["type"] = "boolean", ["description"] = "Return only count metadata and a small top-file histogram; omit row payloads.", ["default"] = false },
                        ["format"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray { "full", "count", "compact" }, ["description"] = "Response shape: full rows, count-only metadata, or compact file/line/column rows without context.", ["default"] = "full" }
                    },
                    ["required"] = new JsonArray { "query" }
                },
                ReadOnlyAnnotations()),
            CreateToolDefinition(
                "callers",
                "Use this when you need to know what calls or depends on a callee symbol before changing it. Prefer it after `definition`/`references`; common next step is `excerpt` on high-ranked caller rows. Find caller symbols that reference a callee. For exact matches, use `exactName`; `exact` is the legacy alias documented in USER_GUIDE.md's flag compatibility table. When `kind` is omitted, only executable kinds (`call`, `instantiate`, `subscribe`) are returned; pass `kind: \"friend\"` explicitly for C++ friend access/coupling edges, while metadata uses (`attribute` / `annotation`) and compile-time type-position references (`type_reference`) do not pollute caller edges; identical constructor `call` + `instantiate` rows at one physical site also collapse. Public `reference_kind`, `reference_kinds`, and `reference_kind_counts` use the same canonical vocabulary. Each grouped row additionally exposes `reference_kinds` (sorted distinct kinds behind the row) and `has_mixed_reference_kinds` so callers do not have to trust the single summary label when a container mixes `call` + `subscribe` edges. The existing `reference_kind` scalar is retained for back-compat and carries the canonical summary priority (`instantiate` > `subscribe` > `call`); `rawKinds` preserves raw-kind priority. `callers` / `callees` are not a reliable path to metadata or type-position references — metadata rows are attributed to their enclosing body-range symbol (for a class-level declaration, that is the class itself; for a file-level target such as `[assembly: ...]`, `containerName` is `null` and the row drops from these graph queries entirely), and `type_reference` rows are compile-time type mentions (declaration types, generic constraints, `is`/`as`/`instanceof`, XML-doc `cref`) rather than runtime calls. Use `references` with `kind: \"attribute\"`, `\"annotation\"`, or `\"type_reference\"` instead. Examples: `callers {\"query\":\"HandleRequest\"}`; `callers {\"query\":\"ExecuteAsync\",\"kind\":\"call\",\"rankBy\":\"weighted\",\"lang\":\"csharp\"}`. / callee シンボルの変更前に呼び出し元や依存元を知りたいときに使う。`definition` / `references` の後に優先し、次は上位 caller 行の `excerpt` を使う。指定シンボルを参照している呼び出し元シンボルを探す。完全一致には `exactName` を使う。`exact` は USER_GUIDE.md の flag compatibility table に記載された legacy alias。`kind` 未指定時は実行可能な種別 (`call` / `instantiate` / `subscribe`) だけを返す。C++ friend の access/coupling edge は `kind: \"friend\"` を明示する。metadata 使用 (`attribute` / `annotation`) と compile-time な型位置参照 (`type_reference`) が phantom caller edge として混入しないようにする。同じ物理位置にある constructor の `call` + `instantiate` 重複行も集約する。公開 `reference_kind`、`reference_kinds`、`reference_kind_counts` は同じ canonical 語彙を使う。各グループ行には `reference_kinds`（行内の distinct kind をソートした配列）と `has_mixed_reference_kinds` も追加で返すため、container が `call` + `subscribe` を混在させている行で要約 1 ラベルに騙されずに済む。既存のスカラー `reference_kind` は後方互換のため維持され、canonical な優先サマリー種別（`instantiate` > `subscribe` > `call`）を持つ。`rawKinds` 指定時は raw kind の優先順を持つ。metadata 行の container は注釈対象そのものではなく body-range 上の外側シンボル（クラス直下宣言ならクラス、ファイルレベル target なら `null`）になり、`type_reference` は実行時呼び出しではなく宣言型・generic 制約・`is`/`as`/`instanceof`・XML-doc `cref` といった compile-time な型言及なので、`callers` / `callees` は metadata / 型位置参照の列挙に向かない。Metadata / 型位置参照の列挙は `references --kind attribute|annotation|type_reference` / MCP `references` を使う。例: `callers {\"query\":\"HandleRequest\"}`; `callers {\"query\":\"ExecuteAsync\",\"kind\":\"call\",\"rankBy\":\"weighted\",\"lang\":\"csharp\"}`。",
                new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["query"] = new JsonObject { ["type"] = "string", ["description"] = "Callee symbol name pattern to search for" },
                        ["kind"] = new JsonObject { ["type"] = "string", ["description"] = "Filter by edge kind. Default results use the canonical call, instantiate, subscribe vocabulary; non-default `friend` remains available explicitly. Metadata and type-only kinds — metadata (attribute, annotation), type-position (type_reference), and JS/TS discriminant narrowing (type_tag) — are rejected here; use `references` with the desired kind instead." },
                        ["rawKinds"] = new JsonObject { ["type"] = "boolean", ["description"] = "Preserve raw reference kinds instead of canonical CLI grouping, matching `--raw-kinds`.", ["default"] = false },
                        ["includeQualifiedCommonCalls"] = new JsonObject { ["type"] = "boolean", ["description"] = "Include unresolved receiver/type-qualified C# calls with common member names. Resolved qualified calls are already included by default.", ["default"] = false },
                        ["includeMemberReads"] = new JsonObject { ["type"] = "boolean", ["description"] = "Include canonical `member_read` value-read edges. Defaults to false; legacy indexes stored these reads as `call` and cannot separate them.", ["default"] = false },
                        ["rankBy"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray { "weighted", "count", "kind" }, ["description"] = "Primary ranking recipe: weighted score then count (default; instantiate=3.0, call=1.0, subscribe=0.1), raw count, or kind priority then count. Only ties use exact-case/name relevance, production before test before docs path category, then stable path/location/name fields. Responses expose the complete applied precedence in rankingRecipe.", ["default"] = "weighted" },
                        ["lang"] = new JsonObject { ["type"] = "string", ["description"] = "Filter by language" },
                        ["limit"] = new JsonObject { ["type"] = "integer", ["description"] = "Max results (default: 20). Responses include `truncated`, `more_available`, and `next_offset` when more rows exist.", ["default"] = QueryCommandRunner.DefaultQueryLimit },
                        ["offset"] = new JsonObject { ["type"] = "integer", ["description"] = "Zero-based result offset for pagination; use `next_offset` from a truncated response.", ["default"] = 0, ["minimum"] = 0 },
                        ["path"] = new JsonObject { ["oneOf"] = new JsonArray { new JsonObject { ["type"] = "string" }, new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" } } }, ["description"] = "Prefer or restrict matches to paths containing this text. Accepts a single string or an array; multiple values are OR'd together." },
                        ["excludePaths"] = StringOrArraySchema("Exclude any paths containing these texts"),
                        ["excludeTests"] = new JsonObject { ["type"] = "boolean", ["description"] = "Exclude likely test files", ["default"] = false },
                        ["includeGenerated"] = new JsonObject { ["type"] = "boolean", ["description"] = "Include files detected as generated code", ["default"] = false },
                        ["exactName"] = new JsonObject { ["type"] = "boolean", ["description"] = "Preferred explicit name for exact callee-name equality. Uses NFKC + Unicode CaseFold so `Run` no longer matches `RunAsync`.", ["default"] = false },
                        ["exact"] = new JsonObject { ["type"] = "boolean", ["description"] = "Backward-compatible alias for `exactName`.", ["default"] = false },
                        ["countOnly"] = new JsonObject { ["type"] = "boolean", ["description"] = "Return only count metadata and a small top-file histogram; omit row payloads.", ["default"] = false },
                        ["format"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray { "full", "count", "compact" }, ["description"] = "Response shape: full rows, count-only metadata, or compact file/line rows without excerpts.", ["default"] = "full" }
                    },
                    ["required"] = new JsonArray { "query" }
                },
                ReadOnlyAnnotations()),
            CreateToolDefinition(
                "callees",
                "Use this when you need to know what a caller/container symbol invokes or depends on. Prefer it after `definition` or `outline`; common next step is `excerpt` on a callee row. Find callees used by a caller/container symbol. For exact matches, use `exactName`; `exact` is the legacy alias documented in USER_GUIDE.md's flag compatibility table. When `kind` is omitted, only executable kinds (`call`, `instantiate`, `subscribe`) are returned; pass `kind: \"friend\"` explicitly for C++ friend access/coupling edges, while metadata uses (`attribute` / `annotation`) and compile-time type-position references (`type_reference`) do not pollute callee edges; identical constructor `call` + `instantiate` rows at one physical site also collapse. Public `reference_kind`, `reference_kinds`, and `reference_kind_counts` use the same canonical vocabulary. Each grouped row additionally exposes `reference_kinds` (sorted distinct kinds behind the row) and `has_mixed_reference_kinds` for symmetry with `callers`, even though rows are already split per kind on this side. The existing `reference_kind` scalar is retained for back-compat and carries the same kind value. `callees` is not a reliable path to metadata or type-position references — the container assigned to an attribute / annotation row is the enclosing body-range symbol, not the annotated declaration, so `callees Method1 --kind attribute` does not return the attributes on `Method1`, and `type_reference` rows are compile-time type mentions (declaration types, generic constraints, `is`/`as`/`instanceof`, XML-doc `cref`) rather than runtime calls. Use `references` with `kind: \"attribute\"`, `\"annotation\"`, or `\"type_reference\"` instead. Examples: `callees {\"query\":\"Run\"}`; `callees {\"query\":\"Program.Main\",\"kind\":\"instantiate\",\"lang\":\"csharp\",\"limit\":10}`. / caller/container シンボルが呼ぶ先や依存先を知りたいときに使う。`definition` または `outline` の後に優先し、次は callee 行の `excerpt` を使う。呼び出し元シンボルが使っている呼び出し先を探す。完全一致には `exactName` を使う。`exact` は USER_GUIDE.md の flag compatibility table に記載された legacy alias。`kind` 未指定時は実行可能な種別 (`call` / `instantiate` / `subscribe`) だけを返す。C++ friend の access/coupling edge は `kind: \"friend\"` を明示する。metadata 使用 (`attribute` / `annotation`) と compile-time な型位置参照 (`type_reference`) が phantom callee edge として混入しないようにする。同じ物理位置にある constructor の `call` + `instantiate` 重複行も集約する。公開 `reference_kind`、`reference_kinds`、`reference_kind_counts` は同じ canonical 語彙を使う。各グループ行には `callers` との対称性のため `reference_kinds`（行内の distinct kind をソートした配列）と `has_mixed_reference_kinds` も返る（`callees` 側は元々 kind ごとに行を分けているため通常は単一要素）。既存のスカラー `reference_kind` は後方互換のため維持され、同じ kind 値を持つ。metadata 行の container は注釈対象自身ではなく body-range 上の外側シンボルになるため、`callees` で `Method1 --kind attribute` を引いても `Method1` に付いた属性は返らない。`type_reference` は実行時呼び出しではなく宣言型・generic 制約・`is`/`as`/`instanceof`・XML-doc `cref` といった compile-time な型言及なので、`callees` は metadata / 型位置参照の列挙に向かない。Metadata / 型位置参照の列挙は `references --kind attribute|annotation|type_reference` / MCP `references` を使う。例: `callees {\"query\":\"Run\"}`; `callees {\"query\":\"Program.Main\",\"kind\":\"instantiate\",\"lang\":\"csharp\",\"limit\":10}`。",
                new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["query"] = new JsonObject { ["type"] = "string", ["description"] = "Caller/container symbol name pattern to search for" },
                        ["kind"] = new JsonObject { ["type"] = "string", ["description"] = "Filter by edge kind. Default results use the canonical call, instantiate, subscribe vocabulary; non-default graph kinds remain available explicitly. Metadata and type-only kinds — metadata (attribute, annotation), type-position (type_reference), and JS/TS discriminant narrowing (type_tag) — are rejected here; use `references` with the desired kind instead." },
                        ["rawKinds"] = new JsonObject { ["type"] = "boolean", ["description"] = "Preserve raw reference kinds instead of canonical CLI grouping, matching `--raw-kinds`.", ["default"] = false },
                        ["includeQualifiedCommonCalls"] = new JsonObject { ["type"] = "boolean", ["description"] = "Include unresolved receiver/type-qualified C# calls with common member names. Resolved qualified calls are already included by default.", ["default"] = false },
                        ["includeMemberReads"] = new JsonObject { ["type"] = "boolean", ["description"] = "Include canonical `member_read` value-read edges. Defaults to false; legacy indexes stored these reads as `call` and cannot separate them.", ["default"] = false },
                        ["rankBy"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray { "weighted", "count", "kind" }, ["description"] = "Primary ranking recipe: weighted score then count (default; instantiate=3.0, call=1.0, subscribe=0.1), raw count, or kind priority then count. Only ties use exact-case/name relevance, production before test before docs path category, then stable path/location/name fields. Responses expose the complete applied precedence in rankingRecipe.", ["default"] = "weighted" },
                        ["lang"] = new JsonObject { ["type"] = "string", ["description"] = "Filter by language" },
                        ["limit"] = new JsonObject { ["type"] = "integer", ["description"] = "Max results (default: 20). Responses include `truncated`, `more_available`, and `next_offset` when more rows exist.", ["default"] = QueryCommandRunner.DefaultQueryLimit },
                        ["offset"] = new JsonObject { ["type"] = "integer", ["description"] = "Zero-based result offset for pagination; use `next_offset` from a truncated response.", ["default"] = 0, ["minimum"] = 0 },
                        ["path"] = new JsonObject { ["oneOf"] = new JsonArray { new JsonObject { ["type"] = "string" }, new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" } } }, ["description"] = "Prefer or restrict matches to paths containing this text. Accepts a single string or an array; multiple values are OR'd together." },
                        ["excludePaths"] = StringOrArraySchema("Exclude any paths containing these texts"),
                        ["excludeTests"] = new JsonObject { ["type"] = "boolean", ["description"] = "Exclude likely test files", ["default"] = false },
                        ["includeGenerated"] = new JsonObject { ["type"] = "boolean", ["description"] = "Include files detected as generated code", ["default"] = false },
                        ["exactName"] = new JsonObject { ["type"] = "boolean", ["description"] = "Preferred explicit name for exact caller/container equality. Uses NFKC + Unicode CaseFold so `Run` no longer matches `RunAsync`.", ["default"] = false },
                        ["exact"] = new JsonObject { ["type"] = "boolean", ["description"] = "Backward-compatible alias for `exactName`.", ["default"] = false },
                        ["countOnly"] = new JsonObject { ["type"] = "boolean", ["description"] = "Return only count metadata and a small top-file histogram; omit row payloads.", ["default"] = false },
                        ["format"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray { "full", "count", "compact" }, ["description"] = "Response shape: full rows, count-only metadata, or compact file/line rows without excerpts.", ["default"] = "full" }
                    },
                    ["required"] = new JsonArray { "query" }
                },
                ReadOnlyAnnotations()),
            CreateToolDefinition(
                "symbols",
                "Use this when discovering candidate symbols before `definition`, `references`, `callers`, or `callees`. Prefer `exactName:true` when the name must match exactly. Search for code symbols (functions, classes, interfaces, imports) by name pattern. Page metadata includes authoritative totals, `result_stable_at`, and an opaque generation-bound `next_cursor`; pass it back unchanged with the same filters, format, and limit. `exact` is the legacy alias documented in USER_GUIDE.md's flag compatibility table. Examples: `symbols {\"query\":\"Service\"}`; `symbols {\"query\":\"Run\",\"kind\":\"function\",\"lang\":\"csharp\",\"exactName\":true}`. / `definition` / `references` / `callers` / `callees` の前に候補シンボルを探すときに使う。名前を厳密一致させるなら `exactName:true` を優先する。シンボル（関数、クラス、インターフェース、import）を名前パターンで検索。ページ metadata は authoritative total、`result_stable_at`、generation-bound な opaque `next_cursor` を含む。同じ filter / format / limit で cursor を変更せず渡す。例: `symbols {\"query\":\"Service\"}`; `symbols {\"query\":\"Run\",\"kind\":\"function\",\"lang\":\"csharp\",\"exactName\":true}`。",
                new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["query"] = new JsonObject { ["type"] = "string", ["description"] = "Symbol name pattern to search for. Treated as a literal substring (no `|`-OR sugar), so operator symbols such as `operator |` remain searchable." },
                        ["names"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" }, ["description"] = "Optional list of additional symbol name patterns, OR-joined with `query`. Use this to resolve multiple candidate names in one call." },
                        ["kind"] = new JsonObject { ["type"] = "string", ["description"] = "Filter by symbol kind (function, class, interface, import, etc.)" },
                        ["lang"] = new JsonObject { ["type"] = "string", ["description"] = "Filter by language" },
                        ["visibility"] = new JsonObject { ["oneOf"] = new JsonArray { new JsonObject { ["type"] = "string" }, new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" } } }, ["description"] = "Filter symbol visibility. Accepts a value, comma-separated string, or array." },
                        ["excludeVisibility"] = new JsonObject { ["oneOf"] = new JsonArray { new JsonObject { ["type"] = "string" }, new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" } } }, ["description"] = "Exclude symbol visibility values. Accepts a value, comma-separated string, or array." },
                        ["limit"] = new JsonObject { ["type"] = "integer", ["description"] = "Max results (default: 20)", ["default"] = QueryCommandRunner.DefaultQueryLimit },
                        ["cursor"] = new JsonObject { ["type"] = "string", ["maxLength"] = MaxMcpQueryCursorCharacters, ["description"] = "Opaque generation-bound next_cursor returned by a previous symbols page. Keep filters, format, and limit unchanged." },
                        ["path"] = new JsonObject { ["oneOf"] = new JsonArray { new JsonObject { ["type"] = "string" }, new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" } } }, ["description"] = "Prefer or restrict matches to paths containing this text. Accepts a single string or an array; multiple values are OR'd together." },
                        ["excludePaths"] = StringOrArraySchema("Exclude any paths containing these texts"),
                        ["excludeTests"] = new JsonObject { ["type"] = "boolean", ["description"] = "Exclude likely test files", ["default"] = false },
                        ["includeGenerated"] = new JsonObject { ["type"] = "boolean", ["description"] = "Include files detected as generated code", ["default"] = false },
                        ["since"] = new JsonObject { ["type"] = "string", ["description"] = "Filter to symbols in files modified since this ISO 8601 timestamp" },
                        ["exactName"] = new JsonObject { ["type"] = "boolean", ["description"] = "Preferred explicit name for exact symbol-name equality instead of substring, so `Run` no longer matches `RunAsync`/`RunImpact`.", ["default"] = false },
                        ["exact"] = new JsonObject { ["type"] = "boolean", ["description"] = "Backward-compatible alias for `exactName`.", ["default"] = false },
                        ["countOnly"] = new JsonObject { ["type"] = "boolean", ["description"] = "Return count metadata and a top-file histogram without symbol rows.", ["default"] = false },
                        ["format"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray { "full", "count", "compact" }, ["description"] = "Response shape: full symbol rows, count metadata, or compact file/line/kind/name rows.", ["default"] = "full" }
                    }
                },
                ReadOnlyAnnotations()),
            CreateToolDefinition(
                "files",
                "Use this when you need to locate indexed files by path, language, or recent-change scope before reading content. Prefer `outline` or `excerpt` as the next step after choosing a file. List indexed files, optionally filtered by name pattern and language. Page metadata includes authoritative totals, `result_stable_at`, and an opaque generation-bound `next_cursor`; pass it back unchanged with the same filters and limit. / 内容を読む前に path、言語、最近の変更範囲でインデックス済みファイルを探すときに使う。ファイルを選んだ後は `outline` または `excerpt` を優先する。インデックス済みファイルを一覧（名前パターン・言語でフィルタ可能）。ページ metadata は authoritative total、`result_stable_at`、generation-bound な opaque `next_cursor` を含む。同じ filter / limit で cursor を変更せず渡す。",
                new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["query"] = new JsonObject { ["type"] = "string", ["description"] = "File path pattern to filter by" },
                        ["lang"] = new JsonObject { ["type"] = "string", ["description"] = "Filter by language" },
                        ["limit"] = new JsonObject { ["type"] = "integer", ["description"] = "Max results (default: 20)", ["default"] = QueryCommandRunner.DefaultQueryLimit },
                        ["cursor"] = new JsonObject { ["type"] = "string", ["maxLength"] = MaxMcpQueryCursorCharacters, ["description"] = "Opaque generation-bound next_cursor returned by a previous files page. Keep filters and limit unchanged." },
                        ["path"] = new JsonObject { ["oneOf"] = new JsonArray { new JsonObject { ["type"] = "string" }, new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" } } }, ["description"] = "Additional path filter text. Accepts a single string or an array; multiple values are OR'd together." },
                        ["excludePaths"] = StringOrArraySchema("Exclude any paths containing these texts"),
                        ["excludeTests"] = new JsonObject { ["type"] = "boolean", ["description"] = "Exclude likely test files", ["default"] = false },
                        ["includeGenerated"] = new JsonObject { ["type"] = "boolean", ["description"] = "Include files detected as generated code", ["default"] = false },
                        ["since"] = new JsonObject { ["type"] = "string", ["description"] = "Filter to files modified since this ISO 8601 timestamp" },
                        ["orderBySize"] = new JsonObject { ["type"] = "boolean", ["description"] = "Sort by indexed byte size descending before path, matching byte-oriented CLI views.", ["default"] = false },
                        ["rawBytes"] = new JsonObject { ["type"] = "boolean", ["description"] = "CLI-compatible alias for byte-oriented file listing. MCP returns indexed size metadata, not raw file bytes.", ["default"] = false }
                    }
                },
                ReadOnlyAnnotations()),
            CreateToolDefinition(
                "excerpt",
                "Use this after `search`, `definition`, `references`, `outline`, or `map` identifies a file and line range. Prefer focused excerpts over whole-file reads; common next step is `outline` for neighboring structure. Reconstruct a file excerpt from indexed chunks for a given line range. Responses preserve requested and context-expanded effective ranges separately and report the indexed total line count. Successful responses include `next_step_suggestion`; empty responses include `recovery_hint`. / `search` / `definition` / `references` / `outline` / `map` でファイルと行範囲を絞った後に使う。ファイル全体ではなく必要範囲の抜粋を優先し、次は周辺構造確認の `outline` を使う。指定行範囲について、インデックス済みチャンクからファイル抜粋を再構成する。response は requested range と context 展開後の effective range を分けて保持し、インデックス済みの総行数も返す。",
                new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["path"] = new JsonObject { ["type"] = "string", ["description"] = "Indexed file path" },
                        ["startLine"] = new JsonObject { ["type"] = "integer", ["description"] = "Start line (1-based)" },
                        ["endLine"] = new JsonObject { ["type"] = "integer", ["description"] = "End line (default: startLine)" },
                        ["before"] = new JsonObject { ["type"] = "integer", ["description"] = "Extra context lines before the range (clamped to 1000)", ["default"] = 0, ["minimum"] = 0 },
                        ["after"] = new JsonObject { ["type"] = "integer", ["description"] = "Extra context lines after the range (clamped to 1000)", ["default"] = 0, ["minimum"] = 0 },
                        ["focusLine"] = new JsonObject { ["type"] = "integer", ["description"] = "Optional line inside the excerpt to focus when clamping; without focusColumn, the leading window is retained", ["minimum"] = 1 },
                        ["focusColumn"] = new JsonObject { ["type"] = "integer", ["description"] = "Optional 1-based column to keep centered when clamping long single-line content; must be within the focused line length", ["minimum"] = 1 },
                        ["focusLength"] = new JsonObject { ["type"] = "integer", ["description"] = "Optional focused span width when clamping (default: 1); requires focusColumn", ["default"] = 1, ["minimum"] = 1 },
                        ["maxLineWidth"] = new JsonObject { ["type"] = "integer", ["description"] = "Clamp very long single-line excerpt payloads per line (default: 512; 0 disables clamping)", ["default"] = LineWidthFormatter.DefaultMaxLineWidth, ["minimum"] = 0, ["maximum"] = LineWidthFormatter.MaxAllowedLineWidth },
                        ["maxOutputBytes"] = new JsonObject { ["type"] = "integer", ["description"] = "Cap excerpt content bytes at a line boundary (default: 1048576; maximum: 1048576). Responses set `truncated: true` and `truncation_reason: output_size_cap` when the cap is reached.", ["default"] = MaxLineByteLength, ["minimum"] = 1, ["maximum"] = MaxLineByteLength }
                    },
                    ["required"] = new JsonArray { "path", "startLine" }
                },
                ReadOnlyAnnotations()),
            CreateToolDefinition(
                "find_in_file",
                "Use this when the target file is already known and you need literal or regex navigation inside it. Prefer `excerpt` on returned lines as the next step. Find literal substring matches inside one known indexed file or a small explicit file list, with line numbers and short surrounding context. / 対象ファイルが既に分かっていて、その中を literal または regex で移動したいときに使う。次は返された行の `excerpt` を優先する。既知のインデックス済みファイル1件または少数の明示ファイル群の中で、行番号と短い前後文脈付きの一致を探す。",
                new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["query"] = new JsonObject { ["type"] = "string", ["description"] = "Literal substring to look for" },
                        ["path"] = new JsonObject { ["oneOf"] = new JsonArray { new JsonObject { ["type"] = "string" }, new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" } } }, ["description"] = "Required file/path scope. Accepts a single string or an array; multiple values are OR'd together." },
                        ["limit"] = new JsonObject { ["type"] = "integer", ["description"] = "Max matching occurrences to return (default: 20)", ["default"] = QueryCommandRunner.DefaultQueryLimit },
                        ["lang"] = new JsonObject { ["type"] = "string", ["description"] = "Filter by language" },
                        ["excludePaths"] = StringOrArraySchema("Exclude any paths containing these texts"),
                        ["excludeTests"] = new JsonObject { ["type"] = "boolean", ["description"] = "Exclude likely test files", ["default"] = false },
                        ["includeGenerated"] = new JsonObject { ["type"] = "boolean", ["description"] = "Include files detected as generated code", ["default"] = false },
                        ["before"] = new JsonObject { ["type"] = "integer", ["description"] = "Context lines before the match (default: 0, clamped to 1000)", ["default"] = 0, ["minimum"] = 0 },
                        ["after"] = new JsonObject { ["type"] = "integer", ["description"] = "Context lines after the match (default: 0, clamped to 1000)", ["default"] = 0, ["minimum"] = 0 },
                        ["snippetLines"] = new JsonObject { ["type"] = "integer", ["description"] = "Total snippet lines around each match when before/after are not set (1-20)", ["default"] = 1, ["minimum"] = 1, ["maximum"] = SearchSnippetFormatter.MaxSnippetLines },
                        ["focusLine"] = new JsonObject { ["type"] = "integer", ["description"] = "Optional 1-based line that must contain the match", ["minimum"] = 1 },
                        ["focusColumn"] = new JsonObject { ["type"] = "integer", ["description"] = "Optional 1-based column that must be inside the match span", ["minimum"] = 1 },
                        ["maxLineWidth"] = new JsonObject { ["type"] = "integer", ["description"] = "Clamp very long single-line snippets per line (default: 512; 0 disables clamping)", ["default"] = LineWidthFormatter.DefaultMaxLineWidth, ["minimum"] = 0, ["maximum"] = LineWidthFormatter.MaxAllowedLineWidth },
                        ["exact"] = new JsonObject { ["type"] = "boolean", ["description"] = "Case-sensitive literal substring match. Default is case-insensitive literal substring matching.", ["default"] = false },
                        ["regex"] = new JsonObject { ["type"] = "boolean", ["description"] = "Treat query as a .NET regular expression with a 500 ms timeout", ["default"] = false }
                    },
                    ["required"] = new JsonArray { "query", "path" }
                },
                ReadOnlyAnnotations()),
            CreateToolDefinition(
                "map",
                "Use this when orienting in an unfamiliar repo, module, language mix, or hotspot area before searching. Prefer `search`, `symbols`, `outline`, or `excerpt` as the next step after choosing a path. Return a repo-level overview with selectable sections (`tree`, `languages`, `hotspots`, `metrics`) and optional module depth control. / 不慣れなリポジトリ、モジュール、言語構成、hotspot 領域を search 前に把握するときに使う。path を選んだ後は `search` / `symbols` / `outline` / `excerpt` を優先する。セクション選択（`tree`, `languages`, `hotspots`, `metrics`）とモジュール深さ制御に対応したリポジトリ俯瞰情報を返す。",
                new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["limit"] = new JsonObject { ["type"] = "integer", ["description"] = "Max items per section (default: 10)", ["default"] = QueryCommandRunner.DefaultMapLimit },
                        ["lang"] = new JsonObject { ["type"] = "string", ["description"] = "Filter by language" },
                        ["path"] = new JsonObject { ["oneOf"] = new JsonArray { new JsonObject { ["type"] = "string" }, new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" } } }, ["description"] = "Prefer or restrict glob-style path patterns. `*` and `?` are wildcards. Accepts a single string or an array; multiple values are OR'd together." },
                        ["excludePaths"] = StringOrArraySchema("Exclude glob-style path patterns. `*` and `?` are wildcards."),
                        ["excludeTests"] = new JsonObject { ["type"] = "boolean", ["description"] = "Exclude likely test files", ["default"] = false },
                        ["sections"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray { "tree", "languages", "hotspots", "metrics" } }, ["description"] = "Only include selected response sections. Omit for the full backward-compatible map." },
                        ["depth"] = new JsonObject { ["type"] = "integer", ["description"] = $"Maximum module/tree depth to include; 0 keeps only root-level modules. Requests above {MaxMcpMapDepth} are clamped with an MCP warning.", ["minimum"] = 0, ["maximum"] = MaxMcpMapDepth },
                        ["minEntrypointConfidence"] = new JsonObject { ["type"] = "number", ["description"] = "Minimum entrypoint confidence threshold, from 0.0 to 1.0, matching CLI `--min-entrypoint-confidence`.", ["minimum"] = 0, ["maximum"] = 1 }
                    }
                },
                ReadOnlyAnnotations()),
            CreateToolDefinition(
                "analyze_symbol",
                "Use this when one symbol needs a compact dossier and you would otherwise chain `definition`, `references`, `callers`, and `callees`. Each bounded graph section reports `total`, `returned`, `truncated`, and an independent `next_cursor`; continue one section by passing that cursor with unchanged query filters. Common next step is `excerpt` on the most relevant rows. Bundle definition, nearby symbols, references, callers, callees, file metadata, and graph-support metadata for one symbol query. For exact matches, use `exactName`; `exact` is the legacy alias documented in USER_GUIDE.md's flag compatibility table. Bundled caller/callee rows carry the same `reference_kind` (preferred summary kind, back-compat) plus `reference_kinds` (sorted distinct) and `has_mixed_reference_kinds` fields as the standalone `callers` / `callees` tools, so mixed `call` + `subscribe` containers stay visible in the bundle. Supports `format: count|compact`; CLI `since` filtering is intentionally not exposed because the backing analysis reader does not support it yet. / 1つのシンボルについて compact な dossier が必要で、`definition` / `references` / `callers` / `callees` を連続呼び出ししそうなときに使う。上限付きの各 graph section は `total`、`returned`、`truncated`、独立した `next_cursor` を返す。同じ query filter のまま cursor を渡すと、その section を継続できる。次は重要行の `excerpt` を使う。1つのシンボルクエリに対して、定義、近傍シンボル、参照、caller、callee、ファイルメタデータ、グラフ対応メタデータをまとめて返す。完全一致には `exactName` を使う。`exact` は USER_GUIDE.md の flag compatibility table に記載された legacy alias。バンドルされた caller / callee 行にも単独の `callers` / `callees` と同じ `reference_kind`（後方互換の優先サマリー種別）、`reference_kinds`（distinct kind の昇順配列）、`has_mixed_reference_kinds` が付くため、`call` + `subscribe` が混在するコンテナも要約 1 ラベルに潰れず見える。`format: count|compact` 対応。CLI の `since` filter は backing analysis reader 未対応のため意図的に未公開。",
                new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["query"] = new JsonObject { ["type"] = "string", ["description"] = "Symbol name to inspect" },
                        ["limit"] = new JsonObject { ["type"] = "integer", ["description"] = "Max items per section (default: 10)", ["default"] = QueryCommandRunner.DefaultMapLimit },
                        ["cursor"] = new JsonObject { ["type"] = "string", ["description"] = "Continue exactly one truncated graph section with its next_cursor; keep query filters unchanged." },
                        ["lang"] = new JsonObject { ["type"] = "string", ["description"] = "Filter by language" },
                        ["includeBody"] = new JsonObject { ["type"] = "boolean", ["description"] = "Include body content in definitions when available", ["default"] = false },
                        ["maxLineWidth"] = new JsonObject { ["type"] = "integer", ["description"] = "Clamp bundled reference context lines so single-line files stay bounded (default: 512; 0 disables clamping)", ["default"] = LineWidthFormatter.DefaultMaxLineWidth, ["minimum"] = 0, ["maximum"] = LineWidthFormatter.MaxAllowedLineWidth },
                        ["path"] = new JsonObject { ["oneOf"] = new JsonArray { new JsonObject { ["type"] = "string" }, new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" } } }, ["description"] = "Prefer or restrict paths containing this text. Accepts a single string or an array; multiple values are OR'd together." },
                        ["excludePaths"] = StringOrArraySchema("Exclude any paths containing these texts"),
                        ["excludeTests"] = new JsonObject { ["type"] = "boolean", ["description"] = "Exclude likely test files", ["default"] = false },
                        ["includeGenerated"] = new JsonObject { ["type"] = "boolean", ["description"] = "Include files detected as generated code", ["default"] = false },
                        ["exactName"] = new JsonObject { ["type"] = "boolean", ["description"] = "Preferred explicit name for exact bundle symbol-name equality. Propagates through definitions, references, callers, and callees so `Run` no longer pulls in `RunAsync` / `RunImpact`.", ["default"] = false },
                        ["exact"] = new JsonObject { ["type"] = "boolean", ["description"] = "Backward-compatible alias for `exactName`.", ["default"] = false },
                        ["countOnly"] = new JsonObject { ["type"] = "boolean", ["description"] = "Return only dossier counts and graph support metadata.", ["default"] = false },
                        ["format"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray { "full", "count", "compact" }, ["description"] = "Response shape: full dossier, count-only metadata, or compact file/line rows.", ["default"] = "full" }
                    },
                    ["required"] = new JsonArray { "query" }
                },
                ReadOnlyAnnotations()),
            CreateToolDefinition(
                "impact_analysis",
                "Use this when planning a symbol change and you need transitive caller impact rather than just direct references. Prefer `definition` first to confirm identity; common next step is `excerpt` on impacted callers or files. Compute the transitive caller chain for a symbol. The symbol-level BFS walks only call-graph kinds (`call`, `instantiate`, `subscribe`) and excludes metadata-only edges (`attribute`, `annotation`, `type_reference`) so metadata cycles do not inflate caller counts. Multiple edge kinds from the same caller to the same target are counted and returned separately, with `reference_kind`, `reference_kinds`, and `reference_kindCounts` on each caller row. When a scoped query resolves to a single class / struct / interface but no symbol-level callers exist, may return heuristic file-level dependency hints instead; those file hints can include metadata edges, so check `impact_mode`, `heuristic`, and `file_impacts`. When `truncated` is true, inspect `truncated_reason` (`user_limit` means raising `limit` returns more; `safety_cap` means the graph is likely pathological and raising `limit` will not help). Pass `withPaths: true` when you need the call chain via specific intermediates — each caller then carries a `paths` array of shortest routes (issue #1536). / シンボル変更を計画していて、直接参照だけでなく推移的 caller 影響が必要なときに使う。identity 確認には先に `definition` を優先し、次は影響 caller/file の `excerpt` を使う。シンボルの推移的呼び出しチェーンを算出。symbol-level BFS は call graph 種別（`call`、`instantiate`、`subscribe`）のみを辿り、metadata-only edge（`attribute`、`annotation`、`type_reference`）を除外するため、metadata cycle で caller 件数が膨らまない。同じ caller から同じ target への複数 edge kind は別々に数えて返し、各 caller 行に `reference_kind`、`reference_kinds`、`reference_kindCounts` が付く。scoped query が単一の class / struct / interface に解決されても symbol-level caller が無い場合は、代わりに heuristic な file-level dependency hint を返すことがある。この file hint は metadata edge を含み得るため、`impact_mode`・`heuristic`・`file_impacts` を確認すること。`truncated` が真のときは `truncated_reason` を見て、`user_limit` なら `limit` を増やせば残りも取得可能、`safety_cap` ならグラフが病的で `limit` を増やしても解消しないことを区別すること。中間シンボル経由の経路が必要な場合は `withPaths: true` を渡すと、各 caller に経路配列 `paths` が付く（issue #1536）。",
                new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["query"] = new JsonObject { ["type"] = "string", ["description"] = "Symbol name to analyze impact for" },
                        ["maxHops"] = new JsonObject { ["type"] = "integer", ["description"] = "Max BFS hops, inclusive (default: 5; maxHops: N returns callers at hop 1..N, so a chain A→B→C→D queried against D with maxHops: 2 yields C at hop 1 and B at hop 2; 0 resolves the symbol without traversing callers). Server-side cap: 50; requests above the cap are clamped and a `warnings` entry plus `max_hops_requested` field is added to the response.", ["default"] = 5, ["minimum"] = 0, ["maximum"] = 50 },
                        ["maxDepth"] = new JsonObject { ["type"] = "integer", ["description"] = "Deprecated alias for `maxHops`; accepted during the compatibility period and reported in `warnings` when used.", ["minimum"] = 0, ["maximum"] = 50 },
                        ["limit"] = new JsonObject { ["type"] = "integer", ["description"] = "Max total callers or heuristic file-level dependency hints to return (default: 50). Check `truncated` when the limit is reached; `truncated_reason` distinguishes `user_limit` (raise `limit` to get more) from `safety_cap` (pathological graph, raising `limit` will not help).", ["default"] = QueryCommandRunner.DefaultImpactLimit },
                        ["lang"] = new JsonObject { ["type"] = "string", ["description"] = "Filter by language" },
                        ["path"] = new JsonObject { ["oneOf"] = new JsonArray { new JsonObject { ["type"] = "string" }, new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" } } }, ["description"] = "Prefer or restrict paths containing this text. Accepts a single string or an array; multiple values are OR'd together." },
                        ["excludePaths"] = StringOrArraySchema("Exclude any paths containing these texts"),
                        ["excludeTests"] = new JsonObject { ["type"] = "boolean", ["description"] = "Exclude likely test files", ["default"] = false },
                        ["includeGenerated"] = new JsonObject { ["type"] = "boolean", ["description"] = "Include files detected as generated code", ["default"] = false },
                        ["withPaths"] = new JsonObject { ["type"] = "boolean", ["description"] = "When true, each caller carries a `paths` array of shortest call chains [resolvedRoot, intermediate..., callerName]; diamond convergence surfaces every shortest route (per-row cap; `pathsTruncated` flag indicates overflow).", ["default"] = false },
                        ["includeMemberReads"] = new JsonObject { ["type"] = "boolean", ["description"] = "Include canonical `member_read` value-read edges in impact traversal. Defaults to false; legacy indexes stored these reads as `call` and cannot separate them.", ["default"] = false },
                        ["countOnly"] = new JsonObject { ["type"] = "boolean", ["description"] = "Return only count metadata and a small top-file histogram; omit caller and file-impact row payloads.", ["default"] = false }
                    },
                    ["required"] = new JsonArray { "query" }
                },
                ReadOnlyAnnotations()),
            CreateToolDefinition(
                "status",
                "Get database statistics, readiness state, and optional CLI-style freshness checks. Use `check`, `scopes`, `staleAfterSeconds`, `explain`, `config`, `logPath`, `format`, or `fields` for bounded health-check views. / DB統計、readiness 状態、必要に応じて CLI 風の freshness check を取得。`check` / `scopes` / `staleAfterSeconds` / `explain` / `config` / `logPath` / `format` / `fields` で health-check 用の出力に絞り込める。",
                new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["check"] = new JsonObject { ["type"] = "boolean", ["description"] = "Run a workspace freshness check and populate `workspace_check`, `index_matches_workspace`, and `failed_checks`.", ["default"] = false },
                        ["scopes"] = new JsonObject { ["oneOf"] = new JsonArray { new JsonObject { ["type"] = "string" }, new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray { "workspace", "graph", "issues", "sql", "hotspot", "csharp", "fold", "newer" } } } }, ["description"] = "Readiness scopes to evaluate for `failed_checks`. Omit to evaluate all scopes." },
                        ["staleAfterSeconds"] = new JsonObject { ["type"] = "integer", ["description"] = "Effective stale-after threshold, in seconds, echoed with `index_age_seconds` when `check` is true.", ["default"] = 86400, ["minimum"] = 1 },
                        ["explain"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray { "freshness", "readiness", "all" }, ["description"] = "Include a focused `explain` object for freshness/readiness diagnostics. `all` includes both.", ["default"] = "all" },
                        ["config"] = new JsonObject { ["type"] = "boolean", ["description"] = "Include effective MCP/CLI status configuration such as DB path, version, log dir, stale threshold, and update-check request state.", ["default"] = false },
                        ["logPath"] = new JsonObject { ["type"] = "boolean", ["description"] = "Include the resolved global tool log directory as `log_path`.", ["default"] = false },
                        ["updateCheck"] = new JsonObject { ["type"] = "boolean", ["description"] = "Run the same update check as CLI status. Defaults to false because it may perform network I/O.", ["default"] = false },
                        ["format"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray { "full", "compact" }, ["description"] = "Response shape. `compact` returns counts, freshness, readiness, and requested diagnostics without full language/kind tables.", ["default"] = "full" },
                        ["fields"] = new JsonObject
                        {
                            ["oneOf"] = new JsonArray
                            {
                                new JsonObject { ["type"] = "string", ["minLength"] = 1, ["maxLength"] = MaxStatusProjectionFieldCharacters },
                                new JsonObject
                                {
                                    ["type"] = "array",
                                    ["minItems"] = 1,
                                    ["maxItems"] = MaxStatusProjectionFields,
                                    ["items"] = new JsonObject { ["type"] = "string", ["minLength"] = 1, ["maxLength"] = MaxStatusProjectionFieldCharacters }
                                }
                            },
                            ["description"] = "Return only these exact top-level structured-content fields after applying `format`, plus the standard `api_version`. Accepts one field or an array; nested paths are not supported."
                        }
                    }
                },
                ReadOnlyAnnotations()),
            CreateToolDefinition(
                "outline",
                "Use this when a file is known but you need structure before reading content. Prefer it before whole-file reads; common next step is `excerpt` on a specific symbol range. Return the symbol outline of a single indexed file: all functions, classes, imports with line numbers, signatures, and nesting. / ファイルは分かっているが本文を読む前に構造を把握したいときに使う。ファイル全体を読む前に優先し、次は特定シンボル範囲の `excerpt` を使う。1ファイルのシンボルアウトラインを返す: 関数、クラス、importの行番号、シグネチャ、ネスト構造。",
                new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["path"] = new JsonObject { ["type"] = "string", ["description"] = "Indexed file path (e.g. src/app.cs)" },
                        ["fields"] = new JsonObject
                        {
                            ["oneOf"] = new JsonArray
                            {
                                new JsonObject { ["type"] = "string", ["minLength"] = 1, ["maxLength"] = 256 },
                                new JsonObject
                                {
                                    ["type"] = "array",
                                    ["minItems"] = 1,
                                    ["maxItems"] = 16,
                                    ["items"] = new JsonObject
                                    {
                                        ["type"] = "string",
                                        ["enum"] = new JsonArray
                                        {
                                            "all", "kind", "name", "display_name", "path", "line", "start_line", "end_line",
                                            "depth", "body_start_line", "body_end_line", "signature", "signature_truncated",
                                            "signature_original_length", "container_kind", "container_name", "visibility",
                                            "return_type", "sort_mode", "reference_count", "size_lines", "complexity_score",
                                            "range", "lines", "body", "body_range", "container", "refs", "references",
                                            "size", "span", "complexity"
                                        }
                                    }
                                }
                            },
                            ["description"] = "CLI-compatible outline projection fields. A string may be comma-separated; aliases expand exactly as `cdidx outline --outline-fields` does."
                        },
                        ["sort"] = new JsonObject
                        {
                            ["type"] = "string",
                            ["enum"] = new JsonArray { "source", "name", "kind", "references", "size", "complexity", "path" },
                            ["description"] = "Deterministic outline ordering shared with `cdidx outline --sort`.",
                            ["default"] = "source"
                        },
                        ["limit"] = new JsonObject
                        {
                            ["type"] = "integer",
                            ["minimum"] = 1,
                            ["maximum"] = MaxLimit,
                            ["description"] = "Maximum complete symbol rows to return (default: 100, maximum: 200).",
                            ["default"] = 100
                        },
                        ["cursor"] = new JsonObject { ["type"] = "string", ["description"] = "Opaque `page:v1` continuation returned as `next_cursor`; it is bound to path, ordering, and index generation." },
                        ["maxBytes"] = new JsonObject
                        {
                            ["type"] = "integer",
                            ["minimum"] = 1,
                            ["maximum"] = MaxClientResponseJsonBytes,
                            ["description"] = "Maximum UTF-8 bytes for serialized structured content. Pages shrink only at complete symbol-row boundaries."
                        },
                    },
                    ["required"] = new JsonArray { "path" }
                },
                ReadOnlyAnnotations()),
            CreateToolDefinition(
                "deps",
                "Show file-level dependency edges, JSON graph payloads, or dependency cycles from the indexed reference graph. / インデックス済み参照グラフからファイル間の依存エッジ、JSON graph ペイロード、依存サイクルを返す。",
                new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["limit"] = new JsonObject { ["type"] = "integer", ["description"] = "Max edges (default: 50)", ["default"] = QueryCommandRunner.DefaultImpactLimit },
                        ["lang"] = new JsonObject { ["type"] = "string", ["description"] = "Filter by language" },
                        ["path"] = new JsonObject { ["oneOf"] = new JsonArray { new JsonObject { ["type"] = "string" }, new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" } } }, ["description"] = "Restrict source files to glob-style path patterns. `*` and `?` are wildcards. Accepts a single string or an array; multiple values are OR'd together." },
                        ["excludePaths"] = StringOrArraySchema("Exclude glob-style path patterns. `*` and `?` are wildcards."),
                        ["excludeTests"] = new JsonObject { ["type"] = "boolean", ["description"] = "Exclude test files", ["default"] = false },
                        ["includeGenerated"] = new JsonObject { ["type"] = "boolean", ["description"] = "Include dependency edges whose source or target file is detected as generated code. Defaults to false, matching other query tools.", ["default"] = false },
                        ["reverse"] = new JsonObject { ["type"] = "boolean", ["description"] = "Reverse lookup: show files that depend ON the matched path", ["default"] = false },
                        ["format"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray { "edgelist", "json-graph" }, ["description"] = "Structured response format. `edgelist` preserves the existing edges array; `json-graph` returns nodes and edges.", ["default"] = "edgelist" },
                        ["cycles"] = new JsonObject { ["type"] = "boolean", ["description"] = "Return stable ranked strongly connected components instead of ordinary edge rows. `limit` paginates the completed analysis; inspect `analysis_complete` and continue with opaque `next_cursor` values. / 通常の edge 行ではなく安定順位付きの強連結成分を返す。`limit` は完了した解析結果をページ分割する。`analysis_complete` を確認し、不透明な `next_cursor` で続きを取得する。", ["default"] = false },
                        ["graphBudget"] = new JsonObject { ["type"] = "integer", ["minimum"] = 1, ["maximum"] = QueryCommandRunner.MaxDependencyCycleGraphBudget, ["description"] = "Maximum dependency edges analyzed for `cycles`, independent of the display `limit`. / 表示用 `limit` と独立した、`cycles` 解析対象の依存 edge 上限。", ["default"] = QueryCommandRunner.DefaultDependencyCycleGraphBudget },
                        ["cursor"] = new JsonObject { ["type"] = "string", ["maxLength"] = 256, ["description"] = "Opaque dependency-cycle `next_cursor`; reuse the same filters and graphBudget. / 同じ filter と graphBudget で再利用する不透明な dependency-cycle `next_cursor`。" }
                    }
                },
                ReadOnlyAnnotations()),
            CreateToolDefinition(
                "languages",
                "List supported languages with exact CLI-compatible language, extension, alias, and capability filters, scoped `language_capability_counts`, and unsupported_guidance fallback commands. Counts separate catalog, matched catalog, and indexed workspace scopes. Results use stable catalog-generation-bound cursor pagination and a JSON-RPC envelope byte budget. / 対応言語一覧を CLI 互換の言語・拡張子・別名・機能の完全一致フィルタ、scope 付き `language_capability_counts`、`unsupported_guidance` の代替コマンド付きで返す。件数は catalog、matched catalog、indexed workspace の scope を分離する。結果はカタログ世代に拘束された安定 cursor pagination と JSON-RPC envelope の byte budget を使用する。",
                new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["indexedOnly"] = new JsonObject { ["type"] = "boolean", ["description"] = "Return only languages currently present in the index. Requires the configured database.", ["default"] = false },
                        ["capability"] = new JsonObject { ["oneOf"] = new JsonArray { new JsonObject { ["type"] = "string", ["enum"] = CreateLanguageCapabilityEnum() }, new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string", ["enum"] = CreateLanguageCapabilityEnum() } } }, ["description"] = "Filter by the same capability or capability-gap values as CLI `languages --capability`. Accepts a single value or an array; all requested capabilities must match." },
                        ["language"] = new JsonObject { ["type"] = "string", ["description"] = "Look up one canonical language using the same exact normalization as CLI `languages --language`, e.g. `csharp` or `cs`." },
                        ["extension"] = new JsonObject { ["type"] = "string", ["description"] = "Look up languages by file extension. Accepts `cs` or `.cs` style values." },
                        ["alias"] = new JsonObject { ["type"] = "string", ["description"] = "Look up languages by exact CLI language alias; canonical language names remain accepted for backward compatibility." },
                        ["limit"] = new JsonObject { ["type"] = "integer", ["minimum"] = 1, ["maximum"] = MaxLimit, ["description"] = "Maximum catalog entries to return per page.", ["default"] = QueryCommandRunner.DefaultQueryLimit },
                        ["cursor"] = new JsonObject { ["type"] = "string", ["maxLength"] = MaxMcpQueryCursorCharacters, ["description"] = "Opaque next_cursor from the previous languages page. Keep every filter, limit, and maxBytes unchanged." },
                        ["maxBytes"] = new JsonObject { ["type"] = "integer", ["minimum"] = MinLanguageCatalogMaxBytes, ["maximum"] = MaxLanguageCatalogMaxBytes, ["description"] = "Maximum UTF-8 bytes for the complete JSON-RPC response envelope.", ["default"] = DefaultLanguageCatalogMaxBytes }
                    }
                },
                ReadOnlyAnnotations()),
            CreateToolDefinition(
                "validate",
                "Report encoding issues found during indexing: U+FFFD replacement chars, BOM markers, null bytes, mixed/CR-only line endings, UTF-16 BOM detection, likely non-UTF8 encodings. replacement_char rows include origin/severity metadata so agents can separate source literals from decoder replacements. Page metadata includes totals that are authoritative only while `file_issues_data_current` is true, plus `result_stable_at` and an opaque generation-bound `next_cursor`; pass it back unchanged with the same filters, format, and limit. / インデックス時に検出したエンコーディング問題を報告。replacement_char 行は source literal と decoder replacement を分ける origin/severity metadata を含む。ページ metadata は `file_issues_data_current` が true の間だけ authoritative な total、`result_stable_at`、generation-bound な opaque `next_cursor` を含む。同じ filter / format / limit で cursor を変更せず渡す。",
                new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["kind"] = new JsonObject { ["type"] = "string", ["description"] = "Filter by issue kind (replacement_char, bom, null_byte, mixed_line_endings, mixed_line_endings_three_way, cr_only_line_endings, utf16_bom, non_utf8_likely, line_too_long)" },
                        ["severity"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray { "error", "warning", "info" }, ["description"] = "Filter by issue severity." },
                        ["limit"] = new JsonObject { ["type"] = "integer", ["description"] = "Max issues to return (default: 20).", ["default"] = QueryCommandRunner.DefaultQueryLimit },
                        ["cursor"] = new JsonObject { ["type"] = "string", ["maxLength"] = MaxMcpQueryCursorCharacters, ["description"] = "Opaque generation-bound next_cursor returned by a previous validate page. Keep filters, format, and limit unchanged." },
                        ["path"] = new JsonObject { ["oneOf"] = new JsonArray { new JsonObject { ["type"] = "string" }, new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" } } }, ["description"] = "Filter to paths containing this text. Accepts a single string or an array; multiple values are OR'd together." },
                        ["excludePaths"] = StringOrArraySchema("Exclude any paths containing these texts"),
                        ["excludeTests"] = new JsonObject { ["type"] = "boolean", ["description"] = "Exclude likely test files", ["default"] = false },
                        ["countOnly"] = new JsonObject { ["type"] = "boolean", ["description"] = "Return only count metadata and a top-file histogram; omit issue rows.", ["default"] = false },
                        ["format"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray { "full", "count", "compact" }, ["description"] = "Response shape: full issue rows, count-only metadata, or compact file/line/kind/severity rows.", ["default"] = "full" }
                    }
                },
                ReadOnlyAnnotations()),
            CreateToolDefinition(
                "ping",
                "Lightweight connection check. Returns server version and timestamp. No database required. / 軽量接続チェック。サーバーバージョンとタイムスタンプを返す。DB不要。",
                new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject()
                },
                ReadOnlyAnnotations()),
            CreateToolDefinition(
                "batch_query",
                "Execute multiple read-only queries in a single call and return all results plus top-level success/failure counts, partial_failure, and failure_scope (none/isolated/cascading). Dramatically reduces round-trips for AI agents. / 複数の読み取り専用クエリを1回の呼び出しで実行し、全結果に加えてトップレベルの成功/失敗件数、partial_failure、failure_scope（none/isolated/cascading）を返す。AIエージェントの往復回数を劇的に削減。",
                new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["queries"] = new JsonObject
                        {
                            ["type"] = "array",
                            ["description"] = $"Array of {{tool, arguments}} objects. Only read-only tools are allowed (not index or backfill_fold). Hard cap: {MaxBatchQuerySize} slots.",
                            ["minItems"] = 1,
                            ["maxItems"] = MaxBatchQuerySize,
                            ["items"] = new JsonObject
                            {
                                ["type"] = "object",
                                ["properties"] = new JsonObject
                                {
                                    ["id"] = new JsonObject { ["type"] = "string", ["description"] = "Optional client-supplied slot identifier echoed as slot_id." },
                                    ["slotId"] = new JsonObject { ["type"] = "string", ["description"] = "Optional client-supplied slot identifier echoed as slot_id." },
                                    ["tool"] = new JsonObject { ["type"] = "string", ["description"] = "Tool name (e.g. search, definition, symbols)" },
                                    ["arguments"] = new JsonObject { ["type"] = "object", ["description"] = "Tool arguments" }
                                },
                                ["required"] = new JsonArray { "tool" }
                            }
                        },
                        ["maxResponseBytes"] = new JsonObject { ["type"] = "integer", ["description"] = "Optional per-call response byte budget for this batch_query response. Values above the server cap are clamped and reported in argument_adjustments.", ["minimum"] = 1, ["maximum"] = MaxBatchQueryResponseByteLimit },
                        ["estimateOnly"] = new JsonObject { ["type"] = "boolean", ["description"] = "Return budget and slot estimate metadata without executing the slots.", ["default"] = false }
                    },
                    ["required"] = new JsonArray { "queries" }
                },
                ReadOnlyAnnotations()),
            CreateToolDefinition(
                "index",
                "Index or re-index a project directory. Scans source files, extracts symbols, and builds FTS5 search index. On transports that can carry out-of-band server messages (stdio, and HTTP clients connected to `/events`), when the tools/call request includes a bounded scalar/object `_meta.progressToken`, this tool emits `notifications/progress` with that token while scanning, indexing, and finalizing; oversized or unsupported tokens are ignored instead of echoed. / プロジェクトディレクトリをインデックス（再インデックス）。ソースファイルをスキャンし、シンボルを抽出してFTS5検索インデックスを構築。out-of-band のサーバーメッセージを送れる transport（stdio、および `/events` に接続した HTTP クライアント）では、tools/call リクエストに bounded scalar/object の `_meta.progressToken` が含まれる場合、スキャン・インデックス・finalize 中に同じ token の `notifications/progress` を送信し、上限超過または未対応 token は echo せず無視する。",
                new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["path"] = new JsonObject { ["type"] = "string", ["description"] = "Project directory path to index" },
                        ["rebuild"] = new JsonObject { ["type"] = "boolean", ["description"] = "Delete existing index and rebuild from scratch (default: false)", ["default"] = false },
                        ["dryRun"] = new JsonObject { ["type"] = "boolean", ["description"] = "Plan the index run without mutating the database. Reports scan counts, effective options, and unsupported MCP modes.", ["default"] = false },
                        ["maxFileBytes"] = new JsonObject { ["type"] = "integer", ["description"] = "Override the per-file indexing size limit for this run. Defaults to CDIDX_MAX_FILE_BYTES or 4MiB.", ["minimum"] = 1, ["maximum"] = int.MaxValue },
                        ["maxSymbolsPerFile"] = new JsonObject { ["type"] = "integer", ["description"] = "Skip symbol/reference indexing for files that produce more symbols than this limit, matching CLI --max-symbols-per-file.", ["default"] = IndexCommandRunner.DefaultMaxSymbolsPerFile, ["minimum"] = 1, ["maximum"] = IndexCommandRunner.MaxSymbolsPerFileLimit },
                        ["maxReferencesPerFile"] = new JsonObject { ["type"] = "integer", ["description"] = "Skip references for files that produce more references than this limit, matching CLI --max-references-per-file.", ["default"] = IndexCommandRunner.DefaultMaxReferencesPerFile, ["minimum"] = 1, ["maximum"] = IndexCommandRunner.MaxReferencesPerFileLimit },
                        ["followSymlinks"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray { "none", "internal", "all" }, ["description"] = "Directory and file symlink policy matching CLI --follow-symlinks.", ["default"] = "none" },
                        ["includeSymbolKind"] = new JsonObject { ["oneOf"] = new JsonArray { new JsonObject { ["type"] = "string" }, new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" } } }, ["description"] = "Only index symbols with these kinds. Accepts a value, comma-separated string, or array." },
                        ["excludeSymbolKind"] = new JsonObject { ["oneOf"] = new JsonArray { new JsonObject { ["type"] = "string" }, new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" } } }, ["description"] = "Drop symbols with these kinds before indexing. Accepts a value, comma-separated string, or array." },
                        ["memoryTrace"] = new JsonObject { ["type"] = "boolean", ["description"] = "Include lightweight MCP memory samples and duration diagnostics in the response.", ["default"] = false },
                        ["parallelism"] = new JsonObject { ["type"] = "integer", ["description"] = "CLI compatibility knob. MCP index currently runs serially and reports effective_parallelism=1 instead of silently using this value.", ["minimum"] = 1, ["maximum"] = IndexCommandRunner.MaxIndexParallelism },
                        ["commits"] = new JsonObject { ["oneOf"] = new JsonArray { new JsonObject { ["type"] = "string" }, new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" } } }, ["description"] = "CLI compatibility scope. Commit-scoped MCP indexing is not supported; non-dry runs reject it explicitly." },
                        ["changedBetween"] = new JsonObject { ["oneOf"] = new JsonArray { new JsonObject { ["type"] = "string" }, new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" } } }, ["description"] = "CLI compatibility scope. changed-between MCP indexing is not supported; non-dry runs reject it explicitly." },
                        ["files"] = new JsonObject { ["oneOf"] = new JsonArray { new JsonObject { ["type"] = "string" }, new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" } } }, ["description"] = "CLI compatibility scope. File-scoped MCP indexing is not supported; non-dry runs reject it explicitly." },
                        ["watch"] = new JsonObject { ["type"] = "boolean", ["description"] = "CLI compatibility flag. Long-running watch mode is intentionally disabled for MCP; non-dry runs reject it explicitly.", ["default"] = false },
                        ["debounce"] = new JsonObject { ["type"] = "integer", ["description"] = "Watch debounce in milliseconds. Reported as unsupported unless watch mode is added to MCP in the future.", ["minimum"] = 0, ["maximum"] = IndexWatchRunner.MaxDebounceMs }
                    },
                    ["required"] = new JsonArray { "path" }
                },
                IndexAnnotations()),
            CreateToolDefinition(
                "backfill_fold",
                "Upgrade folded-name keys in an existing CodeIndex DB without reparsing source files. Rejects missing or blank targets instead of creating a fresh DB. Fills missing `name_folded` columns (or rewrites all keys after fold metadata drift such as version/fingerprint mismatch) and stamps FoldReady on success. Use `dry_run:true` to preview affected row counts without writing, or `force:true` to rewrite every folded key even when metadata appears current. On transports that can carry out-of-band server messages (stdio, and HTTP clients connected to `/events`), when the tools/call request includes a bounded scalar/object `_meta.progressToken`, this tool emits `notifications/progress` with that token during backfill and verification; oversized or unsupported tokens are ignored instead of echoed. / ソース再解析なしで既存の CodeIndex DB の folded-name key を更新する。欠落したDBや空のDBを新規作成せず拒否し、欠損 `name_folded` 列を埋めるか、fold metadata の drift（version / fingerprint 不一致など）時は全 key を再生成し、成功時に FoldReady を stamp する。`dry_run:true` で書き込まず対象行数を確認でき、`force:true` で metadata が current に見える場合でも全 folded key を再生成する。out-of-band のサーバーメッセージを送れる transport（stdio、および `/events` に接続した HTTP クライアント）では、tools/call リクエストに bounded scalar/object の `_meta.progressToken` が含まれる場合、backfill と検証中に同じ token の `notifications/progress` を送信し、上限超過または未対応 token は echo せず無視する。",
                new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["dry_run"] = new JsonObject { ["type"] = "boolean", ["description"] = "Preview affected folded-key row counts without writing to the database.", ["default"] = false },
                        ["force"] = new JsonObject { ["type"] = "boolean", ["description"] = "Rewrite all folded keys even when stored fold metadata matches the current runtime.", ["default"] = false }
                    }
                },
                IndexAnnotations()),
            CreateToolDefinition(
                "symbol_hotspots",
                "Find the most-referenced symbols in the codebase (hotspot analysis). "
                + "Returns symbols ordered by reference score, reference count, then deterministic ties by path, line, name, kind, and symbol id. `groupBy` can be `symbol` or `file`; `statement` is accepted only with `lang=sql` to preserve existing SQL behavior. Structured output includes `grouping_unit`, `count_kind`, `limit_applies_to`, `score_fields`, `ranking_fields`, and matching `query_context` fields so callers can tell whether `limit` applies to symbols, files, or SQL statements. Names that are unique within the active language/kind candidate set use codebase-wide totals; duplicate-name families fall back to conservative same-file counts, and same-file duplicate rows may be grouped when the DB cannot disambiguate targets. Cross-file grouping of duplicate families is trusted only on indexes stamped with the current authoritative hotspot-family version. Useful for identifying central, high-impact code. "
                + "/ コードベースで最も参照されるシンボルを検索する（ホットスポット分析）。"
                + "参照スコア、参照回数の順にシンボルを返し、同点は path、line、name、kind、symbol id で決定的に並べる。`groupBy` は `symbol` / `file` を指定でき、`statement` は既存 SQL 挙動を保つため `lang=sql` の場合のみ受け付ける。structured output には `grouping_unit`、`count_kind`、`limit_applies_to`、`score_fields`、`ranking_fields` と対応する `query_context` fields が含まれ、`limit` が symbols / files / SQL statements のどれに適用されるかを判別できる。active な言語/種別候補集合で一意な名前は codebase 全体の件数を使い、同名ファミリーは保守的な same-file 件数へフォールバックし、DB が対象を曖昧なく結べない同一ファイル重複行は集約される。duplicate family の cross-file 集約は current の authoritative hotspot-family version で stamp された index でのみ信頼する。中心的で影響の大きいコードの特定に有用。",
                new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["kind"] = new JsonObject { ["type"] = "string", ["description"] = "Filter by symbol kind" },
                        ["lang"] = new JsonObject { ["type"] = "string", ["description"] = "Filter by language" },
                        ["limit"] = new JsonObject { ["type"] = "integer", ["description"] = "Max results (default: 20)", ["default"] = QueryCommandRunner.DefaultQueryLimit },
                        ["visibility"] = new JsonObject { ["oneOf"] = new JsonArray { new JsonObject { ["type"] = "string" }, new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" } } }, ["description"] = "Filter symbol visibility. Accepts a value, comma-separated string, or array." },
                        ["excludeVisibility"] = new JsonObject { ["oneOf"] = new JsonArray { new JsonObject { ["type"] = "string" }, new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" } } }, ["description"] = "Exclude symbol visibility values. Accepts a value, comma-separated string, or array." },
                        ["groupBy"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("symbol", "file", "statement"), ["description"] = "Grouping unit. Use symbol or file for non-SQL scopes; statement is accepted only when lang is sql." },
                        ["path"] = new JsonObject { ["oneOf"] = new JsonArray { new JsonObject { ["type"] = "string" }, new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" } } }, ["description"] = "Restrict to glob-style path patterns. `*` and `?` are wildcards. Accepts a single string or an array; multiple values are OR'd together." },
                        ["excludePaths"] = StringOrArraySchema("Exclude glob-style path patterns. `*` and `?` are wildcards."),
                        ["excludeTests"] = new JsonObject { ["type"] = "boolean", ["description"] = "Exclude test files (default: false)", ["default"] = false }
                    }
                },
                ReadOnlyAnnotations()),
            CreateToolDefinition(
                "unused_symbols",
                "Use this when auditing potential dead code before removal. Prefer `references`, `callers`, or `excerpt` to verify surprising hits before editing. Find symbols that are defined but never referenced in the indexed codebase. "
                + "Results include confidence buckets so private hits rank ahead of public/exported suspects; the lowest-confidence bucket also covers reflection, serialization contracts, config, metadata, generated surfaces, documentation headings, and test-only hooks. Only meaningful for languages with reference extraction support. "
                + "Structured output includes `summary.by_bucket`, `summary.by_confidence`, `summary.by_contract_domain`, `bucket_taxonomy`, and per-symbol `unusedContractDomain`; bucket values are `likely_unused_private`, `maybe_unused_nonpublic`, `public_or_exported_no_refs`, and `reflection_or_config_suspect`. Use `bucket` or `minConfidence` to audit a single bucket or confidence class. "
                + "C# nameof/typeof and direct reflection member-name literals such as GetMethod(\"Foo\") are indexed as references; dynamically constructed reflection names can still require manual review. "
                + "/ 削除前に dead code 候補を監査するときに使う。意外なヒットは編集前に `references` / `callers` / `excerpt` で確認する。インデックス済みコードベースで定義されているが一度も参照されていないシンボルを検索する。"
                + "private 候補を public/exported suspect より前に返し、最低信頼 bucket は reflection、serialization contract、config、metadata、generated surface、documentation heading、test-only hook も扱う。参照抽出対応言語でのみ意味がある。"
                + "構造化出力には `summary.by_bucket`、`summary.by_confidence`、`summary.by_contract_domain`、`bucket_taxonomy`、シンボル単位の `unusedContractDomain` が含まれ、bucket 値は `likely_unused_private`、`maybe_unused_nonpublic`、`public_or_exported_no_refs`、`reflection_or_config_suspect`。`bucket` または `minConfidence` で単一 bucket や confidence class を監査できる。"
                + "C# の nameof/typeof と GetMethod(\"Foo\") のような直接の reflection member-name literal は参照として index されるが、動的に組み立てた reflection 名は手動確認が必要な場合がある。",
                new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["kind"] = new JsonObject { ["type"] = "string", ["description"] = "Filter by symbol kind (function, class, property, interface, enum, struct, event, delegate)" },
                        ["lang"] = new JsonObject { ["type"] = "string", ["description"] = "Filter by language (recommended: use a graph-supported language)" },
                        ["limit"] = new JsonObject { ["type"] = "integer", ["description"] = "Max results (default: 50)", ["default"] = QueryCommandRunner.DefaultImpactLimit },
                        ["visibility"] = new JsonObject { ["oneOf"] = new JsonArray { new JsonObject { ["type"] = "string" }, new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" } } }, ["description"] = "Filter symbol visibility. Accepts a value, comma-separated string, or array." },
                        ["excludeVisibility"] = new JsonObject { ["oneOf"] = new JsonArray { new JsonObject { ["type"] = "string" }, new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" } } }, ["description"] = "Exclude symbol visibility values. Accepts a value, comma-separated string, or array." },
                        ["byBucket"] = new JsonObject { ["type"] = "boolean", ["description"] = "Include `symbols_by_bucket` grouped by unused-symbol bucket.", ["default"] = false },
                        ["bucket"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("likely_unused_private", "maybe_unused_nonpublic", "public_or_exported_no_refs", "reflection_or_config_suspect"), ["description"] = "Return only one unused-symbol bucket." },
                        ["minConfidence"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("medium", "low"), ["description"] = "Return symbols at or above this confidence threshold." },
                        ["path"] = new JsonObject { ["oneOf"] = new JsonArray { new JsonObject { ["type"] = "string" }, new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" } } }, ["description"] = "Restrict to paths containing this text. Accepts a single string or an array; multiple values are OR'd together." },
                        ["excludePaths"] = StringOrArraySchema("Exclude paths containing any of these texts"),
                        ["excludeTests"] = new JsonObject { ["type"] = "boolean", ["description"] = "Exclude test files (default: false)", ["default"] = false }
                    }
                },
                ReadOnlyAnnotations()),
            CreateToolDefinition(
                "suggest_improvement",
                "Submit a structured improvement suggestion or error report for cdidx. "
                + "Call this when you notice a gap (e.g. missing language support, poor ranking) or encounter an unexpected error. "
                + "Never include source code — describe the gap in natural language only. "
                + "The tool writes to the resolved .cdidx directory, which must be writable; responses include cdidx_dir for diagnostics. "
                + "Responses also include github_submission_reason: submitted, token_not_configured, repo_not_configured, network_error, or api_error. "
                + "/ cdidxへの構造化された改善提案またはエラー報告を送信する。"
                + "ギャップ（言語サポート不足、ランキング不良等）に気づいたとき、または予期せぬエラーに遭遇したときに呼び出す。"
                + "ソースコードを含めないこと — 自然言語でのみギャップを記述する。"
                + "解決された .cdidx ディレクトリへ書き込むため、そのディレクトリは書き込み可能である必要がある。応答には診断用の cdidx_dir と github_submission_reason が含まれる。",
                new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["category"] = new JsonObject
                        {
                            ["type"] = "string",
                            ["description"] = "Suggestion category: symbol_extraction, reference_extraction, search_ranking, language_support, output_format, crash_report, unexpected_error, security, performance, bug, cleanup, documentation, feature_request, or other",
                            ["enum"] = new JsonArray(SuggestionRecord.ValidCategories.Select(category => (JsonNode?)category).ToArray())
                        },
                        ["language"] = new JsonObject { ["type"] = "string", ["description"] = "Programming language this applies to (optional)" },
                        ["description"] = new JsonObject { ["type"] = "string", ["description"] = "What gap or improvement you observed, or what error occurred (NOT source code)" },
                        ["context"] = new JsonObject { ["type"] = "string", ["description"] = "What you were trying to do when you noticed the gap (NOT source code)" },
                        ["toolInvocationContext"] = new JsonObject { ["type"] = "string", ["description"] = "Natural-language context for the current tool invocation or workflow (optional, NOT source code)" },
                        ["evidencePaths"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" }, ["description"] = "Repository-relative paths that support the suggestion (optional, no source code)" }
                    },
                    ["required"] = new JsonArray { "category", "description" }
                },
                SuggestionAnnotations())
        };

        AddProjectScopeProperties(tools);
        AddCommonSchemaConstraints(tools);
        return tools;
    }
}
