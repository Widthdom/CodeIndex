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

## 日本語
- CI の NuGet キャッシュキーを調整し、テスト用 project file だけの変更で package cache が失効しないようにしました。package 入力の検証は locked restore に任せます。
- C# Razor query alias の検証で 1 回の trimmed publish output を再利用し、そのテストから重複する `dotnet publish` 実行を削減しました。
