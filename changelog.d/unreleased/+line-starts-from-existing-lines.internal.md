---
category: internal
affected: Indexer
---

## English

- Reused existing GraphQL and SQL line arrays to build regex offset line-start tables, avoiding an extra scan over joined content during member and generated-column symbol extraction.

## 日本語

- GraphQL と SQL の既存行配列から regex offset 用の line-start table を作るようにし、member / generated column の symbol 抽出で join 済み content を追加走査しないようにしました。
