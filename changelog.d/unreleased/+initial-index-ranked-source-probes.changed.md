---
category: changed
affected:
  - src/CodeIndex/Database/DbWriter.AuthoritativeFreshReferenceSourceLookup.cs
  - tests/CodeIndex.Tests/AuthoritativeFreshRawBulkInsertTests.cs
---

## English

- **Reduce repeated source-reference sorting during initial full indexing** — temporary name indexes now select the best containing declaration before comparing at most three candidates. All languages retain the same source identities, alias and legacy-name behavior, and transactional recovery.

## 日本語

- **初回フルインデックスでの参照元候補の反復ソートを削減** — 一時的な名前 index で最適な包含宣言を先に選び、最後の比較を最大3候補に制限します。全言語で参照元の同一性、別名・旧形式の名前照合、トランザクションの復旧動作を維持します。
