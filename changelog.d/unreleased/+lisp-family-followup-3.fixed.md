---
category: fixed
affected:
  - src/CodeIndex/Indexer/SymbolExtractor.cs
  - tests/CodeIndex.Tests/SymbolExtractorTests.cs
  - README.md
---

## English

- **Racket follow-up coverage now includes `define-syntaxes`, and Common Lisp package transitions now include `import` / `shadowing-import`** — following up on PR #1196, `cdidx` now extracts Racket `define-syntaxes` forms in addition to the existing `module`, `define`, `define-syntax-rule`, `define-for-syntax`, `struct`, `require`, and `provide` coverage, and it also recognizes Common Lisp `import` and `shadowing-import` package-transition forms alongside `in-package` and `use-package`, so macro-heavy Racket projects and package-oriented Common Lisp projects get more useful symbol search results from common namespace patterns.

## 日本語

- **Racket の follow-up カバレッジが `define-syntaxes` まで拾うようになり、Common Lisp のパッケージ遷移も広がりました** — PR #1196 の follow-up として、`cdidx` は既存の `module`、`define`、`define-syntax-rule`、`define-for-syntax`、`struct`、`require`、`provide` に加えて Racket の `define-syntaxes` も抽出し、さらに Common Lisp では `in-package` や `use-package` に加えて `import` / `shadowing-import` も認識するため、マクロ中心の Racket プロジェクトとパッケージ志向の Common Lisp プロジェクトの両方で検索結果を得やすくなります。
