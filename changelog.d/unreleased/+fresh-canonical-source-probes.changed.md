---
category: changed
affected:
  - src/CodeIndex/Database/DbWriter.AuthoritativeFreshReferenceSourceLookup.cs
  - src/CodeIndex/Database/DbWriter.AuthoritativeFreshBulkInsert.cs
  - tests/CodeIndex.Tests/AuthoritativeFreshRawBulkInsertTests.cs
---

## English

- **Initial indexing skips unused reference-source name probes** — the fresh bulk writer uses a single ranked canonical-name lookup when the materialized files have no display aliases or legacy NULL keys. All indexed languages share the optimization while alternate names, source identities, transaction rollback and cancellation retain their existing behavior.

## 日本語

- **初回インデックスで未使用の参照元名の検索を省略** — 初回一括writerは、一時表のファイルにdisplay aliasや旧形式のNULL keyがない場合、canonical名の順位付き検索だけを使います。全対応言語で共通の最適化で、別名・参照元の識別・transaction rollback・取消の挙動を維持します。
