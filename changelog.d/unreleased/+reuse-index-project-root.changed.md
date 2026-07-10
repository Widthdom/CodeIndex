---
category: changed
affected:
  - src/CodeIndex/Cli/IndexCommandRunner.FullScan.cs
  - src/CodeIndex/Cli/IndexCommandRunner.Update.cs
---

## English

- **Indexing reuses the already-normalized project root during symbol extraction** - Full scans and update mode avoid recomputing `Path.GetFullPath` for every indexed file before symbol extraction.

## 日本語

- **symbol extraction で正規化済み project root を再利用するようになりました** - full scan と update mode で、各ファイルの symbol extraction 前に `Path.GetFullPath` を繰り返し計算しないようにしました。
