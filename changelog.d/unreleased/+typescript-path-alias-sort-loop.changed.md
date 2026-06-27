---
category: changed
affected:
  - src/CodeIndex/Indexer/Symbols/SymbolExtractor.TypeScriptPathAliases.cs
---

## English

- **Trim TypeScript path alias rule sorting allocation** — TypeScript path alias rules now preserve stable priority order with direct indexed sorting and compute literal lengths with a simple loop instead of LINQ helpers.

## 日本語

- **TypeScript path alias rule sort の allocation を削減します** — TypeScript path alias rule で、直接indexed sortにより安定した優先順を保ち、literal length も LINQ helper ではなく単純ループで計算します。
