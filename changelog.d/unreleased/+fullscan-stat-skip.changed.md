---
category: changed
affected:
  - src/CodeIndex/Cli/IndexCommandRunner.FullScan.cs
  - src/CodeIndex/Indexer/CSharpStaticInterfacePrepass.cs
---

## English

- **Full scans skip unchanged files before content loading** — `cdidx index <path>` now reuses stat-matched existing rows before queueing extraction work, and the C# static-interface prepass reuses existing workspace symbols for unchanged C# files instead of reading them again.

## 日本語

- **full scan が content load 前に未変更ファイルを skip するようになりました** — `cdidx index <path>` は抽出キュー投入前に stat が一致する既存行を再利用し、C# static-interface prepass も未変更の C# ファイルを再読込せず既存 workspace symbols を使うようになりました。
