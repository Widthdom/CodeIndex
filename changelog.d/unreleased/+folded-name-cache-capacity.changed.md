---
category: changed
affected:
  - src/CodeIndex/Database/DbWriter.cs
---

## English

- **Symbol and reference folded-name caches are pre-sized** — indexing now sizes per-batch folded-name caches from the number of symbols or references being written, reducing rehashing while storing large extracted graphs.

## 日本語

- **symbol/reference の folded-name cache を事前サイズ指定します** — indexing は書き込み対象の symbol / reference 数から batch 単位の folded-name cache をサイズ指定し、大きな抽出 graph の保存中の rehash を減らします。
