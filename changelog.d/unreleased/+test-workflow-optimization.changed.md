---
category: changed
affected:
  - .github/workflows/dotnet.yml
  - tests/CodeIndex.Tests/CiWorkflowTests.cs
  - TESTING_GUIDE.md
---

## English
- Tightened the CI NuGet cache key to avoid package-cache misses from test-only project file edits while keeping locked restore as the package-input guard.

## 日本語
- CI の NuGet キャッシュキーを調整し、テスト用 project file だけの変更で package cache が失効しないようにしました。package 入力の検証は locked restore に任せます。
