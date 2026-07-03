---
category: internal
affected: Indexer
---

## English
- Skip SQL CTE content joining and regex scans when files contain `WITH` but no `AS` token text.

## 日本語
- SQL ファイルに `WITH` があっても `AS` 文字列がない場合、CTE 用の内容結合と regex 走査を省くようにしました。
