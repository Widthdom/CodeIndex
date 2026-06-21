---
category: fixed
affected:
  - src/CodeIndex/Indexer/Scanning/FileContentLoader.cs
  - src/CodeIndex/Indexer/References/Support/StructuralLineMasker.cs
  - tests/CodeIndex.Tests/FileIndexerTests.cs
  - tests/CodeIndex.Tests/ReferenceExtractorTests.cs
---

## English

- **Reduced full-index file loading and masking overhead** — normal UTF-8 source files no longer repeat UTF-16 detection during binary checks or allocate a second whole-file UTF-8 byte array just to compute the canonical checksum, and languages that do not need structural line masking no longer clone every line array.

## 日本語

- **full index 時の file loading / masking overhead を削減しました** — 通常の UTF-8 source file では binary check 中の UTF-16 判定の重複と、canonical checksum のためだけに whole-file UTF-8 byte 配列をもう一度確保する処理を省き、structural line masking が不要な言語では行配列全体の clone も避けるようにしました。
