---
category: changed
affected:
  - src/CodeIndex/Database/DbWriter.AuthoritativeFreshBulkInsert.cs
---

## English

- Reduce temporary UTF-8 allocations for long chunks, signatures, and reference contexts during initial CLI full indexing across all languages, preserving stored text and rollback behavior.

## 日本語

- 全言語共通の CLI 初回フルインデックスで、長いチャンク・シグネチャ・参照コンテキストの UTF-8 一時割り当てを削減しました。保存される文字列とロールバックの挙動は維持します。
