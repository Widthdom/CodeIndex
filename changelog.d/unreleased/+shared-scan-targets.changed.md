---
category: changed
affected:
  - src/CodeIndex/Indexer/Scanning/FileIndexer.Types.cs
  - src/CodeIndex/Indexer/Scanning/FileIndexer.FileAcceptance.cs
  - src/CodeIndex/Cli/IndexCommandRunner.FullScan.cs
  - src/CodeIndex/Cli/IndexCommandRunner.DryRun.cs
  - src/CodeIndex/Cli/IndexFreshnessChecker.cs
  - src/CodeIndex/Mcp/McpToolHandlers.Indexing.Execution.cs
  - tests/CodeIndex.Tests/FileIndexerTests.cs
  - tests/CodeIndex.Tests/McpServerToolsCallTests.cs
  - DEVELOPER_GUIDE.md
  - TESTING_GUIDE.md
---

## English

- **Full indexing reuses scan-produced file targets across CLI and MCP** — discovery now carries normalized paths, safely reusable language detection, and path-only generated-code suppression directly into full indexing, authoritative dry-run, and freshness checks instead of rebuilding workspace-sized target arrays. Real MCP indexing also reuses the project-marker fingerprints from the same source scan, removing an independent directory-tree walk while preserving secure open, live stat/authorization checks, content-dependent language detection, and fail-closed marker trust.

## 日本語

- **CLIとMCPのfull indexingがscanで生成したfile targetを再利用するようになりました** — discoveryで得た正規化path、安全に再利用できる言語判定、path-onlyのgenerated-code suppressionをfull indexing、authoritative dry-run、freshness checkへ直接引き継ぎ、workspace規模のtarget array再構築をなくしました。実MCP indexingも同じsource scanのproject-marker fingerprintを再利用して独立したdirectory-tree walkを削減しつつ、secure open、live stat/authorization check、content依存の言語判定、fail-closedなmarker trustを維持します。
