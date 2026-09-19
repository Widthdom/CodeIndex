---
category: changed
affected:
  - src/CodeIndex/Database/DbWriter.ReferenceSql.cs
  - tests/CodeIndex.Tests/AuthoritativeFreshRawBulkInsertTests.cs
---

## English

- Initial bulk indexing shares heavily repeated reference-source lookups within each bounded INSERT across all languages, retaining direct probes for unique or sparse sources and absent container names. It preserves every reference row, input order, folded-name overrides and transactional rollback while reducing repeated searches through same-name declarations.

## 日本語

- 全言語共通の初回一括インデックスで、上限付き INSERT 内で多数重複する参照元検索を共有するようにしました。重複が少ない場合やコンテナ名がない場合は直接検索を使い、全参照行・入力順・folded 名の override・トランザクションの rollback を維持しつつ、同名宣言への検索の繰り返しを減らします。
