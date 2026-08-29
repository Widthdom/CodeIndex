---
category: changed
affected:
  - src/CodeIndex/Indexer/Symbols/SymbolExtractor.PatternLoop.cs
  - src/CodeIndex/Indexer/Symbols/SymbolExtractor.CSharpScanner.cs
---

## English

- **Cold C# indexing avoids repeated property probes** — Same-line declaration restarts now share one multiline property candidate, while completed method and structural lines bypass unnecessary property-header regexes without changing extracted symbols.

## 日本語

- **C# の初回 index で property probe の重複を回避** — 同一行の宣言再開で複数行 property candidate を共有し、完結した method・構造行では不要な property-header regex を省いて、抽出結果を変えずに処理量を削減しました。
