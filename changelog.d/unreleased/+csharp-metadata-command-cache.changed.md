---
category: changed
affected:
  - src/CodeIndex/Database/DbWriter.cs
---

## English

- **C# metadata finalization reuses prepared commands** — repeated static-interface contract scans and C# metadata-target resolver passes now reuse fixed read/update SQL instead of rebuilding commands during large indexes.

## 日本語

- **C# metadata finalization の prepared command を再利用します** — static interface contract scan と C# metadata-target resolver の反復で、巨大リポジトリ indexing 中に固定 read/update SQL の command 再生成を避けます。
