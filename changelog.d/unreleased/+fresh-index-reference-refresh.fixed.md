---
category: fixed
affected:
  - src/CodeIndex/Cli/IndexCommandRunner.FullScan.cs
  - src/CodeIndex/Database/DbWriter.cs
  - tests/CodeIndex.Tests/IndexCommandRunnerTests.cs
---

## English

- **Reduced first-time `cdidx .` indexing time.** Fresh full scans now defer mutual-recursion reference finalization until after bulk reference insertion instead of refreshing it after every file, and skip empty-database cleanup probes that cannot match existing rows.

## 日本語

- **初回の `cdidx .` インデックス時間を短縮しました。** fresh full scan では、相互再帰参照の確定処理をファイルごとではなく参照の一括挿入後にまとめて行い、空DBでは既存行に一致しない cleanup probe も省略します。
