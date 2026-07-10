---
category: changed
affected:
  - src/CodeIndex/Indexer/Symbols/SymbolExtractor.CSharpScanner.cs
  - src/CodeIndex/Indexer/Symbols/SymbolExtractor.Sql.cs
  - tests/CodeIndex.Tests/SymbolExtractorCSharpTests.cs
  - tests/CodeIndex.Tests/SymbolExtractorSqlTests.cs
---

## English

- **C# and SQL extraction avoid no-op scan buffers** - plain C# lexer lines and SQL body-scan lines now reuse their source text, C# generic column maps are allocated only when whitespace actually changes, and C# member confirmation avoids redundant property/method regex passes. Large source files therefore spend less CPU and allocation on unchanged scanner text.

## 日本語

- **C# と SQL の extraction で変更を伴わない scan buffer を省略するようにしました** - plain な C# lexer 行と SQL body-scan 行は元の text を再利用し、C# generic column map は空白が実際に変化するときだけ確保し、C# member 確認では property / method regex の重複実行を避けます。これにより大きな source file で未変更の scanner text に使う CPU と allocation を削減します。
