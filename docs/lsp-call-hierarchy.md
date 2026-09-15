# Indexed LSP call hierarchy

## English

`cdidx lsp --db .cdidx/codeindex.db` advertises `callHierarchyProvider` and supports
`textDocument/prepareCallHierarchy`, `callHierarchy/incomingCalls`, and
`callHierarchy/outgoingCalls`. Prepare on a callable declaration or uniquely
resolved indexed reference, then send the returned item unchanged to expand it.
Recursive calls remain traversable. Repeated call sites are grouped by exact
endpoint identity and retained in `fromRanges`.

The initial language scope is C#, Java, JavaScript, TypeScript, Python, Go, Rust,
C, C++, Kotlin, Ruby, PHP and Swift. Only indexed `function`, `test.method` and
`lambda` symbols with usable ranges and current reference identities qualify.
Edges use the shared CLI/MCP persisted `call` references; this does not add language
binding, implicit constructor calls, subscriptions or member reads. Ambiguous
overloads and same-name symbols are never combined by name. Preparation uses the
existing LSP position and invocation-selection rules without a bare-name fallback.

This is heuristic indexed evidence. External calls, dynamic dispatch and unindexed
code can be absent even when the stored graph is complete. Each successful request
sends a standard `window/logMessage` reminder, including for an empty result;
item `detail` also labels the evidence as indexed. An empty expansion means no
eligible indexed calls were found, not that the callable has no possible callers
or callees. Non-callable declarations and positions without a token return `null`
from preparation.

Incomplete/unavailable graphs, unresolved or ambiguous contributing references,
unusable source ranges and exceeded budgets return LSP `RequestFailed` (`-32803`).
The server returns no successful partial hierarchy. Changed files, unsaved open
documents and stale items return `ContentModified` (`-32801`). Error `data.reason`
contains a bounded machine reason. Save documents, refresh the index, and prepare
again. If the live-document cache has discarded any text, save and reconnect too.
The opaque `data` string (at most 160 characters) binds the item to the server
session, indexed generation and exact symbol ID; never reuse it across sessions
or indexing runs. File URIs and all positions follow the existing LSP path policy
and zero-based UTF-16 coordinates.

Each request allows at most 1,000 raw call sites, 100 distinct neighbors, 512 KiB
of response JSON, 4 MiB per source file and a 16 MiB source set read twice, with a five-second
query deadline and SQLite cancellation. `$/cancelRequest` returns `-32800` and
leaves the session usable. Call hierarchy does not stream partial results or
advertise work-done progress. Use CLI/MCP graph diagnostics when a hierarchy
exceeds these bounds. All retained open buffers must match disk and the index;
participating source files are checksum checked before and after range assembly.
This does not perform a whole-workspace filesystem freshness scan per request.

## 日本語

`cdidx lsp --db .cdidx/codeindex.db` は `callHierarchyProvider` を通知し、
`textDocument/prepareCallHierarchy`、`callHierarchy/incomingCalls`、
`callHierarchy/outgoingCalls` に対応します。呼び出し可能な宣言、または索引内で
一意に解決できる参照位置で準備し、返された項目を変更せずに渡して展開します。
再帰呼び出しも辿れます。同じ相手への複数の呼び出し箇所は、正確なシンボルIDで
まとめ、すべての位置を `fromRanges` に保持します。

初期の対象言語は C#、Java、JavaScript、TypeScript、Python、Go、Rust、C、C++、
Kotlin、Ruby、PHP、Swift です。有効な範囲と最新の参照識別情報を持つ、索引内の
`function`、`test.method`、`lambda` シンボルが対象です。CLI/MCP と共通の永続化済み
`call` 参照を使い、言語の束縛解析、暗黙のコンストラクター呼び出し、購読、
メンバー読み取りは追加しません。曖昧なオーバーロードや同名シンボルを名前だけで
統合しません。準備処理は既存LSPの位置・引数に基づく選択規則を利用し、単純な
名前検索へのフォールバックは行いません。

結果は索引に保存されたヒューリスティックな証拠です。保存済みグラフが完全でも、
外部呼び出し、動的ディスパッチ、索引対象外のコードは含まれないことがあります。
成功した要求では空の結果も含めて標準の `window/logMessage` でこの制限を通知し、
項目の `detail` にも索引由来であることを表示します。空の展開結果は該当する索引内の
呼び出しが見つからなかったことを意味し、呼び出し元・先が存在しないことの証明には
なりません。呼び出せない宣言やトークンのない位置での準備は `null` を返します。

グラフが未完成・利用不能、関連する参照が未解決・曖昧、ソース範囲が利用不能、
または上限超過の場合は LSP `RequestFailed`（`-32803`）を返します。部分的な階層を
成功として返しません。ファイル変更、開いている文書の未保存編集、古い項目には
`ContentModified`（`-32801`）を返します。エラーの `data.reason` に上限付きの
機械判読用理由を含めます。保存・索引更新の後、準備からやり直してください。
文書キャッシュがテキストを破棄した場合は、保存後に再接続も必要です。
最大160文字の不透明な `data` はセッション・索引世代・正確なシンボルIDに結び付きます。
再接続や索引更新をまたいで再利用しないでください。ファイルURIは既存のLSPパス方針、
位置は0始まりのUTF-16座標に従います。

要求ごとの上限は生の呼び出し箇所1,000件、相手シンボル100件、応答JSON 512 KiB、
ソース1ファイル4 MiB、2回読み取るソース集合の合計16 MiBです。問い合わせの期限は5秒で、
SQLiteの処理にもキャンセルを適用します。`$/cancelRequest` は `-32800` を返し、
セッションは継続利用できます。コール階層の部分結果配信・進捗通知は提供しません。
上限に達した場合はCLI/MCPのグラフ診断を利用してください。保持中の全バッファは
ディスクと索引に一致する必要があり、結果に関わるソースのチェックサムを範囲の構築前後に
検証します。要求ごとのワークスペース全体のファイル鮮度走査は行いません。
