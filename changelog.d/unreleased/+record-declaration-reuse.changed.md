---
category: changed
affected:
  - src/CodeIndex/Indexer/Symbols/SymbolExtractor.RecordComponents.cs
  - src/CodeIndex/Indexer/Symbols/SymbolExtractor.Java.cs
  - tests/CodeIndex.Tests/PerformanceTests.cs
  - TESTING_GUIDE.md
  - DEVELOPER_GUIDE.md
---

## English

- **C#, Java, and Kotlin record components are extracted with reusable declaration state** — dense record and primary-constructor files no longer construct bounded regex engines for every declaration, and multiline headers are accumulated in one contiguous buffer without materializing every growing prefix. Declaration-name matching and component semantics remain language-accurate while first-index extraction allocation and CPU work stay bounded.

## 日本語

- **C#、Java、Kotlinのrecord component抽出が宣言状態を再利用するようになりました** — recordやprimary constructorが密なfileで宣言ごとにbounded regex engineを構築せず、複数行headerも拡大途中のprefixを毎回文字列化せず1つの連続bufferへ蓄積します。宣言名の照合とcomponent semanticsを言語ごとに維持しながら、初回index抽出のallocationとCPU処理を抑えます。
