---
category: changed
affected:
  - src/CodeIndex/Indexer/References/Support/StructuralLineMasker.cs
  - tests/CodeIndex.Tests/ReferenceExtractorTests.cs
---

## English

- **Perl POD masking avoids per-line trim allocations** — reference extraction now detects indented POD directives with span-based leading-whitespace scanning.

## 日本語

- **Perl POD マスキングで行ごとの trim 割り当てを避けるようになりました** — 参照抽出は、span ベースの先頭空白走査でインデント付き POD directive を検出します。
