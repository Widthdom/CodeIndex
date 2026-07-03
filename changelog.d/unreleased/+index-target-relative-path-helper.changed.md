---
category: changed
affected:
  - src/CodeIndex/Indexer/Scanning/FileIndexer.Paths.cs
  - src/CodeIndex/Indexer/Scanning/FileIndexer.RecordLoading.cs
  - src/CodeIndex/Indexer/CSharpStaticInterfacePrepass.cs
  - src/CodeIndex/Cli/IndexCommandRunner.cs
---

## English

- **Index target relative paths share a root-prefix fast path** — full scans, record loading, and C# prepass target creation now reuse a common project-root-relative path helper and avoid unnecessary separator replacement on POSIX paths.

## 日本語

- **index target の relative path 生成で root-prefix fast path を共有** — full scan、record loading、C# prepass target 作成で共通の project-root-relative path helper を使い、POSIX path では不要な区切り文字置換も避けるようにしました。
