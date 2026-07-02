---
category: changed
affected:
  - src/CodeIndex/Indexer/Scanning/FileIndexer.ProjectMarkers.cs
---

## English

- **Project marker pattern lists are reused during indexing** — C#, VB, F#, and MSBuild hotspot family scope detection now share static marker pattern arrays instead of allocating equivalent lists for every file.

## 日本語

- **indexing 中に project marker pattern list を再利用** — C#、VB、F#、MSBuild の hotspot family scope 検出で、ファイルごとに同等の list を割り当てず static な marker pattern 配列を共有するようにしました。
