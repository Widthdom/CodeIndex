---
category: changed
affected:
  - src/CodeIndex/Database/DbWriter.cs
  - src/CodeIndex/Database/DbWriter.CSharpContracts.cs
  - src/CodeIndex/Indexer/CSharpStaticInterfacePrepass.cs
  - tests/CodeIndex.Tests/PreparedCommandCacheTests.cs
  - TESTING_GUIDE.md
---

## English

- **C# static-interface workspace prepasses now read persisted contract rows once** — full scans, scoped updates, and MCP indexing derive both the reusable-symbol snapshot and the pending-path contract-presence bit from one prepared SQLite reader. Existing public presence queries remain available, while the indexing hot path avoids a second workspace-wide contract scan and preserves removed or changed pending-contract behavior.

## 日本語

- **C# static-interface workspace prepass が永続化済み contract row を1回だけ読むようになりました** — full scan、scoped update、MCP indexing は、再利用する symbol snapshot と pending path の contract 存在 flag を1つの prepared SQLite reader から導出します。既存の public presence query は維持しつつ、indexing hot path の workspace 全体 contract scan を2回から1回へ減らし、削除・変更対象に残る旧 contract の判定互換性も維持します。
