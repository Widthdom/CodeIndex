---
category: changed
affected:
  - src/CodeIndex/Database/DbWriter.cs
---

## English

- **Checksum-based unchanged-file reuse now stops extraction-cap probes at the configured limit** — large symbol and reference sets no longer need to be counted in full before an unchanged file can be reused or rejected.

## 日本語

- **checksum ベースの未変更ファイル再利用で抽出上限の確認を設定値までに制限しました** — 未変更ファイルを再利用または除外する前に、大量の symbol / reference を全件集計する必要がなくなりました。
