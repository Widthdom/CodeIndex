---
category: internal
affected: Indexer
---

## English

- Avoided allocating the C# callable-parameter line-start state array for files whose callable parameter lists never carry state across line boundaries during symbol extraction.

## 日本語

- C# の symbol 抽出で、callable parameter list の状態が行境界をまたがないファイルでは行頭状態配列を確保しないようにしました。
