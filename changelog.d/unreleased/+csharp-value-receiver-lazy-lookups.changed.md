---
title: Lazily allocate CSharp value receiver lookups
category: changed
affected:
  - src/CodeIndex/Indexer/References/Languages/CSharpReferenceExtractor.Support.cs
---

## English

- **C# value receiver setup now lazily allocates lookup storage** — files without containing-type receiver names or scoped receiver names reuse empty lookups, and symbols without bodies no longer allocate unused per-function buffers.

## 日本語

- **C# value receiver 準備が lookup storage を遅延確保するようになりました** — containing type receiver 名や scoped receiver 名が無いファイルでは空 lookup を再利用し、body の無いシンボルでは未使用の per-function buffer を割り当てないようにしました。
