---
category: changed
affected:
  - src/CodeIndex/Indexer/Scanning/FileContentInspection.cs
  - src/CodeIndex/Indexer/Scanning/FileContentLoader.Decoding.cs
  - src/CodeIndex/Indexer/Scanning/FileContentLoader.cs
  - src/CodeIndex/Indexer/Scanning/FileIndexer.cs
---

## English

- **Indexing reuses raw-byte inspection** — normal file loading now carries BOM, NULL-byte, and line-ending classification into validation, avoiding a second full raw-byte pass for each decoded non-UTF-16 file while keeping C# prepass reads lightweight.

## 日本語

- **indexing で raw-byte inspection を再利用します** — 通常の file loading は BOM、NULL byte、line ending 分類を validation へ渡し、UTF-16 以外のデコード済みファイルごとの生バイト再走査を避けます。C# prepass の読み取りは軽いままです。
