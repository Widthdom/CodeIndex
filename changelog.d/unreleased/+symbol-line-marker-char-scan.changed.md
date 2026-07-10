---
category: changed
affected:
  - src/CodeIndex/Indexer/Symbols/SymbolExtractor.cs
  - src/CodeIndex/Indexer/Symbols/SymbolExtractor.CSharpTypeBodyScope.cs
  - src/CodeIndex/Indexer/Symbols/SymbolExtractor.GraphQL.cs
  - src/CodeIndex/Indexer/Symbols/SymbolExtractor.JavaScriptTypeScriptSupport.cs
  - src/CodeIndex/Indexer/Symbols/SymbolExtractor.Markup.cs
---

## English

- **Single-character symbol prechecks now use char scans** - HTML, GraphQL, C#, Dart, JavaScript, and TypeScript symbol extraction now check one-character markers without routing through string comparison scans.

## 日本語

- **1文字の symbol precheck が char scan を使うようになりました** - HTML、GraphQL、C#、Dart、JavaScript、TypeScript の symbol extraction は、1文字 marker の確認で string comparison scan を経由しないようになりました。
