---
category: changed
affected:
  - src/CodeIndex/Cli/IndexCommandRunner.FullScan.cs
---

## English

- **Narrowed generated-suppression cache for the C# prepass** - full scans now build the generated-code suppression lookup used by the C# static-interface prepass only for C# targets instead of every indexed file.

## 日本語

- **C# prepass 用 generated-suppression cache の対象を絞りました** - full scan は C# static-interface prepass が使う generated-code suppression lookup を、全 index 対象 file ではなく C# target だけで作るようになりました。
