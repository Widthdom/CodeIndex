---
category: changed
affected:
  - src/CodeIndex/Cli/IndexCommandRunner.FullScan.cs
  - src/CodeIndex/Cli/IndexCommandRunner.Update.cs
---

## English

- **Avoided redundant delete existence probes during indexing** - full-scan cleanup and scoped update deletes now rely on `DeleteFileByPath` row counts instead of issuing a separate `HasFileAtPath` query immediately before the delete.

## 日本語

- **indexing 中の冗長な delete 前 existence probe を削減しました** - full-scan cleanup と scoped update の delete は、削除直前の `HasFileAtPath` query ではなく `DeleteFileByPath` の row count を使うようになりました。
