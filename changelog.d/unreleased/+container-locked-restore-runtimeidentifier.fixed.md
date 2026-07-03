---
category: fixed
affected:
  - Dockerfile
  - docs/dependency-restore-policy.md
  - tests/CodeIndex.Tests/PackagesLockTests.cs
---

## English

- Fixed the release container build so Docker multi-arch locked restore uses `RuntimeIdentifier` without tripping NuGet's single-RID lock-file mismatch.

## 日本語

- release container build の multi-arch locked restore が NuGet の single-RID lock-file mismatch で失敗しないよう、`RuntimeIdentifier` 指定に修正しました。
