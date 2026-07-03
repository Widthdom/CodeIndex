---
category: internal
affected: Indexer
---

## English

- Skipped C# switch-expression line scanning for files without the `switch` keyword and delayed the helper's brace stack allocation until a brace is encountered.

## 日本語

- `switch` キーワードを含まない C# ファイルでは switch-expression 行走査を省略し、helper 内の brace stack も brace に到達するまで遅延確保するようにしました。
