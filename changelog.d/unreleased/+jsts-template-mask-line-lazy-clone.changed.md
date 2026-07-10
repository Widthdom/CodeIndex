---
category: changed
affected:
  - src/CodeIndex/Indexer/References/Support/StructuralLineMasker.cs
  - tests/CodeIndex.Tests/ReferenceExtractorTests.cs
---

## English

- **JavaScript and TypeScript template masking avoids unchanged line copies** — reference extraction now reuses JavaScript and TypeScript source lines that do not need template literal, template-hole comment, or continued string masking after structural masking is triggered.

## 日本語

- **JavaScript/TypeScript テンプレートマスキングで未変更行のコピーを避けるようになりました** — 参照抽出は、構造マスキング開始後も template literal、template hole comment、継続文字列のマスクが不要な JavaScript/TypeScript 行を再利用します。
