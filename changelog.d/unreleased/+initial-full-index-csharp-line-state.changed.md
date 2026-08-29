---
category: changed
affected:
  - src/CodeIndex/Indexer/Symbols/SymbolExtractor.CSharpScanner.cs
  - src/CodeIndex/Indexer/Symbols/SymbolExtractor.ExtractionPhases.cs
---

## English

- **Cold C# indexing reuses lexer start states** — The initial symbol scan now carries its per-line C# lexer snapshots into later scope analysis instead of lexing the same file a second time, reducing first-index CPU and allocations without changing extracted symbols.

## 日本語

- **C# の初回 index で lexer 開始状態を再利用** — 最初の symbol 走査で得た行別の C# lexer snapshot を後続の scope 解析へ引き継ぎ、同じ file の二重字句解析を避けることで、抽出結果を変えずに初回 index の CPU 時間と allocation を削減しました。
