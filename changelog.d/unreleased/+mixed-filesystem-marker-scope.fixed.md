---
category: fixed
affected:
  - src/CodeIndex/Indexer/Scanning/FileIndexer.ProjectMarkers.cs
  - src/CodeIndex/Indexer/Scanning/FileIndexer.ScanOrchestration.cs
  - src/CodeIndex/Indexer/Scanning/FileIndexer.Types.cs
  - tests/CodeIndex.Tests/FileIndexerTests.cs
  - TESTING_GUIDE.md
---

## English

- **Project family scopes now honor case policy changes inside mixed filesystems** — the completed scan snapshot keeps case-only marker directories distinct under case-sensitive children while still resolving aliases under case-insensitive children, without returning to live marker enumeration or adding filesystem probes.

## 日本語

- **混在 filesystem 内で case policy が変わる場合も project family scope が正しくなりました** — 完了した scan snapshot は case-sensitive child 配下の大小文字だけが異なる marker directory を分離しつつ、case-insensitive child 配下の alias は同一 scope として解決し、live marker 列挙への後退や filesystem probe の追加も行いません。
