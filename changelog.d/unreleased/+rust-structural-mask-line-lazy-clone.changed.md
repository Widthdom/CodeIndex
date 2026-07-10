---
category: changed
affected:
  - src/CodeIndex/Indexer/References/Support/StructuralLineMasker.cs
  - tests/CodeIndex.Tests/ReferenceExtractorTests.cs
---

## English

- **Rust structural masking avoids unchanged line copies** — reference extraction now reuses Rust source lines that do not need raw string or block comment masking after structural masking is triggered.

## 日本語

- **Rust 構造マスキングで未変更行のコピーを避けるようになりました** — 参照抽出は、構造マスキング開始後も raw string や block comment のマスクが不要な Rust 行を再利用します。
