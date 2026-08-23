---
category: changed
affected:
  - src/CodeIndex/Database/DbWriter.ReferenceSql.cs
  - src/CodeIndex/Database/DbWriter.References.cs
  - tests/CodeIndex.Tests/DatabaseTests.cs
  - tests/CodeIndex.Tests/ReferencePersistenceBindingTests.cs
  - DEVELOPER_GUIDE.md
---

## English

- **Reference-line materialization now returns compact input ordinals instead of context tuples** — fresh inserts and replacement lookups project only each line ID and its batch-local ordinal, release the deduplication dictionary before SQLite work, and fail closed on missing, duplicate, or invalid results. Large Unicode contexts no longer cross back from SQLite or get rehashed in managed code before multilingual references are bound.

## 日本語

- **reference-line materializationがcontext tupleではなくcompactなinput ordinalを返すようになりました** — fresh insertとreplacement lookupはline IDとbatch-local ordinalだけを返し、SQLite処理前にdedupe辞書を解放し、欠落・重複・不正な結果をfail-closedにします。大きなUnicode contextをSQLiteから戻したり、multi-language referenceのbind前にmanaged codeで再hashしたりしません。
