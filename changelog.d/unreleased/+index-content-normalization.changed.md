---
category: changed
affected:
  - src/CodeIndex/Cli/IndexCommandRunner.FullScan.cs
  - src/CodeIndex/Cli/IndexCommandRunner.Update.cs
  - src/CodeIndex/Cli/IndexCommandRunner.cs
  - src/CodeIndex/Indexer/Scanning/FileContentInspection.cs
  - src/CodeIndex/Indexer/Scanning/FileContentLoader.cs
  - src/CodeIndex/Indexer/Scanning/FileIndexer.cs
  - src/CodeIndex/Indexer/Scanning/LoadedFileRecord.cs
  - src/CodeIndex/Mcp/McpToolHandlers.cs
  - tests/CodeIndex.Tests/FileIndexerTests.cs
---

## English

- **Indexing now performs less duplicate work while loading file content** — content loading now combines line-ending cleanup, line-leading invisible stripping, and line counting, stable files are read directly into their final byte array, validation reuses loader-detected LFS/UTF-16 metadata, and directory scans reuse per-directory relative paths so large indexes spend less time in repeated scans, path normalization, and buffer copies.

## 日本語

- **インデックス作成時のファイル内容読み込みで重複作業が減りました** — content loading は改行正規化、行頭不可視文字の除去、行数計測をまとめて行い、安定したファイルは最終的な byte 配列へ直接読み込み、validation は loader が検出した LFS / UTF-16 metadata を再利用し、directory scan は directory ごとの相対パスを再利用するため、大きなインデックスで繰り返し走査、path normalization、buffer copy にかかる時間を減らします。
