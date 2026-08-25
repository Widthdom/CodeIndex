---
category: changed
affected:
  - src/CodeIndex/Database/DbContext.cs
  - src/CodeIndex/Database/DbWriter.AuthoritativeFreshBulkInsert.cs
  - tests/CodeIndex.Tests/AuthoritativeFreshRawBulkInsertTests.cs
  - tests/CodeIndex.Tests/IndexCommandRunnerInitialFullIndexPerformanceTests.cs
  - TESTING_GUIDE.md
  - DEVELOPER_GUIDE.md
---

## English

- **Fresh full indexes coalesce file-list generation invalidation** — the authoritative empty-database transaction suspends per-file resource-generation triggers during bulk persistence, restores them after native statements finalize, and advances the generation once when files were persisted. Empty repositories leave the generation unchanged. Rollback restores the rows, trigger schema, and generation together, while incremental, rebuild, MCP, and ordinary writer mutations retain per-change invalidation.

## 日本語

- **初回full indexでfile list generationの無効化をまとめるようになりました** — authoritativeな空database transactionはbulk永続化中のfile単位resource-generation triggerを停止し、native statementのfinalize後に復元してfileを永続化した場合だけgenerationを1回進めます。空repositoryではgenerationを変更しません。rollback時はrow、trigger schema、generationを一括で元へ戻し、incremental、rebuild、MCP、通常writerのmutationは変更ごとの無効化を維持します。
