---
category: changed
affected:
  - src/CodeIndex/Indexer/Symbols/SymbolExtractor.cs
  - src/CodeIndex/Indexer/Symbols/SymbolExtractor.CSharpScanner.cs
  - src/CodeIndex/Indexer/Symbols/SymbolExtractor.Java.cs
  - src/CodeIndex/Indexer/Symbols/SymbolExtractor.VisualBasic.cs
---

## English

- **Reuse a direct enum declaration snapshot across symbol extractors** — C#, Java, and Visual Basic enum member extraction now collect declaration snapshots with one shared loop before sorting, avoiding repeated LINQ iterator pipelines while preserving declaration order ties.

## 日本語

- **enum declaration snapshot を symbol extractor 間で共通化します** — C# / Java / Visual Basic の enum member 抽出で、declaration snapshot を共通の直接ループで収集してから sort し、同順位の宣言順を保ちながら LINQ iterator pipeline を避けます。
