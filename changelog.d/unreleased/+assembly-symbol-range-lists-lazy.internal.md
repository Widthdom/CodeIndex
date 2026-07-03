---
category: internal
affected: Indexer
---

## English

- Deferred Assembly symbol and range-list allocation until declarations are actually emitted, avoiding empty function and section lists on lightweight assembly files.

## 日本語

- Assembly の symbol list と range 用リストを宣言が実際に出るまで遅延確保し、軽量な assembly ファイルで空の function / section list を作らないようにしました。
