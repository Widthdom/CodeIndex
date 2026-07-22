---
category: changed
affected:
  - .github/scripts/run-dotnet-tests.ps1
  - tests/CodeIndex.Tests/CiWorkflowTests.cs
  - TESTING_GUIDE.md
---

## English

- **Clean CI test runs no longer pay crash-collector overhead** — VSTest crash collection is enabled only on the failed-run retry, while reproducible failures still run under the collector and retain diagnostic artifacts.
- **CI failure telemetry skips redundant project evaluation** — Retry filtering and TRX summaries now launch the already-built telemetry assembly directly instead of invoking `dotnet run`.
- **Pre-test CI failures skip empty artifact actions** — Test-result, dump, and coverage uploads now require the test step to have started, avoiding no-op upload setup after restore or build failures.

## 日本語

- **cleanなCIテスト実行でcrash collectorのoverheadを負わなくなりました** — VSTestのcrash収集は失敗後retryだけで有効にし、再現する失敗は引き続きcollector有効下で実行して診断artifactを保持します。
- **CI failure telemetryで重複するproject評価を省きました** — retry filterとTRX summaryは`dotnet run`ではなくbuild済みtelemetry assemblyを直接起動します。
- **test開始前のCI failureでは空のartifact actionを省きます** — test result、dump、coverageのuploadはtest step開始済みの場合だけ実行し、restore/build failure後のno-op setupを避けます。
