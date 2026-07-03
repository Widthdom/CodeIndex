---
title: Reuse empty PowerShell splat key lists
category: changed
affected:
  - src/CodeIndex/Indexer/References/Languages/PowerShellReferenceExtractor.cs
---

## English

- **PowerShell splat extraction now reuses empty key lists** — malformed or keyless splat hashtables no longer allocate short-lived empty lists while indexing large PowerShell-heavy repositories.

## 日本語

- **PowerShell splat 抽出が空キー一覧を再利用するようになりました** — malformed またはキーの無い splat hashtable で、大規模な PowerShell 多めのリポジトリをインデックス化するときの短命な空リスト割り当てを避けます。
