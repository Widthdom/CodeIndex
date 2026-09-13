# MCP status field explanations

## English

Use `status` with `explainField` to retrieve the same static field explanation as
CLI `status --explain`. The server does not open its database or read runtime values
for this mode. Existing `explain: "freshness"`, `"readiness"`, and `"all"` continue
to return aggregate runtime diagnostics.

```json
{"name":"status","arguments":{"explainField":"index_complete"}}
{"name":"status","arguments":{"explainField":"db_pragma_settings.busy_timeout_ms","format":"compact","maxBytes":8192}}
```

Field names come from the CLI `StatusResult` serializer metadata, including nested
members, with no separate MCP allowlist. Names are case-insensitive; surrounding
whitespace and CLI label aliases such as `Index generation completeness` are
accepted. Paths have at most 240 characters and four dot-separated segments.
Ignored properties are excluded. Unknown paths return sanitized errors and bounded
valid candidates. MCP-only runtime fields are not part of this CLI field registry.

Full `structuredContent` contains `field`, `meaning`, `source`, `dependencies`,
`interpretation`, `remediation`, and the other CLI explanation fields, plus the MCP
`api_version` / `tool` discriminators. `format: "compact"` or a positive `maxBytes`
returns one `results` entry containing the required `api_version`, `field`,
`meaning`, `interpretation`, and `remediation` fields. Its `metadata` reports
`explanation_schema`, `explanation_required_fields`,
`explanation_omitted_optional_fields`, and `explanation_omitted_optional_field_count`.
The same compact form is used if a full explanation exceeds the server's response
limit. Explanations contain no database paths, runtime values, timings, indexed
HEADs, or stable-at timestamps.

`maxBytes` bounds successful `structuredContent` UTF-8 bytes plus one newline;
server, transport, and JSON-RPC batch limits independently bound the complete
response. If the complete explanation cannot fit, the tool returns
`isError: true` with `E028_RESPONSE_BUDGET_TOO_SMALL`, measured
`minimum_required_bytes`, `minimum_required_bytes_known: true`, `budget_scope`,
and retry guidance. `minimum_structured_content_bytes` and `minimum_response_bytes`
distinguish the two byte scopes. Increase `maxBytes` for
`structured_content_with_newline` (`retry.action: "increase_max_bytes"`); increase
the server/transport response budget for `json_rpc_response`
(`retry.action: "increase_response_budget"`). When both limits are too small,
increase both. The terminal error may exceed the requested `maxBytes`; transport
limits still apply, so an exceptionally small transport limit may only permit the
transport's own terminal error.

`batch_query` preserves these structured error and retry fields on the failed slot.
For JSON-RPC batches, `budget_scope: "json_rpc_batch_item"` identifies a per-item
allocation, not a configurable whole-batch limit. Follow `retry.action: "split_batch"`
by sending the call individually outside the batch. Its response budget must be at
least `retry.minimum_response_budget_bytes`; if supplied, set `maxBytes` to at least
`retry.minimum_max_bytes`. These sizes describe the individual retry, not the batch.

Use `explainField` only with `format` and `maxBytes`. Combining it with `check`,
`scopes`, `staleAfterSeconds`, `explain`, `config`, `logPath`, `updateCheck`, or
`fields` is rejected, even if a supplied boolean is false. `maxBytes` on `status`
requires `explainField`. The input/output schemas and example are discoverable with
`tools/list` using `{"format":"full","names":"status"}`.

## 日本語

`status` の `explainField` は、CLI の `status --explain` と共通の静的なフィールド説明を
返します。このモードではサーバーの DB を開かず、実行時の値も読みません。
既存の `explain: "freshness"`、`"readiness"`、`"all"` は引き続き実行時診断をまとめて返します。

```json
{"name":"status","arguments":{"explainField":"index_complete"}}
{"name":"status","arguments":{"explainField":"db_pragma_settings.busy_timeout_ms","format":"compact","maxBytes":8192}}
```

フィールド名は CLI の `StatusResult` serializer metadata から取得し、ネストしたメンバーも
同じ仕組みで解決します。MCP 独自の許可リストはありません。大文字小文字を区別せず、
前後の空白と `Index generation completeness` などの CLI ラベル別名を受け入れます。
パスは最大240文字・ドット区切り4階層です。シリアライズ対象外のプロパティは除外し、
未知のパスにはサニタイズ済みエラーと上限付きの有効候補を返します。
MCP 専用の実行時フィールドは、この CLI フィールドレジストリには含まれません。

通常の `structuredContent` は `field`、`meaning`、`source`、`dependencies`、
`interpretation`、`remediation` などの CLI 説明フィールドと、MCP の識別用
`api_version` / `tool` を含みます。`format: "compact"` または正の `maxBytes` を指定すると、
必須の `api_version`、`field`、`meaning`、`interpretation`、`remediation` を持つ1件の
`results` を返します。`metadata` の `explanation_schema`、`explanation_required_fields`、
`explanation_omitted_optional_fields`、`explanation_omitted_optional_field_count` が形式と
省略内容を示します。通常形式がサーバーの応答上限を超える場合も同じ compact 形式を使います。
DB パス、実行時の値、処理時間、索引化時の HEAD、確定時刻は説明に付加しません。

`maxBytes` は成功時の `structuredContent` の UTF-8 バイト数と改行1文字の合計を制限します。
サーバー・トランスポート・JSON-RPC バッチの上限は、それとは別に応答全体を制限します。
説明全体が収まらない場合は `isError: true` と `E028_RESPONSE_BUDGET_TOO_SMALL`、
実測した `minimum_required_bytes`、`minimum_required_bytes_known: true`、
`budget_scope`、再試行案内を返します。`minimum_structured_content_bytes` と
`minimum_response_bytes` は2種類の計測範囲を区別します。
`structured_content_with_newline` なら `maxBytes` を増やし
（`retry.action: "increase_max_bytes"`）、`json_rpc_response` ならサーバー／トランスポートの
応答上限を増やしてください（`retry.action: "increase_response_budget"`）。
両方が不足する場合は両方を増やします。終端エラーは指定された `maxBytes` を超える場合が
ありますが、トランスポートの上限には従います。極端に小さいトランスポート上限では、
トランスポート自身の終端エラーだけが返る場合があります。

`batch_query` は、失敗したスロットにもこれらの構造化エラーと再試行フィールドを保持します。
JSON-RPC バッチの `budget_scope: "json_rpc_batch_item"` は各応答への割当量を示し、
設定可能なバッチ全体の上限を意味しません。`retry.action: "split_batch"` の場合は、
バッチから分離して個別に呼び出してください。その応答上限は
`retry.minimum_response_budget_bytes` 以上、`maxBytes` を指定する場合は
`retry.minimum_max_bytes` 以上にします。このサイズは個別の再試行を対象とし、
バッチ全体のサイズを表しません。

`explainField` と組み合わせられるのは `format` と `maxBytes` です。`check`、`scopes`、
`staleAfterSeconds`、`explain`、`config`、`logPath`、`updateCheck`、`fields` との併用は、
指定した真偽値が false でも拒否します。`status` で `maxBytes` を使うには `explainField` が
必要です。`tools/list` に `{"format":"full","names":"status"}` を渡すと入力・出力スキーマと
使用例を確認できます。
