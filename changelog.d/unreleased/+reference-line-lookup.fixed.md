---
category: fixed
affected:
  - src/CodeIndex/Database/DbWriter.cs
---

## English

- **Reduced first-time reference indexing work.** Reference-line ID lookups now query only the current insert batch's exact `(file, line, context)` keys instead of rereading every reference line for the file on each batch.

## 日本語

- **初回の reference indexing 処理量を削減しました。** reference line ID の取得時に、各 batch でファイル全体の reference line を読み直さず、現在の insert batch の `(file, line, context)` だけを正確に取得します。
