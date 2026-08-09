---
category: changed
affected:
  - src/CodeIndex/Indexer/Symbols/SymbolExtractor.ExtractionPhases.cs
  - src/CodeIndex/Indexer/Symbols/SymbolExtractor.JavaScriptScopes.cs
---

## English

- **JavaScript and TypeScript symbol extraction reuses its sanitized snapshot** — module, supplemental-symbol, and private-scope analysis now share one column-preserving snapshot, avoiding a duplicate full-file lexical pass on scope-heavy files while retaining the flat-file pre-scan fast path.

## 日本語

- **JavaScript / TypeScript の symbol extraction が sanitized snapshot を再利用します** — module、supplemental symbol、private-scope 解析は列位置を保つ snapshot を共有し、flat file の pre-scan fast path を維持しながら scope の多い file で重複していた全file lexical passを避けます。
