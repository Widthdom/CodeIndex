---
category: changed
affected:
  - src/CodeIndex/Database/DbWriter.ReferenceSql.cs
  - src/CodeIndex/Database/DbWriter.AuthoritativeFreshBulkInsert.cs
  - src/CodeIndex/Database/DbWriter.References.cs
  - src/CodeIndex/Database/DbWriter.ReferenceGraphRefreshScope.cs
  - tests/CodeIndex.Tests/AuthoritativeFreshRawBulkInsertTests.cs
  - tests/CodeIndex.Tests/FreshReferenceResolutionTests.cs
  - tests/CodeIndex.Tests/ReferencePersistenceBindingTests.cs
  - tests/CodeIndex.Tests/DatabaseTests.cs
---

## English

- **Initial full indexing does less reference flag work across languages** — fresh reference inserts encode the two provisional zero flags directly in SQL, reducing parameter binding and fitting more rows into the same native batch limit. Full mutual-recursion refreshes skip known-zero rows that cannot have a reverse edge while preserving resolved, unresolved, legacy-name, and stale-flag repair behavior.

## 日本語

- **各言語の初回フルインデックスで参照フラグの処理を削減しました** — 新規参照の暫定的な 2 つのゼロフラグを SQL に直接記述し、パラメーターのバインドを減らして同じネイティブバッチ上限に収まる行数を増やします。全体の相互再帰更新では、逆向きの参照が成立しない既知のゼロ行を省略し、解決済み・未解決・旧形式の名前照合と古いフラグの修復動作を維持します。
