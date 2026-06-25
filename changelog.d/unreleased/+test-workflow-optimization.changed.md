---
category: changed
affected:
  - .github/workflows/dotnet.yml
  - tests/CodeIndex.Tests/CiWorkflowTests.cs
  - tests/CodeIndex.Tests/QueryCommandRunnerTests.cs
  - tests/CodeIndex.Tests/IndexCommandRunnerTests.cs
  - tests/CodeIndex.Tests/ConcurrencyTests.cs
  - tests/CodeIndex.Tests/BackgroundTaskObserverTests.cs
  - tests/CodeIndex.Tests/PostExtractionHookTests.cs
  - tests/CodeIndex.Tests/TrimmedCliTestHelper.cs
  - tests/CodeIndex.Tests/ReleaseWorkflowTests.cs
  - .github/workflows/mutation-testing.yml
  - DEVELOPER_GUIDE.md
  - TESTING_GUIDE.md
---

## English
- Tightened the CI NuGet cache key to avoid package-cache misses from test-only project file edits while keeping locked restore as the package-input guard.
- Reused one trimmed publish output while checking C# Razor query aliases, eliminating a duplicate `dotnet publish` run from that test.
- Folded the trimmed query JSON and Razor-alias smoke coverage into one publish-backed test, removing another redundant `dotnet publish` run from the suite.
- Removed the CI test command's duplicate results-directory argument so `CodeIndex.Tests.runsettings` remains the single owner of the `TestResults` path.
- Stopped writing CI test stdout logs to disk for clean first-pass success lanes while preserving console streaming and failed-run log artifacts.
- Added an OS-scoped NuGet cache restore key so lock-file changes can still reuse compatible package cache contents.
- Removed the redundant explicit `--no-restore` from the CI `dotnet test` command because `--no-build` already suppresses restore.
- Replaced fixed two-second waits in concurrent snapshot stress tests with iteration-based completion and the same two-second slow-host cap.
- Removed post-cancellation fixed sleeps from background-task observer tests by relying on the observer's fault-only continuation contract.
- Replaced shared-writer blocking test grace sleeps with explicit task-start signals.
- Shared the normal trimmed published CLI across published CLI smoke tests so each test process pays that publish cost once; single-file publish coverage remains isolated.
- Moved CI stdout-log directory creation onto the failed-test path so clean first-pass lanes do not pre-create `TestResults` for that log file.
- Ran the CI TRX telemetry summarizer with Release `--no-build` output so failed/pass-on-retry lanes do not rebuild the helper during diagnostics.
- Cached the pinned Stryker global tool and NuGet packages in the weekly mutation workflow, and update/install the tool only on cache misses.
- Moved published CLI subprocess execution into `TrimmedCliTestHelper`, removing duplicated process-launch helpers from index/query tests.
- Shortened post-extraction hook timeout/cancellation leak checks by lowering the artificial hook delay and using a smaller observation window that still catches un-killed workers.

## 日本語
- CI の NuGet キャッシュキーを調整し、テスト用 project file だけの変更で package cache が失効しないようにしました。package 入力の検証は locked restore に任せます。
- C# Razor query alias の検証で 1 回の trimmed publish output を再利用し、そのテストから重複する `dotnet publish` 実行を削減しました。
- trimmed query JSON と Razor alias の smoke coverage を 1 つの publish-backed test に統合し、suite からさらに重複する `dotnet publish` 実行を削減しました。
- CI test command の重複 results-directory 引数を削除し、`CodeIndex.Tests.runsettings` を `TestResults` path の単一管理元にしました。
- clean な初回成功 lane では CI test stdout log を disk に書かないようにし、console streaming と失敗時の log artifact は維持しました。
- OS 単位の NuGet cache restore key を追加し、lock file 変更時にも互換性のある package cache 内容を再利用できるようにしました。
- `--no-build` が restore を抑止するため、CI の `dotnet test` command から重複する明示的な `--no-restore` を削除しました。
- concurrent snapshot stress test の固定 2 秒待機を、反復数ベースの完了判定と従来同等の 2 秒 slow-host 上限へ置き換えました。
- background-task observer test から cancellation 後の固定 sleep を削除し、observer の fault-only continuation 契約に基づく検証へ寄せました。
- shared-writer blocking test の grace sleep を、明示的な task-start signal に置き換えました。
- published CLI smoke test 間で通常の trimmed publish output を共有し、test process あたり 1 回の publish cost に抑えました。single-file publish coverage は引き続き隔離します。
- CI stdout log 用 directory の作成を failed-test path に移し、clean な初回成功 lane ではその log file のために `TestResults` を事前作成しないようにしました。
- CI の TRX telemetry summarizer を Release `--no-build` output で実行し、失敗または retry 成功 lane の診断中に helper を再ビルドしないようにしました。
- weekly mutation workflow で pinned Stryker global tool と NuGet package を cache し、cache miss のときだけ tool を update / install するようにしました。
- published CLI subprocess execution を `TrimmedCliTestHelper` に移し、index/query test の重複 process-launch helper を削除しました。
- post-extraction hook の timeout / cancellation leak check で人工 hook delay と観測 window を短縮し、kill されなかった worker は引き続き検出しつつ固定待機を減らしました。
