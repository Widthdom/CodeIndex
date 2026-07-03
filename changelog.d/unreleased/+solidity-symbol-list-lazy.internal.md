---
category: internal
affected: Indexer
---

## English

- Deferred Solidity symbol list allocation until the first declaration is emitted, avoiding an empty list for files with no Solidity symbols after masking.

## 日本語

- Solidity の symbol list を最初の宣言を出すまで遅延確保し、mask 後に Solidity symbol がないファイルで空 List を作らないようにしました。
