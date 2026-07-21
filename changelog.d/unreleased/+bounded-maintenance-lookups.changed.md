---
category: changed
affected:
  - src/CodeIndex/Database/DbContext.cs
  - src/CodeIndex/Database/DbWriter.FileCleanup.cs
  - tests/CodeIndex.Tests/DatabaseTests.cs
  - TESTING_GUIDE.md
---

## English

- **Large-repository maintenance lookups now stay index-bound** — Index initialization adds checksum and per-file issue lookup indexes, while extension-changing rename cleanup replaces its parameterized `LIKE` scan with an indexed ordinal range without changing literal wildcard, extensionless, or exact-stem behavior.

## 日本語

- **巨大リポジトリの保守 lookup が index 内で完結するようになりました** — index 初期化で checksum と file 単位 issue の lookup index を追加し、拡張子変更 rename の cleanup は parameterized `LIKE` scan を indexed ordinal range に置き換えつつ、wildcard 文字、拡張子なし、stem 完全一致の挙動を維持します。
