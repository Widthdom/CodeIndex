---
category: internal
affected: Indexer
---

## English

- Skipped Dart class-body scope array and stack construction for files without the `class` keyword, treating the scope as all-false for bare-const constructor gating.

## 日本語

- `class` キーワードを含まない Dart ファイルでは class-body scope の配列と stack 構築を省略し、bare const constructor の gate では全 false scope として扱うようにしました。
