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

## 日本語
- CI の NuGet キャッシュキーを調整し、テスト用 project file だけの変更で package cache が失効しないようにしました。package 入力の検証は locked restore に任せます。
- C# Razor query alias の検証で 1 回の trimmed publish output を再利用し、そのテストから重複する `dotnet publish` 実行を削減しました。
- trimmed query JSON と Razor alias の smoke coverage を 1 つの publish-backed test に統合し、suite からさらに重複する `dotnet publish` 実行を削減しました。
- CI test command の重複 results-directory 引数を削除し、`CodeIndex.Tests.runsettings` を `TestResults` path の単一管理元にしました。
