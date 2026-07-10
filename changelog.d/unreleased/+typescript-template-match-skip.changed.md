---
category: changed
affected:
  - src/CodeIndex/Indexer/References/ReferenceExtractor.PatternTypeReferences.cs
---

## English

- **TypeScript template-hole matching avoids throwaway reference collections** - Nested template literals encountered while matching `${...}` braces now use a skip-only path during reference extraction.

## 日本語

- **TypeScript template hole matching が使い捨て reference collection を避けるようになりました** - reference extraction 中に `${...}` の brace を対応付ける際、nested template literal は skip 専用経路で処理します。
