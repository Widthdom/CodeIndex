---
category: changed
affected:
  - .github/workflows/dotnet.yml
  - .github/scripts/run-dotnet-tests.ps1
  - tests/CodeIndex.Tests/CiWorkflowTests.cs
  - tests/CodeIndex.Tests/TestProjectHelper.cs
  - tests/CodeIndex.Tests/TestProjectHelperTests.cs
  - tests/CodeIndex.Tests/IndexCommandRunnerFullScanTests.cs
  - TESTING_GUIDE.md
---

## English

- **CI failure telemetry skips redundant project evaluation** — Retry filtering and TRX summaries now launch the already-built telemetry assembly directly instead of invoking `dotnet run`.
- **Pre-test CI failures skip empty artifact actions** — Test-result, dump, and coverage uploads now require the test step to have started, avoiding no-op upload setup after restore or build failures.
- **Temporary-project cleanup avoids an eager filesystem walk** — Clean fixtures now attempt recursive deletion directly, while attribute normalization and bounded Windows recovery remain available after a real deletion failure.
- **Hook scheduling coverage uses a compact assembly** — Full-scan scheduling tests now stage the dedicated hook-isolation fixture instead of copying the much larger test assembly into a worker directory.

## 日本語

- **CI failure telemetryで重複するproject評価を省きました** — retry filterとTRX summaryは`dotnet run`ではなくbuild済みtelemetry assemblyを直接起動します。
- **test開始前のCI failureでは空のartifact actionを省きます** — test result、dump、coverageのuploadはtest step開始済みの場合だけ実行し、restore/build failure後のno-op setupを避けます。
- **temporary project cleanupの先行filesystem走査を省きました** — 正常なfixtureは直接recursive deleteを試し、実際に削除が失敗した場合はattribute正規化とWindows向けbounded recoveryを引き続き利用します。
- **hook scheduling coverageで小型assemblyを使います** — full-scan scheduling testは巨大なtest assemblyをworker directoryへcopyせず、専用のhook-isolation fixtureをstageします。
