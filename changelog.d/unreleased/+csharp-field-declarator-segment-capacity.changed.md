---
category: changed
affected:
  - src/CodeIndex/Indexer/Symbols/SymbolExtractor.CSharpFieldDeclarators.cs
---

## English

- **C# field declarator expansion reduces segment overhead** — comma-split segment lists now pre-size from declaration length and trailing empty checks avoid trim allocation.

## 日本語

- **C# field declarator 展開の segment overhead を削減しました** — comma split の segment list は宣言長から初期容量を決め、trailing empty 判定では trim allocation を避けます。
