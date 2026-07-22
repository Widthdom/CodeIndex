---
category: changed
affected:
  - src/CodeIndex/Indexer/Scanning/CaseSensitivityProbeDirectory.cs
  - src/CodeIndex/Indexer/Scanning/FileIndexer.CaseSensitivity.cs
  - src/CodeIndex/Indexer/Scanning/FileIndexer.DirectoryEnumeration.cs
  - src/CodeIndex/Indexer/Scanning/FileIndexer.cs
  - tests/CodeIndex.Tests/FileIndexerTests.cs
  - tests/CodeIndex.Tests/PathCompatibilityMatrixTests.cs
  - TESTING_GUIDE.md
---

## English

- **Large-tree scans now enumerate each normally visited directory once** — The default per-directory case-sensitivity probe and the main entry walk share one ordered, scan-local snapshot while preserving custom probes, legacy test enumeration, dangling-entry diagnostics, and single-error enumeration-failure diagnostics.

## 日本語

- **巨大treeの通常scanで各訪問directoryを1回だけ列挙するようになりました** — directory単位の既定case-sensitivity probeと本entry走査が、順序を保ったscan-local snapshotを共有しつつ、custom probe、legacy test列挙、dangling entry診断、列挙失敗を1件にまとめるerror診断を維持します。
