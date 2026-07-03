---
category: changed
affected:
  - src/CodeIndex/Indexer/Scanning/FileIndexer.RecordLoading.cs
  - src/CodeIndex/Indexer/CSharpStaticInterfacePrepass.cs
---

## English

- **C# static-interface prepass reuses known indexability** — prepass raw-byte probes and normalized content reads avoid repeating the regular-file probe for targets produced by the index scan.

## 日本語

- **C# static-interface prepass が既知の indexability を再利用** — index scan が生成した target の raw-byte probe と normalized content read で、regular-file probe を繰り返さないようにしました。
