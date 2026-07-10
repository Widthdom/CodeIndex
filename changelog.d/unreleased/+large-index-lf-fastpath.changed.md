---
category: changed
affected:
  - src/CodeIndex/Indexer/Scanning/FileContentLoader.cs
  - tests/CodeIndex.Tests/FileIndexerContentLoadingTests.cs
---

## English

- **Large LF-only source files with angle brackets now stay on the fast content-normalization path** - Indexing avoids a character-by-character normalization pass for ordinary source files that contain `<` or `>` while still detecting conflict markers.

## 日本語

- **山括弧を含む大きな LF-only ソースファイルが content normalization の高速経路に残るようになりました** - `<` や `>` を含む通常のソースファイルで文字単位の normalization pass を避けつつ、conflict marker 検出は維持します。
