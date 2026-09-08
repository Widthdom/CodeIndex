# Unused analysis performance

## English

Issue #5296 concerns computation cost, not output size. `--limit` and `--max-json-bytes` cannot bound the work required to decide whether a private member is referenced from a sibling partial declaration.

The candidate query now materializes partial-type shape and peer content once per SQL statement. `EXPLAIN QUERY PLAN` shows `MATERIALIZE unused_partial_types` and `MATERIALIZE unused_partial_contents`, indexed chunk selection by `file_id`, and automatic indexes for the identity joins. The previous plan put the peer window/`GROUP_CONCAT` and ancestor shape subqueries directly under each candidate's correlated exclusion. The existing #5089 chunk-order and overlap reconstruction remains intact. Exact-content lexical masks are reused only during one unused operation, with fixed entry and retained-character bounds. Saturation preserves the uncached algorithm.

The CLI has a default 30,000-ms analysis deadline after database open, configurable from 1 to 600,000 ms with `--analysis-timeout-ms`. SQLite interruption and cooperative lexical checks stop analysis. Deadline/cancellation results are non-authoritative and contain no unverified candidates. Database open is outside this analysis budget; cooperative cancellation and finishing valid JSON can add overhead beyond the deadline. Bounded JSON pages no longer request a hidden whole-result count; explicit `--count` and `--summary-only` retain full analysis under the same deadline. Progress is emitted once per second on interactive stderr or with `--progress`, independently of stdout capture.

### Regression budget

`QueryCommandRunnerUnusedIssue5296Tests.ManyPartialDeclarations_ReconstructOncePerStatementAndRemainReadOnly` seeds 16 sibling files with 192 private members. It removes reference edges so the protective sibling-content path must prove usage. The test requires at most 64 reconstructed pieces and at most 2,000 progress callbacks at 1,000 SQLite VM instructions per callback. It also requires empty unused-field results, unchanged `total_changes()` and `query_only`, and correct results after a new content generation on the same reader. These work-count gates, rather than tight shared-CI timing assertions, detect the old repeated reconstruction.

Run the focused fixture with detailed console logging to collect elapsed/CPU observations and SQL plans. Retain the #5089, #4834 and #3673 regressions, unused CLI/count/pagination and JSON-envelope tests in both `net8.0` and `net9.0` lanes. Native-SQL cancellation tests require interruption while SQLite is executing, parseable byte-bounded JSON, connection/token reuse, and a heartbeat before completion.

### Measurement procedure

Build Debug and Release from the same commit. Finish indexing and both workspace checks first. Run each configuration sequentially with no owned build, index, search or test jobs running. Observe other workloads and record contention rather than terminating unrelated processes. Measure wall time and child CPU time; terminate the old unbounded implementation after 60 seconds. Compare the same repository database and the same synthetic fixture, retaining SQL plans and operation counts.

The baseline at `f50223e2e24f11279aad1fa11169381c25263163` on macOS ARM64 / .NET 8 had a complete 1,502-file index. The requested repository command produced no stdout or stderr before the 60-second cutoff in either configuration (Debug CPU 62.19 s; Release CPU 58.16 s). Other checkout workloads were present during these initial observations, so these are liveness/CPU observations rather than isolated throughput claims. On the synthetic fixture, baseline Debug/Release reconstructed 1,632 pieces each (350,000 VM instructions upper bound); observed query elapsed times were 51/58 ms. The new 64-piece gate intentionally fails on that baseline.

The updated synthetic query reconstructed 16 pieces with an 84,000-instruction upper bound. Debug observed 26 ms elapsed / 25.66 ms CPU; Release observations across the supported test lanes were 7–14 ms. The deterministic improvement is 102-fold fewer reconstructed pieces and about 4.2-fold fewer VM instructions; timings are supporting observations, not CI pass criteria.

The updated repository command completed successfully in Debug at 18.42 s wall / 17.76 s CPU and Release at 11.88 s wall / 12.51 s CPU, within the default analysis budget and 12,000-byte output limit. These runs used the same baseline database for comparison; unrelated checkout activity prevents an isolated-machine claim.

## 日本語

Issue #5296 の対象は出力量ではなく計算コストです。`--limit` と `--max-json-bytes` では、private メンバーが sibling partial 宣言から参照されているかを判定する処理量を制限できません。

候補 SQL は partial 型の形状と peer 本文を statement ごとに一度だけ具体化します。`EXPLAIN QUERY PLAN` では `MATERIALIZE unused_partial_types` と `MATERIALIZE unused_partial_contents`、`file_id` による索引付き chunk 選択、同一性の結合用の自動索引を確認できます。修正前は候補ごとの相関除外条件の下で peer の window・`GROUP_CONCAT` と祖先の形状を繰り返し計算していました。#5089 の chunk 順序・重複の再構築規則は維持します。内容が完全一致する字句マスクの再利用は unused 処理一回に限り、entry 数と保持文字数に固定上限を設けています。上限到達後も同じアルゴリズムで計算します。

CLI の解析時間は DB を開いた後から既定で 30,000 ms で、`--analysis-timeout-ms` により 1〜600,000 ms に変更できます。SQLite 割り込みと字句処理の協調的なチェックで解析を停止します。時間上限到達・キャンセル時の結果は非確定で、未検証候補を含みません。DB の open は解析時間上限の対象外です。協調的キャンセルと有効な JSON 出力の完了には、deadline を超える追加時間がかかる場合があります。バイト上限付き JSON ページは暗黙の全件集計を追加実行しません。明示的な `--count`・`--summary-only` は同じ時間上限の下で全件を解析します。対話的 stderr または `--progress` 指定時は、stdout の取得とは独立して毎秒進行状況を表示します。

### 回帰検査の処理量上限

`QueryCommandRunnerUnusedIssue5296Tests.ManyPartialDeclarations_ReconstructOncePerStatementAndRemainReadOnly` は sibling ファイル 16 個に private メンバー 192 個を配置します。参照 edge を削除し、sibling 本文の保護経路で使用を証明させます。再構築する片は最大 64 個、SQLite の 1,000 VM 命令ごとの progress callback は最大 2,000 回です。未使用 field が空であること、`total_changes()` と `query_only` が変わらないこと、同じ reader で本文の新しい世代を正しく評価することも検証します。共有 CI の厳しい実時間条件ではなく、処理回数で以前の反復再構築を検出します。

対象 fixture を詳細なコンソールログで実行すると経過・CPU 時間と SQL plan を取得できます。#5089・#4834・#3673、unused の CLI・集計・ページング、JSON envelope の回帰テストを `net8.0` と `net9.0` の両方で維持してください。native SQL のキャンセルテストでは SQLite 実行中の割り込み、バイト上限内で解析可能な JSON、接続・token の再利用、完了前の heartbeat を検証します。

### 計測手順

同じコミットから Debug と Release をビルドし、先に索引更新と両方の workspace チェックを完了させます。自分が起動した build・index・search・test を同時実行せず、各構成を順に測定してください。他の負荷は記録し、無関係なプロセスを停止しません。実時間と子プロセスの CPU 時間を計測し、旧実装は 60 秒で打ち切ります。同一のリポジトリ DB と合成 fixture を使い、SQL plan と処理回数を保存します。

macOS ARM64／.NET 8 の `f50223e2e24f11279aad1fa11169381c25263163` では、1,502 ファイルの完全な索引で指定のコマンドを実行しました。Debug・Release とも 60 秒で打ち切るまで stdout・stderr は空でした（CPU 時間は Debug 62.19 秒、Release 58.16 秒）。この初回観測時には別 checkout の負荷もあったため、無応答・CPU 消費の観測であり、完全に隔離した throughput の比較ではありません。合成 fixture では修正前の両構成とも 1,632 片を再構築し、VM 命令数の上限は 350,000 でした。観測したクエリ時間は Debug 51 ms／Release 58 ms です。追加した 64 片の回帰検査はこの旧実装に対して意図どおり失敗します。

修正後の合成クエリは再構築 16 片、VM 命令数の上限 84,000 でした。Debug は経過 26 ms／CPU 25.66 ms、Release の対応テスト lane では 7〜14 ms を観測しました。再構築回数は 102 分の 1、VM 命令数は約 4.2 分の 1 です。実時間は補足的な観測値であり、CI の合否条件にはしません。

修正後の指定リポジトリコマンドは Debug で実時間 18.42 秒／CPU 17.76 秒、Release で実時間 11.88 秒／CPU 12.51 秒で正常終了し、既定の解析時間と 12,000-byte 出力上限内に収まりました。比較には同じ baseline DB を使用しました。別 checkout の負荷があるため、完全に隔離した環境の値とはみなしません。
