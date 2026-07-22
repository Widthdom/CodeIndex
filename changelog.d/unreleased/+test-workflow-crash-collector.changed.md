---
category: changed
affected:
  - .github/scripts/run-dotnet-tests.ps1
  - tests/CodeIndex.Tests/CiWorkflowTests.cs
  - TESTING_GUIDE.md
---

## English

- **CI retries avoid duplicate crash-collector overhead** — VSTest crash collection stays on the initial attempt so transient crashes retain dumps, while the single classification retry reuses that evidence and keeps only hang collection.
- **CI failure telemetry skips redundant project evaluation** — Retry filtering and TRX summaries now launch the already-built telemetry assembly directly instead of invoking `dotnet run`.
- **Pre-test CI failures skip empty artifact actions** — Test-result, dump, and coverage uploads now require the test step to have started, avoiding no-op upload setup after restore or build failures.

## 日本語

- **CI retryで重複するcrash collector overheadを省きました** — 一過性crashもdumpを残すため初回attemptのVSTest crash収集は維持し、1回だけのclassification retryはそのevidenceを再利用してhang収集だけを保ちます。
- **CI failure telemetryで重複するproject評価を省きました** — retry filterとTRX summaryは`dotnet run`ではなくbuild済みtelemetry assemblyを直接起動します。
- **test開始前のCI failureでは空のartifact actionを省きます** — test result、dump、coverageのuploadはtest step開始済みの場合だけ実行し、restore/build failure後のno-op setupを避けます。
