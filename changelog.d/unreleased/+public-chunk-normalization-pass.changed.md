---
category: changed
affected:
  - src/CodeIndex/Indexer/Scanning/ChunkSplitter.cs
---

## English

- **Public chunk splitting shares indexed normalization** — `ChunkSplitter.Split` now uses the same normalization result as indexing, carrying oversize-line and line-count metadata into chunking instead of rescanning normalized content.

## 日本語

- **public chunk splitting が index 用 normalization を共有します** — `ChunkSplitter.Split` は indexing と同じ normalization 結果を使い、oversize-line と line-count metadata を chunking へ渡して、正規化済み content の再走査を避けます。
