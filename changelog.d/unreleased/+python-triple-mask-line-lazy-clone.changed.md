---
category: changed
affected:
  - src/CodeIndex/Indexer/References/Support/StructuralLineMasker.cs
  - tests/CodeIndex.Tests/ReferenceExtractorTests.cs
---

## English

- **Python triple-string masking avoids unchanged line copies** — reference extraction now reuses Python source lines that do not need triple-string or f-string structural masking after structural masking is triggered.

## 日本語

- **Python triple string マスキングで未変更行のコピーを避けるようになりました** — 参照抽出は、構造マスキング開始後も triple string や f-string 構造マスクが不要な Python 行を再利用します。
