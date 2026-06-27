---
category: changed
affected:
  - src/CodeIndex/Indexer/Symbols/SymbolExtractor.JavaScriptTypeScriptSupport.cs
---

## English

- **Trim JavaScript/TypeScript scan target sorting allocation** — class, synthetic class, and object-literal scan targets now use direct collection and in-place sorting instead of LINQ sorting pipelines.

## 日本語

- **JavaScript/TypeScript scan target sort の allocation を削減します** — class、synthetic class、object-literal の scan target を LINQ sorting pipeline ではなく、直接収集と in-place sort で構築します。
