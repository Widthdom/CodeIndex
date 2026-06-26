---
category: changed
affected:
  - src/CodeIndex/Cli/IndexCommandRunner.FullScan.cs
---

## English

- **No-op full scans reuse current C# metadata-target trust** — full-repository `cdidx index` runs now skip the C# metadata-target resolver when no C# rows changed and the existing metadata-target contract is already current.

## 日本語

- **変更なし full scan が最新の C# metadata-target trust を再利用するようになりました** — full-repository の `cdidx index` は、C# 行に変更がなく既存の metadata-target 契約が最新の場合、C# metadata-target resolver をスキップします。
