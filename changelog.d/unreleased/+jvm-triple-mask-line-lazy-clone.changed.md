---
category: changed
affected:
  - src/CodeIndex/Indexer/References/Support/StructuralLineMasker.cs
  - tests/CodeIndex.Tests/ReferenceExtractorTests.cs
---

## English

- **Kotlin and Scala structural masking avoids unchanged line copies** — reference extraction now reuses Kotlin and Scala source lines that do not need triple-string or block-comment masking after structural masking is triggered.

## 日本語

- **Kotlin/Scala 構造マスキングで未変更行のコピーを避けるようになりました** — 参照抽出は、構造マスキング開始後も triple string や block comment のマスクが不要な Kotlin/Scala 行を再利用します。
