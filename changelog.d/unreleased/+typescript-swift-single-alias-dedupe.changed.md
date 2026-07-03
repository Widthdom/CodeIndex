---
title: Skip alias dedupe sets for single TypeScript and Swift aliases
category: changed
affected:
  - src/CodeIndex/Indexer/References/Languages/TypeScriptReferenceExtractor.cs
  - src/CodeIndex/Indexer/References/Languages/SwiftReferenceExtractor.cs
---

## English

- **TypeScript and Swift alias expansion skips single-alias dedupe sets** — files with one type alias no longer allocate a per-line alias de-duplication `HashSet` during reference extraction.

## 日本語

- **TypeScript / Swift の alias 展開が単一 alias では重複排除集合を作らないようになりました** — type alias が1件だけのファイルで、参照抽出中に行ごとの alias 重複排除 `HashSet` を割り当てないようにしました。
