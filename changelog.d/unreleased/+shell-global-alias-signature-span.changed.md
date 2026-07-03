---
title: Shell global alias signature checks avoid trim strings
category: changed
---

## English

- **Shell global alias signature checks avoid trim strings** — Shell reference extraction now scans alias signatures with spans before running the global-alias regex, avoiding trimmed signature allocations for non-global aliases during large shell indexes.

## 日本語

- **Shellのglobal alias signature判定でtrim文字列を避けるようになりました** — Shell参照抽出はglobal alias正規表現を実行する前にalias signatureをspanで走査し、大規模なshellインデックス作成時にglobalでないaliasのtrim済みsignature割当を避けます。
