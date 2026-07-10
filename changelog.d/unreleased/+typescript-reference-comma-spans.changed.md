---
category: changed
affected:
  - src/CodeIndex/Indexer/References/Languages/TypeScriptReferenceExtractor.cs
  - src/CodeIndex/Indexer/References/Support/TypedLanguageReferenceExtractor.cs
---

## English

- **TypeScript reference comma lists scan with spans** — generic constraints, type parameter defaults, function parameters, where clauses, and shared comma-separated type lists avoid copying the full list before segment processing.

## 日本語

- **TypeScript reference の comma list を span 走査するようになりました** — generic constraint、type parameter default、function parameter、where clause、共通 comma-separated type list で segment 処理前の list 全体コピーを避けます。
