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
                "Full-text search across indexed code chunks. Returns match-centered snippets with line metadata plus `result_stable_at` for index-drift checks, `next_cursor` for non-empty paginated responses, and `next_step_suggestion` or `recovery_hint`. Use `prefix` or trailing `*` to widen token matching, `rawQuery` for FTS5 syntax, and `exactSubstring` for case-sensitive text identity. Details and examples: USER_GUIDE.md#search. / インデックス済みコードチャンクの全文検索。レスポンスには index drift 検出用の `result_stable_at`、非空ページ継続用の `next_cursor`、`next_step_suggestion` または `recovery_hint` を含める。`prefix` / 末尾 `*` / `rawQuery` / `exactSubstring` の詳細と例は USER_GUIDE.md#search を参照。",
                new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["query"] = new JsonObject { ["type"] = "string", ["description"] = "Search query text. Append `*` to a token to make that token a prefix phrase (`計算*` matches `計算する`)." },
                        ["limit"] = new JsonObject { ["type"] = "integer", ["description"] = "Max results (default: 20). Responses include `truncated` and `more_available` when more rows exist.", ["default"] = QueryCommandRunner.DefaultQueryLimit },
                        ["lang"] = new JsonObject { ["type"] = "string", ["description"] = "Filter by language (e.g. csharp, python, javascript)" },
                        ["snippetLines"] = new JsonObject { ["type"] = "integer", ["description"] = "Max snippet lines per result (default: 8, max: 20)", ["default"] = 8, ["minimum"] = 1, ["maximum"] = SearchSnippetFormatter.MaxSnippetLines },
                        ["maxLineWidth"] = new JsonObject { ["type"] = "integer", ["description"] = "Clamp very long single-line snippets per line (default: 512; 0 disables clamping). Match lines are clamped around the first match; non-match lines are clamped from the head. Each clamp inserts a `...(+N)...` marker showing how many chars were elided.", ["default"] = LineWidthFormatter.DefaultMaxLineWidth, ["minimum"] = 0, ["maximum"] = LineWidthFormatter.MaxAllowedLineWidth },
                        ["rawQuery"] = new JsonObject { ["type"] = "boolean", ["description"] = "Use raw FTS5 syntax instead of literal-safe quoting: content:term, NEAR(a b, 5), OR, NOT, parenthesized groups, prefix*, and quoted phrases.", ["default"] = false },
                        ["cursor"] = new JsonObject { ["type"] = "string", ["description"] = "Optional pagination cursor returned as `next_cursor` by a previous search response with the same query and filters. Compare `result_stable_at` across pages to detect index drift." },
                        ["path"] = new JsonObject { ["oneOf"] = new JsonArray { new JsonObject { ["type"] = "string" }, new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" } } }, ["description"] = "Prefer or restrict glob-style path patterns. `*` and `?` are wildcards. Accepts a single string or an array; multiple values are OR'd together." },
                        ["excludePaths"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" }, ["description"] = "Exclude glob-style path patterns. `*` and `?` are wildcards." },
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
                        ["countOnly"] = new JsonObject { ["type"] = "boolean", ["description"] = "Return only count metadata and a small top-file histogram; omit row payloads.", ["default"] = false },
                        ["format"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray { "full", "count", "compact" }, ["description"] = "Response shape: full rows, count-only metadata, or compact file/line rows without snippets.", ["default"] = "full" }
                    },
                    ["required"] = new JsonArray { "query" }
                },
                ReadOnlyAnnotations()),
            CreateToolDefinition(
                "definition",
                "Resolve symbol definitions with definition ranges, signatures, and optional body content. Pass `lsp_compatible:true` to add `uri` and LSP `range` fields to each result. For exact matches, use `exactName`; `exact` is the legacy alias documented in USER_GUIDE.md's flag compatibility table. Examples: `definition {\"query\":\"McpServer\"}`; `definition {\"query\":\"HandleMessage\",\"lang\":\"csharp\",\"includeBody\":true,\"exactName\":true}`. / 定義範囲、シグネチャ、必要に応じて本体内容付きでシンボル定義を解決。`lsp_compatible:true` で各結果に `uri` と LSP `range` を追加する。完全一致には `exactName` を使う。`exact` は USER_GUIDE.md の flag compatibility table に記載された legacy alias。例: `definition {\"query\":\"McpServer\"}`; `definition {\"query\":\"HandleMessage\",\"lang\":\"csharp\",\"includeBody\":true,\"exactName\":true}`。",
                new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["query"] = new JsonObject { ["type"] = "string", ["description"] = "Symbol name pattern to resolve" },
                        ["kind"] = new JsonObject { ["type"] = "string", ["description"] = "Filter by symbol kind" },
                        ["lang"] = new JsonObject { ["type"] = "string", ["description"] = "Filter by language" },
                        ["limit"] = new JsonObject { ["type"] = "integer", ["description"] = "Max results (default: 20)", ["default"] = QueryCommandRunner.DefaultQueryLimit },
                        ["includeBody"] = new JsonObject { ["type"] = "boolean", ["description"] = "Include body content when body ranges are available", ["default"] = false },
                        ["lsp_compatible"] = new JsonObject { ["type"] = "boolean", ["description"] = "Add file:// uri and LSP range fields to each result", ["default"] = false },
                        ["path"] = new JsonObject { ["oneOf"] = new JsonArray { new JsonObject { ["type"] = "string" }, new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" } } }, ["description"] = "Prefer or restrict matches to paths containing this text. Accepts a single string or an array; multiple values are OR'd together." },
                        ["excludePaths"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" }, ["description"] = "Exclude any paths containing these texts" },
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
                "Search indexed symbol references such as call sites. Non-empty responses include `next_step_suggestion` for reading the top hit context; empty responses include `recovery_hint`. Pass `lsp_compatible:true` to add `uri` and LSP `range` fields to each result. For exact matches, use `exactName`; `exact` is the legacy alias documented in USER_GUIDE.md's flag compatibility table. When `kind` is omitted, all indexed reference kinds including metadata uses (`attribute` / `annotation`) and compile-time type-position references (`type_reference`) stay visible, and identical constructor `call` + `instantiate` rows at one physical site are collapsed. Pass `kind: \"type_reference\"` to enumerate declaration types, generic constraints, `is`/`as`/`instanceof`, and XML-doc `cref` targets. Examples: `references {\"query\":\"Run\"}`; `references {\"query\":\"Service\",\"kind\":\"type_reference\",\"lang\":\"csharp\"}`. / 呼び出し箇所などのインデックス済みシンボル参照を検索。非空レスポンスには先頭ヒットの文脈を読む `next_step_suggestion`、空レスポンスには `recovery_hint` を含める。`lsp_compatible:true` で各結果に `uri` と LSP `range` を追加する。完全一致には `exactName` を使う。`exact` は USER_GUIDE.md の flag compatibility table に記載された legacy alias。`kind` 未指定時は metadata (`attribute` / `annotation`) と compile-time な型位置参照 (`type_reference`) も含む全 reference kind を表示したうえで、同じ物理位置にある constructor の `call` + `instantiate` 重複行を集約する。`kind: \"type_reference\"` を指定すると、宣言型・generic 制約・`is`/`as`/`instanceof`・XML-doc `cref` 対象を列挙できる。例: `references {\"query\":\"Run\"}`; `references {\"query\":\"Service\",\"kind\":\"type_reference\",\"lang\":\"csharp\"}`。",
                new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["query"] = new JsonObject { ["type"] = "string", ["description"] = "Referenced symbol name pattern to search for" },
                        ["kind"] = new JsonObject { ["type"] = "string", ["description"] = "Filter by reference kind (call, instantiate, subscribe, friend, attribute, annotation, type_reference)" },
                        ["lang"] = new JsonObject { ["type"] = "string", ["description"] = "Filter by language" },
                        ["limit"] = new JsonObject { ["type"] = "integer", ["description"] = "Max results (default: 20). Responses include `truncated`, `more_available`, and `next_offset` when more rows exist.", ["default"] = QueryCommandRunner.DefaultQueryLimit },
                        ["offset"] = new JsonObject { ["type"] = "integer", ["description"] = "Zero-based result offset for pagination; use `next_offset` from a truncated response.", ["default"] = 0, ["minimum"] = 0 },
                        ["maxLineWidth"] = new JsonObject { ["type"] = "integer", ["description"] = "Clamp very long single-line context payloads per result (default: 512; 0 disables clamping)", ["default"] = LineWidthFormatter.DefaultMaxLineWidth, ["minimum"] = 0, ["maximum"] = LineWidthFormatter.MaxAllowedLineWidth },
                        ["lsp_compatible"] = new JsonObject { ["type"] = "boolean", ["description"] = "Add file:// uri and LSP range fields to each result", ["default"] = false },
                        ["path"] = new JsonObject { ["oneOf"] = new JsonArray { new JsonObject { ["type"] = "string" }, new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" } } }, ["description"] = "Prefer or restrict matches to paths containing this text. Accepts a single string or an array; multiple values are OR'd together." },
                        ["excludePaths"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" }, ["description"] = "Exclude any paths containing these texts" },
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
                "Find caller symbols that reference a callee. For exact matches, use `exactName`; `exact` is the legacy alias documented in USER_GUIDE.md's flag compatibility table. When `kind` is omitted, call-graph kinds (`call`, `instantiate`, `subscribe`, `friend`) are returned so C++ friend access/coupling edges stay visible while metadata uses (`attribute` / `annotation`) and compile-time type-position references (`type_reference`) do not pollute caller edges; identical constructor `call` + `instantiate` rows at one physical site also collapse. Each grouped row additionally exposes `reference_kinds` (sorted distinct kinds behind the row) and `has_mixed_reference_kinds` so callers do not have to trust the single summary label when a container mixes `call` + `subscribe` edges. The existing `reference_kind` scalar is retained for back-compat and carries the preferred summary kind (`instantiate` > `subscribe` > `unsubscribe` > `MIN(kind)`). `callers` / `callees` are not a reliable path to metadata or type-position references — metadata rows are attributed to their enclosing body-range symbol (for a class-level declaration, that is the class itself; for a file-level target such as `[assembly: ...]`, `containerName` is `null` and the row drops from these graph queries entirely), and `type_reference` rows are compile-time type mentions (declaration types, generic constraints, `is`/`as`/`instanceof`, XML-doc `cref`) rather than runtime calls. Use `references` with `kind: \"attribute\"`, `\"annotation\"`, or `\"type_reference\"` instead. Examples: `callers {\"query\":\"HandleRequest\"}`; `callers {\"query\":\"ExecuteAsync\",\"kind\":\"call\",\"rankBy\":\"weighted\",\"lang\":\"csharp\"}`. / 指定シンボルを参照している呼び出し元シンボルを探す。完全一致には `exactName` を使う。`exact` は USER_GUIDE.md の flag compatibility table に記載された legacy alias。`kind` 未指定時は call-graph 種別 (`call` / `instantiate` / `subscribe` / `friend`) を返し、C++ friend の access/coupling edge は可視化しつつ、metadata 使用 (`attribute` / `annotation`) と compile-time な型位置参照 (`type_reference`) が phantom caller edge として混入しないようにする。同じ物理位置にある constructor の `call` + `instantiate` 重複行も集約する。各グループ行には `reference_kinds`（行内の distinct kind をソートした配列）と `has_mixed_reference_kinds` も追加で返すため、container が `call` + `subscribe` を混在させている行で要約 1 ラベルに騙されずに済む。既存のスカラー `reference_kind` は後方互換のため維持され、優先サマリー種別（`instantiate` > `subscribe` > `unsubscribe` > `MIN(kind)`）を持つ。metadata 行の container は注釈対象そのものではなく body-range 上の外側シンボル（クラス直下宣言ならクラス、ファイルレベル target なら `null`）になり、`type_reference` は実行時呼び出しではなく宣言型・generic 制約・`is`/`as`/`instanceof`・XML-doc `cref` といった compile-time な型言及なので、`callers` / `callees` は metadata / 型位置参照の列挙に向かない。Metadata / 型位置参照の列挙は `references --kind attribute|annotation|type_reference` / MCP `references` を使う。例: `callers {\"query\":\"HandleRequest\"}`; `callers {\"query\":\"ExecuteAsync\",\"kind\":\"call\",\"rankBy\":\"weighted\",\"lang\":\"csharp\"}`。",
                new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["query"] = new JsonObject { ["type"] = "string", ["description"] = "Callee symbol name pattern to search for" },
                        ["kind"] = new JsonObject { ["type"] = "string", ["description"] = "Filter by call-graph reference kind (call, instantiate, subscribe, friend). Non-call-graph kinds — metadata (attribute, annotation) and type-position (type_reference) — are rejected here; use `references` with the desired kind instead." },
                        ["rankBy"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray { "weighted", "count", "kind" }, ["description"] = "Ranking model: weighted (default; instantiate=3.0, call=1.0, subscribe=0.1, friend=0.3), count, or kind.", ["default"] = "weighted" },
                        ["lang"] = new JsonObject { ["type"] = "string", ["description"] = "Filter by language" },
                        ["limit"] = new JsonObject { ["type"] = "integer", ["description"] = "Max results (default: 20). Responses include `truncated`, `more_available`, and `next_offset` when more rows exist.", ["default"] = QueryCommandRunner.DefaultQueryLimit },
                        ["offset"] = new JsonObject { ["type"] = "integer", ["description"] = "Zero-based result offset for pagination; use `next_offset` from a truncated response.", ["default"] = 0, ["minimum"] = 0 },
                        ["path"] = new JsonObject { ["oneOf"] = new JsonArray { new JsonObject { ["type"] = "string" }, new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" } } }, ["description"] = "Prefer or restrict matches to paths containing this text. Accepts a single string or an array; multiple values are OR'd together." },
                        ["excludePaths"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" }, ["description"] = "Exclude any paths containing these texts" },
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
                "Find callees used by a caller/container symbol. For exact matches, use `exactName`; `exact` is the legacy alias documented in USER_GUIDE.md's flag compatibility table. When `kind` is omitted, call-graph kinds (`call`, `instantiate`, `subscribe`, `friend`) are returned so C++ friend access/coupling edges stay visible while metadata uses (`attribute` / `annotation`) and compile-time type-position references (`type_reference`) do not pollute callee edges; identical constructor `call` + `instantiate` rows at one physical site also collapse. Each grouped row additionally exposes `reference_kinds` (sorted distinct kinds behind the row) and `has_mixed_reference_kinds` for symmetry with `callers`, even though rows are already split per kind on this side. The existing `reference_kind` scalar is retained for back-compat and carries the same kind value. `callees` is not a reliable path to metadata or type-position references — the container assigned to an attribute / annotation row is the enclosing body-range symbol, not the annotated declaration, so `callees Method1 --kind attribute` does not return the attributes on `Method1`, and `type_reference` rows are compile-time type mentions (declaration types, generic constraints, `is`/`as`/`instanceof`, XML-doc `cref`) rather than runtime calls. Use `references` with `kind: \"attribute\"`, `\"annotation\"`, or `\"type_reference\"` instead. Examples: `callees {\"query\":\"Run\"}`; `callees {\"query\":\"Program.Main\",\"kind\":\"instantiate\",\"lang\":\"csharp\",\"limit\":10}`. / 呼び出し元シンボルが使っている呼び出し先を探す。完全一致には `exactName` を使う。`exact` は USER_GUIDE.md の flag compatibility table に記載された legacy alias。`kind` 未指定時は call-graph 種別 (`call` / `instantiate` / `subscribe` / `friend`) を返し、C++ friend の access/coupling edge は可視化しつつ、metadata 使用 (`attribute` / `annotation`) と compile-time な型位置参照 (`type_reference`) が phantom callee edge として混入しないようにする。同じ物理位置にある constructor の `call` + `instantiate` 重複行も集約する。各グループ行には `callers` との対称性のため `reference_kinds`（行内の distinct kind をソートした配列）と `has_mixed_reference_kinds` も返る（`callees` 側は元々 kind ごとに行を分けているため通常は単一要素）。既存のスカラー `reference_kind` は後方互換のため維持され、同じ kind 値を持つ。metadata 行の container は注釈対象自身ではなく body-range 上の外側シンボルになるため、`callees` で `Method1 --kind attribute` を引いても `Method1` に付いた属性は返らない。`type_reference` は実行時呼び出しではなく宣言型・generic 制約・`is`/`as`/`instanceof`・XML-doc `cref` といった compile-time な型言及なので、`callees` は metadata / 型位置参照の列挙に向かない。Metadata / 型位置参照の列挙は `references --kind attribute|annotation|type_reference` / MCP `references` を使う。例: `callees {\"query\":\"Run\"}`; `callees {\"query\":\"Program.Main\",\"kind\":\"instantiate\",\"lang\":\"csharp\",\"limit\":10}`。",
                new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["query"] = new JsonObject { ["type"] = "string", ["description"] = "Caller/container symbol name pattern to search for" },
                        ["kind"] = new JsonObject { ["type"] = "string", ["description"] = "Filter by call-graph reference kind (call, instantiate, subscribe). Non-call-graph kinds — metadata (attribute, annotation) and type-position (type_reference) — are rejected here; use `references` with the desired kind instead." },
                        ["rankBy"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray { "weighted", "count", "kind" }, ["description"] = "Ranking model: weighted (default; instantiate=3.0, call=1.0, subscribe=0.1), count, or kind.", ["default"] = "weighted" },
                        ["lang"] = new JsonObject { ["type"] = "string", ["description"] = "Filter by language" },
                        ["limit"] = new JsonObject { ["type"] = "integer", ["description"] = "Max results (default: 20). Responses include `truncated`, `more_available`, and `next_offset` when more rows exist.", ["default"] = QueryCommandRunner.DefaultQueryLimit },
                        ["offset"] = new JsonObject { ["type"] = "integer", ["description"] = "Zero-based result offset for pagination; use `next_offset` from a truncated response.", ["default"] = 0, ["minimum"] = 0 },
                        ["path"] = new JsonObject { ["oneOf"] = new JsonArray { new JsonObject { ["type"] = "string" }, new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" } } }, ["description"] = "Prefer or restrict matches to paths containing this text. Accepts a single string or an array; multiple values are OR'd together." },
                        ["excludePaths"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" }, ["description"] = "Exclude any paths containing these texts" },
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
                "Search for code symbols (functions, classes, interfaces, imports) by name pattern. For exact matches, use `exactName`; `exact` is the legacy alias documented in USER_GUIDE.md's flag compatibility table. Examples: `symbols {\"query\":\"Service\"}`; `symbols {\"query\":\"Run\",\"kind\":\"function\",\"lang\":\"csharp\",\"exactName\":true}`. / シンボル（関数、クラス、インターフェース、import）を名前パターンで検索。完全一致には `exactName` を使う。`exact` は USER_GUIDE.md の flag compatibility table に記載された legacy alias。例: `symbols {\"query\":\"Service\"}`; `symbols {\"query\":\"Run\",\"kind\":\"function\",\"lang\":\"csharp\",\"exactName\":true}`。",
                new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["query"] = new JsonObject { ["type"] = "string", ["description"] = "Symbol name pattern to search for. Treated as a literal substring (no `|`-OR sugar), so operator symbols such as `operator |` remain searchable." },
                        ["names"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" }, ["description"] = "Optional list of additional symbol name patterns, OR-joined with `query`. Use this to resolve multiple candidate names in one call." },
                        ["kind"] = new JsonObject { ["type"] = "string", ["description"] = "Filter by symbol kind (function, class, interface, import, etc.)" },
                        ["lang"] = new JsonObject { ["type"] = "string", ["description"] = "Filter by language" },
                        ["limit"] = new JsonObject { ["type"] = "integer", ["description"] = "Max results (default: 20)", ["default"] = QueryCommandRunner.DefaultQueryLimit },
                        ["path"] = new JsonObject { ["oneOf"] = new JsonArray { new JsonObject { ["type"] = "string" }, new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" } } }, ["description"] = "Prefer or restrict matches to paths containing this text. Accepts a single string or an array; multiple values are OR'd together." },
                        ["excludePaths"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" }, ["description"] = "Exclude any paths containing these texts" },
                        ["excludeTests"] = new JsonObject { ["type"] = "boolean", ["description"] = "Exclude likely test files", ["default"] = false },
                        ["includeGenerated"] = new JsonObject { ["type"] = "boolean", ["description"] = "Include files detected as generated code", ["default"] = false },
                        ["since"] = new JsonObject { ["type"] = "string", ["description"] = "Filter to symbols in files modified since this ISO 8601 timestamp" },
                        ["exactName"] = new JsonObject { ["type"] = "boolean", ["description"] = "Preferred explicit name for exact symbol-name equality instead of substring, so `Run` no longer matches `RunAsync`/`RunImpact`.", ["default"] = false },
                        ["exact"] = new JsonObject { ["type"] = "boolean", ["description"] = "Backward-compatible alias for `exactName`.", ["default"] = false }
                    }
                },
                ReadOnlyAnnotations()),
            CreateToolDefinition(
                "files",
                "List indexed files, optionally filtered by name pattern and language. / インデックス済みファイルを一覧（名前パターン・言語でフィルタ可能）。",
                new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["query"] = new JsonObject { ["type"] = "string", ["description"] = "File path pattern to filter by" },
                        ["lang"] = new JsonObject { ["type"] = "string", ["description"] = "Filter by language" },
                        ["limit"] = new JsonObject { ["type"] = "integer", ["description"] = "Max results (default: 20)", ["default"] = QueryCommandRunner.DefaultQueryLimit },
                        ["path"] = new JsonObject { ["oneOf"] = new JsonArray { new JsonObject { ["type"] = "string" }, new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" } } }, ["description"] = "Additional path filter text. Accepts a single string or an array; multiple values are OR'd together." },
                        ["excludePaths"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" }, ["description"] = "Exclude any paths containing these texts" },
                        ["excludeTests"] = new JsonObject { ["type"] = "boolean", ["description"] = "Exclude likely test files", ["default"] = false },
                        ["includeGenerated"] = new JsonObject { ["type"] = "boolean", ["description"] = "Include files detected as generated code", ["default"] = false },
                        ["since"] = new JsonObject { ["type"] = "string", ["description"] = "Filter to files modified since this ISO 8601 timestamp" }
                    }
                },
                ReadOnlyAnnotations()),
            CreateToolDefinition(
                "excerpt",
                "Reconstruct a file excerpt from indexed chunks for a given line range. Successful responses include `next_step_suggestion` for the file outline; empty responses include `recovery_hint`. / 指定行範囲について、インデックス済みチャンクからファイル抜粋を再構成。成功レスポンスにはファイル outline への `next_step_suggestion`、空レスポンスには `recovery_hint` を含める。",
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
                "Find literal substring matches inside one known indexed file or a small explicit file list, with line numbers and short surrounding context. / 既知のインデックス済みファイル1件または少数の明示ファイル群の中で、行番号と短い前後文脈付きのリテラル部分文字列一致を探す。",
                new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["query"] = new JsonObject { ["type"] = "string", ["description"] = "Literal substring to look for" },
                        ["path"] = new JsonObject { ["oneOf"] = new JsonArray { new JsonObject { ["type"] = "string" }, new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" } } }, ["description"] = "Required file/path scope. Accepts a single string or an array; multiple values are OR'd together." },
                        ["limit"] = new JsonObject { ["type"] = "integer", ["description"] = "Max matching occurrences to return (default: 20)", ["default"] = QueryCommandRunner.DefaultQueryLimit },
                        ["lang"] = new JsonObject { ["type"] = "string", ["description"] = "Filter by language" },
                        ["excludePaths"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" }, ["description"] = "Exclude any paths containing these texts" },
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
                "Return a repo-level overview with selectable sections (`tree`, `languages`, `hotspots`, `metrics`) and optional module depth control. / セクション選択（`tree`, `languages`, `hotspots`, `metrics`）とモジュール深さ制御に対応したリポジトリ俯瞰情報を返す。",
                new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["limit"] = new JsonObject { ["type"] = "integer", ["description"] = "Max items per section (default: 10)", ["default"] = QueryCommandRunner.DefaultMapLimit },
                        ["lang"] = new JsonObject { ["type"] = "string", ["description"] = "Filter by language" },
                        ["path"] = new JsonObject { ["oneOf"] = new JsonArray { new JsonObject { ["type"] = "string" }, new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" } } }, ["description"] = "Prefer or restrict glob-style path patterns. `*` and `?` are wildcards. Accepts a single string or an array; multiple values are OR'd together." },
                        ["excludePaths"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" }, ["description"] = "Exclude glob-style path patterns. `*` and `?` are wildcards." },
                        ["excludeTests"] = new JsonObject { ["type"] = "boolean", ["description"] = "Exclude likely test files", ["default"] = false },
                        ["sections"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray { "tree", "languages", "hotspots", "metrics" } }, ["description"] = "Only include selected response sections. Omit for the full backward-compatible map." },
                        ["depth"] = new JsonObject { ["type"] = "integer", ["description"] = "Maximum module/tree depth to include; 0 keeps only root-level modules.", ["minimum"] = 0 }
                    }
                },
                ReadOnlyAnnotations()),
            CreateToolDefinition(
                "analyze_symbol",
                "Bundle definition, nearby symbols, references, callers, callees, file metadata, and graph-support metadata for one symbol query. For exact matches, use `exactName`; `exact` is the legacy alias documented in USER_GUIDE.md's flag compatibility table. Bundled caller/callee rows carry the same `reference_kind` (preferred summary kind, back-compat) plus `reference_kinds` (sorted distinct) and `has_mixed_reference_kinds` fields as the standalone `callers` / `callees` tools, so mixed `call` + `subscribe` containers stay visible in the bundle. / 1つのシンボルクエリに対して、定義、近傍シンボル、参照、caller、callee、ファイルメタデータ、グラフ対応メタデータをまとめて返す。完全一致には `exactName` を使う。`exact` は USER_GUIDE.md の flag compatibility table に記載された legacy alias。バンドルされた caller / callee 行にも単独の `callers` / `callees` と同じ `reference_kind`（後方互換の優先サマリー種別）、`reference_kinds`（distinct kind の昇順配列）、`has_mixed_reference_kinds` が付くため、`call` + `subscribe` が混在するコンテナも要約 1 ラベルに潰れず見える。",
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
                        ["excludePaths"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" }, ["description"] = "Exclude any paths containing these texts" },
                        ["excludeTests"] = new JsonObject { ["type"] = "boolean", ["description"] = "Exclude likely test files", ["default"] = false },
                        ["includeGenerated"] = new JsonObject { ["type"] = "boolean", ["description"] = "Include files detected as generated code", ["default"] = false },
                        ["exactName"] = new JsonObject { ["type"] = "boolean", ["description"] = "Preferred explicit name for exact bundle symbol-name equality. Propagates through definitions, references, callers, and callees so `Run` no longer pulls in `RunAsync` / `RunImpact`.", ["default"] = false },
                        ["exact"] = new JsonObject { ["type"] = "boolean", ["description"] = "Backward-compatible alias for `exactName`.", ["default"] = false }
                    },
                    ["required"] = new JsonArray { "query" }
                },
                ReadOnlyAnnotations()),
            CreateToolDefinition(
                "impact_analysis",
                "Compute the transitive caller chain for a symbol. The symbol-level BFS walks only call-graph kinds (`call`, `instantiate`, `subscribe`) and excludes metadata-only edges (`attribute`, `annotation`, `type_reference`) so metadata cycles do not inflate caller counts. Multiple edge kinds from the same caller to the same target are counted and returned separately, with `reference_kind`, `reference_kinds`, and `reference_kindCounts` on each caller row. When a scoped query resolves to a single class / struct / interface but no symbol-level callers exist, may return heuristic file-level dependency hints instead; those file hints can include metadata edges, so check `impact_mode`, `heuristic`, and `file_impacts`. When `truncated` is true, inspect `truncated_reason` (`user_limit` means raising `limit` returns more; `safety_cap` means the graph is likely pathological and raising `limit` will not help). Pass `withPaths: true` when you need the call chain via specific intermediates — each caller then carries a `paths` array of shortest routes (issue #1536). / シンボルの推移的呼び出しチェーンを算出。symbol-level BFS は call graph 種別（`call`、`instantiate`、`subscribe`）のみを辿り、metadata-only edge（`attribute`、`annotation`、`type_reference`）を除外するため、metadata cycle で caller 件数が膨らまない。同じ caller から同じ target への複数 edge kind は別々に数えて返し、各 caller 行に `reference_kind`、`reference_kinds`、`reference_kindCounts` が付く。scoped query が単一の class / struct / interface に解決されても symbol-level caller が無い場合は、代わりに heuristic な file-level dependency hint を返すことがある。この file hint は metadata edge を含み得るため、`impact_mode`・`heuristic`・`file_impacts` を確認すること。`truncated` が真のときは `truncated_reason` を見て、`user_limit` なら `limit` を増やせば残りも取得可能、`safety_cap` ならグラフが病的で `limit` を増やしても解消しないことを区別すること。中間シンボル経由の経路が必要な場合は `withPaths: true` を渡すと、各 caller に経路配列 `paths` が付く（issue #1536）。",
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
                        ["excludePaths"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" }, ["description"] = "Exclude any paths containing these texts" },
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
                "Get database statistics: file count, chunk count, symbol count, reference count, and language breakdown. / DB統計情報を取得：ファイル数、チャンク数、シンボル数、参照数、言語別内訳。",
                new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject()
                },
                ReadOnlyAnnotations()),
            CreateToolDefinition(
                "outline",
                "Return the symbol outline of a single indexed file: all functions, classes, imports with line numbers, signatures, and nesting. / 1ファイルのシンボルアウトラインを返す: 関数、クラス、importの行番号、シグネチャ、ネスト構造。",
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
                        ["excludePaths"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" }, ["description"] = "Exclude glob-style path patterns. `*` and `?` are wildcards." },
                        ["excludeTests"] = new JsonObject { ["type"] = "boolean", ["description"] = "Exclude test files", ["default"] = false },
                        ["reverse"] = new JsonObject { ["type"] = "boolean", ["description"] = "Reverse lookup: show files that depend ON the matched path", ["default"] = false },
                        ["format"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray { "edgelist", "json-graph" }, ["description"] = "Structured response format. `edgelist` preserves the existing edges array; `json-graph` returns nodes and edges.", ["default"] = "edgelist" },
                        ["cycles"] = new JsonObject { ["type"] = "boolean", ["description"] = "Return dependency cycles instead of ordinary edge rows.", ["default"] = false }
                    }
                },
                ReadOnlyAnnotations()),
            CreateToolDefinition(
                "languages",
                "List all supported languages with their file extensions and capabilities (symbol extraction, call-graph queries). No database required. / 対応言語一覧を拡張子・機能（シンボル抽出、コールグラフ対応）付きで返す。DB不要。",
                new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject()
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
                        ["path"] = new JsonObject { ["oneOf"] = new JsonArray { new JsonObject { ["type"] = "string" }, new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" } } }, ["description"] = "Filter to paths containing this text. Accepts a single string or an array; multiple values are OR'd together." },
                        ["excludePaths"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" }, ["description"] = "Exclude any paths containing these texts" },
                        ["excludeTests"] = new JsonObject { ["type"] = "boolean", ["description"] = "Exclude likely test files", ["default"] = false }
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
                            ["description"] = "Array of {tool, arguments} objects. Only read-only tools are allowed (not index or backfill_fold).",
                            ["items"] = new JsonObject
                            {
                                ["type"] = "object",
                                ["properties"] = new JsonObject
                                {
                                    ["tool"] = new JsonObject { ["type"] = "string", ["description"] = "Tool name (e.g. search, definition, symbols)" },
                                    ["arguments"] = new JsonObject { ["type"] = "object", ["description"] = "Tool arguments" }
                                },
                                ["required"] = new JsonArray { "tool" }
                            }
                        }
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
                        ["maxFileBytes"] = new JsonObject { ["type"] = "integer", ["description"] = "Override the per-file indexing size limit for this run. Defaults to CDIDX_MAX_FILE_BYTES or 4MiB.", ["minimum"] = 1, ["maximum"] = int.MaxValue }
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
                + "Returns symbols ordered by reference score, reference count, then deterministic ties by path, line, name, kind, and symbol id. `groupBy` can be `symbol`, `file`, or `statement`; the default is symbol grouping for non-SQL scopes and statement grouping for SQL scopes to preserve existing SQL behavior. Names that are unique within the active language/kind candidate set use codebase-wide totals; duplicate-name families fall back to conservative same-file counts, and same-file duplicate rows may be grouped when the DB cannot disambiguate targets. Cross-file grouping of duplicate families is trusted only on indexes stamped with the current authoritative hotspot-family version. Useful for identifying central, high-impact code. "
                + "/ コードベースで最も参照されるシンボルを検索する（ホットスポット分析）。"
                + "参照スコア、参照回数の順にシンボルを返し、同点は path、line、name、kind、symbol id で決定的に並べる。`groupBy` は `symbol` / `file` / `statement` を指定でき、既存 SQL 挙動を保つため既定は非 SQL scope では symbol、SQL scope では statement。active な言語/種別候補集合で一意な名前は codebase 全体の件数を使い、同名ファミリーは保守的な same-file 件数へフォールバックし、DB が対象を曖昧なく結べない同一ファイル重複行は集約される。duplicate family の cross-file 集約は current の authoritative hotspot-family version で stamp された index でのみ信頼する。中心的で影響の大きいコードの特定に有用。",
                new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["kind"] = new JsonObject { ["type"] = "string", ["description"] = "Filter by symbol kind" },
                        ["lang"] = new JsonObject { ["type"] = "string", ["description"] = "Filter by language" },
                        ["limit"] = new JsonObject { ["type"] = "integer", ["description"] = "Max results (default: 20)", ["default"] = QueryCommandRunner.DefaultQueryLimit },
                        ["groupBy"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("symbol", "file", "statement"), ["description"] = "Grouping unit. Defaults to symbol for non-SQL scopes and statement for SQL scopes." },
                        ["path"] = new JsonObject { ["oneOf"] = new JsonArray { new JsonObject { ["type"] = "string" }, new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" } } }, ["description"] = "Restrict to glob-style path patterns. `*` and `?` are wildcards. Accepts a single string or an array; multiple values are OR'd together." },
                        ["excludePaths"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" }, ["description"] = "Exclude glob-style path patterns. `*` and `?` are wildcards." },
                        ["excludeTests"] = new JsonObject { ["type"] = "boolean", ["description"] = "Exclude test files (default: false)", ["default"] = false }
                    }
                },
                ReadOnlyAnnotations()),
            CreateToolDefinition(
                "unused_symbols",
                "Find symbols that are defined but never referenced in the indexed codebase. "
                + "Useful for dead code detection. Results include confidence buckets so private hits rank ahead of public/exported suspects, and the lowest-confidence bucket is reserved for config-bound properties or C#-style attribute-adjacent reflection surfaces. Only meaningful for languages with reference extraction support. "
                + "Structured output includes `summary.by_bucket`, `summary.by_confidence`, and `bucket_taxonomy`; bucket values are `likely_unused_private`, `maybe_unused_nonpublic`, `public_or_exported_no_refs`, and `reflection_or_config_suspect`. Use `bucket` or `minConfidence` to audit a single bucket or confidence class. "
                + "C# nameof/typeof and direct reflection member-name literals such as GetMethod(\"Foo\") are indexed as references; dynamically constructed reflection names can still require manual review. "
                + "/ インデックス済みコードベースで定義されているが一度も参照されていないシンボルを検索する。"
                + "デッドコード検出に有用。private 候補を public/exported suspect より前に返し、最低信頼 bucket は config-bound な property または C# 風 attribute 隣接の reflection surface 用に使う。参照抽出対応言語でのみ意味がある。"
                + "構造化出力には `summary.by_bucket`、`summary.by_confidence`、`bucket_taxonomy` が含まれ、bucket 値は `likely_unused_private`、`maybe_unused_nonpublic`、`public_or_exported_no_refs`、`reflection_or_config_suspect`。`bucket` または `minConfidence` で単一 bucket や confidence class を監査できる。"
                + "C# の nameof/typeof と GetMethod(\"Foo\") のような直接の reflection member-name literal は参照として index されるが、動的に組み立てた reflection 名は手動確認が必要な場合がある。",
                new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["kind"] = new JsonObject { ["type"] = "string", ["description"] = "Filter by symbol kind (function, class, property, interface, enum, struct, event, delegate)" },
                        ["lang"] = new JsonObject { ["type"] = "string", ["description"] = "Filter by language (recommended: use a graph-supported language)" },
                        ["limit"] = new JsonObject { ["type"] = "integer", ["description"] = "Max results (default: 50)", ["default"] = QueryCommandRunner.DefaultImpactLimit },
                        ["bucket"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("likely_unused_private", "maybe_unused_nonpublic", "public_or_exported_no_refs", "reflection_or_config_suspect"), ["description"] = "Return only one unused-symbol bucket." },
                        ["minConfidence"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("medium", "low"), ["description"] = "Return symbols at or above this confidence threshold." },
                        ["path"] = new JsonObject { ["oneOf"] = new JsonArray { new JsonObject { ["type"] = "string" }, new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" } } }, ["description"] = "Restrict to paths containing this text. Accepts a single string or an array; multiple values are OR'd together." },
                        ["excludePaths"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" }, ["description"] = "Exclude paths containing any of these texts" },
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

        var result = new JsonObject { ["tools"] = filtered };
        return CreateSuccessResponse(id, result);
    }

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
                ApplyCommonSchemaConstraint(toolName, name, schema);
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
                obj.TryAdd("minLength", 1);
                obj.TryAdd("maxLength", 4096);
                obj.TryAdd("pattern", @"^(?!/)(?![A-Za-z]:)(?!.*(^|/)\.\.(/|$))(?!.*\u0000).*$");
                AppendConstraintDescription(obj, "Must be workspace-relative, non-empty, and must not contain NUL bytes or `..` path traversal segments.");
                break;
            case "excludePaths":
                obj.TryAdd("maxItems", 100);
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

    private static void AppendConstraintDescription(JsonObject obj, string sentence)
    {
        var description = obj["description"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(description) || description.Contains(sentence, StringComparison.Ordinal))
            return;
        obj["description"] = $"{description} {sentence}";
    }
}
