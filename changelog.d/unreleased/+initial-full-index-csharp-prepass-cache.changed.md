---
category: changed
affected:
  - src/CodeIndex/Indexer/CSharpPrepassSymbolArtifactCache.cs
  - src/CodeIndex/Indexer/CSharpStaticInterfacePrepass.cs
---

## English

- **Cold C# prepass caching now scales past 4,096 files** — Fresh full indexes retain reusable symbol artifacts up to the existing symbol and estimated-memory budgets, prioritizing larger decoded sources when capacity binds so costly extraction work is less likely to repeat.

## 日本語

- **C# の初回事前解析 cache が4,096 fileを超えて拡張** — fresh full index では既存の symbol 数・推定 memory budget まで再利用可能な artifact を保持し、容量到達時は decode 済み source の大きい順に優先して、高コストな抽出の繰り返しを減らします。
