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
                "Full-text search across indexed code chunks using FTS5. Returns compact match-centered snippets with line metadata. The literal-safe path quotes each whitespace-separated token as an FTS5 phrase and matches only what the user typed — for CJK that means `search 計算` no longer also matches `計算する`/`計算機`, because unicode61 keeps adjacent CJK codepoints in one token. Two opt-ins enable FTS5 prefix expansion: (1) trailing `*` on a single token in the `query` string (`search 計算*` to match `計算する`); (2) the `prefix` flag, which promotes every token in the query to a prefix phrase. Use `exact` for case-sensitive exact-substring matching that bypasses FTS5 entirely. Non-CJK tokens follow the same rule — ASCII identifiers also no longer auto-prefix, so use `--prefix` or trailing `*` to widen. Emoji-mixed tokens cannot be distinguished from their plain ASCII counterpart at the FTS layer (unicode61 drops the emoji on both index and query side — `foo🎉` is FTS-equivalent to `foo`), and pure emoji substring search is 0-result for the same reason; use `exact` when emoji identity matters. / FTS5を使ったコードチャンクの全文検索。一致中心の軽量スニペットと行メタデータを返す。literal-safe 経路は空白区切りの各トークンを FTS5 phrase として引用し、ユーザーが入力したものだけにマッチする。CJK の場合、unicode61 は隣接 CJK コードポイントを一語として扱うため、`search 計算` は `計算する`/`計算機` にはマッチしない。FTS5 prefix への昇格は 2 通りでオプトイン: (1) `query` 文字列内のトークン末尾に `*` を付ける（`search 計算*` で `計算する` にマッチ）。(2) `prefix` フラグで、クエリの全トークンを prefix phrase に昇格させる。`exact` を使うと FTS5 を経由せず大小文字区別の厳密部分文字列マッチになる。CJK 以外（ASCII 識別子等）も同じルールで、自動 prefix は行わないため、広げたい場合は `--prefix` か末尾 `*` を使う。絵文字混在トークンは、unicode61 が indexing とクエリの両側で絵文字を削ぐため FTS 層で素の ASCII トークンと区別できず（`foo🎉` は FTS 上 `foo` と等価）、絵文字単独の部分一致も同じ理由で 0 件になる。絵文字の同一性が必要な場合は `exact` を使う。",
                new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["query"] = new JsonObject { ["type"] = "string", ["description"] = "Search query text. Append `*` to a token to make that token a prefix phrase (`計算*` matches `計算する`)." },
                        ["limit"] = new JsonObject { ["type"] = "integer", ["description"] = "Max results (default: 20)", ["default"] = 20 },
                        ["lang"] = new JsonObject { ["type"] = "string", ["description"] = "Filter by language (e.g. csharp, python, javascript)" },
                        ["snippetLines"] = new JsonObject { ["type"] = "integer", ["description"] = "Max snippet lines per result (default: 8, max: 20)", ["default"] = 8, ["minimum"] = 1, ["maximum"] = SearchSnippetFormatter.MaxSnippetLines },
                        ["maxLineWidth"] = new JsonObject { ["type"] = "integer", ["description"] = "Clamp very long single-line snippets per line (default: 512; 0 disables clamping). Match lines are clamped around the first match; non-match lines are clamped from the head. Each clamp inserts a `...(+N)...` marker showing how many chars were elided.", ["default"] = LineWidthFormatter.DefaultMaxLineWidth, ["minimum"] = 0, ["maximum"] = LineWidthFormatter.MaxAllowedLineWidth },
                        ["rawQuery"] = new JsonObject { ["type"] = "boolean", ["description"] = "Use raw FTS5 syntax instead of literal-safe quoting", ["default"] = false },
                        ["path"] = new JsonObject { ["oneOf"] = new JsonArray { new JsonObject { ["type"] = "string" }, new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" } } }, ["description"] = "Prefer or restrict glob-style path patterns. `*` and `?` are wildcards. Accepts a single string or an array; multiple values are OR'd together." },
                        ["excludePaths"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" }, ["description"] = "Exclude glob-style path patterns. `*` and `?` are wildcards." },
                        ["excludeTests"] = new JsonObject { ["type"] = "boolean", ["description"] = "Exclude likely test files", ["default"] = false },
                        ["since"] = new JsonObject { ["type"] = "string", ["description"] = "Filter to files modified since this ISO 8601 timestamp" },
                        ["noDedup"] = new JsonObject { ["type"] = "boolean", ["description"] = "Disable overlapping-chunk deduplication for raw results", ["default"] = false },
                        ["exactSubstring"] = new JsonObject { ["type"] = "boolean", ["description"] = "Preferred explicit name for search's exact mode: case-sensitive exact substring match (bypasses FTS5).", ["default"] = false },
                        ["exact"] = new JsonObject { ["type"] = "boolean", ["description"] = "Backward-compatible alias for `exactSubstring`.", ["default"] = false },
                        ["prefix"] = new JsonObject { ["type"] = "boolean", ["description"] = "Opt into FTS5 prefix expansion for every token in `query`. Cannot be combined with `exact`/`exactSubstring`.", ["default"] = false }
                    },
                    ["required"] = new JsonArray { "query" }
                },
                ReadOnlyAnnotations()),
            CreateToolDefinition(
                "definition",
                "Resolve symbol definitions with definition ranges, signatures, and optional body content. / 定義範囲、シグネチャ、必要に応じて本体内容付きでシンボル定義を解決。",
                new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["query"] = new JsonObject { ["type"] = "string", ["description"] = "Symbol name pattern to resolve" },
                        ["kind"] = new JsonObject { ["type"] = "string", ["description"] = "Filter by symbol kind" },
                        ["lang"] = new JsonObject { ["type"] = "string", ["description"] = "Filter by language" },
                        ["limit"] = new JsonObject { ["type"] = "integer", ["description"] = "Max results (default: 20)", ["default"] = 20 },
                        ["includeBody"] = new JsonObject { ["type"] = "boolean", ["description"] = "Include body content when body ranges are available", ["default"] = false },
                        ["path"] = new JsonObject { ["oneOf"] = new JsonArray { new JsonObject { ["type"] = "string" }, new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" } } }, ["description"] = "Prefer or restrict matches to paths containing this text. Accepts a single string or an array; multiple values are OR'd together." },
                        ["excludePaths"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" }, ["description"] = "Exclude any paths containing these texts" },
                        ["excludeTests"] = new JsonObject { ["type"] = "boolean", ["description"] = "Exclude likely test files", ["default"] = false },
                        ["since"] = new JsonObject { ["type"] = "string", ["description"] = "Filter to symbols in files modified since this ISO 8601 timestamp" },
                        ["exactName"] = new JsonObject { ["type"] = "boolean", ["description"] = "Preferred explicit name for exact symbol-name equality: NFKC + Unicode CaseFold exact name match instead of substring, so `Run` no longer also returns `RunAsync`.", ["default"] = false },
                        ["exact"] = new JsonObject { ["type"] = "boolean", ["description"] = "Backward-compatible alias for `exactName`.", ["default"] = false }
                    },
                    ["required"] = new JsonArray { "query" }
                },
                ReadOnlyAnnotations()),
            CreateToolDefinition(
                "references",
                "Search indexed symbol references such as call sites. When `kind` is omitted, all indexed reference kinds including metadata uses (`attribute` / `annotation`) and compile-time type-position references (`type_reference`) stay visible, and identical constructor `call` + `instantiate` rows at one physical site are collapsed. Pass `kind: \"type_reference\"` to enumerate declaration types, generic constraints, `is`/`as`/`instanceof`, and XML-doc `cref` targets. / 呼び出し箇所などのインデックス済みシンボル参照を検索。`kind` 未指定時は metadata (`attribute` / `annotation`) と compile-time な型位置参照 (`type_reference`) も含む全 reference kind を表示したうえで、同じ物理位置にある constructor の `call` + `instantiate` 重複行を集約する。`kind: \"type_reference\"` を指定すると、宣言型・generic 制約・`is`/`as`/`instanceof`・XML-doc `cref` 対象を列挙できる。",
                new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["query"] = new JsonObject { ["type"] = "string", ["description"] = "Referenced symbol name pattern to search for" },
                        ["kind"] = new JsonObject { ["type"] = "string", ["description"] = "Filter by reference kind (call, instantiate, subscribe, attribute, annotation, type_reference)" },
                        ["lang"] = new JsonObject { ["type"] = "string", ["description"] = "Filter by language" },
                        ["limit"] = new JsonObject { ["type"] = "integer", ["description"] = "Max results (default: 20)", ["default"] = 20 },
                        ["maxLineWidth"] = new JsonObject { ["type"] = "integer", ["description"] = "Clamp very long single-line context payloads per result (default: 512; 0 disables clamping)", ["default"] = LineWidthFormatter.DefaultMaxLineWidth, ["minimum"] = 0, ["maximum"] = LineWidthFormatter.MaxAllowedLineWidth },
                        ["path"] = new JsonObject { ["oneOf"] = new JsonArray { new JsonObject { ["type"] = "string" }, new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" } } }, ["description"] = "Prefer or restrict matches to paths containing this text. Accepts a single string or an array; multiple values are OR'd together." },
                        ["excludePaths"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" }, ["description"] = "Exclude any paths containing these texts" },
                        ["excludeTests"] = new JsonObject { ["type"] = "boolean", ["description"] = "Exclude likely test files", ["default"] = false },
                        ["exactName"] = new JsonObject { ["type"] = "boolean", ["description"] = "Preferred explicit name for exact referenced-symbol equality. Uses NFKC + Unicode CaseFold so `Run` no longer matches `RunAsync`.", ["default"] = false },
                        ["exact"] = new JsonObject { ["type"] = "boolean", ["description"] = "Backward-compatible alias for `exactName`.", ["default"] = false }
                    },
                    ["required"] = new JsonArray { "query" }
                },
                ReadOnlyAnnotations()),
            CreateToolDefinition(
                "callers",
                "Find caller symbols that reference a callee. When `kind` is omitted, only call-graph kinds (`call`, `instantiate`, `subscribe`) are returned so metadata uses (`attribute` / `annotation`) and compile-time type-position references (`type_reference`) do not pollute caller edges; identical constructor `call` + `instantiate` rows at one physical site also collapse. Each grouped row additionally exposes `referenceKinds` (sorted distinct kinds behind the row) and `hasMixedReferenceKinds` so callers do not have to trust the single summary label when a container mixes `call` + `subscribe` edges. The existing `referenceKind` scalar is retained for back-compat and carries the preferred summary kind (`instantiate` > `subscribe` > `MIN(call)`). `callers` / `callees` are not a reliable path to metadata or type-position references — metadata rows are attributed to their enclosing body-range symbol (for a class-level declaration, that is the class itself; for a file-level target such as `[assembly: ...]`, `containerName` is `null` and the row drops from these graph queries entirely), and `type_reference` rows are compile-time type mentions (declaration types, generic constraints, `is`/`as`/`instanceof`, XML-doc `cref`) rather than runtime calls. Use `references` with `kind: \"attribute\"`, `\"annotation\"`, or `\"type_reference\"` instead. / 指定シンボルを参照している呼び出し元シンボルを探す。`kind` 未指定時は call-graph 種別 (`call` / `instantiate` / `subscribe`) のみを返し、metadata 使用 (`attribute` / `annotation`) と compile-time な型位置参照 (`type_reference`) が phantom caller edge として混入しないようにする。同じ物理位置にある constructor の `call` + `instantiate` 重複行も集約する。各グループ行には `referenceKinds`（行内の distinct kind をソートした配列）と `hasMixedReferenceKinds` も追加で返すため、container が `call` + `subscribe` を混在させている行で要約 1 ラベルに騙されずに済む。既存のスカラー `referenceKind` は後方互換のため維持され、優先サマリー種別（`instantiate` > `subscribe` > `MIN(call)`）を持つ。metadata 行の container は注釈対象そのものではなく body-range 上の外側シンボル（クラス直下宣言ならクラス、ファイルレベル target なら `null`）になり、`type_reference` は実行時呼び出しではなく宣言型・generic 制約・`is`/`as`/`instanceof`・XML-doc `cref` といった compile-time な型言及なので、`callers` / `callees` は metadata / 型位置参照の列挙に向かない。Metadata / 型位置参照の列挙は `references --kind attribute|annotation|type_reference` / MCP `references` を使う。",
                new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["query"] = new JsonObject { ["type"] = "string", ["description"] = "Callee symbol name pattern to search for" },
                        ["kind"] = new JsonObject { ["type"] = "string", ["description"] = "Filter by call-graph reference kind (call, instantiate, subscribe). Non-call-graph kinds — metadata (attribute, annotation) and type-position (type_reference) — are rejected here; use `references` with the desired kind instead." },
                        ["lang"] = new JsonObject { ["type"] = "string", ["description"] = "Filter by language" },
                        ["limit"] = new JsonObject { ["type"] = "integer", ["description"] = "Max results (default: 20)", ["default"] = 20 },
                        ["path"] = new JsonObject { ["oneOf"] = new JsonArray { new JsonObject { ["type"] = "string" }, new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" } } }, ["description"] = "Prefer or restrict matches to paths containing this text. Accepts a single string or an array; multiple values are OR'd together." },
                        ["excludePaths"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" }, ["description"] = "Exclude any paths containing these texts" },
                        ["excludeTests"] = new JsonObject { ["type"] = "boolean", ["description"] = "Exclude likely test files", ["default"] = false },
                        ["exactName"] = new JsonObject { ["type"] = "boolean", ["description"] = "Preferred explicit name for exact callee-name equality. Uses NFKC + Unicode CaseFold so `Run` no longer matches `RunAsync`.", ["default"] = false },
                        ["exact"] = new JsonObject { ["type"] = "boolean", ["description"] = "Backward-compatible alias for `exactName`.", ["default"] = false }
                    },
                    ["required"] = new JsonArray { "query" }
                },
                ReadOnlyAnnotations()),
            CreateToolDefinition(
                "callees",
                "Find callees used by a caller/container symbol. When `kind` is omitted, only call-graph kinds (`call`, `instantiate`, `subscribe`) are returned so metadata uses (`attribute` / `annotation`) and compile-time type-position references (`type_reference`) do not pollute callee edges; identical constructor `call` + `instantiate` rows at one physical site also collapse. Each grouped row additionally exposes `referenceKinds` (sorted distinct kinds behind the row) and `hasMixedReferenceKinds` for symmetry with `callers`, even though rows are already split per kind on this side. The existing `referenceKind` scalar is retained for back-compat and carries the same kind value. `callees` is not a reliable path to metadata or type-position references — the container assigned to an attribute / annotation row is the enclosing body-range symbol, not the annotated declaration, so `callees Method1 --kind attribute` does not return the attributes on `Method1`, and `type_reference` rows are compile-time type mentions (declaration types, generic constraints, `is`/`as`/`instanceof`, XML-doc `cref`) rather than runtime calls. Use `references` with `kind: \"attribute\"`, `\"annotation\"`, or `\"type_reference\"` instead. / 呼び出し元シンボルが使っている呼び出し先を探す。`kind` 未指定時は call-graph 種別 (`call` / `instantiate` / `subscribe`) のみを返し、metadata 使用 (`attribute` / `annotation`) と compile-time な型位置参照 (`type_reference`) が phantom callee edge として混入しないようにする。同じ物理位置にある constructor の `call` + `instantiate` 重複行も集約する。各グループ行には `callers` との対称性のため `referenceKinds`（行内の distinct kind をソートした配列）と `hasMixedReferenceKinds` も返る（`callees` 側は元々 kind ごとに行を分けているため通常は単一要素）。既存のスカラー `referenceKind` は後方互換のため維持され、同じ kind 値を持つ。metadata 行の container は注釈対象自身ではなく body-range 上の外側シンボルになるため、`callees` で `Method1 --kind attribute` を引いても `Method1` に付いた属性は返らない。`type_reference` は実行時呼び出しではなく宣言型・generic 制約・`is`/`as`/`instanceof`・XML-doc `cref` といった compile-time な型言及なので、`callees` は metadata / 型位置参照の列挙に向かない。Metadata / 型位置参照の列挙は `references --kind attribute|annotation|type_reference` / MCP `references` を使う。",
                new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["query"] = new JsonObject { ["type"] = "string", ["description"] = "Caller/container symbol name pattern to search for" },
                        ["kind"] = new JsonObject { ["type"] = "string", ["description"] = "Filter by call-graph reference kind (call, instantiate, subscribe). Non-call-graph kinds — metadata (attribute, annotation) and type-position (type_reference) — are rejected here; use `references` with the desired kind instead." },
                        ["lang"] = new JsonObject { ["type"] = "string", ["description"] = "Filter by language" },
                        ["limit"] = new JsonObject { ["type"] = "integer", ["description"] = "Max results (default: 20)", ["default"] = 20 },
                        ["path"] = new JsonObject { ["oneOf"] = new JsonArray { new JsonObject { ["type"] = "string" }, new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" } } }, ["description"] = "Prefer or restrict matches to paths containing this text. Accepts a single string or an array; multiple values are OR'd together." },
                        ["excludePaths"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" }, ["description"] = "Exclude any paths containing these texts" },
                        ["excludeTests"] = new JsonObject { ["type"] = "boolean", ["description"] = "Exclude likely test files", ["default"] = false },
                        ["exactName"] = new JsonObject { ["type"] = "boolean", ["description"] = "Preferred explicit name for exact caller/container equality. Uses NFKC + Unicode CaseFold so `Run` no longer matches `RunAsync`.", ["default"] = false },
                        ["exact"] = new JsonObject { ["type"] = "boolean", ["description"] = "Backward-compatible alias for `exactName`.", ["default"] = false }
                    },
                    ["required"] = new JsonArray { "query" }
                },
                ReadOnlyAnnotations()),
            CreateToolDefinition(
                "symbols",
                "Search for code symbols (functions, classes, interfaces, imports) by name pattern. / シンボル（関数、クラス、インターフェース、import）を名前パターンで検索。",
                new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["query"] = new JsonObject { ["type"] = "string", ["description"] = "Symbol name pattern to search for. Treated as a literal substring (no `|`-OR sugar), so operator symbols such as `operator |` remain searchable." },
                        ["names"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" }, ["description"] = "Optional list of additional symbol name patterns, OR-joined with `query`. Use this to resolve multiple candidate names in one call." },
                        ["kind"] = new JsonObject { ["type"] = "string", ["description"] = "Filter by symbol kind (function, class, interface, import, etc.)" },
                        ["lang"] = new JsonObject { ["type"] = "string", ["description"] = "Filter by language" },
                        ["limit"] = new JsonObject { ["type"] = "integer", ["description"] = "Max results (default: 20)", ["default"] = 20 },
                        ["path"] = new JsonObject { ["oneOf"] = new JsonArray { new JsonObject { ["type"] = "string" }, new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" } } }, ["description"] = "Prefer or restrict matches to paths containing this text. Accepts a single string or an array; multiple values are OR'd together." },
                        ["excludePaths"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" }, ["description"] = "Exclude any paths containing these texts" },
                        ["excludeTests"] = new JsonObject { ["type"] = "boolean", ["description"] = "Exclude likely test files", ["default"] = false },
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
                        ["limit"] = new JsonObject { ["type"] = "integer", ["description"] = "Max results (default: 20)", ["default"] = 20 },
                        ["path"] = new JsonObject { ["oneOf"] = new JsonArray { new JsonObject { ["type"] = "string" }, new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" } } }, ["description"] = "Additional path filter text. Accepts a single string or an array; multiple values are OR'd together." },
                        ["excludePaths"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" }, ["description"] = "Exclude any paths containing these texts" },
                        ["excludeTests"] = new JsonObject { ["type"] = "boolean", ["description"] = "Exclude likely test files", ["default"] = false },
                        ["since"] = new JsonObject { ["type"] = "string", ["description"] = "Filter to files modified since this ISO 8601 timestamp" }
                    }
                },
                ReadOnlyAnnotations()),
            CreateToolDefinition(
                "excerpt",
                "Reconstruct a file excerpt from indexed chunks for a given line range. / 指定行範囲について、インデックス済みチャンクからファイル抜粋を再構成。",
                new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["path"] = new JsonObject { ["type"] = "string", ["description"] = "Indexed file path" },
                        ["startLine"] = new JsonObject { ["type"] = "integer", ["description"] = "Start line (1-based)" },
                        ["endLine"] = new JsonObject { ["type"] = "integer", ["description"] = "End line (default: startLine)" },
                        ["before"] = new JsonObject { ["type"] = "integer", ["description"] = "Extra context lines before the range", ["default"] = 0, ["minimum"] = 0 },
                        ["after"] = new JsonObject { ["type"] = "integer", ["description"] = "Extra context lines after the range", ["default"] = 0, ["minimum"] = 0 },
                        ["focusLine"] = new JsonObject { ["type"] = "integer", ["description"] = "Optional line inside the excerpt whose focused column should stay visible when clamping; requires focusColumn", ["minimum"] = 1 },
                        ["focusColumn"] = new JsonObject { ["type"] = "integer", ["description"] = "Optional 1-based column to keep centered when clamping long single-line content; must be within the focused line length", ["minimum"] = 1 },
                        ["focusLength"] = new JsonObject { ["type"] = "integer", ["description"] = "Optional focused span width when clamping (default: 1); requires focusColumn", ["default"] = 1, ["minimum"] = 1 },
                        ["maxLineWidth"] = new JsonObject { ["type"] = "integer", ["description"] = "Clamp very long single-line excerpt payloads per line (default: 512; 0 disables clamping)", ["default"] = LineWidthFormatter.DefaultMaxLineWidth, ["minimum"] = 0, ["maximum"] = LineWidthFormatter.MaxAllowedLineWidth }
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
                        ["limit"] = new JsonObject { ["type"] = "integer", ["description"] = "Max matching occurrences to return (default: 20)", ["default"] = 20 },
                        ["lang"] = new JsonObject { ["type"] = "string", ["description"] = "Filter by language" },
                        ["excludePaths"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" }, ["description"] = "Exclude any paths containing these texts" },
                        ["excludeTests"] = new JsonObject { ["type"] = "boolean", ["description"] = "Exclude likely test files", ["default"] = false },
                        ["before"] = new JsonObject { ["type"] = "integer", ["description"] = "Context lines before the match (default: 0)", ["default"] = 0, ["minimum"] = 0 },
                        ["after"] = new JsonObject { ["type"] = "integer", ["description"] = "Context lines after the match (default: 0)", ["default"] = 0, ["minimum"] = 0 },
                        ["maxLineWidth"] = new JsonObject { ["type"] = "integer", ["description"] = "Clamp very long single-line snippets per line (default: 512; 0 disables clamping)", ["default"] = LineWidthFormatter.DefaultMaxLineWidth, ["minimum"] = 0, ["maximum"] = LineWidthFormatter.MaxAllowedLineWidth },
                        ["exact"] = new JsonObject { ["type"] = "boolean", ["description"] = "Case-sensitive literal substring match. Default is case-insensitive literal substring matching.", ["default"] = false }
                    },
                    ["required"] = new JsonArray { "query", "path" }
                },
                ReadOnlyAnnotations()),
            CreateToolDefinition(
                "map",
                "Return a repo-level overview with languages, modules, top files, and likely entrypoints. / 言語、モジュール、主要ファイル、推定エントリポイントを含むリポジトリ俯瞰情報を返す。",
                new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["limit"] = new JsonObject { ["type"] = "integer", ["description"] = "Max items per section (default: 10)", ["default"] = 10 },
                        ["lang"] = new JsonObject { ["type"] = "string", ["description"] = "Filter by language" },
                        ["path"] = new JsonObject { ["oneOf"] = new JsonArray { new JsonObject { ["type"] = "string" }, new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" } } }, ["description"] = "Prefer or restrict glob-style path patterns. `*` and `?` are wildcards. Accepts a single string or an array; multiple values are OR'd together." },
                        ["excludePaths"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" }, ["description"] = "Exclude glob-style path patterns. `*` and `?` are wildcards." },
                        ["excludeTests"] = new JsonObject { ["type"] = "boolean", ["description"] = "Exclude likely test files", ["default"] = false }
                    }
                },
                ReadOnlyAnnotations()),
            CreateToolDefinition(
                "analyze_symbol",
                "Bundle definition, nearby symbols, references, callers, callees, file metadata, and graph-support metadata for one symbol query. Bundled caller/callee rows carry the same `referenceKind` (preferred summary kind, back-compat) plus `referenceKinds` (sorted distinct) and `hasMixedReferenceKinds` fields as the standalone `callers` / `callees` tools, so mixed `call` + `subscribe` containers stay visible in the bundle. / 1つのシンボルクエリに対して、定義、近傍シンボル、参照、caller、callee、ファイルメタデータ、グラフ対応メタデータをまとめて返す。バンドルされた caller / callee 行にも単独の `callers` / `callees` と同じ `referenceKind`（後方互換の優先サマリー種別）、`referenceKinds`（distinct kind の昇順配列）、`hasMixedReferenceKinds` が付くため、`call` + `subscribe` が混在するコンテナも要約 1 ラベルに潰れず見える。",
                new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["query"] = new JsonObject { ["type"] = "string", ["description"] = "Symbol name to inspect" },
                        ["limit"] = new JsonObject { ["type"] = "integer", ["description"] = "Max items per section (default: 10)", ["default"] = 10 },
                        ["lang"] = new JsonObject { ["type"] = "string", ["description"] = "Filter by language" },
                        ["includeBody"] = new JsonObject { ["type"] = "boolean", ["description"] = "Include body content in definitions when available", ["default"] = false },
                        ["maxLineWidth"] = new JsonObject { ["type"] = "integer", ["description"] = "Clamp bundled reference context lines so single-line files stay bounded (default: 512; 0 disables clamping)", ["default"] = LineWidthFormatter.DefaultMaxLineWidth, ["minimum"] = 0, ["maximum"] = LineWidthFormatter.MaxAllowedLineWidth },
                        ["path"] = new JsonObject { ["oneOf"] = new JsonArray { new JsonObject { ["type"] = "string" }, new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" } } }, ["description"] = "Prefer or restrict paths containing this text. Accepts a single string or an array; multiple values are OR'd together." },
                        ["excludePaths"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" }, ["description"] = "Exclude any paths containing these texts" },
                        ["excludeTests"] = new JsonObject { ["type"] = "boolean", ["description"] = "Exclude likely test files", ["default"] = false },
                        ["exactName"] = new JsonObject { ["type"] = "boolean", ["description"] = "Preferred explicit name for exact bundle symbol-name equality. Propagates through definitions, references, callers, and callees so `Run` no longer pulls in `RunAsync` / `RunImpact`.", ["default"] = false },
                        ["exact"] = new JsonObject { ["type"] = "boolean", ["description"] = "Backward-compatible alias for `exactName`.", ["default"] = false }
                    },
                    ["required"] = new JsonArray { "query" }
                },
                ReadOnlyAnnotations()),
            CreateToolDefinition(
                "impact_analysis",
                "Compute the transitive caller chain for a symbol. When a scoped query resolves to a single class / struct / interface but no symbol-level callers exist, may return heuristic file-level dependency hints instead; check `impact_mode`, `heuristic`, and `file_impacts`. When `truncated` is true, inspect `truncated_reason` (`user_limit` means raising `limit` returns more; `safety_cap` means the graph is likely pathological and raising `limit` will not help). Pass `withPaths: true` when you need the call chain via specific intermediates — each caller then carries a `paths` array of shortest routes (issue #1536). / シンボルの推移的呼び出しチェーンを算出。scoped query が単一の class / struct / interface に解決されても symbol-level caller が無い場合は、代わりに heuristic な file-level dependency hint を返すことがあるため、`impact_mode`・`heuristic`・`file_impacts` を確認すること。`truncated` が真のときは `truncated_reason` を見て、`user_limit` なら `limit` を増やせば残りも取得可能、`safety_cap` ならグラフが病的で `limit` を増やしても解消しないことを区別すること。中間シンボル経由の経路が必要な場合は `withPaths: true` を渡すと、各 caller に経路配列 `paths` が付く（issue #1536）。",
                new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["query"] = new JsonObject { ["type"] = "string", ["description"] = "Symbol name to analyze impact for" },
                        ["maxDepth"] = new JsonObject { ["type"] = "integer", ["description"] = "Max BFS depth, inclusive (default: 5; maxDepth: N returns callers at depth 1..N, so a chain A→B→C→D queried against D with maxDepth: 2 yields C at depth 1 and B at depth 2; 0 resolves the symbol without traversing callers). Server-side cap: 50; requests above the cap are clamped and a `warnings` entry plus `max_depth_requested` field is added to the response.", ["default"] = 5, ["minimum"] = 0, ["maximum"] = 50 },
                        ["limit"] = new JsonObject { ["type"] = "integer", ["description"] = "Max total callers or heuristic file-level dependency hints to return (default: 50). Check `truncated` when the limit is reached; `truncated_reason` distinguishes `user_limit` (raise `limit` to get more) from `safety_cap` (pathological graph, raising `limit` will not help).", ["default"] = 50 },
                        ["lang"] = new JsonObject { ["type"] = "string", ["description"] = "Filter by language" },
                        ["path"] = new JsonObject { ["oneOf"] = new JsonArray { new JsonObject { ["type"] = "string" }, new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" } } }, ["description"] = "Prefer or restrict paths containing this text. Accepts a single string or an array; multiple values are OR'd together." },
                        ["excludePaths"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" }, ["description"] = "Exclude any paths containing these texts" },
                        ["excludeTests"] = new JsonObject { ["type"] = "boolean", ["description"] = "Exclude likely test files", ["default"] = false },
                        ["withPaths"] = new JsonObject { ["type"] = "boolean", ["description"] = "When true, each caller carries a `paths` array of shortest call chains [resolvedRoot, intermediate..., callerName]; diamond convergence surfaces every shortest route (per-row cap; `pathsTruncated` flag indicates overflow).", ["default"] = false }
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
                "Show file-level dependency edges from the indexed reference graph. / インデックス済み参照グラフからファイル間の依存エッジを返す。",
                new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["limit"] = new JsonObject { ["type"] = "integer", ["description"] = "Max edges (default: 50)", ["default"] = 50 },
                        ["lang"] = new JsonObject { ["type"] = "string", ["description"] = "Filter by language" },
                        ["path"] = new JsonObject { ["oneOf"] = new JsonArray { new JsonObject { ["type"] = "string" }, new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" } } }, ["description"] = "Restrict source files to glob-style path patterns. `*` and `?` are wildcards. Accepts a single string or an array; multiple values are OR'd together." },
                        ["excludePaths"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" }, ["description"] = "Exclude glob-style path patterns. `*` and `?` are wildcards." },
                        ["excludeTests"] = new JsonObject { ["type"] = "boolean", ["description"] = "Exclude test files", ["default"] = false },
                        ["reverse"] = new JsonObject { ["type"] = "boolean", ["description"] = "Reverse lookup: show files that depend ON the matched path", ["default"] = false }
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
                "Report encoding issues found during indexing: U+FFFD replacement chars, BOM markers, null bytes, mixed/CR-only line endings, UTF-16 BOM detection, likely non-UTF8 encodings. / インデックス時に検出したエンコーディング問題を報告。",
                new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["kind"] = new JsonObject { ["type"] = "string", ["description"] = "Filter by issue kind (replacement_char, bom, null_byte, mixed_line_endings, mixed_line_endings_three_way, cr_only_line_endings, utf16_bom, non_utf8_likely, line_too_long)" },
                        ["path"] = new JsonObject { ["oneOf"] = new JsonArray { new JsonObject { ["type"] = "string" }, new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" } } }, ["description"] = "Filter to paths containing this text. Accepts a single string or an array; multiple values are OR'd together." }
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
                "Execute multiple read-only queries in a single call and return all results. Dramatically reduces round-trips for AI agents. / 複数の読み取り専用クエリを1回の呼び出しで実行し、全結果を返す。AIエージェントの往復回数を劇的に削減。",
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
                "Index or re-index a project directory. Scans source files, extracts symbols, and builds FTS5 search index. / プロジェクトディレクトリをインデックス（再インデックス）。ソースファイルをスキャンし、シンボルを抽出してFTS5検索インデックスを構築。",
                new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["path"] = new JsonObject { ["type"] = "string", ["description"] = "Project directory path to index" },
                        ["rebuild"] = new JsonObject { ["type"] = "boolean", ["description"] = "Delete existing index and rebuild from scratch (default: false)", ["default"] = false }
                    },
                    ["required"] = new JsonArray { "path" }
                },
                IndexAnnotations()),
            CreateToolDefinition(
                "backfill_fold",
                "Upgrade folded-name keys in an existing CodeIndex DB without reparsing source files. Rejects missing or blank targets instead of creating a fresh DB. Fills missing `name_folded` columns (or rewrites all keys after fold metadata drift such as version/fingerprint mismatch) and stamps FoldReady on success. / ソース再解析なしで既存の CodeIndex DB の folded-name key を更新する。欠落したDBや空のDBを新規作成せず拒否し、欠損 `name_folded` 列を埋めるか、fold metadata の drift（version / fingerprint 不一致など）時は全 key を再生成し、成功時に FoldReady を stamp する。",
                new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject()
                },
                IndexAnnotations()),
            CreateToolDefinition(
                "symbol_hotspots",
                "Find the most-referenced symbols in the codebase (hotspot analysis). "
                + "Returns symbols ordered by reference count. Names that are unique within the active language/kind candidate set use codebase-wide totals; duplicate-name families fall back to conservative same-file counts, and same-file duplicate rows may be grouped when the DB cannot disambiguate targets. Cross-file grouping of duplicate families is trusted only on indexes stamped with the current authoritative hotspot-family version. Useful for identifying central, high-impact code. "
                + "/ コードベースで最も参照されるシンボルを検索する（ホットスポット分析）。"
                + "参照回数順にシンボルを返す。active な言語/種別候補集合で一意な名前は codebase 全体の件数を使い、同名ファミリーは保守的な same-file 件数へフォールバックし、DB が対象を曖昧なく結べない同一ファイル重複行は集約される。duplicate family の cross-file 集約は current の authoritative hotspot-family version で stamp された index でのみ信頼する。中心的で影響の大きいコードの特定に有用。",
                new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["kind"] = new JsonObject { ["type"] = "string", ["description"] = "Filter by symbol kind" },
                        ["lang"] = new JsonObject { ["type"] = "string", ["description"] = "Filter by language" },
                        ["limit"] = new JsonObject { ["type"] = "integer", ["description"] = "Max results (default: 20)", ["default"] = 20 },
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
                + "/ インデックス済みコードベースで定義されているが一度も参照されていないシンボルを検索する。"
                + "デッドコード検出に有用。private 候補を public/exported suspect より前に返し、最低信頼 bucket は config-bound な property または C# 風 attribute 隣接の reflection surface 用に使う。参照抽出対応言語でのみ意味がある。",
                new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["kind"] = new JsonObject { ["type"] = "string", ["description"] = "Filter by symbol kind (function, class, property, interface, enum, struct, event, delegate)" },
                        ["lang"] = new JsonObject { ["type"] = "string", ["description"] = "Filter by language (recommended: use a graph-supported language)" },
                        ["limit"] = new JsonObject { ["type"] = "integer", ["description"] = "Max results (default: 50)", ["default"] = 50 },
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
                + "/ cdidxへの構造化された改善提案またはエラー報告を送信する。"
                + "ギャップ（言語サポート不足、ランキング不良等）に気づいたとき、または予期せぬエラーに遭遇したときに呼び出す。"
                + "ソースコードを含めないこと — 自然言語でのみギャップを記述する。",
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
                        ["context"] = new JsonObject { ["type"] = "string", ["description"] = "What you were trying to do when you noticed the gap (NOT source code)" }
                    },
                    ["required"] = new JsonArray { "category", "description" }
                },
                SuggestionAnnotations())
        };

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
}
