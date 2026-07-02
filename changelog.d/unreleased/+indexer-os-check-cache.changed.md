---
category: changed
affected:
  - src/CodeIndex/Indexer/Scanning/FileIndexer.Paths.cs
  - src/CodeIndex/Indexer/Scanning/FileIndexer.FileIdentity.cs
  - src/CodeIndex/Indexer/CSharpStaticInterfacePrepass.cs
---

## English

- **Indexer hot paths reuse cached OS checks** — relative-path prefix matching, hardlink identity probing, and C# prepass root checks now reuse process-wide platform flags instead of calling `OperatingSystem` per file.

## 日本語

- **indexer hot path が cached OS check を再利用** — relative-path prefix matching、hardlink identity probe、C# prepass root check は、file ごとに `OperatingSystem` を呼ばず process-wide な platform flag を再利用するようにしました。
