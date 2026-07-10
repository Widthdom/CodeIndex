---
category: changed
affected:
  - src/CodeIndex/Indexer/References/Support/StructuralLineMasker.cs
  - tests/CodeIndex.Tests/ReferenceExtractorTests.cs
---

## English

- **Swift structural masking avoids unchanged line copies** — reference extraction now reuses Swift source lines that do not need multiline string, raw string, or block-comment masking after structural masking is triggered.

## 日本語

- **Swift 構造マスキングで未変更行のコピーを避けるようになりました** — 参照抽出は、構造マスキング開始後も multiline string、raw string、block comment のマスクが不要な Swift 行を再利用します。
