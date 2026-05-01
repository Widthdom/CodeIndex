---
category: changed
affected:
  - src/CodeIndex/Indexer/SymbolExtractor.cs
  - tests/CodeIndex.Tests/SymbolExtractorTests.cs
---

## English

- Improved Java symbol search coverage in `symbols`/`definition` by adding package extraction, broader qualified/generic return-type matching, and annotation-friendly matching for methods and constants.
- Expanded parity checks so Java-focused extraction improvements are validated with regression tests, which also helps Kotlin/C# cross-language consistency efforts.

## 日本語

- `symbols` / `definition` の Java シンボル検索カバレッジを強化し、`package` 抽出、修飾名/ジェネリック戻り値型の対応拡張、メソッド・定数のアノテーション併用形を取得できるようにしました。
- Java 抽出強化に対する回帰テストを追加し、Kotlin/C# への横展開時に整合性を保ちやすくしました。
