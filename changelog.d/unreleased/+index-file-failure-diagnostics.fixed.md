---
category: fixed
affected:
  - src/CodeIndex/Cli/IndexCommandRunner.FullScan.cs
  - src/CodeIndex/Indexer/Symbols/SymbolExtractionWorker.cs
  - src/CodeIndex/Diagnostics/DiagnosticRedactor.cs
  - DEVELOPER_GUIDE.md
---

## English

- **Per-file indexing failures now retain actionable diagnostics** — full-scan and update failures record the active phase and a bounded safe detail, exceptions retain their extraction stack across the parallel full-scan boundary, isolated symbol-worker failures report a redacted origin frame, and long stack frames preserve their source-line suffix instead of losing it behind a truncated method signature.

## 日本語

- **ファイル単位の index failure で原因調査に必要な診断情報を保持するようになりました** — full scan / update の failure は active phase と上限付きの安全な detail を記録し、parallel full-scan boundary をまたぐ exception も extraction stack を保持します。隔離 symbol worker の failure は redaction 済み origin frame を報告し、長い stack frame でも method signature の切り詰めによって source-line suffix が失われません。
