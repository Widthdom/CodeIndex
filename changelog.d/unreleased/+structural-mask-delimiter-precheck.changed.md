---
category: changed
affected:
  - src/CodeIndex/Indexer/References/Support/StructuralLineMasker.cs
  - tests/CodeIndex.Tests/ReferenceExtractorTests.cs
---

## English

- **Structural masking now skips more files that only contain ordinary literals** — symbol and reference extraction now use language-specific prechecks before cloning line arrays, avoiding unnecessary work for Python, Rust, Kotlin, Swift, Scala, and C# files that do not contain structural multiline string or block-comment delimiters.

## 日本語

- **通常リテラルだけのファイルでは構造マスキングをより多くスキップするようになりました** — シンボル抽出と参照抽出は行配列を clone する前に言語別の事前判定を行い、構造的な複数行文字列やブロックコメントの区切りを含まない Python / Rust / Kotlin / Swift / Scala / C# ファイルで不要な処理を避けます。
