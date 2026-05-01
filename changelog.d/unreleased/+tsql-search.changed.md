---
category: changed
affected:
  - src/CodeIndex/Indexer/ReferenceExtractor.cs
  - src/CodeIndex/Indexer/SymbolExtractor.cs
  - tests/CodeIndex.Tests/ReferenceExtractorTests.cs
  - tests/CodeIndex.Tests/SymbolExtractorTests.cs
---

## English

- **Improved T-SQL stored-procedure symbol/reference extraction** — SQL `EXEC`/`EXECUTE` now handles escaped bracket identifiers (for example `[proc]]name]`) and numbered procedure suffixes (for example `sp_helptext;1`) more reliably in reference extraction, and SQL symbol parsing now accepts escaped bracket segments in qualified identifiers.

## 日本語

- **T-SQL のストアドプロシージャ向けシンボル/参照抽出を改善** — SQL `EXEC`/`EXECUTE` の参照抽出で、エスケープされた角括弧識別子（例: `[proc]]name]`）と番号付きプロシージャ接尾辞（例: `sp_helptext;1`）をより正確に扱えるようにし、SQL シンボル抽出でも修飾識別子セグメント内の角括弧エスケープを受理するようにしました。
