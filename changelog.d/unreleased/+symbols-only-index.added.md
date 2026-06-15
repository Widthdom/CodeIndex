---
category: added
affected:
  - src/CodeIndex/Cli/IndexCommandRunner.FullScan.cs
  - src/CodeIndex/Cli/IndexCommandRunner.Parse.cs
  - src/CodeIndex/Cli/IndexCommandRunner.Validation.cs
  - src/CodeIndex/Cli/ConsoleUi.cs
  - src/CodeIndex/Cli/CliFlagSchema.cs
  - src/CodeIndex/Database/DbWriter.cs
  - README.md
  - USER_GUIDE.md
---

## English

- **Added `cdidx index --symbols-only` for fast first-pass indexing** — full scans can now build chunks, symbols, and issues while skipping reference graph extraction, so search, definition, symbols, and map workflows become usable sooner; graph commands stay degraded until a normal index run.

## 日本語

- **高速な初回 index 向けに `cdidx index --symbols-only` を追加しました** — フルスキャンで chunks、symbols、issues だけを作り、reference graph 抽出を省けるため、search、definition、symbols、map をより早く使い始められます。graph 系コマンドは通常の index 実行まで degraded のままです。
