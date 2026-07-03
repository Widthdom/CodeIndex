---
category: changed
affected:
  - src/CodeIndex/Indexer/Scanning/FileIndexer.FileIndexability.cs
---

## English

- **File indexability caches the Windows platform check** — scan-time file probes reuse a process-wide OS flag instead of asking the runtime for every file attribute decision.

## 日本語

- **file indexability が Windows platform 判定を cache** — scan 時の file probe は、file attribute 判定ごとに runtime へ OS を問い合わせず、process-wide な OS flag を再利用するようにしました。
