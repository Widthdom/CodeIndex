---
category: fixed
affected:
  - src/CodeIndex/Indexer/SymbolExtractor.cs
  - tests/CodeIndex.Tests/SymbolExtractorTests.cs
---

## English

- **Improved Visual Basic symbol extraction coverage** — `SymbolExtractor` now better captures VB delegates, operators, enum members, and inheritance anchors while reducing enum-member false positives on non-enum control lines.

## 日本語

- **Visual Basic のシンボル抽出カバレッジを改善** — `SymbolExtractor` が VB の delegate・operator・enum メンバー・継承アンカーをより適切に抽出し、enum メンバー判定の誤検出（非 enum の制御行）も抑制しました。
