# C# incremental update scope (#5347)

## English

`index --files`, `--commits`, and `--changed-between` may need to refresh untouched
C# consumers when static-interface contracts or qualified member-read targets
change. The update result now includes `csharp_workspace_expansion`. Successful
runs persist the same object as `status.last_index_run.csharp_workspace_expansion`,
also available through MCP status. It is diagnostic evidence, not a readiness flag.

| Field | Meaning |
| --- | --- |
| `trigger` | Why the C# preflight considered workspace work: persisted/source static-interface contracts, member-reference targets, incomplete contract evidence, ordinary C# targets, or no C# targets. Configuration/partial-index full-scan fallbacks identify their trigger too. |
| `decision`, `reason` | `expanded`, `narrowed`, `not_expanded`, `deferred`, or `full_scan`, with a stable reason code. |
| `original_target_count` | Resolved update paths, including Git reconciliation and deletion paths, before C# expansion. |
| `expanded_target_count` | Peak planned target count, including the original set, before any safe narrowing. |
| `final_target_count` | Paths handed to the update loop after preflight guards and narrowing. In a full-scan fallback this is the discovered file count. |
| `initial_prepass_ms` | Time for the initial C# target/evidence preflight. |
| `workspace_scan_ms`, `workspace_prepass_ms` | Time for expanded discovery and the authoritative C# source prepass. Zero means that phase did not run or rounded below one millisecond. Full-scan fallbacks do not run these scoped-update phases. |
| `workspace_prepass_file_count`, `workspace_prepass_input_bytes` | Number of C# prepass inputs and the sum of their captured file lengths. These are input-volume evidence, not measured physical I/O; generated-suppressed and over-limit files can be included. |

Counts describe paths, not unique filesystem identities or successfully rewritten
rows. Use the result summary and `last_index_run.files_scanned`, `rows_upserted`,
`rows_deleted`, `bytes_read`, and `duration_ms` for completed update work.
`bytes_read` retains its existing file-loop meaning and does not include prepass
rereads. All three phase times exclude the file-update loop. Human update output
shows the decision and original → expanded → final counts. Full-scan fallback
summaries honor `--quiet`.

The narrow path still scans and validates the whole C# workspace. It reduces
main-pass re-extraction only after a successful expanded update has established a
matching source-input fingerprint. The fingerprint includes every C# path,
generated suppression, complete contents of sources admitted by the existing
`static`/`enum`/`const` prepass gate and their contribution order, complete C# project-marker evidence,
observed configuration, extraction limits,
symlink policy, project root, and the exact binary build. For example, changing a
method body in an ordinary instance-only class can leave these contract inputs
unchanged. Changing a source containing `static`, even just a comment in that
source, remains conservative. This is not a general semantic comparison of C#.

Project-marker evidence comes from the same expanded scan that establishes family
scopes and the validated input snapshot. Marker changes or discovery-budget
exhaustion between the initial and expanded scans retain full expansion.

The proof is optional, limited to 50,000 C# paths, and bound to the last successful
run. Legacy/missing evidence (`baseline_unavailable`), changed inputs or binary
(`contract_inputs_changed`), incomplete or incompatible prior readiness, active
symbol filters, hooks, custom extractors/patterns, incomplete project-marker discovery,
prepass timeouts, and retained
out-of-scan symbols retain conservative work. Full scans and other successful
indexing paths clear the proof; no rebuild or migration is required to use existing
databases. Updates without an authoritative workspace prepass also clear it. Rename/deletion and configuration
transitions keep existing cleanup and full-scan fallback behavior.

The expanded source/configuration snapshots still guard writes and final
readiness after narrowing. Detected drift defers C# mutation or marks the run
partial; cancellation does not advance successful-run provenance. Failed attempts
can expose immediate diagnostics while `last_index_run` continues to describe the
previous successful run. Readiness/completeness and HEAD verification retain their
existing independent meanings. Persisted diagnostics use the standard bounded
status metadata reader and reject malformed codes, negative costs, and inconsistent
counts without trusting them.

### Measured example

On macOS arm64, Debug/net8.0, parallelism 4, an isolated Git repository contained
99 C# files: 96 instance-only worker classes with 32 methods each, one static-interface
contract, its implementation, and a constant holder, plus one Markdown file.
After a full index and a successful Git-scoped refresh established a verified old
HEAD and source proof, another instance method changed. A SQLite backup of that
same old-ref database, with only the optimization proof removed, provided the
conservative control. Both ran `index . --changed-between <old> <new> --json`
with an explicit, private `--db`; the control was not an already-current database.

| Case | Updated-loop files | Loop bytes | Persisted duration (ms) | Process wall (ms) | C# scan / prepass (ms) |
| --- | ---: | ---: | ---: | ---: | ---: |
| Independent instance edit, narrowed | 1 | 1,777 | 1,850 | 1,987 | 9 / 222 |
| Same edit, conservative control | 99 | 170,819 | 2,720 | 2,856 | 8 / 244 |
| Shared constant target renamed | 99 | 170,821 | 3,478 | 3,612 | 8 / 235 |

All cases retained fresh, complete source/graph status and verified HEADs. The
independent result matched its conservative control; the shared-target result
matched a full rebuild of the same source. Prepass input volume remained about
171 KB in all cases. These single-run fixture measurements do not establish a
general speedup.

### Historical workload reproduction

A separate detached checkout reproduced the original workload using fresh Debug/net8.0
builds at each historical ref. Its private root database was first fully indexed and
verified at `b84d28d20670cf41de7f310758953c9ea43efd51`, including root and manifest checks.
After switching to `36f2e68cb0d7f17bc9591d01ad3045abe870da9b` and rebuilding, this command ran:

```sh
dotnet ./src/CodeIndex/bin/Debug/net8.0/cdidx.dll index . --changed-between b84d28d20670cf41de7f310758953c9ea43efd51 36f2e68cb0d7f17bc9591d01ad3045abe870da9b --db .cdidx/codeindex.db --json
```

The 24 Git paths expanded to 1,424 scanned paths, with 1,410 updated, 14 removed,
39,061,997 loop bytes, and 154,465 ms persisted duration (154,471 ms immediate elapsed).
Counts and bytes matched the issue's audit. Both freshness checks passed afterward,
with complete source/graph data and the new verified HEAD. This is a single-run
baseline reproduction on macOS arm64; the fixture above measures the narrowing behavior.

## 日本語

`index --files`、`--commits`、`--changed-between` は、static interface 契約や
修飾付きメンバー読み取りの参照先が変わると、未変更の C# 利用側も更新する必要があります。
更新結果の `csharp_workspace_expansion` にその判断を出力します。成功した実行では同じ
オブジェクトを `status.last_index_run.csharp_workspace_expansion` に保存し、MCP の
status からも参照できます。これは診断情報であり、準備状態のフラグではありません。

| フィールド | 意味 |
| --- | --- |
| `trigger` | 永続化済み／ソース上の static interface 契約、メンバー参照先、不完全な契約証拠、通常の C# 対象、C# 対象なしのいずれか。設定変更や以前の部分実行による全走査への切り替えも識別します。 |
| `decision`、`reason` | `expanded`（展開）、`narrowed`（絞り込み）、`not_expanded`（展開なし）、`deferred`（延期）、`full_scan`（全走査）と、機械処理用の理由コード。 |
| `original_target_count` | Git の照合追加分や削除パスを含む、C# 展開前の確定対象数。 |
| `expanded_target_count` | 元の集合を含む、安全な絞り込みを行う前の最大計画対象数。 |
| `final_target_count` | 前処理の検証と絞り込み後、更新ループへ渡すパス数。全走査への切り替えでは検出ファイル数。 |
| `initial_prepass_ms` | 最初の C# 対象・証拠確認にかかった時間。 |
| `workspace_scan_ms`、`workspace_prepass_ms` | 展開時の全体検出と、C# ソース前処理にかかった時間。0 は未実行または 1 ミリ秒未満です。全走査への切り替えでは、この部分更新用の各工程は実行しません。 |
| `workspace_prepass_file_count`、`workspace_prepass_input_bytes` | C# 前処理の入力数と、取得したファイル長の合計。実際の物理 I/O 計測値ではなく、生成コードの抽出抑制対象やサイズ上限超過ファイルも含み得ます。 |

対象数はパス数であり、ファイル実体の一意数や書き換え成功行数ではありません。完了した
更新処理量は結果の summary と、`last_index_run` の `files_scanned`、`rows_upserted`、
`rows_deleted`、`bytes_read`、`duration_ms` で確認します。`bytes_read` は従来どおり
ファイル更新ループの値で、前処理の再読込は含みません。3種類の工程時間も更新ループを
含みません。人間向けの更新出力には判断と、元の対象数 → 展開後 → 最終対象数を表示します。
全走査への切り替え時の summary は `--quiet` に従います。

絞り込む場合も C# 全体の走査と検証を続けます。正常な全 C# 更新で作成したソース入力の
指紋と一致した場合だけ、本処理で再抽出する対象を減らします。指紋には全 C# パス、
生成コードの抽出抑制、既存の `static`／`enum`／`const` 候補判定を通過するソースの全文と順序、
完全な C# プロジェクトマーカーの証拠、観測した設定、抽出上限、シンボリックリンク方針、
プロジェクトルート、実行バイナリの
ビルドを含めます。通常のインスタンス専用クラスのメソッド本体の変更は、これらの入力を
変えない場合があります。一方、`static` を含むソースの変更は、そのファイルのコメント
編集だけでも保守的に処理します。C# 全般の意味的な同値性を判定する機能ではありません。

プロジェクトマーカーの証拠は、family scope と検証対象の入力スナップショットを確立する
同一の展開走査から取得します。初期走査と展開走査の間にマーカーが変わるか、探索上限に
達した場合も、全体への展開を維持します。

この証拠は最適化専用で、C# パス数の上限は 50,000、対象世代は直近の成功実行です。
古い DB などで証拠がない場合（`baseline_unavailable`）、入力やバイナリが変わった場合
（`contract_inputs_changed`）、以前の準備状態が不完全・非互換の場合、シンボルフィルター、
フック、独自の抽出器・パターン、プロジェクトマーカー探索の不完全性、前処理タイムアウト、走査対象外に保持したシンボルが
ある場合は保守的に処理します。全走査など別経路での正常な索引更新と、全体のソース前処理をしない更新は
証拠を消去します。既存 DB の利用に再構築や移行は不要です。rename・削除時の後始末と、
設定変更時に全走査へ切り替える動作を維持します。

絞り込み後も、全体のソース・設定スナップショットで書き込み前と最終準備状態を検証します。
変化を検出した場合は C# 更新を延期するか部分実行として扱い、キャンセル時に成功実行の
来歴を進めません。失敗直後の結果に今回の診断を出しても、`last_index_run` は前回の
成功実行を示し続けます。準備状態・完全性と HEAD 検証は従来どおり独立した意味を保ちます。
保存済み診断は既存の上限付き status メタデータ読み取りを使い、不正なコード、負のコスト、
矛盾した対象数を拒否します。

### 実測例

macOS arm64、Debug/net8.0、並列度4で、分離した Git リポジトリを測定しました。
C# は99ファイル（32メソッドずつのインスタンス専用クラス96個、static interface 契約、
実装型、定数保持型）で、別に Markdown が1ファイルあります。全量索引と正常な Git 範囲更新で
旧 HEAD の鮮度とソース証拠を確立してから、別のインスタンスメソッドを編集しました。
同じ旧参照の DB を SQLite backup で複製し、最適化用の証拠だけを除去して保守的な対照実行に
使いました。両方とも専用の `--db` を明示し、`index . --changed-between <old> <new> --json`
を実行しています。対照側を更新済み DB で代用していません。

| ケース | 更新ループのファイル数 | ループの bytes | 保存済み時間 (ms) | プロセス実時間 (ms) | C# 走査／前処理 (ms) |
| --- | ---: | ---: | ---: | ---: | ---: |
| 独立したインスタンス編集・絞り込みあり | 1 | 1,777 | 1,850 | 1,987 | 9 / 222 |
| 同じ編集・保守的な対照実行 | 99 | 170,819 | 2,720 | 2,856 | 8 / 244 |
| 共有定数の参照先名を変更 | 99 | 170,821 | 3,478 | 3,612 | 8 / 235 |

全ケースでソース・グラフの鮮度と完全性、検証済み HEAD を維持しました。独立した編集の
結果は保守的な対照実行と一致し、共有参照先の変更結果は同じソースの全量再構築と一致しました。
前処理の入力は全ケースで約171 KBのままです。このフィクスチャの各1回の実測は、一般的な
高速化率を保証するものではありません。

### 過去の workload の再現

別の detached checkout で、各参照の Debug/net8.0 バイナリを新しくビルドして再現しました。
専用のルート DB を `b84d28d20670cf41de7f310758953c9ea43efd51` で全量索引し、ルートと
manifest の鮮度を確認してから、`36f2e68cb0d7f17bc9591d01ad3045abe870da9b` へ切り替え、
再ビルドして次を実行しました。

```sh
dotnet ./src/CodeIndex/bin/Debug/net8.0/cdidx.dll index . --changed-between b84d28d20670cf41de7f310758953c9ea43efd51 36f2e68cb0d7f17bc9591d01ad3045abe870da9b --db .cdidx/codeindex.db --json
```

Git の24パスが1,424パスへ展開され、1,410更新、14削除、ループ内39,061,997バイト、
保存済み時間154,465 ms（直後の elapsed は154,471 ms）でした。件数とバイト数は Issue の
監査記録と一致しました。更新後の両方の鮮度確認も通過し、ソース・グラフの完全性と新しい
検証済み HEAD を確認しました。これは macOS arm64 での各1回のベースライン再現です。
絞り込みの動作は上記フィクスチャで測定しています。
