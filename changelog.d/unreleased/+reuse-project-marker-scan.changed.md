---
category: changed
affected:
  - src/CodeIndex/Indexer/Scanning/FileIndexer.ProjectMarkers.cs
  - src/CodeIndex/Indexer/Scanning/FileIndexer.DirectoryEnumeration.cs
  - src/CodeIndex/Indexer/Scanning/FileIndexer.ScanOrchestration.cs
  - src/CodeIndex/Cli/IndexCommandRunner.FullScan.cs
  - src/CodeIndex/Cli/IndexCommandRunner.DryRun.cs
---

## English

- **Full indexing no longer walks large directory trees again for project-family metadata** — authoritative scans now collect C#, VB, F#, and MSBuild marker fingerprints during normal discovery and reuse a complete marker-directory snapshot for per-file family scopes. Fingerprint limits, ignore boundaries, incomplete-scan warnings, and live fallbacks remain fail-closed.

## 日本語

- **full indexing が project-family metadata のために巨大な directory tree を再走査しなくなりました** — authoritative scan は通常 discovery 中に C#、VB、F#、MSBuild の marker fingerprint を収集し、complete な marker-directory snapshot を file ごとの family scope に再利用します。fingerprint 上限、ignore 境界、不完全 scan warning、live fallback は引き続き fail-closed です。
