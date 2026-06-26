---
category: changed
affected:
  - src/CodeIndex/Cli/IndexCommandRunner.FullScan.cs
  - src/CodeIndex/Database/DbContext.cs
  - src/CodeIndex/Database/DbReader.cs
  - src/CodeIndex/Database/DbWriter.cs
---

## English

- **No-op full scans skip TypeScript augmentation rebuilds** — `cdidx index <path>` now avoids the all-TypeScript augmentation reference rebuild when the scan reused every TypeScript row, no stale rows were purged, and the existing hotspot-family trust is current.

## 日本語

- **no-op full scan が TypeScript augmentation rebuild を skip します** — `cdidx index <path>` は、すべての TypeScript row を再利用し、stale row purge がなく、既存の hotspot-family trust が現行の場合に、全 TypeScript augmentation reference の再構築を避けるようになりました。
