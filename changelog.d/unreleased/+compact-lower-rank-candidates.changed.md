---
category: changed
affected:
  - src/CodeIndex/Database/DbWriter.References.cs
  - src/CodeIndex/Database/DbWriter.ReferenceGraphRefreshScope.cs
---

## English

- **Initial reference-graph finalization now uses a compact lower-rank match set** — all rank-5 fallbacks probe one TEMP row per reference that matched ranks 0–4 instead of repeatedly searching the much larger physical candidate table, while full, scoped, and retained refreshes preserve candidate and ambiguity results across languages.

## 日本語

- **初回reference graph確定がcompactな下位rank一致集合を使うようになりました** — 全rank 5 fallbackは巨大な物理candidate tableを反復検索せず、rank 0〜4で一致したreferenceごとに1行のTEMP集合を参照します。full / scoped / retained refreshのcandidate・ambiguity結果は全言語で維持されます。
