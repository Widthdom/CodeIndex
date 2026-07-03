---
title: Lazily allocate CSharp symbol name sets
category: changed
affected:
  - src/CodeIndex/Indexer/References/Languages/CSharpReferenceExtractor.Support.cs
---

## English

- **C# reference extraction now lazily allocates symbol-derived name sets** — C# files without type or callable symbols reuse empty lookups instead of allocating unused `HashSet` instances during per-file setup.

## 日本語

- **C# 参照抽出がシンボル由来の name set を遅延確保するようになりました** — type または callable シンボルを持たない C# ファイルで、ファイル単位の準備時に未使用の `HashSet` を割り当てず空 lookup を再利用します。
