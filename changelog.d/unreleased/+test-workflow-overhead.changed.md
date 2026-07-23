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
  - tests/CodeIndex.Tests/McpServerToolsCallTests.cs
  - tests/CodeIndex.Tests/ReleaseWorkflowDockerContractTests.cs
  - tests/CodeIndex.Tests/SymbolExtractorCSharpTests.cs
  - tests/CodeIndex.Tests/SymbolExtractorTests.cs
  - TESTING_GUIDE.md
---

## English

- **CI failure telemetry skips redundant project evaluation** — Retry filtering and TRX summaries now launch the already-built telemetry assembly directly instead of invoking `dotnet run`.
- **Pre-test CI failures skip empty artifact actions** — Test-result, dump, and coverage uploads now require the test step to have started, avoiding no-op upload setup after restore or build failures.
- **Temporary-project cleanup avoids an eager filesystem walk** — Clean fixtures now attempt recursive deletion directly, while attribute normalization and bounded Windows recovery remain available after a real deletion failure.
- **SQLite fixture cleanup releases pools only after a failure** — Seeded-file writes and clean database deletion no longer trigger unconditional process-wide pool clearing; Windows retries still release pools when a file is actually locked.
- **Hook scheduling coverage uses a compact assembly** — Full-scan scheduling tests now stage the dedicated hook-isolation fixture instead of copying the much larger test assembly into a worker directory.
- **Unix shell tests skip before Windows fixture setup** — Installer and container-entrypoint cases now skip during discovery on Windows instead of constructing temporary directories and returning from the test body.
- **Production query boundaries reuse indexed workspaces** — Related C# order-by comma and range-scope, ternary-operator, throw-expression, query-terminal, postfix, casted local-select, and nullable-suffix cases now share one production CLI indexing subprocess per syntax family while retaining distinct assertions.
- **Seeded query boundaries reuse databases** — C# generic type-pattern, query range-scope, foreach-shadowing, lambda-parameter, and declaration-pattern variants now share graph-ready databases by family while path filters preserve their exact result contracts.
- **Production SQL boundaries reuse indexed workspaces** — Line-end comment, `USING` / `MERGE`, semicolonless temporary-table, non-code masking, and TRUNCATE variants now share one production CLI indexing subprocess per family while path filters preserve exact contracts.
- **Non-coverage net8 lanes run complementary test shards** — Windows and macOS split `IndexCommandRunnerTests` from the remainder into separate processes; Ubuntu coverage remains full-suite, retries retain or intersect shard filters, and artifacts include shard identity.
- **Dense extractor guards retain CI timing headroom** — C# and Java dense-constructor fixtures remain half-sized while restoring ten-second runaway ceilings, keeping the shorter workloads without false failures from coverage instrumentation.
- **MCP C# stat-race coverage is enumeration-order independent** — Source drift is injected after final workspace validation and before the file loop, so the actual-skip revalidation contract no longer depends on native directory order.

## 日本語

- **CI failure telemetryで重複するproject評価を省きました** — retry filterとTRX summaryは`dotnet run`ではなくbuild済みtelemetry assemblyを直接起動します。
- **test開始前のCI failureでは空のartifact actionを省きます** — test result、dump、coverageのuploadはtest step開始済みの場合だけ実行し、restore/build failure後のno-op setupを避けます。
- **temporary project cleanupの先行filesystem走査を省きました** — 正常なfixtureは直接recursive deleteを試し、実際に削除が失敗した場合はattribute正規化とWindows向けbounded recoveryを引き続き利用します。
- **SQLite fixture cleanupは失敗後だけpoolを解放します** — seed fileのwriteと正常なdatabase削除ではprocess-wide pool clearを行わず、実際にfileがlockされたWindows retryでは引き続きpoolを解放します。
- **hook scheduling coverageで小型assemblyを使います** — full-scan scheduling testは巨大なtest assemblyをworker directoryへcopyせず、専用のhook-isolation fixtureをstageします。
- **Unix shell testはWindowsのfixture setup前にskipします** — installerとcontainer entrypointのcaseはtemporary directoryを作ってtest bodyからreturnせず、Windowsではdiscovery時にskipします。
- **production query boundaryでindexed workspaceを再利用します** — 関連するC# order-by comma / range scope、ternary operator、throw-expression、query-terminal、postfix、casted local-select、nullable-suffix caseは個別の構文assertionを保ちながら、構文familyごとにproduction CLI indexing subprocessを1回だけ共有します。
- **seed済みquery boundaryでdatabaseを再利用します** — C# generic type-pattern、query range-scope、foreach-shadowing、lambda-parameter、declaration-pattern variantはfamilyごとにgraph-ready databaseを共有し、path filterでexact result contractを維持します。
- **production SQL boundaryでindexed workspaceを再利用します** — line-end comment、`USING` / `MERGE`、semicolonless temporary-table、non-code masking、TRUNCATE variantはfamilyごとにproduction CLI indexing subprocessを1回だけ共有し、path filterでexact contractを維持します。
- **coverageなしのnet8 laneを補完的なtest shardで実行します** — WindowsとmacOSでは`IndexCommandRunnerTests`と残りを別processに分けます。Ubuntuのcoverageはfull suiteを維持し、retryはshard filterを保持または交差させ、artifact名にはshard identityを含めます。
- **dense extractor guardでCI計測の余裕を維持します** — C#とJavaのdense constructor fixtureは半減したまま10秒のrunaway上限を復元し、短いworkloadを保ちながらcoverage instrumentationによる誤失敗を防ぎます。
- **MCP C# stat-race coverageを列挙順に依存させません** — source driftを最終workspace validation後かつfile loop前に注入し、actual-skip revalidation contractがnative directory順序に左右されないようにします。
