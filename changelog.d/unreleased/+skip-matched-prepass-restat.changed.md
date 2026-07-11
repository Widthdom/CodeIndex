---
category: changed
affected:
  - src/CodeIndex/Indexer/Scanning/FileContentLoader.RawBytes.cs
---

## English

- **Successful raw-byte prepass probes no longer restat the source file** — once a C# contract candidate is found, indexing proceeds directly to the guarded content read instead of issuing an unnecessary metadata syscall.

## 日本語

- **raw-byte prepass が一致した場合の source file 再 stat を省略します** — C# contract 候補が見つかった後は不要な metadata syscall を行わず、保護された content read へ直接進みます。
