---
category: changed
affected:
  - src/CodeIndex/Indexer/CSharpStaticInterfacePrepass.cs
---

## English

- **Fresh C# prepass lookup assembly no longer flattens symbol lists** — per-file artifacts are enumerated through a bounded non-owning segmented view until immutable workspace lookups are complete, avoiding duplicate workspace-sized pointer buffers while preserving extraction order, evidence, checksum, and cache ownership contracts.

## 日本語

- **初回C# prepassのlookup構築がsymbol listをflattenしなくなりました** — immutableなworkspace lookupが完成するまでfile単位artifactをboundedなnon-owning segmented viewで列挙し、extraction順、evidence、checksum、cache所有権契約を維持しながらworkspace規模の重複pointer bufferを避けます。
