---
category: changed
affected:
  - src/CodeIndex/Indexer/Symbols/SymbolExtractor.cs
  - src/CodeIndex/Indexer/Symbols/SymbolExtractor.CSharpTypeBodyScope.cs
  - src/CodeIndex/Indexer/Symbols/SymbolExtractor.JavaScriptTypeScriptSupport.cs
---

## English

- **Symbol prechecks coalesce repeated line scans** - C# type-body detection and JavaScript/TypeScript private-scope detection now check their marker sets in one pass over large files.

## 日本語

- **symbol precheck が重複する line scan をまとめるようになりました** - C# type-body 検出と JavaScript/TypeScript private-scope 検出は、長いファイル上の marker 集合確認を1回の走査で行います。
