---
category: changed
affected:
  - src/CodeIndex/Indexer/References/Support/StructuralLineMasker.cs
---

## English

- **JavaScript tagged-template filtering now reuses unchanged for-of scan lines** — the secondary scan buffer used to suppress `of` hits inside `for (... of ...)` headers now allocates replacement lines only where strings, regex literals, or line comments actually need masking.

## 日本語

- **JavaScript のタグ付き template フィルタで未変更の for-of scan 行を再利用するようになりました** — `for (... of ...)` ヘッダ内の `of` ヒットを抑制する二次 scan buffer は、文字列・regex リテラル・行コメントのマスクが実際に必要な行だけを置換します。
