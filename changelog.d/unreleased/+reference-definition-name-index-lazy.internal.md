---
category: internal
affected: Indexer
---

## English

- Deferred per-line definition-name index allocation during reference extraction until a definition name is actually present on the prepared line, reducing empty dictionaries across large non-SQL files.

## 日本語

- 参照抽出時の行単位 definition-name index を、準備済み行に定義名が実際に存在するまで遅延確保するようにし、大きな非 SQL ファイルで空 Dictionary の生成を減らしました。
