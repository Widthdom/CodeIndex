---
category: changed
affected:
  - .github/workflows/dotnet.yml
  - .github/scripts/run-dotnet-tests.ps1
  - tests/CodeIndex.Tests/CiWorkflowTests.cs
  - tests/CodeIndex.Tests/TestProjectHelper.cs
  - tests/CodeIndex.Tests/TestProjectHelperTests.cs
  - tests/CodeIndex.Tests/IndexCommandRunnerFullScanTests.cs
  - tests/CodeIndex.Tests/QueryCommandRunnerReferencesTests.cs
  - tests/CodeIndex.Tests/InstallScriptTests.cs
  - tests/CodeIndex.Tests/ReleaseWorkflowDockerContractTests.cs
  - TESTING_GUIDE.md
---

## English

- **CI failure telemetry skips redundant project evaluation** — Retry filtering and TRX summaries now launch the already-built telemetry assembly directly instead of invoking `dotnet run`.
- **Pre-test CI failures skip empty artifact actions** — Test-result, dump, and coverage uploads now require the test step to have started, avoiding no-op upload setup after restore or build failures.
- **Temporary-project cleanup avoids an eager filesystem walk** — Clean fixtures now attempt recursive deletion directly, while attribute normalization and bounded Windows recovery remain available after a real deletion failure.
- **SQLite fixture cleanup releases pools only after a failure** — Seeded-file writes and clean database deletion no longer trigger unconditional process-wide pool clearing; Windows retries still release pools when a file is actually locked.
- **Hook scheduling coverage uses a compact assembly** — Full-scan scheduling tests now stage the dedicated hook-isolation fixture instead of copying the much larger test assembly into a worker directory.
- **Unix shell tests skip before Windows fixture setup** — Installer and container-entrypoint cases now skip during discovery on Windows instead of constructing temporary directories and returning from the test body.
- **Production query boundaries reuse indexed workspaces** — Related C# order-by comma and ternary-operator cases now share one production CLI indexing subprocess per syntax family while retaining distinct assertions.

## 日本語

- **CI failure telemetryで重複するproject評価を省きました** — retry filterとTRX summaryは`dotnet run`ではなくbuild済みtelemetry assemblyを直接起動します。
- **test開始前のCI failureでは空のartifact actionを省きます** — test result、dump、coverageのuploadはtest step開始済みの場合だけ実行し、restore/build failure後のno-op setupを避けます。
- **temporary project cleanupの先行filesystem走査を省きました** — 正常なfixtureは直接recursive deleteを試し、実際に削除が失敗した場合はattribute正規化とWindows向けbounded recoveryを引き続き利用します。
- **SQLite fixture cleanupは失敗後だけpoolを解放します** — seed fileのwriteと正常なdatabase削除ではprocess-wide pool clearを行わず、実際にfileがlockされたWindows retryでは引き続きpoolを解放します。
- **hook scheduling coverageで小型assemblyを使います** — full-scan scheduling testは巨大なtest assemblyをworker directoryへcopyせず、専用のhook-isolation fixtureをstageします。
- **Unix shell testはWindowsのfixture setup前にskipします** — installerとcontainer entrypointのcaseはtemporary directoryを作ってtest bodyからreturnせず、Windowsではdiscovery時にskipします。
- **production query boundaryでindexed workspaceを再利用します** — 関連するC# order-by comma / ternary operator caseは個別の構文assertionを保ちながら、構文familyごとにproduction CLI indexing subprocessを1回だけ共有します。
