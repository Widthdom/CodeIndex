---
category: changed
affected:
  - src/CodeIndex/Indexer/References/ReferenceExtractor.Core.cs
  - src/CodeIndex/Indexer/References/ReferenceExtractor.cs
---

## English

- **Trim reference prepass snapshot allocation** — Razor file definitions, COBOL callable symbols, and Rust enum container candidates now use direct snapshot builders instead of LINQ materialization pipelines.

## 日本語

- **reference prepass snapshot の allocation を削減します** — Razor file definition、COBOL callable symbol、Rust enum container candidate を LINQ materialization pipeline ではなく直接 snapshot builder で構築します。
