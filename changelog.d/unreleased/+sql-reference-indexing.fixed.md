---
category: fixed
affected:
  - src/CodeIndex/Models/SymbolKindCatalog.cs
  - src/CodeIndex/Indexer/References/Languages/SqlReferenceExtractor.cs
  - src/CodeIndex/Indexer/References/Languages/SqlReferenceExtractor.StatementBoundaries.cs
---

## English

- Fixed SQL indexing failures when saving `@@ROWCOUNT`, `@@IDENTITY`, and other `system_variable` references. Existing database kind constraints upgrade automatically on the next index run while preserving stored rows.
- Avoid repeatedly rescanning preceding seed statements at `GO` boundaries and between independent INSERT/UPDATE/DELETE statements without semicolons. Multiline SQL and compound-statement context remain intact.

## 日本語

- `@@ROWCOUNT`、`@@IDENTITY` などの `system_variable` 参照を保存する際にSQLインデックスが失敗する問題を修正しました。既存DBの種別制約は次回のインデックス実行時に保存済みの行を維持して自動更新します。
- `GO` 区切りやセミコロンのない独立した INSERT／UPDATE／DELETE 文で、過去の初期データ文を繰り返し再解析する問題を修正しました。複数行SQLと複合文の文脈は維持します。
