using System.Text.Json.Nodes;
using CodeIndex.Cli;
using CodeIndex.Database;

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
    private JsonNode HandleToolsList(JsonNode? id)
    {
        var tools = new JsonArray
        {
            CreateToolDefinition(
                "search",
                "Use this when starting broad code discovery, checking error text, or running named search audit recipes. Prefer it before shell grep; common next step is `excerpt`, `definition`, or `references` on the best hit. Returns snippets plus `result_stable_at`, `next_cursor`, and `next_step_suggestion` or `recovery_hint`. Use `prefix`/trailing `*` to widen token matching, `rawQuery` for FTS5 syntax, and `exactSubstring` for case-sensitive identity. Details and examples: USER_GUIDE.md#search. / 広いコード調査、エラー文言確認、search audit recipe 実行の起点に使う。shell grep より優先し、次は最有力ヒットに `excerpt` / `definition` / `references` を使う。`prefix` / 末尾 `*` / `rawQuery` / `exactSubstring` の詳細と例は USER_GUIDE.md#search を参照。",
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
                        ["exact"] = new JsonObject { ["type"] = "boolean", ["description"] = "Backward-compatible alias for `exactSubstring`.", ["default"] = false },
                        ["prefix"] = new JsonObject { ["type"] = "boolean", ["description"] = "Opt into FTS5 prefix expansion for every token in `query`. Cannot be combined with `exact`/`exactSubstring`.", ["default"] = false },
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
                "Use this when you need usage sites, examples, tests, metadata references, or type-position references for a symbol. Prefer it after `definition`; common next step is `excerpt` on representative rows or `callers`/`callees` for runtime impact. Search indexed symbol references such as call sites. Non-empty responses include `next_step_suggestion`; empty responses include `recovery_hint`. Pass `lsp_compatible:true` to add `uri` and LSP `range` fields to each result. For exact matches, use `exactName`; `exact` is the legacy alias documented in USER_GUIDE.md's flag compatibility table. When `kind` is omitted, all indexed reference kinds including metadata uses (`attribute` / `annotation`), C# BCL Regex timeout audit rows (`bcl_regex_without_timeout`), and compile-time type-position references (`type_reference`) stay visible, and identical constructor `call` + `instantiate` rows at one physical site are collapsed. Pass `kind: \"type_reference\"` to enumerate declaration types, generic constraints, `is`/`as`/`instanceof`, and XML-doc `cref` targets. Pass `kind: \"bcl_regex_without_timeout\"` with query `Regex` to audit direct System.Text.RegularExpressions.Regex construction without a timeout argument. Examples: `references {\"query\":\"Run\"}`; `references {\"query\":\"Service\",\"kind\":\"type_reference\",\"lang\":\"csharp\"}`. / シンボルの利用箇所、例、テスト、metadata 参照、型位置参照を調べるときに使う。`definition` の後に優先し、次は代表行の `excerpt` または実行時影響の `callers` / `callees` を使う。`kind: \"bcl_regex_without_timeout\"` と query `Regex` で timeout 引数なしの直接 `System.Text.RegularExpressions.Regex` 生成を監査できる。例: `references {\"query\":\"Run\"}`; `references {\"query\":\"Service\",\"kind\":\"type_reference\",\"lang\":\"csharp\"}`。",
                new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["query"] = new JsonObject { ["type"] = "string", ["description"] = "Referenced symbol name pattern to search for" },
                        ["kind"] = new JsonObject { ["type"] = "string", ["description"] = "Filter by reference kind (call, instantiate, subscribe, friend, attribute, annotation, type_reference, bcl_regex_without_timeout)" },
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
                "Use this when you need to know what calls or depends on a callee symbol before changing it. Prefer it after `definition`/`references`; common next step is `excerpt` on high-ranked caller rows. Find caller symbols that reference a callee. For exact matches, use `exactName`; `exact` is the legacy alias documented in USER_GUIDE.md's flag compatibility table. When `kind` is omitted, call-graph kinds (`call`, `instantiate`, `subscribe`, `friend`) are returned so C++ friend access/coupling edges stay visible while metadata uses (`attribute` / `annotation`) and compile-time type-position references (`type_reference`) do not pollute caller edges; identical constructor `call` + `instantiate` rows at one physical site also collapse. Each grouped row additionally exposes `reference_kinds` (sorted distinct kinds behind the row) and `has_mixed_reference_kinds` so callers do not have to trust the single summary label when a container mixes `call` + `subscribe` edges. The existing `reference_kind` scalar is retained for back-compat and carries the preferred summary kind (`instantiate` > `subscribe` > `unsubscribe` > `MIN(kind)`). `callers` / `callees` are not a reliable path to metadata or type-position references — metadata rows are attributed to their enclosing body-range symbol (for a class-level declaration, that is the class itself; for a file-level target such as `[assembly: ...]`, `containerName` is `null` and the row drops from these graph queries entirely), and `type_reference` rows are compile-time type mentions (declaration types, generic constraints, `is`/`as`/`instanceof`, XML-doc `cref`) rather than runtime calls. Use `references` with `kind: \"attribute\"`, `\"annotation\"`, or `\"type_reference\"` instead. Examples: `callers {\"query\":\"HandleRequest\"}`; `callers {\"query\":\"ExecuteAsync\",\"kind\":\"call\",\"rankBy\":\"weighted\",\"lang\":\"csharp\"}`. / callee シンボルの変更前に呼び出し元や依存元を知りたいときに使う。`definition` / `references` の後に優先し、次は上位 caller 行の `excerpt` を使う。指定シンボルを参照している呼び出し元シンボルを探す。完全一致には `exactName` を使う。`exact` は USER_GUIDE.md の flag compatibility table に記載された legacy alias。`kind` 未指定時は call-graph 種別 (`call` / `instantiate` / `subscribe` / `friend`) を返し、C++ friend の access/coupling edge は可視化しつつ、metadata 使用 (`attribute` / `annotation`) と compile-time な型位置参照 (`type_reference`) が phantom caller edge として混入しないようにする。同じ物理位置にある constructor の `call` + `instantiate` 重複行も集約する。各グループ行には `reference_kinds`（行内の distinct kind をソートした配列）と `has_mixed_reference_kinds` も追加で返すため、container が `call` + `subscribe` を混在させている行で要約 1 ラベルに騙されずに済む。既存のスカラー `reference_kind` は後方互換のため維持され、優先サマリー種別（`instantiate` > `subscribe` > `unsubscribe` > `MIN(kind)`）を持つ。metadata 行の container は注釈対象そのものではなく body-range 上の外側シンボル（クラス直下宣言ならクラス、ファイルレベル target なら `null`）になり、`type_reference` は実行時呼び出しではなく宣言型・generic 制約・`is`/`as`/`instanceof`・XML-doc `cref` といった compile-time な型言及なので、`callers` / `callees` は metadata / 型位置参照の列挙に向かない。Metadata / 型位置参照の列挙は `references --kind attribute|annotation|type_reference` / MCP `references` を使う。例: `callers {\"query\":\"HandleRequest\"}`; `callers {\"query\":\"ExecuteAsync\",\"kind\":\"call\",\"rankBy\":\"weighted\",\"lang\":\"csharp\"}`。",
                new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["query"] = new JsonObject { ["type"] = "string", ["description"] = "Callee symbol name pattern to search for" },
                        ["kind"] = new JsonObject { ["type"] = "string", ["description"] = "Filter by call-graph reference kind (call, instantiate, subscribe, friend). Non-call-graph kinds — metadata (attribute, annotation) and type-position (type_reference) — are rejected here; use `references` with the desired kind instead." },
                        ["rawKinds"] = new JsonObject { ["type"] = "boolean", ["description"] = "Preserve raw reference kinds instead of CLI logical grouping, matching `--raw-kinds`.", ["default"] = false },
                        ["rankBy"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray { "weighted", "count", "kind" }, ["description"] = "Ranking model: weighted (default; instantiate=3.0, call=1.0, subscribe=0.1, friend=0.3), count, or kind.", ["default"] = "weighted" },
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
                "Use this when you need to know what a caller/container symbol invokes or depends on. Prefer it after `definition` or `outline`; common next step is `excerpt` on a callee row. Find callees used by a caller/container symbol. For exact matches, use `exactName`; `exact` is the legacy alias documented in USER_GUIDE.md's flag compatibility table. When `kind` is omitted, call-graph kinds (`call`, `instantiate`, `subscribe`, `friend`) are returned so C++ friend access/coupling edges stay visible while metadata uses (`attribute` / `annotation`) and compile-time type-position references (`type_reference`) do not pollute callee edges; identical constructor `call` + `instantiate` rows at one physical site also collapse. Each grouped row additionally exposes `reference_kinds` (sorted distinct kinds behind the row) and `has_mixed_reference_kinds` for symmetry with `callers`, even though rows are already split per kind on this side. The existing `reference_kind` scalar is retained for back-compat and carries the same kind value. `callees` is not a reliable path to metadata or type-position references — the container assigned to an attribute / annotation row is the enclosing body-range symbol, not the annotated declaration, so `callees Method1 --kind attribute` does not return the attributes on `Method1`, and `type_reference` rows are compile-time type mentions (declaration types, generic constraints, `is`/`as`/`instanceof`, XML-doc `cref`) rather than runtime calls. Use `references` with `kind: \"attribute\"`, `\"annotation\"`, or `\"type_reference\"` instead. Examples: `callees {\"query\":\"Run\"}`; `callees {\"query\":\"Program.Main\",\"kind\":\"instantiate\",\"lang\":\"csharp\",\"limit\":10}`. / caller/container シンボルが呼ぶ先や依存先を知りたいときに使う。`definition` または `outline` の後に優先し、次は callee 行の `excerpt` を使う。呼び出し元シンボルが使っている呼び出し先を探す。完全一致には `exactName` を使う。`exact` は USER_GUIDE.md の flag compatibility table に記載された legacy alias。`kind` 未指定時は call-graph 種別 (`call` / `instantiate` / `subscribe` / `friend`) を返し、C++ friend の access/coupling edge は可視化しつつ、metadata 使用 (`attribute` / `annotation`) と compile-time な型位置参照 (`type_reference`) が phantom callee edge として混入しないようにする。同じ物理位置にある constructor の `call` + `instantiate` 重複行も集約する。各グループ行には `callers` との対称性のため `reference_kinds`（行内の distinct kind をソートした配列）と `has_mixed_reference_kinds` も返る（`callees` 側は元々 kind ごとに行を分けているため通常は単一要素）。既存のスカラー `reference_kind` は後方互換のため維持され、同じ kind 値を持つ。metadata 行の container は注釈対象自身ではなく body-range 上の外側シンボルになるため、`callees` で `Method1 --kind attribute` を引いても `Method1` に付いた属性は返らない。`type_reference` は実行時呼び出しではなく宣言型・generic 制約・`is`/`as`/`instanceof`・XML-doc `cref` といった compile-time な型言及なので、`callees` は metadata / 型位置参照の列挙に向かない。Metadata / 型位置参照の列挙は `references --kind attribute|annotation|type_reference` / MCP `references` を使う。例: `callees {\"query\":\"Run\"}`; `callees {\"query\":\"Program.Main\",\"kind\":\"instantiate\",\"lang\":\"csharp\",\"limit\":10}`。",
                new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["query"] = new JsonObject { ["type"] = "string", ["description"] = "Caller/container symbol name pattern to search for" },
                        ["kind"] = new JsonObject { ["type"] = "string", ["description"] = "Filter by call-graph reference kind (call, instantiate, subscribe). Non-call-graph kinds — metadata (attribute, annotation) and type-position (type_reference) — are rejected here; use `references` with the desired kind instead." },
                        ["rawKinds"] = new JsonObject { ["type"] = "boolean", ["description"] = "Preserve raw reference kinds instead of CLI logical grouping, matching `--raw-kinds`.", ["default"] = false },
                        ["rankBy"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray { "weighted", "count", "kind" }, ["description"] = "Ranking model: weighted (default; instantiate=3.0, call=1.0, subscribe=0.1), count, or kind.", ["default"] = "weighted" },
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
                "Use this when discovering candidate symbols before `definition`, `references`, `callers`, or `callees`. Prefer `exactName:true` when the name must match exactly. Search for code symbols (functions, classes, interfaces, imports) by name pattern. `exact` is the legacy alias documented in USER_GUIDE.md's flag compatibility table. Examples: `symbols {\"query\":\"Service\"}`; `symbols {\"query\":\"Run\",\"kind\":\"function\",\"lang\":\"csharp\",\"exactName\":true}`. / `definition` / `references` / `callers` / `callees` の前に候補シンボルを探すときに使う。名前を厳密一致させるなら `exactName:true` を優先する。シンボル（関数、クラス、インターフェース、import）を名前パターンで検索。例: `symbols {\"query\":\"Service\"}`; `symbols {\"query\":\"Run\",\"kind\":\"function\",\"lang\":\"csharp\",\"exactName\":true}`。",
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
                "Use this when you need to locate indexed files by path, language, or recent-change scope before reading content. Prefer `outline` or `excerpt` as the next step after choosing a file. List indexed files, optionally filtered by name pattern and language. / 内容を読む前に path、言語、最近の変更範囲でインデックス済みファイルを探すときに使う。ファイルを選んだ後は `outline` または `excerpt` を優先する。インデックス済みファイルを一覧（名前パターン・言語でフィルタ可能）。",
                new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["query"] = new JsonObject { ["type"] = "string", ["description"] = "File path pattern to filter by" },
                        ["lang"] = new JsonObject { ["type"] = "string", ["description"] = "Filter by language" },
                        ["limit"] = new JsonObject { ["type"] = "integer", ["description"] = "Max results (default: 20)", ["default"] = QueryCommandRunner.DefaultQueryLimit },
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
                "Use this after `search`, `definition`, `references`, `outline`, or `map` identifies a file and line range. Prefer focused excerpts over whole-file reads; common next step is `outline` for neighboring structure. Reconstruct a file excerpt from indexed chunks for a given line range. Successful responses include `next_step_suggestion`; empty responses include `recovery_hint`. / `search` / `definition` / `references` / `outline` / `map` でファイルと行範囲を絞った後に使う。ファイル全体ではなく必要範囲の抜粋を優先し、次は周辺構造確認の `outline` を使う。指定行範囲について、インデックス済みチャンクからファイル抜粋を再構成。",
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
                        ["focusLine"] = new JsonObject { ["type"] = "integer", ["description"] = "Optional line inside the excerpt whose focused column should stay visible when clamping; requires focusColumn", ["minimum"] = 1 },
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
                "Use this when one symbol needs a compact dossier and you would otherwise chain `definition`, `references`, `callers`, and `callees`. Prefer standalone tools when you need deeper pagination; common next step is `excerpt` on the most relevant rows. Bundle definition, nearby symbols, references, callers, callees, file metadata, and graph-support metadata for one symbol query. For exact matches, use `exactName`; `exact` is the legacy alias documented in USER_GUIDE.md's flag compatibility table. Bundled caller/callee rows carry the same `reference_kind` (preferred summary kind, back-compat) plus `reference_kinds` (sorted distinct) and `has_mixed_reference_kinds` fields as the standalone `callers` / `callees` tools, so mixed `call` + `subscribe` containers stay visible in the bundle. Supports `format: count|compact`; CLI `since` filtering is intentionally not exposed because the backing analysis reader does not support it yet. / 1つのシンボルについて compact な dossier が必要で、`definition` / `references` / `callers` / `callees` を連続呼び出ししそうなときに使う。深い pagination が必要なら単独ツールを優先し、次は重要行の `excerpt` を使う。1つのシンボルクエリに対して、定義、近傍シンボル、参照、caller、callee、ファイルメタデータ、グラフ対応メタデータをまとめて返す。完全一致には `exactName` を使う。`exact` は USER_GUIDE.md の flag compatibility table に記載された legacy alias。バンドルされた caller / callee 行にも単独の `callers` / `callees` と同じ `reference_kind`（後方互換の優先サマリー種別）、`reference_kinds`（distinct kind の昇順配列）、`has_mixed_reference_kinds` が付くため、`call` + `subscribe` が混在するコンテナも要約 1 ラベルに潰れず見える。`format: count|compact` 対応。CLI の `since` filter は backing analysis reader 未対応のため意図的に未公開。",
                new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["query"] = new JsonObject { ["type"] = "string", ["description"] = "Symbol name to inspect" },
                        ["limit"] = new JsonObject { ["type"] = "integer", ["description"] = "Max items per section (default: 10)", ["default"] = QueryCommandRunner.DefaultMapLimit },
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
                        ["countOnly"] = new JsonObject { ["type"] = "boolean", ["description"] = "Return only count metadata and a small top-file histogram; omit caller and file-impact row payloads.", ["default"] = false }
                    },
                    ["required"] = new JsonArray { "query" }
                },
                ReadOnlyAnnotations()),
            CreateToolDefinition(
                "status",
                "Get database statistics, readiness state, and optional CLI-style freshness checks. Use `check`, `scopes`, `staleAfterSeconds`, `explain`, `config`, `logPath`, or `format` for bounded health-check views. / DB統計、readiness 状態、必要に応じて CLI 風の freshness check を取得。`check` / `scopes` / `staleAfterSeconds` / `explain` / `config` / `logPath` / `format` で health-check 用の出力に絞り込める。",
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
                        ["format"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray { "full", "compact" }, ["description"] = "Response shape. `compact` returns counts, freshness, readiness, and requested diagnostics without full language/kind tables.", ["default"] = "full" }
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
                        ["cycles"] = new JsonObject { ["type"] = "boolean", ["description"] = "Return dependency cycles instead of ordinary edge rows.", ["default"] = false }
                    }
                },
                ReadOnlyAnnotations()),
            CreateToolDefinition(
                "languages",
                "List supported languages with extensions, aliases, and capabilities. Use `indexedOnly`, `capability`, `extension`, or `alias` to match CLI language filters and extension lookup. / 対応言語一覧を拡張子・別名・機能付きで返す。`indexedOnly` / `capability` / `extension` / `alias` で CLI の言語フィルタと拡張子 lookup に合わせて絞り込める。",
                new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["indexedOnly"] = new JsonObject { ["type"] = "boolean", ["description"] = "Return only languages currently present in the index. Requires the configured database.", ["default"] = false },
                        ["capability"] = new JsonObject { ["oneOf"] = new JsonArray { new JsonObject { ["type"] = "string", ["enum"] = new JsonArray { "symbols", "graph", "references" } }, new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray { "symbols", "graph", "references" } } } }, ["description"] = "Filter by language capability. `graph` and `references` both require call-graph/reference extraction support. Accepts a single value or an array; all requested capabilities must match." },
                        ["extension"] = new JsonObject { ["type"] = "string", ["description"] = "Look up languages by file extension. Accepts `cs` or `.cs` style values." },
                        ["alias"] = new JsonObject { ["type"] = "string", ["description"] = "Look up languages by canonical language name or CLI language alias, e.g. `cs` for `csharp`." }
                    }
                },
                ReadOnlyAnnotations()),
            CreateToolDefinition(
                "validate",
                "Report encoding issues found during indexing: U+FFFD replacement chars, BOM markers, null bytes, mixed/CR-only line endings, UTF-16 BOM detection, likely non-UTF8 encodings. replacement_char rows include origin/severity metadata so agents can separate source literals from decoder replacements. / インデックス時に検出したエンコーディング問題を報告。replacement_char 行は source literal と decoder replacement を分ける origin/severity metadata を含む。",
                new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["kind"] = new JsonObject { ["type"] = "string", ["description"] = "Filter by issue kind (replacement_char, bom, null_byte, mixed_line_endings, mixed_line_endings_three_way, cr_only_line_endings, utf16_bom, non_utf8_likely, line_too_long)" },
                        ["severity"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray { "error", "warning", "info" }, ["description"] = "Filter by issue severity." },
                        ["limit"] = new JsonObject { ["type"] = "integer", ["description"] = "Max issues to return (default: 20).", ["default"] = QueryCommandRunner.DefaultQueryLimit },
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
                            ["description"] = "Suggestion category: symbol_extraction, reference_extraction, search_ranking, language_support, output_format, crash_report, unexpected_error, or other",
                            ["enum"] = new JsonArray { "symbol_extraction", "reference_extraction", "search_ranking", "language_support", "output_format", "crash_report", "unexpected_error", "other" }
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

        var result = new JsonObject
        {
            ["tools"] = filtered,
            ["_meta"] = BuildToolsListCatalogMeta(filtered),
        };
        return CreateSuccessResponse(id, result);
    }

    private static JsonObject BuildToolsListCatalogMeta(JsonArray tools)
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
                ["file_reading"] = ToolNameArray(enabledToolNames, "excerpt", "find_in_file"),
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
                WorkflowMeta(enabledToolNames, "safe_file_review", "Locate files and read constrained excerpts without dumping whole large files.", "files", "find_in_file", "excerpt"),
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
            },
        };
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
                AppendConstraintDescription(obj, "Use identifiers, symbol names, error messages, config keys, or short code/text fragments; add exactName/exactSubstring when identity matters.");
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
