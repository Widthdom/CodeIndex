---
category: changed
affected:
  - src/CodeIndex/Cli/IndexCommandRunner.cs
  - src/CodeIndex/Cli/IndexCommandRunner.DryRun.cs
  - src/CodeIndex/Indexer/Scanning/FileContentLoader.cs
  - src/CodeIndex/Indexer/Scanning/FileContentLoader.Decoding.cs
  - src/CodeIndex/Indexer/Scanning/FileContentLoader.GitLfs.cs
  - src/CodeIndex/Indexer/Scanning/FileContentLoader.UnknownLanguage.cs
  - src/CodeIndex/Indexer/Scanning/FileIndexer.ContentLoadingContracts.cs
  - src/CodeIndex/Indexer/Scanning/FileIndexer.FileAcceptance.cs
  - src/CodeIndex/Indexer/Scanning/FileIndexer.LanguageDetection.cs
  - src/CodeIndex/Indexer/Scanning/FileIndexer.Shebang.cs
  - tests/CodeIndex.Tests/FileIndexerTests.cs
  - tests/CodeIndex.Tests/IndexCommandRunnerDryRunTests.cs
  - tests/CodeIndex.Tests/McpServerToolsCallTests.cs
  - DEVELOPER_GUIDE.md
  - TESTING_GUIDE.md
---

## English

- **Unknown-language discovery reuses one bounded authorized file snapshot** — final extensionless and unregistered-extension candidates now read the 256-byte script header and, only when it is unsupported, continue Git LFS, UTF-16, NUL, and max-file coverage checks on the same pooled stream. Stable unknown files avoid a second open and payload-sized allocation, while recognized shebangs and `#compdef`, ambiguous extensions, mutation retries, CLI dry-run, freshness, and MCP authorization semantics remain intact.

## 日本語

- **未知言語の探索が1つの上限付き認可済みfile snapshotを再利用するようになりました** — 最終的な拡張子なし・未登録拡張子candidateでは256 byteのscript headerを読み、未対応の場合だけ同じpooled stream上でGit LFS、UTF-16、NUL、max-fileのcoverage判定を続行します。stableな未知fileは2回目のopenとpayload size比例allocationを回避しつつ、認識済みshebang / `#compdef`、曖昧拡張子、mutation retry、CLI dry-run、freshness、MCP authorizationの意味論を維持します。
