---
category: fixed
affected:
  - src/CodeIndex/Indexer/SymbolExtractor.cs
  - tests/CodeIndex.Tests/SymbolExtractorTests.cs
---

## English

- **TypeScript generic type aliases now stay searchable when they use default type parameters** — `SymbolExtractor` now recognizes `type Foo<T = string> = ...`-style aliases instead of missing them, so TypeScript symbol search covers a common generic form that previously fell through.

## 日本語

- **TypeScript の generic type alias が default type parameter を使っていても検索対象に残るようになりました** — `SymbolExtractor` が `type Foo<T = string> = ...` 形式の alias を取りこぼさず認識するため、TypeScript の symbol search でよくある generic 形式を拾えるようになりました。
