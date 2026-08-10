---
category: fixed
affected:
  - src/CodeIndex/Database/DbWriter.References.cs
  - DEVELOPER_GUIDE.md
  - TESTING_GUIDE.md
---

## English

- **Fresh large-codebase indexes finalize mutual-recursion edges without duplicate reverse lookups** — reference graph finalization now materializes each candidate edge's desired recursion flag once, avoiding a costly second set of random B-tree probes when only a small number of flags change.

## 日本語

- **巨大コードベースの新規インデックスで相互再帰 edge の reverse lookup を重複実行しないようにしました** — reference graph の確定時に各候補 edge の望ましい recursion flag を一度だけ materialize し、変更対象の flag が少数の場合に発生していた高コストな2回目のランダム B-tree probe を避けます。
