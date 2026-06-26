---
category: changed
affected:
  - src/CodeIndex/Database/DbWriter.cs
---

## English

- **Batch insert SQL builders are pre-sized** — chunk, symbol, reference, and reference-line inserts now initialize SQL builders from the known batch size, reducing string-buffer growth while indexing files with large extracted outputs.

## 日本語

- **batch insert SQL builder を事前サイズ指定します** — chunk、symbol、reference、reference-line の insert は既知の batch size から SQL builder を初期化し、大きな抽出結果を持つファイルの indexing 中の string buffer 拡張を減らします。
