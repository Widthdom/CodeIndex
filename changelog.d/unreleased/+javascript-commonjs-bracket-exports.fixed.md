---
category: fixed
affected:
  - src/CodeIndex/Database/DbSymbolReader.cs
  - tests/CodeIndex.Tests/DbReaderTests.cs
---

## English

- **JavaScript CommonJS search now resolves bracket-notation exports to leaf names** — exact symbol and reference searches now normalize `module.exports["foo"]` and `exports['bar']` to the exported leaf name, so JavaScript CommonJS queries behave consistently across dot and bracket syntax.

## 日本語

- **JavaScript の CommonJS 検索でブラケット記法の export を leaf 名へ解決するようになりました** — exact な symbol / reference 検索が `module.exports["foo"]` と `exports['bar']` を export 先の leaf 名へ正規化するため、JavaScript CommonJS の query がドット記法とブラケット記法の間で一貫して動作します。
