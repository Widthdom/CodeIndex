---
category: changed
---

## English

- Reduce existing-file reindex cleanup from six prepared-command executions to two by reusing the UPSERT-returned file ID and batching child-table deletes while preserving FTS and rollback semantics.

## 日本語

- UPSERTが返すfile IDを再利用し、子テーブル削除を一括実行することで、既存ファイル再インデックス時のprepared command実行を6回から2回へ削減しつつ、FTSとrollbackの意味論を維持しました。
