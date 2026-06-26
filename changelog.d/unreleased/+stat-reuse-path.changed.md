---
category: changed
affected:
  - src/CodeIndex/Cli/IndexCommandRunner.cs
  - src/CodeIndex/Cli/IndexCommandRunner.FullScan.cs
  - src/CodeIndex/Cli/IndexCommandRunner.Update.cs
---

## English

- **Stat-based unchanged checks reuse normalized paths** — full-scan, update, and C# prepass skip checks now pass the already-normalized index path into the reuse helper instead of recomputing a relative path per file.

## 日本語

- **stat ベースの未変更判定が正規化済み path を再利用するようになりました** — full scan、update、C# prepass の skip 判定は、ファイルごとに relative path を再計算せず、既に正規化済みの index path を reuse helper に渡します。
