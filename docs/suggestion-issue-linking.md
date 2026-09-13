# Linking existing issues to suggestions

## English

After publishing a finding manually, associate it with its existing GitHub issue:

```sh
cdidx suggestions link <id> --repo Widthdom/CodeIndex --issue 5350 --actor maintainer --db .cdidx/codeindex.db --json
cdidx suggestions link <another-id> --repo Widthdom/CodeIndex --issue https://github.com/Widthdom/CodeIndex/issues/5350 --db .cdidx/codeindex.db --json
```

Each command atomically links one record. Repeat it for findings consolidated into
one issue. A full ID or unambiguous prefix is accepted. `--actor` defaults to
`cdidx-cli`; `--reason` supplies optional audit context. Both are redacted and
bounded before persistence (256 and 2,048 characters respectively).

The operation is local and works offline without GitHub credentials. It never
creates an issue or verifies one remotely. It accepts an ASCII `owner/name`
repository and an integer from 1 through 2,147,483,647, or the matching canonical
`https://github.com/owner/name/issues/number` URL. Repository casing is normalized.
Other hosts, PR URLs, credentials, ports, escaped/traversal paths, leading-zero
numbers, query strings, fragments and trailing slashes are rejected. Existence,
issue-versus-PR identity (including for numeric input), and current open/closed
state remain unverified; check those independently before linking. GitHub
Enterprise hosts and remote verification are not supported by this operation.

| Evidence | Meaning after a new link |
|---|---|
| `upstream_url`, `upstream_issue_number` | The operator-supplied existing issue identity. |
| `upstream_association` | Canonical `repository`, UTC `linked_at`, `linked_by`, optional `reason`, `provenance: manual_external`, and `verification: not_performed`. This audit survives later status transitions. |
| `status` | A draft becomes `submitted_pending_triage`: published according to the operator, awaiting triage. Other states are preserved; linking never marks implementation complete. |
| `submitted_to_github`, `--status submitted` | Compatibility indicators of upstream publication, including external publication. They do not prove a cdidx submission attempt. |
| Submission/sync/resolution fields | Actual attempt count, last attempt/error, `last_synced_at` and `resolved_at` are preserved. No attempt, sync or resolution is invented; a scheduled retry is cleared. |

Repeating the same complete identity succeeds without rewriting history, changing
the original audit timestamp, or relabelling a historical cdidx submission as
external. Different or incomplete existing identities produce
`upstream_association_conflict`; reassociation is not supported. Submission
reservations and stale revisions are checked under the existing store lock.
Failed atomic publication leaves the previous store intact. The completed staged
JSON must fit the 8 MiB store read limit; oversized results fail with a storage
error before replacement, preserving the readable original history.

Full list/show/JSON exports include the association; human show and Markdown
export also display its provenance. Compact lists retain the lifecycle status;
use `show --json` for the full identity and audit. `--status unsubmitted` excludes
linked records. Issue-draft exports retain the records, expose the identity in
`source`, set `duplicate_preflight.already_published: true` with the upstream
identity, and explicitly say not to file again. This local evidence does not
turn an unchecked remote duplicate preflight into a checked one. Subsequent
duplicate submissions through CLI/MCP store callers return the existing issue
without invoking the issue-creation callback, even for multiple suggestions
sharing one issue. Existing retention limits still apply to local history.

Older stores remain readable. Linking touches only the requested suggestion's
association; context text is never scraped to infer publication. Use the separate
`suggestions update <id> --status open_in_upstream|resolved_in_upstream` operation
after independently checking the upstream state.

## 日本語

手動で公開した指摘は、既存の GitHub Issue に明示的に関連付けます。

```sh
cdidx suggestions link <id> --repo Widthdom/CodeIndex --issue 5350 --actor maintainer --db .cdidx/codeindex.db --json
cdidx suggestions link <another-id> --repo Widthdom/CodeIndex --issue https://github.com/Widthdom/CodeIndex/issues/5350 --db .cdidx/codeindex.db --json
```

1回のコマンドで1件を原子的に関連付けます。複数の指摘を1つの Issue に統合した場合は、
各 ID に対して繰り返してください。完全な ID または一意な接頭辞を指定できます。
`--actor` の既定値は `cdidx-cli`、`--reason` は任意の監査理由です。
両者は保存前に伏字化され、それぞれ256文字、2,048文字に制限されます。

この操作はローカルで完結し、GitHub の認証情報がないオフライン環境でも使えます。
Issue の新規作成やリモート検証は行いません。ASCII の `owner/name` と
1〜2,147,483,647の整数、または同じリポジトリの標準形式
`https://github.com/owner/name/issues/number` URL を受け付けます。
リポジトリの大文字・小文字は正規化します。他のホスト、PR URL、認証情報、ポート、
エスケープや親ディレクトリ参照を含むパス、先頭が0の番号、クエリ文字列、フラグメント、
末尾のスラッシュは拒否します。存在確認、Issue と PR の区別（番号指定の場合も含む）、
現在のオープン・クローズ状態は未検証です。関連付け前に別途確認してください。
この操作は GitHub Enterprise のホストとリモート検証には対応していません。

| 証跡 | 新規関連付け後の意味 |
|---|---|
| `upstream_url`、`upstream_issue_number` | 操作者が指定した既存 Issue の識別情報。 |
| `upstream_association` | 正規化した `repository`、UTC の `linked_at`、`linked_by`、任意の `reason`、`provenance: manual_external`、`verification: not_performed`。後の状態変更でもこの監査証跡を保持します。 |
| `status` | 下書きは `submitted_pending_triage` になります。操作者が公開済みと申告し、トリアージを待つ状態です。他の状態は維持し、関連付けによって実装完了にはしません。 |
| `submitted_to_github`、`--status submitted` | 外部での公開を含む互換用の公開済み表示です。cdidx による投稿試行を証明しません。 |
| 投稿・同期・解決フィールド | 実際の試行回数、最終試行・エラー、`last_synced_at`、`resolved_at` を維持します。試行・同期・解決を捏造せず、再試行予約だけを解除します。 |

同じ完全な識別情報で再実行した場合は成功し、履歴や最初の監査日時を更新しません。
過去の cdidx 投稿を外部投稿として記録し直すこともありません。異なる、または不完全な
既存識別情報には `upstream_association_conflict` を返します。付け替えには対応しません。
既存のストアロック内で投稿中の予約と古い revision を検査します。
原子的な公開処理が失敗した場合、以前のストアを保持します。完成した一時 JSON は
ストアの読み取り上限8 MiBに収まる必要があります。超過時は置換前にストレージエラーとし、
読み取り可能な元の履歴を維持します。

通常の一覧・詳細・JSON エクスポートには関連付け情報が含まれ、人間向け詳細表示と
Markdown エクスポートにも出所を表示します。簡略一覧は状態を保持します。
完全な識別情報と監査証跡には `show --json` を使ってください。
`--status unsubmitted` は関連付け済みレコードを除外します。Issue draft のエクスポートは
レコードを保持し、`source` に識別情報を含め、`duplicate_preflight.already_published: true`
と upstream 識別情報を出力して、再起票しないよう明記します。このローカル証跡によって
未実行のリモート重複確認を確認済みにはしません。CLI/MCP のストア利用側が同じ提案を
再送しても、Issue 作成コールバックを呼ばず既存 Issue を返します。複数の提案を
同じ Issue に関連付けた場合も同様です。ローカル履歴の既存の保持制限は適用されます。

旧ストアも引き続き読み取れます。関連付け対象は指定した提案だけで、文脈テキストから
公開を推測して変更することはありません。upstream の状態を別途確認した後に、
独立した `suggestions update <id> --status open_in_upstream|resolved_in_upstream`
操作で状態を変更してください。
