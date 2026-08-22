---
category: changed
affected:
  - src/CodeIndex/Indexer/Symbols/SymbolExtractor.ExtractCore.cs
  - src/CodeIndex/Indexer/Symbols/SymbolExtractor.Patterns.cs
  - tests/CodeIndex.Tests/SymbolExtractorRequiredLiteralGateTests.cs
  - DEVELOPER_GUIDE.md
  - TESTING_GUIDE.md
---

## English

- **Initial symbol indexing avoids more provably impossible regular-expression work** — Audited JavaScript/TypeScript HOC, TypeScript namespace/module, and Kotlin declaration/property alternatives now skip a built-in pattern at both file and exact-input scope only when none of its case-sensitive required literals is present, while preserving symbol output, pattern order, and recovery behavior.

## 日本語

- **初回のシンボルインデックスで、成功し得ないことを証明できる正規表現処理をさらに回避します** — 監査済みの JavaScript / TypeScript HOC、TypeScript namespace / module、Kotlin declaration / property の alternative について、case-sensitive な必須 literal が1つもない場合だけ built-in pattern を file 単位と exact-input 単位の両方で skip し、symbol output、pattern 順序、recovery 動作は維持します。
