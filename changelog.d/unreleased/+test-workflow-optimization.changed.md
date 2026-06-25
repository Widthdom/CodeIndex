---
category: changed
affected:
  - .github/workflows/dotnet.yml
  - tests/CodeIndex.Tests/CiWorkflowTests.cs
  - tests/CodeIndex.Tests/QueryCommandRunnerTests.cs
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

## 日本語
- CI の NuGet キャッシュキーを調整し、テスト用 project file だけの変更で package cache が失効しないようにしました。package 入力の検証は locked restore に任せます。
- C# Razor query alias の検証で 1 回の trimmed publish output を再利用し、そのテストから重複する `dotnet publish` 実行を削減しました。
- trimmed query JSON と Razor alias の smoke coverage を 1 つの publish-backed test に統合し、suite からさらに重複する `dotnet publish` 実行を削減しました。
- CI test command の重複 results-directory 引数を削除し、`CodeIndex.Tests.runsettings` を `TestResults` path の単一管理元にしました。
- clean な初回成功 lane では CI test stdout log を disk に書かないようにし、console streaming と失敗時の log artifact は維持しました。
- OS 単位の NuGet cache restore key を追加し、lock file 変更時にも互換性のある package cache 内容を再利用できるようにしました。
- `--no-build` が restore を抑止するため、CI の `dotnet test` command から重複する明示的な `--no-restore` を削除しました。
