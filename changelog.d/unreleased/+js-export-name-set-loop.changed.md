---
category: changed
affected:
  - src/CodeIndex/Indexer/Symbols/SymbolExtractor.JavaScriptTypeScriptSupport.cs
---

## English

- **Trim JavaScript/TypeScript export name-set allocation** — exported variable and exported object-literal property extraction now builds symbol-name sets with direct loops instead of LINQ iterator pipelines.

## 日本語

- **JavaScript/TypeScript export name set の allocation を削減します** — exported variable と exported object-literal property の抽出で、symbol-name set を LINQ iterator pipeline ではなく直接ループで構築します。
