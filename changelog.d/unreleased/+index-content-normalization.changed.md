---
category: changed
affected:
  - src/CodeIndex/Cli/IndexCommandRunner.CSharpStaticInterfacePrepass.cs
  - src/CodeIndex/Cli/IndexCommandRunner.FullScan.cs
  - src/CodeIndex/Cli/IndexCommandRunner.Update.cs
  - src/CodeIndex/Cli/IndexCommandRunner.cs
  - src/CodeIndex/Cli/IndexFreshnessChecker.cs
  - src/CodeIndex/Indexer/Scanning/FileContentInspection.cs
  - src/CodeIndex/Indexer/Scanning/FileContentLoader.cs
  - src/CodeIndex/Indexer/Scanning/FileIndexer.cs
  - src/CodeIndex/Indexer/Scanning/ChunkSplitter.cs
  - src/CodeIndex/Indexer/Scanning/LoadedFileRecord.cs
  - src/CodeIndex/Indexer/References/ReferenceExtractionContext.cs
  - src/CodeIndex/Indexer/References/ReferenceExtractor.Core.cs
  - src/CodeIndex/Indexer/References/ReferenceExtractor.Preparation.cs
  - src/CodeIndex/Indexer/References/ReferenceExtractor.cs
  - src/CodeIndex/Indexer/Symbols/SymbolExtractor.cs
  - src/CodeIndex/Indexer/Symbols/SymbolExtractionWorker.cs
  - src/CodeIndex/Mcp/McpToolHandlers.cs
  - tests/CodeIndex.Tests/FileIndexerTests.cs
  - tests/CodeIndex.Tests/IndexCommandRunnerTests.cs
  - tests/CodeIndex.Tests/ReferenceExtractorTests.cs
  - tests/CodeIndex.Tests/SymbolExtractorTests.cs
---

## English

- **Indexing now performs less duplicate work while loading file content** — content loading now combines line-ending cleanup, line-leading invisible stripping, line counting, and oversized-line detection, stable files are read directly into their final byte array, validation reuses loader-detected LFS/UTF-16 metadata and oversized-line state, chunking plus symbol/reference extraction reuse loader-normalized content and oversized-line state in full/update/MCP index paths, C# static-interface preflight scans avoid checksum/line-count/record construction, skip decoding files that lack raw static-interface contract tokens, and scan UTF-16 token candidates on two-byte boundaries, parallel full-scan work items stop queuing decoded content/raw bytes after extraction is complete, and full/update/directory/freshness scans reuse computed relative paths and scan-detected target languages so large indexes spend less time in repeated scans, path normalization, language detection, buffer copies, decoding, extraction guards, and GC work.

## 日本語

- **インデックス作成時のファイル内容読み込みで重複作業が減りました** — content loading は改行正規化、行頭不可視文字の除去、行数計測、長すぎる行の検出をまとめて行い、安定したファイルは最終的な byte 配列へ直接読み込み、validation は loader が検出した LFS / UTF-16 metadata と長行検出結果を再利用し、chunking と symbol / reference extraction は full scan / update / MCP index 経路で loader が正規化済みの content と長行検出結果を再利用し、C# static interface の事前 scan は checksum / line count / record 構築を避け、raw bytes に static interface contract token が無いファイルの decode も省略し、UTF-16 token 候補を 2 byte 境界で走査し、parallel full scan の work item は抽出完了後の decoded content / raw bytes を queue しなくなり、full scan / update / directory scan / freshness scan は算出済みの相対パスと scan 時に判定した言語を再利用するため、大きなインデックスで繰り返し走査、path normalization、language detection、buffer copy、decode、extraction guard、GC work にかかる時間を減らします。
