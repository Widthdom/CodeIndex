---
category: changed
affected:
  - src/CodeIndex/Indexer/Scanning/FileIndexer.ScanOrchestration.cs
---

## English

- **Full-scan diagnostic path collections now materialize as exact-size arrays** — large non-indexable, unknown-extension, directory, repository, and dangling-link result sets no longer retain an additional list wrapper and spare capacity.

## 日本語

- **full-scan の診断 path collection を正確なサイズの配列として確定します** — 大量の non-indexable / unknown-extension / directory / repository / dangling-link 結果で、追加の List wrapper と余剰容量を保持しなくなりました。
