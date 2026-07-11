---
category: changed
affected:
  - src/CodeIndex/Indexer/Scanning/FileIndexer.ScanOrchestration.cs
---

## English

- **Incremental file-count hints now pre-size full-scan directory tracking** — large repositories avoid repeated growth of listed, fully-scanned, and visited-directory sets while retaining a bounded allocation ceiling.

## 日本語

- **incremental の file-count hint を full-scan の directory 追跡容量にも利用します** — 上限付きの確保を維持しながら、大規模リポジトリで listed / fully-scanned / visited directory set の再拡張を避けます。
