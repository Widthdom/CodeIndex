---
category: changed
issues: []
affected:
  - src/CodeIndex/Indexer/SymbolExtractor.cs
  - tests/CodeIndex.Tests/SymbolExtractorTests.cs
---

## English

- **XAML symbol search now captures `x:Class` and `x:Name` in XML files** — `.xaml`/`.axaml` content indexed as XML now emits class/property symbols for `x:Class` and `x:Name`, improving `symbols`, `definition`, and related searches in C#/XAML projects.

## 日本語

- **XML として扱う XAML のシンボル検索で `x:Class` と `x:Name` を抽出するようになりました** — `.xaml`/`.axaml` を XML としてインデックスした際に `x:Class` / `x:Name` から class/property シンボルを生成し、C#/XAML プロジェクトで `symbols` / `definition` などの検索精度を向上させます。
