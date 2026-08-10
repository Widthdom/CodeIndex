---
category: fixed
affected:
  - src/CodeIndex/Database/DbWriter.References.cs
  - src/CodeIndex/Database/DbWriter.ReferenceGraphRefreshScope.cs
  - DEVELOPER_GUIDE.md
  - TESTING_GUIDE.md
---

## English

- **Fresh large-codebase indexes finalize mutual-recursion edges without duplicate reverse lookups** — reference graph finalization now materializes each candidate edge's desired recursion flag once, avoiding a costly second set of random B-tree probes when only a small number of flags change.
- **Fresh and rebuilt indexes skip unused incremental graph bookkeeping** — once a full reference-graph refresh is known, symbol and reference batches no longer populate dirty-scope tables that the full plan never reads, removing repeated set construction across all indexed languages.

## 日本語

- **巨大コードベースの新規インデックスで相互再帰 edge の reverse lookup を重複実行しないようにしました** — reference graph の確定時に各候補 edge の望ましい recursion flag を一度だけ materialize し、変更対象の flag が少数の場合に発生していた高コストな2回目のランダム B-tree probe を避けます。
- **新規作成および rebuild 時に未使用の差分 graph bookkeeping を省くようにしました** — reference graph の full refresh が確定した後は、その plan が参照しない dirty scope table を symbol / reference batch ごとに投入せず、全インデックス対象言語にまたがる反復的な set 構築を取り除きます。
