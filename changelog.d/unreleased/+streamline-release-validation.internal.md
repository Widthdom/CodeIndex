---
category: internal
affected:
  - .github/workflows/release.yml
  - tests/CodeIndex.Tests/ReleaseWorkflowTests.cs
  - tests/CodeIndex.Tests/PackagesLockTests.cs
  - tests/CodeIndex.Tests/CiWorkflowTests.cs
  - TESTING_GUIDE.md
---

## English

- **Release validation now avoids repeating the full suite across native RIDs** — The release workflow runs the RID-independent net8 suite once on linux-x64, scopes publish-only lanes to the production restore graph, and exercises each natively runnable self-contained artifact with an index/status SQLite smoke test.

## 日本語

- **release 検証で native RID ごとの full suite 反復を廃止しました** — release workflow は RID 非依存の net8 suite を linux-x64 で 1 回実行し、publish-only lane の restore を production graph に限定したうえで、native 実行可能な各 self-contained artifact に index/status の SQLite smoke test を通します。
