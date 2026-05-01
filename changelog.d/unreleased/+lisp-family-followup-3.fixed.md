---
category: fixed
affected:
  - src/CodeIndex/Indexer/SymbolExtractor.cs
  - tests/CodeIndex.Tests/SymbolExtractorTests.cs
  - README.md
---

## English

- **Racket follow-up coverage now includes `define-syntaxes`** — following up on PR #1196, `cdidx` now extracts Racket `define-syntaxes` forms in addition to the existing `module`, `define`, `define-syntax-rule`, `define-for-syntax`, `struct`, `require`, and `provide` coverage, so macro-heavy Racket projects get more useful symbol search results from common syntax-definition patterns.

## 日本語

- **Racket の follow-up カバレッジが `define-syntaxes` まで拾うようになりました** — PR #1196 の follow-up として、`cdidx` は既存の `module`、`define`、`define-syntax-rule`、`define-for-syntax`、`struct`、`require`、`provide` に加えて Racket の `define-syntaxes` も抽出するため、マクロ中心の Racket プロジェクトでよく使う構文定義パターンから検索結果を得やすくなります。
