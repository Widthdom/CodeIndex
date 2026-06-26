---
category: changed
affected:
  - src/CodeIndex/Indexer/Scanning/ChunkSplitter.cs
  - tests/CodeIndex.Tests/ChunkSplitterTests.cs
---

## English

- **Small-file chunking uses the known line count** — normalized chunking now returns a single chunk directly when indexing already knows the file has at most one chunk of lines, avoiding an extra line-start offset scan for small files across CLI and MCP indexing.

## 日本語

- **小さなファイルの chunking で既知の line count を使います** — normalized chunking は、indexing 側で1チャンク以内の行数だと分かっている場合に直接1チャンクを返し、CLI/MCP indexing 全体で小さなファイルの line-start offset 再走査を避けます。
