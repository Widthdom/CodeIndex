# Testing Guide

> **[日本語版はこちら / Japanese version](#テストガイド)**

This document explains how the `cdidx` test suite is organized, how to add or update tests safely, and which conventions to follow when the behavior or test infrastructure changes.

If you change test code, test helpers, test execution flow, or testing conventions, update this document in the same commit.

## Quick Start

```bash
dotnet test
dotnet test tests/CodeIndex.Tests/CodeIndex.Tests.csproj
dotnet test tests/CodeIndex.Tests/CodeIndex.Tests.csproj --settings tests/CodeIndex.Tests/CodeIndex.Tests.runsettings --blame-crash --blame-hang --blame-hang-timeout 5m
dotnet test --filter "FullyQualifiedName~GitHelperTests"
```

Use the full suite by default. Use targeted filters only while iterating locally, then finish with `dotnet test`.

## Test Stack

- Framework: xUnit
- Target frameworks: `net8.0` and `net9.0`
- Main test project: `tests/CodeIndex.Tests/CodeIndex.Tests.csproj`
- Common direct test-only packages: `Microsoft.NET.Test.Sdk`, `xunit`, `xunit.runner.visualstudio`, `coverlet.collector`, `Microsoft.Data.Sqlite`, `FsCheck.Xunit`
- These test-only packages are separate from the production dependency rule in `src/CodeIndex`, which still allows only `Microsoft.Data.Sqlite` at runtime.
- `FsCheck.Xunit` is reserved for property-based tests that assert universal invariants (never-throws contracts, idempotence, "output is parseable by downstream consumer") across randomly generated inputs. Use it to complement, not replace, the example-based `[Fact]` / `[Theory]` tests — pick FsCheck when the property is a universally quantified claim, and an example test when a specific concrete case is the contract.
- Test parallelism: enabled by default across independent test classes. Tests that touch process-global state such as SQLite pool resets, environment variables, or current-directory overrides must use an explicit non-parallel collection, and tests that swap `Console.Out` / `Console.Error` must lock on `TestConsoleLock.Gate`.
- CI runs the test project through `tests/CodeIndex.Tests/CodeIndex.Tests.runsettings`, enables VSTest blame crash and hang collection, applies a 45-minute session timeout plus 60-second xUnit long-running diagnostics, and reruns the suite once after an initial failure. If the retry passes, CI uploads `TestResults/flaky-retry.txt` with the TRX and blame artifacts so the run is treated as suspect instead of silently trusted. TRX telemetry summaries and test-result artifact uploads run only for failed or pass-on-retry lanes, not for clean first-pass success lanes; streamed test output is also written under `TestResults` only after a failed run needs upload/timeout inspection, and that failure-log directory is created only on the failure path. The telemetry summarizer runs with `--configuration Release` and may build its helper during failure diagnostics so every matrix lane can produce a TRX summary. XPlat Code Coverage collection is limited to the `ubuntu-24.04` / `net8.0` lane so every active CI lane still exercises the full suite without paying collector overhead. OS coverage runs on `net8.0`, the production CLI target, while `net9.0` compatibility coverage runs on `ubuntu-24.04` only. Test execution runs with `--no-build` after locked restore and Release build steps: the primary lane restores the full solution for audit and publish coverage, then builds `tests/CodeIndex.Tests/CodeIndex.Tests.csproj` for the matrix framework; non-primary lanes restore only that test project's matrix framework with `RestoreTargetFrameworks` before the same per-framework build. CI installs both the production `8.0.413` SDK and the pinned `9.0.301` SDK on every lane because `global.json` disables SDK roll-forward before target-framework-specific restore/build filtering can run. `CodeIndex.Tests.runsettings` is the single owner of the `TestResults` output directory; local `dev.sh coverage` follows that same ownership instead of passing a second results-directory argument. The `ubuntu-24.04` / `net8.0` lane no longer builds the test project's unused `net9.0` target; `net9.0` build coverage stays in the Ubuntu compatibility lane. It also uses `make lint` as the single formatting verifier. The NuGet cache key is based on `packages.lock.json` and `global.json` instead of every project file, with an OS-scoped restore key for partial cache reuse; locked restore still catches package-input drift, while test-only project edits no longer evict the package cache. The weekly mutation workflow also caches the pinned Stryker global tool and NuGet packages so scheduled mutation runs avoid reinstalling unchanged test tooling.
- Keep the CI initial test run and its single retry routed through one workflow helper so logger, blame, and coverage arguments cannot drift. When a PowerShell helper returns the test exit code, keep streamed test output off the function success stream so assignments capture only the numeric exit code.
- Coverage collection runs only on the initial primary-lane test attempt; the one flaky-classification retry reuses the same test arguments without rerunning the coverage collector.
- Matrix test invocations use both `--no-build` and `--no-restore` because each lane completes its scoped locked restore and Release build before entering the shared test helper.
- Primary-lane publish also uses `--no-build --no-restore`, reusing the production project output and dependency graph built through the Release test project.
- Release workflow tests use `--no-build --no-restore` after the solution's locked restore and Release build so each runtime lane does not reevaluate dependencies.
- Keep package audit, primary-lane build/lint, coverage collection, coverage artifact upload, publish, and build artifact upload keyed to the matrix `primary_lane` value; define the lane set once with explicit matrix entries instead of recomputing or excluding combinations in later steps.
- Workflow path filters do not repeat individual Markdown files already covered by `**.md`; keep equivalent push and pull-request filters aligned. Build/Test and CodeQL also ignore license text paths owned by the focused license-policy workflow.
- Test workflows group pull-request runs by workflow and pull request, use a unique run ID otherwise, and cancel only superseded pull-request runs so push, schedule, and manual runs remain independent.
- TRX telemetry summarization reuses the Release telemetry tool already built through the test project's direct reference and must not restore or build it again after the test step.
- The changelog-fragments workflow caches packages from the changelog tool lock file, performs one locked restore, and validates with `dotnet run --no-restore`.
- The focused license-policy workflow caches NuGet packages, performs a locked `net8.0`-only restore, and runs its filtered tests with `--no-restore` so dependency resolution is not repeated.
- The C# CodeQL lane uses setup-dotnet's lock-file-keyed NuGet cache; the Actions-only lane skips both SDK setup and package caching.

## Test Layout

The test project mirrors the production areas closely.
Use `docs/test-doc-maintenance-plan.md` before moving oversized suites or adding `Skip =` cases; it tracks the current split sequence, skip classifications, and large-document boundaries.

- `ChunkSplitterTests.cs`, `SymbolExtractorTests.cs`, `ReferenceExtractorTests.cs`, `SearchSnippetFormatterTests.cs`, `DbPathResolverTests.cs`, `ConsoleUiTests.cs`
  Pure or mostly pure behavior tests with in-memory inputs.
- `SymbolExtractor*Tests.cs` and `ReferenceExtractor*Tests.cs`
  Extractor coverage is split by language or feature area with partial test classes, while shared helpers remain on the root `SymbolExtractorTests` / `ReferenceExtractorTests` parts.
  When moving repeated extractor scenarios out of a giant suite, keep the new partial file grouped by a readable domain such as language, build-file format, or protocol surface, and prefer small semantic assertion helpers over repeated raw substring or predicate assertions.
  Use `AssertSymbolsContain(...)` when a fixture only needs to verify several symbol names of the same kind across language-specific partials; keep direct predicates for metadata such as line, container, subtype, or return type.
  Likewise, use `AssertReferencesContain(...)` for repeated `(reference kind, container, symbol names)` checks, while retaining predicates for flags, context, line, and other edge-specific metadata.
  Use `AssertReferencesContainInContext(...)` when several reference names share the same kind and exact source context; keep direct predicates when context is only one part of a richer edge contract.
  Use `AssertReferencesDoNotContain(...)` for negative checks over one reference kind; retain direct predicates when the exclusion depends on container, context, line, or other metadata.
  `ReferenceExtractorTests.ExtractSymbolsAndReferences(...)` owns the common symbol-then-reference extraction setup for tests that need both lists; use it instead of repeating the two extractor calls when the fixture does not need a specialized path or workspace symbol setup, and discard the symbol tuple element with `_` instead of keeping an unused `symbols` local when the test only asserts references.
- `FileIndexerTests.cs`, `FileIndexerContentLoadingTests.cs`, `FileIndexerTestSupport.cs`
  File scanning, language detection, scan-result language reuse, content-sensitive header safeguards, content loading/canonicalization, checksum, Git LFS pointer detection, and record-building behavior, including extensionless shebang detection's 256-byte first-line cap, binary/NUL-byte rejection, and Windows-only >=260-character path walker/purge coverage. Shared `FileIndexerTests` helpers live in `FileIndexerTestSupport.cs`.
- `PathCompatibilityMatrixTests.cs`
  Cross-platform path compatibility matrix coverage for path casing, boundary-prefix comparisons, Windows long-path prefixing, POSIX sensitive-file permissions, symlink/dangling-entry scan behavior, submodule passthrough under default skip directories, and git skip-worktree path normalization. Keep new platform/path fixture scenarios here when the same assumption needs to be visible across indexing, Git helper, DB/query, installer, or status surfaces.
- `DatabaseTests.cs`, `DbReader*Tests.cs`
  SQLite schema, write paths, migrations, and query behavior. DbReader coverage is split by query family, including search, SQL qualified-name handling, file dependencies, impact, and symbol-query suites, while shared seeded fixture state remains on the root `DbReaderTests` part.
  `DbSchemaConstraintTests.cs` also locks schema constraints to `SymbolKindCatalog` and required file foreign keys so DB readiness checks fail when code enums and SQLite CHECK clauses drift.
  Hotspot ranking fixtures should use the smallest counts that cross each ranking threshold; for structural-rank tests, keep one side just above the raw-reference comparison and the other just above the symbol-count threshold instead of scaling both far beyond the boundary.
  Checkpoint listing cap fixtures should exceed the checkpoint count cap once and exceed the inspected-file cap on only one checkpoint; multiplying both caps together adds filesystem work without increasing boundary coverage.
- `ConcurrencyTests.cs`
  WAL snapshot and shared-writer stress tests. The concurrent reader/writer
  snapshot tests stop after enough reader and writer iterations are observed,
  with a two-second cap as the slow-host fallback; do not replace that with a
  fixed sleep because fast lanes should not spend the full cap once the race has
  already been exercised. Shared-writer blocking tests signal once the worker
  task has started before asserting it remains blocked, instead of sleeping for
  a fixed grace period.
- `LegacySchemaMigrationTests.cs`
  End-to-end upgrade path: seeds a pre-column legacy DB, opens it through `TryMigrateForRead`, and exercises the read paths that touch nullable symbol ordinals (outline, symbol search, nearby, unused, analyze bundle) to lock in the real-world failure mode behind #58 / #49.
- `IndexCommandRunner*Tests.cs`, `QueryCommandRunner*Tests.cs`, `ProgramCliTests.cs`, `InstallScriptTests.cs`
  CLI parsing, command execution, and installer behavior. Index command coverage is split by run mode or feature area, and query command coverage is split by command family with partial test classes so shared console and fixture helpers stay centralized. Keep repeated query-result fixtures, such as overlapping chunk content used by multiple search deduplication tests, in narrow class-level helpers instead of duplicating local builders. `ProgramCliTests.cs` covers top-level entrypoint behavior that must be exercised through a subprocess, while `InstallScriptTests.cs` runs focused bash snippets against `install.sh` in library mode to lock in release-installer regressions without performing real network installs.
  Argument-validation variants that only differ by invalid scalar input share one database fixture and iterate within a fact when no per-case state or discovery identity is required.
  Excerpt focus-column validation follows this rule for zero and non-numeric values, reusing one indexed Markdown fixture.
  Inspect path-line exact/enclosing-symbol cases reuse one indexed source fixture and iterate read-only line queries within a fact.
  Definition and symbols exact-mode conflict validation share one empty database and a cross-command flag-pair table.
  Razor directive kind-filter queries share one indexed component fixture and iterate route, implements, attribute, and layout expectations in one fact.
  Symbols literal-query coverage shares one empty database for double-dash and explicit `--query` forms when proving compact-looking text is not expanded.
  Symbols hotspot and references ranking queries share one symbol-sort fixture because both are read-only views of the same ranking signals.
  `TrimmedCliTestHelper` owns trimmed publish setup and published CLI subprocess execution. Use its shared non-single-file publish for published CLI smoke coverage so a test process pays that publish cost once; keep single-file tests on an explicit per-test publish because they verify a distinct apphost shape. Published CLI smoke tests run only on the `net8.0` test target because the production CLI targets `net8.0`; focused in-process tests keep cross-target behavior covered without repeating the expensive publish on `net9.0`.
  Installer snippet and Docker entrypoint script coverage use `ProductionCliFactAttribute` / `ProductionCliTheoryAttribute` and run only on the `net8.0` test target because those shell scripts are target-framework independent and the production CLI targets `net8.0`.
  `RunBuiltCli` / `RunCliInSubprocess` subprocess coverage, including timeout-guarded subprocess probes, uses `ProductionRuntimeFactAttribute` / `ProductionRuntimeTheoryAttribute` and runs only on the `net8.0` test target when the subprocess resolves to the production `net8.0` CLI; keep direct in-process command-runner tests cross-target.
  When a production-runtime test only needs the built CLI to create an indexed fixture, keep that subprocess boundary on the indexing step and run query assertions, including count/path/format variants, in-process through the command runner helpers unless the assertion depends on process-boundary behavior.
  `InstallScriptTests.RunInstallerSnippet` enforces a bounded timeout and kills the snippet process tree on timeout, so installer regressions fail with captured output instead of hanging the suite.
- `CiWorkflowTests.cs`, `ReleaseWorkflowTests.cs`, `ReleaseWorkflowTests.PackageHelpers.cs`
  CI and release workflow contract tests. Keep repeated related workflow/script string contract assertions, including test-result artifact, retry-output, install, Homebrew, changelog, release-payload job splits, container image, SBOM, NuGet publish, secret scope, SDK pin, tool/action pin, and runner/cache policy contracts, in small grouped helpers so the tests emphasize the contract being checked; use the comparison-aware helpers when the contract intentionally requires ordinal matching. Release workflow package-normalization ZIP fixture helpers live in `ReleaseWorkflowTests.PackageHelpers.cs` so workflow assertions stay near the workflow contracts.
- `PackageNormalizeDiagnosticsTests.cs`
  Package normalizer diagnostic redaction coverage. Keep timeout-budget assertions aligned with the shared diagnostic redaction policy so high-load full-suite runs do not treat expected path/secret placeholders as flaky.
- `DocumentationStatusContractTests.cs`, `DocumentationDriftTests.cs`
  Checked-in documentation contract tests. They use `RepositoryTestPaths` to keep status fields, workflow references, documented `cdidx` command examples, release/changelog workflow snippets, and representative English/Japanese guide sections synchronized.
  `DocumentationStatusContractTests.cs` includes readiness, maintenance, and MCP status fields so status JSON support contracts stay visible in the user and agent guides.
  Workflow tests that compare multiline YAML snippets should use `RepositoryTestPaths.ReadNormalizedWorkflow(...)` so line-ending normalization stays centralized across CI, release, package-lock, and mutation workflow contracts.
  Reuse that normalized value for all assertions in a test instead of reading or retaining both raw and normalized copies of the same workflow.
  Use `RepositoryTestPaths.ReadNormalizedText(...)` for other checked-in multiline fixtures such as `Dockerfile`; workflow-specific reads should continue through `ReadNormalizedWorkflow(...)`.
  Use `ReadDockerfile()` and `ReadDockerIgnore()` for release-container contract tests so canonical fixture paths do not drift across workflow suites.
  `RepositoryTestPaths` caches checked-in text, normalized derived text, and normalized workflow inventories for the lifetime of the test process. Keep it for immutable repository contracts only; tests that rewrite fixtures must use their own temporary paths.
  License-policy contract tests use the same accessor for legal notices, workflow files, and distribution docs instead of rediscovering the repository root and rereading overlapping files.
  Repository-backed documentation, source-audit, JSONL-policy, and trimmed-publish tests reuse `RepositoryTestPaths.Root` instead of maintaining suite-local upward directory walks.
  Large command-runner, installer, and extractor suites also delegate their legacy root helpers to that single cached root.
  Changelog limit tests resolve checked-in files through `RepositoryTestPaths` instead of performing another root walk.
  Whole-workflow policy audits should use `RepositoryTestPaths.ReadNormalizedWorkflows()` so enumeration order, extension filtering, file naming, and normalization are shared instead of being rebuilt inside individual test classes.
  When one policy test checks several step-level rules, parse workflow step blocks once and filter the retained blocks for each rule instead of rerunning the multiline step regex for every assertion family.
- `IndexCommandRunnerTests.Run_CancelDuringFreshIndex_ReturnsInterruptedJson`, `Run_CancelDuringDryRunScan_ReturnsInterruptedJson`, and `Run_CancelBeforeFreshScan_ReturnsInterruptedJson`
  exercise the same in-process cancellation paths used after Ctrl-C/SIGINT wiring, including scan-time cancellation, so interrupted index runs keep returning the canonical JSON error contract.
- `IndexCommandRunnerTests.SymbolExtractionWorker_LegacyEnvironmentHooksAreIgnored_Issue3398`
  launches the isolated symbol worker to prove legacy worker environment variables are ignored. Its callback budget includes process startup and is intentionally wider than ordinary in-process checks so local process load does not turn the legacy-env regression check into a timeout flake (#3863).
- `IndexCommandRunnerTests` and `FileIndexerTests` also cover `CSharpStaticInterfacePrepass` text, raw-byte, chunked raw-token, and streaming file contract probes. Keep byte-array, chunked, and file-level probe coverage aligned so the prepass can avoid whole-file allocation without losing UTF-8 / UTF-16 static-interface contract candidates.
- Project-marker budget integration coverage overrides the directory budget to its smallest boundary and enumerates one child; do not materialize the production 8,192-directory cap merely to prove warning propagation.
- `IndexWatchRunnerTests.RunCore_CancellationToken_StopsImmediately` and `RunCore_EmitsHumanFriendlyStartStop_WhenJsonDisabled`
  exercise watch-loop startup and shutdown under redirected console output. They call the asynchronous watch core directly without prebuilding an unused index, cancel after its synchronous startup prefix emits the start event, and use a bounded shutdown wait; do not reintroduce index setup, dedicated threads, signal polling, or fixed sleeps for this path.
- `BackgroundTaskObserverTests`
  relies on `BackgroundTaskObserver`'s fault-only continuation contract: canceled
  tasks are awaited directly and do not need a post-cancellation fixed sleep to
  prove warnings were suppressed.
- Cancellation-only timeout tests use an infinite cancellable delay so the fixture has no unrelated wall-clock completion path.
- SQL diagnostic truncation tests cross the character cap with a minimal fixed-width suffix instead of constructing thousands of progressively longer `UNION` clauses.
- Oversized file guards create sparse length-only fixtures when content is irrelevant, avoiding a matching managed byte-array allocation.
- Full-scan and scoped-update oversized-file warning tests share `TestProjectHelper.WriteSparseFile(...)` so both execution modes avoid 10 MiB fixture allocations.
- Oversized ignore-file rejection uses the same sparse helper because the size preflight rejects the file before rule contents matter.
- Per-line oversize detection uses four short physical lines; hundreds of repetitions add no boundary coverage because the contract is independent per line.
- Captured-output overflow tests cross the character budget by one character unless a larger excess is itself part of the contract.
- Suggestion store and archive cap tests share one sparse-file helper instead of allocating arrays at either persisted-size limit.
- Persistent-log rotation tests size existing log fixtures through `FileStream.SetLength(...)` rather than allocating a 1 MiB zero buffer.
- JSON-depth boundary fixtures use fixed-width character strings instead of `Enumerable.Repeat` pipelines for repeated brackets.
- CSV entry-cap tests share `TestProjectHelper.RepeatCsvEntry(...)` across index and query parsers so boundary construction stays consistent.
- Raw FTS operator boundary tests reuse the same joined-entry builder with their grammar-specific separators.
- Synchronized console-write coverage uses the smallest repeated slow-writer workload that still exercises both concurrent producers; do not scale iteration counts as a stress test.
- `SymbolExtractorTests.Extract_CSharp_InstallScriptFixture_CompletesWithinPracticalBudget`
  is a coarse runaway guard for the real `InstallScriptTests.cs` C# extraction fixture. Its wall-clock budget is intentionally broader than a benchmark so slower or noisy CI hosts do not fail the suite for ordinary variance.
- `SymbolExtractorTests.Extract_JavaScriptLargeExportedObjectLiteralProperties_CompletesWithinPracticalBudget` and `Extract_CSharp_ReferenceExtractorFixture_CompletesWithinPracticalBudget`
  are broad runaway guards for known large symbol-extraction fixtures. Keep their budgets generous enough for full-suite load; tighten them only with focused optimization evidence, not as benchmark thresholds.
- `ReferenceExtractorTests.Extract_CSharpLargePlainCallFile_CompletesWithinPracticalBudget`
  is a broad runaway guard for high-volume C# reference extraction on ordinary call lines. Treat its budget as a regression tripwire, not a benchmark target; keep it wide enough for noisy CI unless a focused optimization change justifies tightening it.
- Broad extractor `*CompletesWithinPracticalBudget` runaway guards run only on the primary `net8.0` test target. Keep focused functional extractor tests cross-target, but do not duplicate the large-fixture budget guards across every target framework unless the guard is specifically proving a target-framework-specific contract.
- `IndexCommandRunnerTests.RunBackfillFold_PublishedTrimmedBinary_SerializesSuccessAndErrorJson`
  publishes a trimmed RID-specific CLI and runs whichever entry point the SDK emits (`cdidx.dll` through `dotnet` or the native `cdidx`/`cdidx.exe` apphost). Its publish smoke disables NuGet vulnerability auditing because package advisory validation is covered by the normal build/test workflow's package vulnerability check, not by this runtime serialization test. It is reported as skipped on macOS arm64 while SDK/ILLink can crash before exercising `cdidx` (#2586). Do not assume every SDK/runtime pair writes a `cdidx.dll` into self-contained publish output.
- `QueryCommandRunnerTests.RunPublishedTrimmedCli_SerializesQueryJsonAndSupportsRazorAliases`
  uses one trimmed RID-specific publish output for query JSON coverage and both `cshtml` / `razor` C# Razor language aliases, writes publish-specific lock files under the test's temporary intermediate directory, disables NuGet vulnerability auditing for the publish smoke, and runs whichever `cdidx` entry point the SDK emits so the test does not depend on source-tree lock-file mutation, advisory-feed availability, or a DLL-only publish layout. If `dotnet publish` reaches an SDK/ILLink tool that requires an unavailable `Microsoft.NETCore.App` runtime, the test is reported as skipped with that missing-runtime diagnostic instead of failing before it can exercise `cdidx` (#3571). It is also reported as skipped on macOS arm64 because the SDK/ILLink crash happens before the test reaches `cdidx` (#2586).
- `McpServer*Tests.cs`
  MCP JSON-RPC behavior and tool outputs. Large server coverage is split into focused partial suites for tool calls, tool listing, protocol/session handling, and error handling while the root `McpServerTests` part keeps shared seeded fixture state. Request-timeout tests use signal-gated delay hooks instead of fixed sleeps: start the request, confirm the hook has begun, then await the timeout response with a bounded wait so they pay only the configured timeout while still proving in-flight actions drain after the timeout response.
  Stdio response-order tests use the same signal-gated pattern: make the synthetic transport signal the parse-error path instead of sleeping in the response serializer.
  Rate-limit-disabled coverage uses the lightweight `languages` tool for repeated successful calls; do not pay repeated `status` database aggregation cost when the assertion only concerns limiter bypass.
- `HttpMcpTransportTests.cs`
  HTTP MCP transport behavior, including authentication responses, warm server reuse, concurrent requests, and request logging. Request-log assertions must validate recorded contents without assuming callback order between independently handled HTTP requests.
  Request-log metadata bounds for long paths, long JSON-RPC IDs, and over-depth IDs share one sequential logger harness and select records by content rather than callback order.
  Bearer-token denial variants share one table-driven harness that verifies the common wire response, challenge header, redaction, and request-log outcome; keep same-length mismatch in that table to exercise the hashed comparison path.
  Matching canonical and lowercase bearer schemes share one authenticated harness because RFC auth-scheme casing does not require isolated server state.
  Transport disposal coverage uses one live event-stream lifecycle to verify concurrent idempotent disposal, stream cancellation, server-loop completion, and owned semaphore release together.
  Event-stream lifecycle coverage uses one harness to verify response headers, concurrent POST handling, oversized server-side closure, and client disposal in sequence, with a short test-owned keep-alive instead of the production heartbeat interval.
  No-stream initialize coverage reuses one default harness for explicit protocol negotiation and default handshake behavior; event-stream notification delivery keeps an isolated session.
  Basic HTTP endpoint coverage reuses one default harness for notification 204, root-method 405/Allow, and structured healthy `/healthz` responses.
  Invalid and oversized health-provider JSON fallbacks run against one harness by replacing the provider between requests.
  Health degradation coverage uses one harness to verify an event-stream drop remains healthy before injected response-cleanup failures transition the status to degraded.
  Warm-server coverage reuses one harness for sequential initialize/list, concurrent correlated pings, an empty-body 204, and a successful follow-up request.
  Malformed/deep cancellation and deep JSON-RPC response payloads share one direct transport and one queue/read/write assertion helper when proving they bypass out-of-band handling.
- `GitHelperTests.cs`, `GitProcessRunnerTests.cs`
  Git-specific behavior, including worktrees, commit-based updates, direct git process runner diagnostics, and cancellation of git subprocesses. Tests that create real repositories or launch real/fake git subprocesses use `ExternalProcessFactAttribute` / `ExternalProcessTheoryAttribute` and run only on the `net8.0` test target; keep pure `.git` metadata parsing and trusted-candidate enumeration cross-target. Timeout and cancellation wall-clock assertions should stay below the fake git scripts' natural completion while leaving room for macOS CI scheduling and process-cleanup overhead. Fake git scripts that run after commit-ref validation should echo the verified commit argument for `rev-parse --verify <ref>^{commit}` so timeout tests reach the intended git command.
- `WorkspaceMetadataEnricherTests.cs`
  Workspace freshness and git metadata enrichment behavior.
- `SuggestionStoreTests.cs`
  Local suggestion JSON storage: dedup hashing, persistence, corruption recovery, atomic writes.
- `SourceCodeDetectorTests.cs`
  Source code leak prevention: allowed natural-language inputs vs rejected code blocks (fenced, indented, import runs, etc.).
- `ConsoleUiTests.cs`
  Completion flag parity tests should route shared bash/zsh/fish flag extraction through one helper and keep individual tests focused on command-specific required flags or shell-specific aliases.
- `ReportCommandRunnerTests.cs`
  Report log-tail fixtures should use the local log-directory and log-file helpers instead of repeating `Path.Combine(workDir, "logs")`, `Directory.CreateDirectory`, and ad hoc `File.WriteAllText` setup.
- `PostExtractionHookTests.cs`
  Post-extraction hook discovery, mutation, diagnostics, callback budgets, and collectible hook assembly cleanup. Heavy hook worker and collectible assembly-load integration tests use `ProductionRuntimeFactAttribute` and run only on the `net8.0` production target, while direct worker protocol and metadata tests remain cross-target. Timed-out and canceled callback tests use a hook delay shorter than their leak-observation window, not a full one-second absence check, so worker-kill regressions still write the completion marker before the assertion exits. These tests mutate hook-related environment variables and test-only callback budget state, so the class belongs to the `SQLite pool sensitive` non-parallel collection.
- `GitHubIssueReporterTests.cs`
  GitHub token resolution logic (CDIDX_GITHUB_TOKEN only; generic GITHUB_TOKEN is ignored), outbound code scrubbing, idempotency checks, and rate-limit diagnostics.
- `PackagesLockTests.cs`
  NuGet lock-file guard coverage for direct package references that must remain synchronized across all target frameworks, including the net9.0 compatibility references that keep locked CI restore green.
- `ConcurrencyTests.cs`
  Concurrent read and read-during-write scenarios (WAL mode validation), including the issue #180 bug-catching snapshot-isolation regressions for all three multi-statement reader entry points: (1) `GetStatus` seeds `refs == files * refsPerFile` and asserts every concurrent observation preserves that invariant; (2) `AnalyzeSymbol` seeds one symbol `S` plus matching reference/caller pairs, toggles a second file symmetrically, and asserts `references.Count == callers.Count` across every `inspect`/`analyze_symbol` bundle; (3) `GetRepoMap` seeds a baseline modified timestamp and toggles a newer file, asserting `latest_modified == workspace_latest_modified` across every map call. Each test fails without the DEFERRED-transaction wrap on the matching reader and passes with it.
- `PerformanceTests.cs`
  Bounded CI smoke coverage plus large-scale data benchmarks. `CiPerformanceSmoke_IndexAndSearchSmallFixture_StaysWithinBudget` and the allocation budget guards run in the default `net8.0` suite, so they are blocking PR/CI checks on the production target, but their broad budgets are intended to catch only severe indexing/search or allocation regressions rather than act as benchmarks. The 10K+ large-scale tests remain skip-by-default; run them manually with `--filter`.
- `.github/scripts/run-dotnet-tests.ps1`
  The `dotnet.yml` matrix test step delegates test argument construction, coverage gating, `TestResults` path ownership for failure-log capture, TestSessionTimeout handling, and single flaky retry classification to this script. Keep workflow YAML limited to matrix/lane parameter wiring, and update `CiWorkflowTests` when changing either the script contract or artifact/summarize gating.
- `.github/scripts/configure-windows-test-host.ps1`
  The `dotnet.yml` and `release.yml` Windows lanes share TMP/TEMP pinning and Defender exclusion setup here so both workflows keep the same test-host performance assumptions. Update `CiWorkflowTests` when changing this script or its workflow call contract.
- The `dotnet.yml` SDK setup has one conditional retry for transient SDK download failures. Keep the first attempt marked `continue-on-error` only while the retry is guarded by its failed outcome, so a second failure still fails the job.
- `DbRecoveryTests.cs`
  Database corruption recovery and graceful degradation behavior. Filesystem setup failures for `cdidx index` (read-only DB files and unwritable DB parent directories) are covered in `IndexCommandRunnerTests.cs` so they exercise the same CLI JSON/stderr boundary users see.
- `JsonOutputSnapshotTests.cs`, `JsonOutputSnapshotHelper.cs`
  Golden-file regression fixtures for the CLI `--json` output contracts (issue #1548). Each test runs one command (`status`, `search`, `references`, `impact`, `excerpt`) against a deterministic in-memory fixture, normalizes volatile fields (timestamps, absolute paths, commit SHAs, FTS5 scores, SQLite page counts), and diffs against the matching file under `tests/CodeIndex.Tests/golden/`. Renames, removals, reordered arrays, or new keys fail the snapshot so the contract change is forced to land alongside an intentional golden update. See "JSON `--json` output snapshots" below for the update procedure.
- `PropertyBasedParserTests.cs`
  FsCheck-driven property tests for parser-heavy paths called out in issue #1572: `ArgHelper.WantsHelp` and `ProgramRunner.IsProjectPathArg` never throw on arbitrary inputs; `FileIndexer.NormalizePathSeparators` is idempotent under double application; the literal-safe FTS5 sanitizer (`DbReader.SanitizeFtsQuery`) always emits a query that a real in-memory FTS5 virtual table can parse. They complement, not replace, the example-based tests in `ArgHelperTests.cs` / `QueryCommandRunnerTests.cs`.
- `TestProjectHelper.cs`, `TestDeterminism.cs`, `RepositoryTestPaths.cs`, `TestConsoleLock.cs`
  Shared test helpers. Prefer `TestProjectHelper.CreateTempProjectScope` when a test owns a temporary project directory, including package-normalizer CLI, diagnostic, legacy-temp, size-limit, entry-name, external-attribute, path-casing, filesystem-traversal, workspace-manifest, active-workspace, workspace-use, config-show, DB path resolver, and file-indexer scan/probe fixtures; use `TestProjectHelper.DeleteDirectory` / `DeleteFile` for ordinary temp cleanup and `DeleteSqliteDatabaseFiles` when SQLite `-wal` / `-shm` sidecars must be removed with the database. Use `TestProjectHelper.WaitForFileSystemReleaseRetry` only inside bounded cleanup retry loops that are reacting to a failed filesystem delete. Use `TestDeterminism` for bounded polling, eventual assertions, blocked-task observation, same-start concurrent workers, and seeded random inputs. MCP indexing tests should route temporary codeindex DB cleanup through this helper as well. Do not copy local retry loops unless the test needs a genuinely specialized path shape such as Windows long-path fixtures.

## Conventions

- Keep test names descriptive. The current suite mostly uses `Method_Scenario_ExpectedBehavior`.
- Keep tests deterministic. Do not depend on machine-global git config, locale-specific output, or ambient files.
- Prefer `ManualTimeProvider` for fake clocks and `TestDeterminism.CreateRandom` for randomized fixture input so repeated test runs replay the same timeline and data. Use `TestDeterminism.WaitUntilAsync` or the synchronous `WaitUntil` for bounded polling/eventual assertions instead of local `Task.Delay` loops or fixed sleeps. Use `AssertConditionRemainsTrue` for short absence/stability observations, and `TestDeterminism.RunConcurrentlyAsync` when a test needs workers to start from the same gate.
- Prefer small fixtures and explicit assertions over broad snapshot-style checks. The one narrow exception is the `--json` output contract harness (`JsonOutputSnapshotTests`), which pins the full field shape on purpose — see "JSON `--json` output snapshots" below.
- For cross-language extractor budget tests, exceed the shared boundary by the smallest value that triggers truncation; do not add arbitrary padding independently per language.
- When repeated expected-value construction obscures a boundary contract such as raw bytes vs canonical content, use a narrowly named local helper instead of duplicating the low-level expression at each assertion.
- Batch independent file mutations into one `--files` update when a file-indexer test only needs to verify their normalized paths and outcomes after the same initial index.
- Keep related index invalidation transitions in one fixture when every intermediate state is asserted; static-interface contract add, remove, restore, and delete coverage should not rebuild equivalent projects independently.
- Cap issue lifecycle tests should reuse one indexed fixture across full-scan and update transitions when they assert the issue after each low/high boundary change.
- Unreadable-directory full-scan coverage keeps JSON and human diagnostics, purge protection, checkpoint creation, and successful retry in one fixture so the shared partial scan setup is not rebuilt.
- Checkpoint write/delete failure coverage transitions one unreadable-directory fixture from an injected save failure through a successful save to an injected delete failure.
- HEAD freshness coverage uses one two-commit fixture to prove a `--files` refresh remains stale before a `--commits HEAD` refresh marks the current head matched.
- When a test locks a long table of equivalent key/value expectations, keep the table as data and route the repeated lookup/assertion shape through one helper so duplicate rows are visible.
- When extractor tests repeat the same `SymbolName` / `ReferenceKind` predicate shape across positive and negative reference assertions, use a semantic assertion helper so each call site names only the behavioral differences such as container name/kind, context, line, column, or the excluded symbol set.
- When a production comment or error string is bilingual, preserve that expectation in tests where it matters.
- If a behavior change is user-visible, update tests, `CHANGELOG.md`, and any affected docs together.

### Shared state and parallelism audit

Use the inventory below before adding or moving a test class:

- SQLite pool resets, direct `SqliteConnection.ClearAllPools()` calls, process current-directory changes, or process-global environment variable mutation: put the class in the `SQLite pool sensitive` non-parallel collection.
- Environment variables: use `EnvironmentVariableScope.Capture(...)` so setup failures and assertion failures restore the original values through one cleanup path.
- `Console.Out` or `Console.Error` replacement: lock `TestConsoleLock.Gate` around the whole capture/swap window.
- Temporary repositories and files: create them through `TestProjectHelper` when practical, and do not depend on user-level git config.
- Long-running or performance-oriented tests: keep them skipped by default or give them broad deterministic budgets; if CI reports them in xUnit long-running diagnostics, first check runner load before tightening thresholds.
- When a response-size algorithm needs a large production ceiling, prefer a narrowly scoped test-only budget override and a small representative payload; restore process-global overrides in `finally` and keep the suite non-parallel.
- Pagination and suppression fixtures should cross the fetched-window boundary with the fewest additional records needed to distinguish full totals from the returned page.
- Graph edge-budget tests should seed the minimum symbols needed to create each distinct edge; repeated references between the same file pair do not strengthen a distinct-edge assertion.
- Ignore-rule fail-closed tests use a restored test-only rule budget so the parser boundary is exercised without generating thousands of irrelevant patterns.

## Shared Helpers

### `TestProjectHelper`

Prefer the existing helper before writing new setup code.

- `CreateTempProject(prefix)` creates a unique temp workspace.
- Use `CreateTempProject(prefix)` instead of adding local `Path.GetTempPath()` / `Guid.NewGuid()` directory helpers; keep any local wrapper as a thin prefix-specific delegate only when it preserves existing call-site readability.
- `ProjectPath(projectRoot, ...)` resolves fixture paths relative to the temp project and rejects absolute paths or `..` escapes outside that root.
- `CreateDirectory(projectRoot, ...)`, `WriteTextFile(...)`, `WriteTextFiles(...)`, `WriteBinaryFile(...)`, `AppendTextFile(...)`, and `ReadTextFile(...)` centralize fixture directory creation and file setup. Prefer them over local `Path.Combine` + `Directory.CreateDirectory` + `File.*` chains when the path belongs to a temp project.
- Use the `WriteTextFile(..., Encoding)` overload when fixture encoding is part of the behavior under test; do not drop back to `File.WriteAllText(Path.Combine(...), ..., encoding)` for temp-project files.
- In `FileIndexerTests`, use the local relative-path helpers for scan result assertions instead of repeating `Path.GetRelativePath(...)`, separator normalization, sorting, or set creation at each call site.
- `InitializeGitRepo(projectRoot)` initializes git and sets repo-local `user.name` and `user.email`.
- `CreateProjectDb(projectRoot)` creates `<projectRoot>/.cdidx/codeindex.db`, initializes schema, and seeds `codeindex_meta.indexed_project_root` to match the project root.
- `InsertIndexedFile(...)` inserts a realistic indexed file with content-derived checksum, chunks, symbols, and references, and now passes the file path into Python symbol extraction so `__init__.py`-based re-export tests can exercise qualified package names.
- `RunGit(...)` executes git without shell quoting issues.
- `DeleteDirectory(path)` retries temp-project cleanup and normalizes attributes. To avoid process-global cross-test interference, it only requests SQLite pool cleanup through `SqlitePoolCleanup` as a Windows-specific retry fallback after a delete failure.
- Use `DeleteDirectory(path)` in temp-workspace `finally` / `Dispose` cleanup paths, including tests that intentionally remove the workspace earlier in the scenario.
- Call `DeleteDirectory(path)` directly instead of wrapping it in `Directory.Exists(...)`; the helper already handles missing paths.
- Apply the same direct-call rule to local `DeleteDirectory` wrappers that only delegate to `TestProjectHelper.DeleteDirectory`.
- Do not reimplement recursive temp-directory cleanup in individual test files, including report/output bundle workspaces, changelog tool repositories, duplicate-preflight fixtures, file-indexer skipped-root and symlink fixtures, import/export archive and rollback fixtures, configured pattern and out-of-tree language workspaces, audit/metrics write-target directories and private/global log workspaces, program-runner extractor/cache/trace log workspaces, temporary dotnet host fixtures, watch runner workspaces, DB purge/temporary cleanup workspaces, reader BOM fixtures, legacy-migration database workspaces, MCP indexing fixtures, suggestion-store and suggestion CLI fixtures, and small traversal/security fixtures; keep local wrappers as thin delegates to `TestProjectHelper.DeleteDirectory` when a shorter call keeps existing tests readable.
- `DeleteFile(path)` retries standalone temp-DB cleanup and uses the same Windows-specific SQLite pool release fallback when pooled handles block deletion.
- Prefer `DeleteFile(path)` for temp DB, lock, metadata, cache, script, and outside-fixture file cleanup instead of hand-written `File.Exists(...)` / `File.Delete(...)` pairs.
- When a test class already has a robust file cleanup wrapper that clears SQLite pools, route temporary DB cleanup through that wrapper and keep non-DB sidecars on `TestProjectHelper.DeleteFile`.
- DB maintenance test files may keep thin local helpers such as `InitializeEmptyDb`, `ReleaseSqlitePools`, and `DeleteDbFile` when a class owns standalone `.db` files directly; keep those wrappers delegated to `DbContext`, `SqliteConnection.ClearAllPools()`, and `TestProjectHelper.DeleteFile` so pool-release intent remains explicit.
- ページングや抑制の fixture は、返却ページと全件集計を区別できる最小限の追加レコードで fetch window の境界を超えてください。
- グラフの edge budget テストは各 distinct edge を作る最小限のシンボルだけを投入し、同じファイル間の参照を重複させないでください。
- ignore rule の fail-closed テストは復元可能なテスト専用上限を使い、無関係なパターンを数千件生成せず parser 境界を検証してください。
- `SqlitePoolCleanup` centralizes the Windows SQLite pool workaround for tests. Tests that own a temporary SQLite file for their whole lifetime can enter an exclusive owner lease and dispose it idempotently before deleting the file, instead of calling `SqliteConnection.ClearAllPools()` directly from `Dispose`.
- Tests that intentionally call `SqliteConnection.ClearAllPools()`, mutate process-global environment variables, or override the process current directory are grouped into the non-parallel `SQLite pool sensitive` xUnit collection. Add new tests with those hazards to that collection instead of letting them run in parallel with unrelated classes.
- Tests that mutate process-global environment variables should use `EnvironmentVariableScope.Capture(...)` so the original values are restored from a single disposable cleanup path even if setup or assertions fail.

Use these helpers when possible so test behavior stays consistent across files and operating systems.

### `TestDeterminism` and `ManualTimeProvider`

Use `ManualTimeProvider` when production code accepts a `TimeProvider`, and advance it explicitly with `Advance(...)` instead of sampling wall-clock time in fixture data.

Use `TestDeterminism.WaitUntilAsync(...)` for eventual assertions and asynchronous polling, or `WaitUntil(...)` when the surrounding test is synchronous. Both apply a shared timeout and poll interval, and accept a diagnostic callback so timeout failures include the observed state. For loops that should stop after either enough work or a slow-host cap, use `WaitUntilOrTimeoutAsync(...)`.

Use `TestDeterminism.AssertTaskRemainsBlockedAsync(...)` when a test needs a short bounded observation that a task has not completed, and `AssertConditionRemainsTrue(...)` when a synchronous test must prove an absence or stability condition remains true for a bounded window. Use `RunConcurrentlyAsync(...)` when workers should be released from one gate. Use `CreateRandom(...)` for deterministic pseudo-random fixture data instead of constructing ambient or time-seeded `Random` instances.

### `RepositoryTestPaths`

Use `RepositoryTestPaths` for tests that inspect checked-in repository files such as GitHub workflows, `global.json`, or documentation. Prefer `ReadText(...)`, `ReadWorkflow(...)`, and `Combine(...)` over repeating repository-root discovery and `Path.Combine` chains inside individual test classes.

### `TestConsoleLock`

Any test that swaps `Console.Out` or `Console.Error` must lock on `TestConsoleLock.Gate`.

This prevents parallel console redirection from corrupting captured output and avoids flaky assertions in CLI and console UI tests.

Keep the console lock even when a test class already belongs to a non-parallel collection: it documents the process-global console hazard locally and protects shared helper code if the class is ever moved out of that collection later.

## Writing Tests

### Adding coverage

Add or update tests whenever you change:

- CLI argument parsing or output shape
- database schema, migrations, or query semantics
- symbol or reference extraction rules
- indexing skip/update/purge behavior
- MCP tool output or JSON structure
- console/progress behavior
- git/worktree behavior
- workspace freshness or trust metadata

Prefer extending the closest existing `*Tests.cs` file. Create a new test file only when the area does not fit an existing one cleanly.
For boundary tests, use the smallest fixture that still crosses the boundary. If the behavior only needs one page, chunk, cache, query-plan row, or offset overflow, do not scale synthetic data far past that point unless the larger size is part of the contract.

### CLI and console tests

- Capture stdout and stderr explicitly.
- Prefer `ConsoleCapture` for simple stdout/stderr capture, and lock direct console mutations with `TestConsoleLock.Gate`.
- Assert exit codes with `CommandExitCodes`.
- For JSON output, parse it with `JsonDocument` instead of asserting raw strings.

### JSON `--json` output snapshots

`JsonOutputSnapshotTests` and `JsonOutputSnapshotHelper` form a small golden-file harness that catches accidental shape drift in CLI `--json` output (renamed keys, removed keys, reordered top-level arrays, new keys without a contract update). Use them alongside the narrower assertion-style JSON tests in `QueryCommandRunnerTests`; they complement each other rather than replace it.

- Goldens live at `tests/CodeIndex.Tests/golden/<command>.json` and are checked in to the source tree.
- `JsonOutputSnapshotHelper` normalizes volatile fields before comparison: `indexed_at` / `latest_modified` / other timestamp keys → `<TIMESTAMP>`; `git_head` / `indexed_head_commit` / other commit-SHA keys → `<COMMIT_SHA>`; `project_root` → `<PROJECT_ROOT>`; `version` → `<VERSION>`; per-result `score` (BM25, FTS5-implementation-sensitive) → `<SCORE>`; SQLite `page_count` → `<COUNT>`. Per-test temp paths are redacted via the helper's `BuildPathReplacements`.
- When a shape change is intentional, regenerate the matching golden(s) by setting `UPDATE_SNAPSHOTS=1` and re-running only the snapshot tests, then review the diff before committing:

  ```bash
  UPDATE_SNAPSHOTS=1 dotnet test tests/CodeIndex.Tests/CodeIndex.Tests.csproj \
      --filter "FullyQualifiedName~JsonOutputSnapshotTests"
  git diff tests/CodeIndex.Tests/golden/
  ```

- Treat any unintentional snapshot diff as a contract regression: either fix the production code, or update the golden together with the schema/docs/changelog in the same PR.
- Keep fixtures minimal and deterministic. If a new `--json` output joins the contract, add a dedicated snapshot test plus a golden file in the same change.

### Git tests

- Never assume global git identity exists.
- Configure repo-local `user.name` and `user.email` inside the test setup.
- Disable repo-local commit/tag signing for fixture repositories so global signing settings cannot prompt or fail non-interactively.
- Use helper methods or `ProcessStartInfo.ArgumentList`; do not depend on shell-specific quoting behavior.

### Database tests

- Prefer isolated temporary databases per test.
- Initialize schema explicitly when the test needs real DB behavior.
- If the scenario touches read compatibility, verify both the normal path and any fallback or migration path that matters.

## Cross-Platform Rules

- Use `Path.Combine` and relative paths that work on Windows, macOS, and Linux.
- Normalize newline-sensitive fixtures when the assertion is about content rather than platform line endings.
- Be careful with file cleanup on Windows. SQLite connections and file attributes can delay deletion.
- Do not assume shell tools, path separators, or process behavior are identical across platforms.
- If a platform workaround is required, document it in the test and in this guide when it affects future contributors.

## Before You Commit a Test Change

Check the following:

1. The affected production behavior is covered by a focused test.
2. The suite still passes with `dotnet test`.
3. Temporary file, git, and SQLite cleanup paths are robust.
4. Console capture is serialized when needed.
5. This document still matches the current test structure and conventions.

---

<a id="テストガイド"></a>
# テストガイド

このドキュメントは、`cdidx` のテストスイートがどう構成されているか、どのように安全にテストを追加・更新するか、そして挙動やテスト基盤を変更したときに従うべき規約をまとめたものです。

テストコード、テストヘルパー、テストの実行フロー、またはテスト規約を変更した場合は、このドキュメントも同じコミットで更新してください。

## クイックスタート

```bash
dotnet test
dotnet test tests/CodeIndex.Tests/CodeIndex.Tests.csproj
dotnet test tests/CodeIndex.Tests/CodeIndex.Tests.csproj --settings tests/CodeIndex.Tests/CodeIndex.Tests.runsettings --blame-crash --blame-hang --blame-hang-timeout 5m
dotnet test --filter "FullyQualifiedName~GitHelperTests"
```

基本はフルスイートを実行してください。手元での反復中だけ対象を絞り、最後は `dotnet test` で締めます。

## テストスタック

- フレームワーク: xUnit
- メインのテストプロジェクト: `tests/CodeIndex.Tests/CodeIndex.Tests.csproj`
- 対象フレームワーク: `net8.0` と `net9.0`
- 主な直接参照の test-only package: `Microsoft.NET.Test.Sdk`、`xunit`、`xunit.runner.visualstudio`、`coverlet.collector`、`Microsoft.Data.Sqlite`、`FsCheck.Xunit`
- これらの test-only package は `src/CodeIndex` の本番依存ルールとは別であり、runtime 側は引き続き `Microsoft.Data.Sqlite` のみを許容する。
- `FsCheck.Xunit` はランダム生成入力に対する普遍的不変条件（never-throws、idempotence、"出力が downstream consumer で parse 可能" 等）を表明する property-based テスト専用です。例ベースの `[Fact]` / `[Theory]` を置き換えるのではなく補完するもので、普遍量化された主張なら FsCheck、特定の具体ケースが契約なら例ベースという形で使い分けてください。
- テスト並列実行: 独立したテストクラス間ではデフォルトで有効です。SQLite pool の解放、環境変数の変更、カレントディレクトリの上書きのような process-global 状態を触るテストは、明示的な non-parallel collection に入れてください。`Console.Out` / `Console.Error` を差し替えるテストは `TestConsoleLock.Gate` で lock してください。
- CI は `tests/CodeIndex.Tests/CodeIndex.Tests.runsettings` 経由でテストプロジェクトを実行し、VSTest の blame crash / hang 収集、45分のセッションタイムアウト、60秒の xUnit long-running 診断を有効にします。初回失敗時は suite を1回だけ再実行し、再実行で成功した場合は TRX / blame artifact と一緒に `TestResults/flaky-retry.txt` を upload して、その実行を疑わしい flaky run として扱います。TRX telemetry summary と test-result artifact upload は失敗または retry 成功 lane だけで実行し、初回で clean に成功した lane では実行しません。stream された test output も、失敗後に upload / timeout inspection が必要な場合だけ `TestResults` 配下へ書き、failure log directory もその failure path でだけ作成します。telemetry summarizer は `--configuration Release` で実行し、failure diagnostics 中に必要なら helper を build するため、全 matrix lane で TRX summary を出せます。XPlat Code Coverage の収集は `ubuntu-24.04` / `net8.0` lane に限定し、すべての active CI lane で full suite を実行しつつ collector overhead を避けます。OS coverage は production CLI target の `net8.0` で実行し、`net9.0` compatibility coverage は `ubuntu-24.04` のみに絞ります。テスト実行は locked restore と Release build の後に `--no-build` で走らせます。primary lane は audit / publish coverage のため solution 全体を restore し、その後 `tests/CodeIndex.Tests/CodeIndex.Tests.csproj` を matrix framework 向けに build します。non-primary lane は同じ per-framework build の前に、`RestoreTargetFrameworks` でその test project の matrix framework だけを restore します。CI は production 用の `8.0.413` SDK と pinned `9.0.301` SDK を全 lane に入れます。`global.json` が SDK roll-forward を無効化しており、target framework 別の restore / build 絞り込みより前に SDK 解決が走るためです。`TestResults` 出力ディレクトリは `CodeIndex.Tests.runsettings` だけが管理します。ローカルの `dev.sh coverage` も同じ所有関係に従い、2 つ目の results-directory 引数は渡しません。`ubuntu-24.04` / `net8.0` lane では test project の未使用 `net9.0` target を build しません。`net9.0` build coverage は Ubuntu compatibility lane で維持します。また、formatting verifier は `make lint` だけを使います。NuGet cache key は全 project file ではなく `packages.lock.json` と `global.json` に基づき、OS 単位の restore key で partial cache reuse も許可します。package 入力の drift は locked restore で検出しつつ、テスト用 project だけの変更では package cache を失効させません。weekly mutation workflow も pinned Stryker global tool と NuGet package を cache し、変更のない test tooling を scheduled mutation run で再インストールしないようにします。
- CI の初回テスト実行と1回だけの retry は同じ workflow helper 経由にし、logger、blame、coverage 引数が drift しないようにしてください。PowerShell helper がテストの exit code を返す場合は、stream されたテスト出力を関数の success stream に載せず、代入で数値の exit code だけを受け取れるようにします。
- coverage collection は primary lane の初回 test attempt だけで実行し、flaky classification の1回だけの retry では同じ test 引数を再利用しつつ coverage collector を再実行しないでください。
- matrix test invocation は shared test helper の前に各 lane の scoped locked restore と Release build が完了しているため、`--no-build` と `--no-restore` の両方を使ってください。
- primary-lane publish も `--no-build --no-restore` を使い、Release test project 経由で build 済みの production project output と dependency graph を再利用してください。
- release workflow の test も solution の locked restore と Release build 後に `--no-build --no-restore` を使い、runtime lane ごとの dependency 再評価を避けてください。
- package audit、primary-lane build/lint、coverage の収集、coverage artifact upload、publish、build artifact upload は matrix の `primary_lane` 値に揃えてください。lane の組み合わせは明示的な matrix entry で一度だけ定義し、後続 step で再計算したり exclude したりしません。
- workflow path filter では `**.md` がすでに対象とする個別 Markdown file を重複して列挙せず、同等の push / pull-request filter を同期させます。Build/Test と CodeQL は focused license-policy workflow が所有する license text path も無視します。
- test workflow は pull-request run を workflow と pull request ごとに group 化し、それ以外は一意な run ID を使ってください。古い pull-request run だけを cancel し、push、schedule、manual run は独立させます。
- TRX telemetry summary は test project の direct reference 経由で build 済みの Release telemetry tool を再利用し、test step 後に restore/build を繰り返さないでください。
- changelog-fragments workflow は changelog tool の lock file を使って package を cache し、locked restore を1回行ってから `dotnet run --no-restore` で検証してください。
- focused license-policy workflow は NuGet package を cache し、`net8.0` だけを locked restore した後、dependency resolution を繰り返さないよう filtered test を `--no-restore` で実行します。
- C# CodeQL lane は setup-dotnet の lock-file-keyed NuGet cache を使い、Actions だけの lane は SDK setup と package cache の両方を skip します。

## テスト構成

テストプロジェクトは、本番コードの責務にかなり近い形で分かれています。
巨大 suite を移動する場合や `Skip =` case を追加する場合は、現在の分割順序、skip 分類、巨大ドキュメントの境界を追跡する `docs/test-doc-maintenance-plan.md` を先に確認してください。

- `ChunkSplitterTests.cs`、`SymbolExtractorTests.cs`、`ReferenceExtractorTests.cs`、`SearchSnippetFormatterTests.cs`、`DbPathResolverTests.cs`、`ConsoleUiTests.cs`
  インメモリ入力中心の、純粋またはほぼ純粋な振る舞いのテスト。
- `SymbolExtractor*Tests.cs` と `ReferenceExtractor*Tests.cs`
  extractor のカバレッジは言語または機能領域ごとの partial test class に分割し、共有 helper は root 側の `SymbolExtractorTests` / `ReferenceExtractorTests` に残します。
  巨大 suite から繰り返しの extractor シナリオを切り出す場合は、言語、build-file 形式、protocol surface など読みやすい領域ごとの partial file にまとめ、raw substring や predicate assertion の繰り返しより小さな semantic assertion helper を優先してください。
  fixture が同じ kind の複数 symbol name だけを検証する場合は、言語別 partial をまたいで `AssertSymbolsContain(...)` を使ってください。line、container、subtype、return type などの metadata を検証する場合は直接 predicate を維持します。
  同様に `(reference kind, container, symbol names)` の繰り返し検証には `AssertReferencesContain(...)` を使い、flag、context、line など edge 固有 metadata の検証には predicate を維持します。
  複数の reference name が同じ kind と完全一致 source context を共有する場合は `AssertReferencesContainInContext(...)` を使い、context がより詳細な edge contract の一部にすぎない場合は直接 predicate を維持します。
  1つの reference kind に対する否定チェックには `AssertReferencesDoNotContain(...)` を使い、container、context、line など他の metadata に依存する除外は直接 predicate を維持します。
  `ReferenceExtractorTests.ExtractSymbolsAndReferences(...)` は symbol 抽出から reference 抽出までの共通 setup を所有します。fixture が特殊な path や workspace symbol setup を必要としない場合は 2 つの extractor 呼び出しを繰り返さずこの helper を使い、reference だけを検証するテストでは未使用の `symbols` local を残さず symbol 側を `_` で捨ててください。
- `FileIndexerTests.cs`、`FileIndexerContentLoadingTests.cs`、`FileIndexerTestSupport.cs`
  ファイル走査、言語判定、scan result 言語の再利用、content loading / canonicalization、checksum、レコード構築のテスト。拡張子なし shebang 判定の「先頭物理行 256 byte 上限」、binary/NUL byte 除外、Windows 専用の 260 文字以上 path walker/purge カバレッジも含みます。共有 `FileIndexerTests` helper は `FileIndexerTestSupport.cs` に置きます。
- `PathCompatibilityMatrixTests.cs`
  path casing、boundary-prefix 比較、Windows long-path prefix、POSIX の sensitive file 権限、symlink / dangling entry の scan 挙動、既定 skip directory 配下の submodule passthrough、git skip-worktree path 正規化を横断する compatibility matrix カバレッジです。同じ platform/path 前提を indexing、Git helper、DB/query、installer、status の各 surface で見える形にしたい場合は、新しい fixture シナリオをここに追加してください。
- `DatabaseTests.cs`、`DbReader*Tests.cs`
  SQLite スキーマ、書き込み経路、マイグレーション、クエリ挙動のテスト。DbReader のカバレッジは search、SQL qualified name、file dependency、impact、symbol query などの query family ごとの partial suite に分割し、共有の seed 済み fixture 状態は root 側の `DbReaderTests` に残します。
  `DbSchemaConstraintTests.cs` は DB readiness check が code enum と SQLite CHECK 句の drift を検出できるよう、schema constraint と `SymbolKindCatalog`、必須 file foreign key の同期も固定します。
  hotspot ranking fixture は各 ranking threshold を跨ぐ最小 count を使ってください。structural-rank test では、raw reference 比較をわずかに超える側と symbol-count threshold をわずかに超える側を用意し、境界から大きく離れた件数まで膨らませないでください。
  checkpoint listing cap fixture は checkpoint count cap を 1 件だけ超え、inspected-file cap は 1 checkpoint だけで超えてください。両方の cap を掛け合わせても boundary coverage は増えず、filesystem work だけが増えます。
- `ConcurrencyTests.cs`
  WAL snapshot と shared-writer の stress test。concurrent reader/writer snapshot
  テストは reader / writer の十分な反復を観測した時点で停止し、遅い host 用に
  2 秒上限だけを fallback として残します。race が既に十分 exercised された fast lane
  で上限時間を使い切らないよう、固定 sleep に戻さないでください。shared-writer
  blocking test は固定 grace period で sleep せず、worker task が開始したことを signal
  してから blocked のままであることを検証します。
- `LegacySchemaMigrationTests.cs`
  エンドツーエンドのアップグレード経路: カラム追加前のレガシー DB を用意し、`TryMigrateForRead` 経由で開いてから NULL になりうるシンボル列を触る read path（outline、シンボル検索、近傍、unused、analyze バンドル）を一通り叩き、#58 / #49 の実機失敗モードを固定する。
- `IndexCommandRunner*Tests.cs`、`QueryCommandRunner*Tests.cs`、`ProgramCliTests.cs`、`InstallScriptTests.cs`
  CLI の引数解析、コマンド実行、installer 挙動のテスト。Index command coverage は run mode または機能領域ごとの partial suite に分割し、Query command coverage は command family ごとの partial test class に分割して、共有 console / fixture helper は一箇所に保ちます。`ProgramCliTests.cs` はグローバル引数の解釈や完全な CLI 起動フローのように subprocess 経由で確認すべき Program エントリポイント挙動を扱い、`InstallScriptTests.cs` は `install.sh` を library mode で source した bash snippet を実行して、実ネットワーク install を行わずに release installer の回帰を固定する。
  invalid scalar input だけが異なる argument-validation variant は、case ごとの state や discovery identity が不要なら1つの database fixture を共有し、fact 内で反復してください。
  excerpt の focus-column validation も zero と non-numeric value にこの規則を適用し、1つの indexed Markdown fixture を再利用してください。
  inspect path-line の exact/enclosing-symbol case は1つの indexed source fixture を再利用し、read-only line query を fact 内で反復してください。
  definition と symbols の exact-mode conflict validation は1つの空databaseとcross-command flag-pair tableを共有してください。
  Razor directive kind-filter query は1つの indexed component fixture を共有し、route、implements、attribute、layout の期待値を1つの fact 内で反復してください。
  symbols literal-query coverage は、compact風のtextが展開されないことを確認するdouble-dash形式と明示的`--query`形式で1つの空databaseを共有してください。
  symbols の hotspot と references ranking query は同じranking signalのread-only viewなので、1つのsymbol-sort fixtureを共有してください。
  `TrimmedCliTestHelper` が trimmed publish setup と published CLI subprocess execution を所有します。published CLI smoke coverage は共有の non-single-file publish を使い、test process あたり 1 回の publish cost に抑えてください。single-file test は apphost shape が別なので、明示的な per-test publish のままにします。published CLI smoke test は production CLI が `net8.0` target であることに合わせて `net8.0` test target でのみ実行し、`net9.0` では高コストな publish を繰り返さず focused な in-process test で cross-target behavior を維持します。
  installer snippet と Docker entrypoint script coverage は `ProductionCliFactAttribute` / `ProductionCliTheoryAttribute` を使い、これらの shell script が target framework 非依存で production CLI が `net8.0` target であることに合わせて `net8.0` test target でのみ実行します。
  `RunBuiltCli` / `RunCliInSubprocess` subprocess coverage は、timeout guard 付きの subprocess probe も含め、subprocess が production `net8.0` CLI に解決される場合は `ProductionRuntimeFactAttribute` / `ProductionRuntimeTheoryAttribute` を使って `net8.0` test target でのみ実行し、direct in-process command-runner test は cross-target のままにします。
  production-runtime test が built CLI を indexed fixture の作成にだけ必要とする場合は、subprocess boundary を indexing step に残し、count/path/format variant も含め、process boundary の挙動に依存する assertion を除いて query assertion を command runner helper 経由で in-process 実行してください。
  `InstallScriptTests.RunInstallerSnippet` は bounded timeout を強制し、timeout 時は snippet の process tree を kill するため、installer 回帰は suite を hang させずに captured output 付きで失敗します。
- `CiWorkflowTests.cs`、`ReleaseWorkflowTests.cs`、`ReleaseWorkflowTests.PackageHelpers.cs`
  CI と release workflow の契約テスト。test-result artifact、retry-output、install、Homebrew、changelog、release-payload job split、container image、SBOM、NuGet publish、secret scope、SDK pin、tool/action pin、runner/cache policy の契約も含め、繰り返しの関連する workflow/script string contract assertion は小さな grouped helper に寄せ、テスト本文が確認している契約を読み取りやすくしてください。contract が ordinal matching を明示的に必要とする場合は comparison-aware helper を使います。Release workflow の package-normalization ZIP fixture helper は `ReleaseWorkflowTests.PackageHelpers.cs` に置き、workflow assertion が workflow 契約の近くに残るようにします。
- `PackageNormalizeDiagnosticsTests.cs`
  package normalizer の diagnostic redaction カバレッジです。高負荷の full-suite 実行で、期待される path / secret placeholder が flaky に見えないよう、timeout budget の assertion は共有 diagnostic redaction policy と同期させてください。
- `DocumentationStatusContractTests.cs`、`DocumentationDriftTests.cs`
  checked-in documentation の契約テスト。`RepositoryTestPaths` を使って、status field、workflow 参照、文書化された `cdidx` コマンド例、release/changelog workflow の snippet、代表的な英日 guide セクションの同期を維持します。
  `DocumentationStatusContractTests.cs` は readiness、maintenance、MCP status field も含め、status JSON support contract が user guide と agent guide に残るようにします。
  複数行の YAML snippet を比較する workflow test は `RepositoryTestPaths.ReadNormalizedWorkflow(...)` を使い、CI、release、package-lock、mutation workflow contract 間の line-ending normalization を一か所に集約します。
  同じ test 内の全 assertion でその normalized value を再利用し、同じ workflow の raw copy と normalized copy を重複して読み込んだり保持したりしません。
  `Dockerfile` など workflow 以外の checked-in multiline fixture には `RepositoryTestPaths.ReadNormalizedText(...)` を使い、workflow 固有の読み込みは引き続き `ReadNormalizedWorkflow(...)` を使います。
  release-container contract test では `ReadDockerfile()` と `ReadDockerIgnore()` を使い、canonical fixture path が workflow suite 間でずれないようにします。
  `RepositoryTestPaths` は checked-in text、normalized derived text、normalized workflow inventory を test process の生命期間 cache します。不変の repository contract だけに使い、fixture を書き換えるテストは独自の一時 path を使ってください。
  license-policy contract test は legal notice、workflow file、distribution doc に同じ accessor を使い、repository root の再検出や重複 file read を行いません。
  repository-backed の documentation、source-audit、JSONL-policy、trimmed-publish test は suite ごとの上位 directory walk を持たず、`RepositoryTestPaths.Root` を再利用します。
  大規模な command-runner、installer、extractor suite の legacy root helper も、その単一の cached root へ委譲します。
  changelog limit test も別の root walk を行わず、`RepositoryTestPaths` 経由で checked-in file を解決します。
  workflow 全体の policy audit には `RepositoryTestPaths.ReadNormalizedWorkflows()` を使い、列挙順、extension filter、file name、normalization を個別 test class 内で再構築せず共有します。
  1つの policy test が複数の step-level rule を検証する場合は、workflow step block を一度だけ解析して保持し、assertion family ごとに multiline step regex を再実行せず保持済み block を絞り込みます。
- `IndexCommandRunnerTests.Run_CancelDuringFreshIndex_ReturnsInterruptedJson`、`Run_CancelDuringDryRunScan_ReturnsInterruptedJson`、`Run_CancelBeforeFreshScan_ReturnsInterruptedJson`
  Ctrl-C/SIGINT 配線後に使われる in-process cancellation 経路を、scan 中のキャンセルも含めて検証し、interrupted index run が標準の JSON error contract を返し続けることを固定する。
- `IndexCommandRunnerTests.SymbolExtractionWorker_LegacyEnvironmentHooksAreIgnored_Issue3398`
  isolated symbol worker を起動し、legacy worker 環境変数が無視されることを検証します。この callback budget はプロセス起動時間も含むため、通常の in-process チェックより意図的に広く取り、ローカル負荷で legacy-env 回帰テストが timeout flake にならないようにします（#3863）。
- `IndexCommandRunnerTests` と `FileIndexerTests` は `CSharpStaticInterfacePrepass` のテキスト判定、raw-byte、chunked raw-token、streaming file 契約 probe も扱います。prepass がファイル全体の割り当てを避けても UTF-8 / UTF-16 の static-interface 契約候補を落とさないよう、byte-array、chunked、file-level probe のカバレッジを揃えてください。
- project-marker budget の integration coverage は directory budget を最小境界に override し、child を 1 件だけ列挙します。warning 伝播の検証だけのために本番の 8,192-directory cap を実体化しないでください。
- `IndexWatchRunnerTests.RunCore_CancellationToken_StopsImmediately` と `RunCore_EmitsHumanFriendlyStartStop_WhenJsonDisabled`
  リダイレクトした console 出力の下で watch loop の起動と停止を検証する。未使用の index を事前構築せず非同期 watch core を直接呼び、同期的な起動 prefix が start event を出力した後に cancel して bounded shutdown wait を行う。この経路に index setup、専用 thread、signal polling、固定 sleep を再導入しないこと。
- `BackgroundTaskObserverTests`
  は `BackgroundTaskObserver` の fault-only continuation 契約に依存します。canceled
  task は直接 await し、warning が抑止されたことを示すための cancellation 後の固定 sleep
  は不要です。
- cancellation だけを検証する timeout test は、fixture に無関係な wall-clock 完了経路を持たせないよう infinite cancellable delay を使います。
- SQL diagnostic truncation test は、長さが増え続ける数千個の `UNION` 句を組み立てず、最小の固定幅 suffix で文字数 cap を超えます。
- 内容が契約に無関係な oversized file guard は sparse な length-only fixture を作り、同サイズの managed byte array 割り当てを避けます。
- full-scan と scoped-update の oversized-file warning test は `TestProjectHelper.WriteSparseFile(...)` を共有し、両方の実行モードで 10 MiB fixture 割り当てを避けます。
- oversized ignore-file rejection も同じ sparse helper を使います。size preflight が rule 内容を読む前に拒否するためです。
- per-line oversize detection は 4 本の短い物理行を使います。契約は各行で独立しており、数百回の反復は境界 coverage を増やしません。
- captured-output overflow test は、より大きな超過量自体が契約でない限り、文字数 budget を 1 文字だけ超えます。
- suggestion store と archive cap の test は、どちらの persisted-size limit でも array を割り当てず、1 つの sparse-file helper を共有します。
- persistent-log rotation test は 1 MiB の zero buffer を割り当てず、`FileStream.SetLength(...)` で既存 log fixture のサイズを設定します。
- JSON-depth 境界 fixture の繰り返し bracket には、`Enumerable.Repeat` pipeline ではなく固定幅の文字列を使います。
- CSV entry cap test は index / query parser 間で `TestProjectHelper.RepeatCsvEntry(...)` を共有し、境界 fixture の構築を揃えます。
- raw FTS operator 境界 test も、grammar 固有の separator を指定して同じ joined-entry builder を再利用します。
- synchronized console write の coverage は、2 つの concurrent producer を検証できる最小の反復 slow-writer workload を使います。stress test として iteration 数を増やさないでください。
- `SymbolExtractorTests.Extract_CSharp_InstallScriptFixture_CompletesWithinPracticalBudget`
  は実ファイル `InstallScriptTests.cs` を C# 抽出に通す coarse な runaway guard です。wall-clock の予算は benchmark より意図的に広く取り、遅い / 混雑した CI host で通常の揺れだけにより suite が失敗しないようにしています。
- `SymbolExtractorTests.Extract_JavaScriptLargeExportedObjectLiteralProperties_CompletesWithinPracticalBudget` と `Extract_CSharp_ReferenceExtractorFixture_CompletesWithinPracticalBudget`
  は既知の大きな symbol extraction fixture に対する広めの runaway guard です。full suite の負荷に耐えるよう budget は十分広く保ち、benchmark 閾値としてではなく、焦点を絞った最適化根拠がある場合にだけ締めてください。
- extractor の広い `*CompletesWithinPracticalBudget` runaway guard は primary の `net8.0` test target だけで実行します。focused な extractor 機能テストは cross-target のまま維持しますが、その guard が target-framework 固有の契約を証明する場合を除き、大規模 fixture の budget guard をすべての target framework で重複実行しないでください。
- `IndexCommandRunnerTests.RunBackfillFold_PublishedTrimmedBinary_SerializesSuccessAndErrorJson`
  は trimmed な RID 固有 CLI を publish し、SDK が生成した entry point（`dotnet` 経由の `cdidx.dll`、または native の `cdidx`/`cdidx.exe` apphost）を実行します。この publish smoke は NuGet 脆弱性監査を無効化します。package advisory の検証は通常の build/test workflow の package vulnerability check が担い、この runtime serialization テストの責務ではないためです。macOS arm64 では SDK/ILLink が `cdidx` に到達する前にクラッシュし得るため、このテストは skipped として報告されます（#2586）。self-contained publish output に常に `cdidx.dll` が出るとは仮定しないでください。
- `QueryCommandRunnerTests.RunPublishedTrimmedCli_SerializesQueryJsonAndSupportsRazorAliases`
  は 1 つの trimmed RID 固有 publish output で query JSON coverage と `cshtml` / `razor` の C# Razor 言語 alias を検証し、publish 専用の lock file をテストの一時 intermediate directory 配下に書き、publish smoke の NuGet 脆弱性監査を無効化し、SDK が生成した `cdidx` entry point を実行します。source tree の lock file 変更、advisory feed の可用性、DLL 固定の publish layout には依存しません。`dotnet publish` が、利用できない `Microsoft.NETCore.App` runtime を必要とする SDK/ILLink tool に到達した場合は、`cdidx` を実行する前に失敗させるのではなく、その missing-runtime diagnostic を付けて skipped として報告します（#3571）。このテストも macOS arm64 では、`cdidx` に到達する前に SDK/ILLink がクラッシュし得るため skipped として報告されます（#2586）。
- `McpServer*Tests.cs`
  MCP の JSON-RPC 挙動とツール出力のテスト。大きな server coverage は tool call、tool listing、protocol/session handling、error handling ごとの focused partial suite に分割し、共有の seed 済み fixture 状態は root 側の `McpServerTests` に残します。request-timeout test は固定 sleep ではなく signal-gated delay hook を使います。request を開始し、hook が始まったことを確認してから timeout response を bounded wait で待つことで、timeout response 後に in-flight action が drain されることは保ったまま、設定した timeout 分だけを待つようにします。
  stdio response-order test も同じ signal-gated pattern を使い、response serializer で sleep する代わりに synthetic transport が parse-error path を signal するようにします。
  rate-limit-disabled coverage の繰り返し成功 call には軽量な `languages` tool を使い、limiter bypass だけの assertion で `status` の database aggregation cost を繰り返し支払わないでください。
- `HttpMcpTransportTests.cs`
  HTTP MCP transport の挙動。認証レスポンス、warm server reuse、並行リクエスト、リクエストログを含みます。リクエストログの assertion は、独立に処理される HTTP リクエスト間の callback 順序を仮定せず、記録内容を検証してください。
  request-log metadata の long path、long JSON-RPC ID、over-depth ID 境界は1つの sequential logger harness を共有し、callback 順序ではなく record 内容で対象を選んでください。
  bearer-token denial variant は、共通 wire response、challenge header、redaction、request-log outcome を検証する1つの table-driven harness を共有し、hash comparison 経路を通す同長 mismatch もその表に含めてください。
  一致する canonical と lowercase の bearer scheme は、RFC auth-scheme casing に独立 server state が不要なため1つの authenticated harness を共有してください。
  transport disposal coverage は1つのlive event-stream lifecycleで、concurrent idempotent disposal、stream cancellation、server-loop completion、owned semaphore releaseをまとめて検証してください。
  event-stream lifecycle coverage は1つの harness で response header、並行POST処理、oversized server-side closure、client disposal を順に検証し、production heartbeat interval ではなく短い test-owned keep-alive を使ってください。
  event streamなしのinitialize coverageは1つのdefault harnessでexplicit protocol negotiationとdefault handshakeを検証し、event-stream notification deliveryは独立sessionを維持してください。
  basic HTTP endpoint coverageは1つのdefault harnessでnotification 204、root methodの405/Allow、structured healthy `/healthz` responseを検証してください。
  invalid/oversized health-provider JSON fallbackはrequest間でproviderを差し替え、1つのharnessで検証してください。
  health degradation coverageは1つのharnessでevent-stream drop後もhealthyであることを確認し、その後response-cleanup failureを注入してstatusがdegradedへ遷移することを検証してください。
  warm-server coverageは1つのharnessでsequential initialize/list、並行correlated ping、empty-body 204、その後のsuccessful requestを検証してください。
  malformed/deep cancellationとdeep JSON-RPC response payloadは、out-of-band handlingを迂回する契約の検証で1つのdirect transportとqueue/read/write assertion helperを共有してください。
- `GitHelperTests.cs`、`GitProcessRunnerTests.cs`
  worktree や commit ベース更新、direct git process runner diagnostics、git subprocess の cancellation を含む Git まわりのテスト。実 repo を作る、または real/fake git subprocess を起動するテストは `ExternalProcessFactAttribute` / `ExternalProcessTheoryAttribute` を使い、`net8.0` test target だけで実行します。純粋な `.git` metadata parsing と trusted-candidate enumeration は cross-target のままにしてください。Timeout と cancellation の wall-clock assertion は fake git script の自然完了より短く保ちつつ、macOS CI の scheduling や process cleanup の遅れを許容する余裕を持たせます。commit-ref validation 後に使う fake git script は `rev-parse --verify <ref>^{commit}` の検証対象 commit 引数を返し、timeout テストが意図した git command まで到達するようにします。
- `WorkspaceMetadataEnricherTests.cs`
  ワークスペース鮮度と git メタデータ付与のテスト。
- `SuggestionStoreTests.cs`
  ローカル提案JSON蓄積: ハッシュ重複排除、永続化、破損復旧、アトミック書き込み。
- `SourceCodeDetectorTests.cs`
  ソースコード漏洩防止: 許容される自然言語入力 vs 拒否されるコードブロック（フェンス、インデント、import連打等）。
- `ConsoleUiTests.cs`
  completion flag parity のテストでは、bash / zsh / fish の共有 flag extraction を 1 つの helper に通し、個別テストは command 固有の必須 flag や shell 固有 alias の確認に集中させてください。
- `ReportCommandRunnerTests.cs`
  report log-tail fixture では、`Path.Combine(workDir, "logs")`、`Directory.CreateDirectory`、ad hoc な `File.WriteAllText` setup を繰り返さず、ローカルの log directory / log file helper を使ってください。
- `PostExtractionHookTests.cs`
  post-extraction hook の discovery、mutation、diagnostics、callback budget、collectible hook assembly cleanup のテスト。重い hook worker と collectible assembly-load の integration test は `ProductionRuntimeFactAttribute` を使って `net8.0` production target でのみ実行し、direct worker protocol と metadata test は cross-target のままにします。timeout / cancel された callback のテストは、hook delay を leak-observation window より短くし、1 秒丸ごとの absence check には戻しません。worker kill の回帰がある場合は assertion が終わる前に completion marker が書かれるようにします。hook 関連の環境変数と test-only callback budget 状態を変更するため、このクラスは non-parallel な `SQLite pool sensitive` collection に入れます。
- `GitHubIssueReporterTests.cs`
  GitHubトークン解決ロジック（CDIDX_GITHUB_TOKENのみ。汎用GITHUB_TOKENは無視）、送信前のコード scrubbing、冪等性チェック、rate-limit diagnostics を扱います。
- `PackagesLockTests.cs`
  すべての target framework で同期が必要な direct package reference の NuGet lock-file guard。CI の locked restore を通すための net9.0 compatibility reference も対象です。
- `ConcurrencyTests.cs`
  並行読み取りと書き込み中読み取りシナリオ（WALモード検証）。issue #180 の bug-catching な snapshot 隔離回帰テストを 3 つの multi-statement reader 経路について含む。(1) `GetStatus` は `refs == files * refsPerFile` の seed 不変条件を立て、並行観測が常にこの条件を維持することを要求する。(2) `AnalyzeSymbol` はシンボル `S` に対して reference/caller を対称に 1 対 1 で seed し、もう 1 ファイルを対称に toggle することで `inspect` / `analyze_symbol` bundle の `references.Count == callers.Count` を常に保証する。(3) `GetRepoMap` はベースラインの modified と新しい toggle 対象ファイルを用意し、`latest_modified == workspace_latest_modified` が常に一致することを要求する。各テストは対応する reader の DEFERRED transaction を外すと落ち、戻すと通ることを確認済み。
- `PerformanceTests.cs`
  bounded な CI smoke と大規模データベンチマークを扱います。`CiPerformanceSmoke_IndexAndSearchSmallFixture_StaysWithinBudget` と allocation budget guard は通常の `net8.0` suite で実行されるため production target 上の PR / CI blocking check ですが、benchmark ではなく重大な indexing/search または allocation 退行だけを拾う広めの budget を使います。10K+ の大規模テストは引き続きデフォルト Skip で、`--filter` で手動実行します。
- `.github/scripts/run-dotnet-tests.ps1`
  `dotnet.yml` の matrix test step は、test 引数構築、coverage gating、failure log capture 用の `TestResults` path ownership、TestSessionTimeout handling、1 回だけの flaky retry classification をこのスクリプトに委譲します。workflow YAML は matrix/lane parameter wiring に限定し、script contract や artifact/summarize gating を変更するときは `CiWorkflowTests` も更新してください。
- `.github/scripts/configure-windows-test-host.ps1`
  `dotnet.yml` と `release.yml` の Windows lane は、TMP/TEMP 固定と Defender 除外 setup をこのスクリプトで共有します。両 workflow の test-host performance 前提を揃えるため、スクリプトまたは workflow からの呼び出し contract を変更するときは `CiWorkflowTests` も更新してください。
- `DbRecoveryTests.cs`
  DB破損からの復旧とグレースフル劣化のテスト。`cdidx index` の filesystem setup failure（read-only DB file や書き込み不可の DB 親ディレクトリ）は、ユーザーが見る CLI JSON/stderr 境界を通すため `IndexCommandRunnerTests.cs` で扱います。
- `JsonOutputSnapshotTests.cs`、`JsonOutputSnapshotHelper.cs`
  CLI の `--json` 出力契約に対するゴールデンファイル回帰フィクスチャ (issue #1548)。各テストは `status` / `search` / `references` / `impact` / `excerpt` を決定的なインメモリ fixture に対して実行し、揺らぐフィールド（timestamp、絶対パス、commit SHA、FTS5 score、SQLite page count など）を正規化したうえで `tests/CodeIndex.Tests/golden/` 配下のファイルと差分比較します。フィールドの rename / 削除 / 並び替え / 新規追加が起きると snapshot が失敗するため、契約変更は意図的な golden 更新と同じ PR で揃えざるを得ません。更新手順は下記「JSON `--json` 出力 snapshot」を参照してください。
- `PropertyBasedParserTests.cs`
  issue #1572 で挙げられたパーサー系経路に対する FsCheck 駆動の property テスト: `ArgHelper.WantsHelp` と `ProgramRunner.IsProjectPathArg` が任意入力で例外を投げないこと、`FileIndexer.NormalizePathSeparators` が二重適用で idempotent であること、literal-safe な FTS5 サニタイザ (`DbReader.SanitizeFtsQuery`) が常にインメモリ FTS5 仮想テーブルで parse 可能なクエリを出力すること。`ArgHelperTests.cs` / `QueryCommandRunnerTests.cs` の例ベーステストを置き換えるものではなく補完します。
- `TestProjectHelper.cs`、`TestDeterminism.cs`、`RepositoryTestPaths.cs`、`TestConsoleLock.cs`
  共有テストヘルパー。package-normalizer CLI / diagnostic / legacy-temp / size-limit / entry-name / external-attribute / path-casing / filesystem-traversal / workspace-manifest / active-workspace / workspace-use / config-show / DB path resolver / file-indexer scan/probe fixture も含め、テストが一時 project directory を所有する場合は `TestProjectHelper.CreateTempProjectScope` を優先し、通常の temp cleanup は `TestProjectHelper.DeleteDirectory` / `DeleteFile`、SQLite の `-wal` / `-shm` sidecar を DB と一緒に消す必要がある場合は `DeleteSqliteDatabaseFiles` を使ってください。失敗した filesystem delete に反応する bounded cleanup retry loop の中だけ、`TestProjectHelper.WaitForFileSystemReleaseRetry` を使ってください。境界付きポーリング、最終的な条件成立のアサーション、ブロック中タスクの観測、同時開始するワーカー、固定シード乱数入力には `TestDeterminism` を使ってください。MCP indexing test の一時 codeindex DB cleanup もこの helper に寄せてください。Windows long-path fixture のように特殊な path shape が必要な場合を除き、ローカルの retry loop をコピーしないでください。

## 規約

- テスト名は説明的にする。現在のスイートは `Method_Scenario_ExpectedBehavior` 形式が中心です。
- テストは決定的に保つ。マシン全体の git 設定、ロケール依存出力、外部の残存ファイルに依存しないこと。
- 本番コードが `TimeProvider` を受け取れる場合は `ManualTimeProvider` を使い、fixture data の時刻は wall clock ではなく明示的に進めてください。ランダム入力は `TestDeterminism.CreateRandom` を使い、同じ timeline とデータを再実行できるようにします。境界付きポーリング / 最終的な条件成立のアサーションには、ローカルの `Task.Delay` loop や固定 sleep ではなく `TestDeterminism.WaitUntilAsync` または同期版の `WaitUntil` を使い、短い不在・安定性の観測には `AssertConditionRemainsTrue` を使ってください。ワーカーを同じ gate から開始したい場合は `TestDeterminism.RunConcurrentlyAsync` を使ってください。
- 広いスナップショット風の検証より、小さなフィクスチャと明示的な assertion を優先する。例外は `--json` 出力契約の harness (`JsonOutputSnapshotTests`) で、こちらは意図的にフィールド形状全体を固定します（下記「JSON `--json` 出力 snapshot」参照）。
- raw bytes と canonical content のような境界契約で期待値生成が重複して読みづらくなる場合は、各 assertion に低レベル式を複製せず、契約名が分かる小さな local helper に寄せてください。
- file-indexer テストが同じ初回 index 後の normalized path と結果だけを検証する場合は、独立した file mutation を 1 回の `--files` update にまとめてください。
- 関連する index invalidation の各中間状態を assertion する場合は 1 fixture にまとめてください。static-interface contract の追加、除去、復元、削除を同等の project 再構築へ分割しないでください。
- cap issue lifecycle test は low/high boundary の変更ごとに issue を assertion する場合、full-scan と update の遷移で 1 つの indexed fixture を再利用してください。
- unreadable-directory の full-scan coverage は JSON/human diagnostics、purge protection、checkpoint 作成、successful retry を 1 fixture に保ち、共通の partial scan setup を再構築しないでください。
- checkpoint write/delete failure coverage は 1 つの unreadable-directory fixture を、注入した save failure から successful save、注入した delete failure へ遷移させてください。
- HEAD freshness coverage は 1 つの two-commit fixture で、`--files` refresh 後は stale のまま、`--commits HEAD` refresh 後は current head が matched になることを検証してください。
- 同種の key/value 期待値を長い表で固定するテストでは、期待値をデータとして残し、繰り返しの lookup/assertion 形は helper に通してください。重複行を見つけやすくするためです。
- extractor テストで `SymbolName` / `ReferenceKind` の同じ predicate 形を positive / negative reference assertion の両方に繰り返す場合は、semantic assertion helper を使い、各 call site には container name/kind、context、line、column、除外 symbol set など挙動差分だけを残してください。
- 境界を証明するテストでは、その境界をまたぐ最小の fixture を使う。1 ページ、1 chunk、1 cache、1 offset overflow で十分なら、それ以上に synthetic data を増やさない。ただし、より大きいサイズ自体が契約の一部なら例外です。
- 本番コードのコメントやエラー文字列が英日併記前提なら、重要な箇所ではその期待もテストに反映する。
- ユーザーに見える挙動を変えたら、テストに加えて `CHANGELOG.md` と関連ドキュメントも同じ変更に含める。

### 共有状態と並列実行の監査

テストクラスを追加または移動する前に、次の一覧を確認してください。

- SQLite pool reset、`SqliteConnection.ClearAllPools()` の直接呼び出し、プロセスの current directory 変更、process-global な環境変数変更: クラスを non-parallel な `SQLite pool sensitive` collection に入れる。
- 環境変数: `EnvironmentVariableScope.Capture(...)` を使い、setup failure や assertion failure でも単一の cleanup 経路で元の値に戻す。
- `Console.Out` / `Console.Error` の差し替え: capture / swap 期間全体を `TestConsoleLock.Gate` で lock する。
- 一時 repo / file: 可能な限り `TestProjectHelper` 経由で作り、user-level の git config に依存しない。
- 長時間または performance 系テスト: デフォルト skip にするか、決定的で十分広い budget を与える。CI の xUnit long-running 診断に出た場合は、閾値を締める前に runner 負荷を確認する。

## 共通ヘルパー

### `TestProjectHelper`

新しいセットアップコードを書く前に、既存ヘルパーを優先してください。

- `CreateTempProject(prefix)` は一意な一時ワークスペースを作成します。
- 独自に `Path.GetTempPath()` / `Guid.NewGuid()` を組み合わせた directory helper を増やさず、`CreateTempProject(prefix)` を使ってください。既存呼び出し側の読みやすさを保つ場合だけ、local wrapper は prefix 固有の薄い委譲に留めます。
- `ProjectPath(projectRoot, ...)` は temp project からの相対 fixture path を解決し、その root の外へ出る絶対 path や `..` escape を拒否します。
- `CreateDirectory(projectRoot, ...)`、`WriteTextFile(...)`、`WriteTextFiles(...)`、`WriteBinaryFile(...)`、`AppendTextFile(...)`、`ReadTextFile(...)` は fixture directory 作成と file setup を集約します。path が temp project に属する場合は、ローカルな `Path.Combine` + `Directory.CreateDirectory` + `File.*` の連鎖より優先してください。
- fixture encoding がテスト対象の挙動に含まれる場合は `WriteTextFile(..., Encoding)` overload を使い、temp project 配下の file に対して `File.WriteAllText(Path.Combine(...), ..., encoding)` へ戻さないでください。
- `FileIndexerTests` では、scan result assertion ごとに `Path.GetRelativePath(...)`、separator normalization、sorting、set creation を繰り返さず、ローカルの relative-path helper を使ってください。
- `InitializeGitRepo(projectRoot)` は git を初期化し、repo-local の `user.name` と `user.email` を設定します。
- `CreateProjectDb(projectRoot)` は `<projectRoot>/.cdidx/codeindex.db` を作成し、スキーマを初期化したうえで `codeindex_meta.indexed_project_root` に project root を書き込みます。
- `InsertIndexedFile(...)` は内容由来の checksum、chunks、symbols、references を含む現実的なインデックス済みファイルを挿入し、Python の symbol extraction には file path も渡すため、`__init__.py` ベースの再エクスポートテストで package 修飾名を扱えます。
- `RunGit(...)` は shell の quoting 問題に依存せず git を実行します。
- `DeleteDirectory(path)` は temp project cleanup のリトライと属性正規化を扱います。プロセス全体への干渉を避けるため、SQLite pool の解放は Windows で削除に失敗した場合のリトライ時だけに限定します。
- 一時 workspace の `finally` / `Dispose` cleanup では、そのテストシナリオ内で workspace を意図的に先に削除する場合も含めて、`DeleteDirectory(path)` を使ってください。
- `DeleteDirectory(path)` は存在しない path を内部で扱うため、`Directory.Exists(...)` で囲まず直接呼び出してください。
- `TestProjectHelper.DeleteDirectory` に委譲するだけの local `DeleteDirectory` wrapper でも、同じく直接呼び出してください。
- report / output bundle 用 workspace、changelog tool repository、duplicate-preflight fixture、file-indexer skipped-root / symlink fixture、import / export archive / rollback fixture、configured pattern / out-of-tree language workspace、audit / metrics write-target directory と private / global log workspace、program-runner extractor / cache / trace log workspace、temporary dotnet host fixture、watch runner workspace、DB purge / temporary cleanup workspace、reader BOM fixture、legacy-migration database workspace、MCP indexing fixture、suggestion-store / suggestion CLI fixture、小さな traversal / security fixture を含め、個別のテストファイルで再帰的な一時ディレクトリ cleanup を再実装しないでください。短い呼び出し名で既存テストの読みやすさを保ちたい場合も、ローカル wrapper は `TestProjectHelper.DeleteDirectory` への薄い委譲に留めます。
- `DeleteFile(path)` は standalone な temp DB cleanup をリトライし、pooled handle が削除を妨げる場合は同じ Windows 向け SQLite pool 解放フォールバックを使います。
- temp DB、lock、metadata、cache、script、outside fixture file の cleanup では、手書きの `File.Exists(...)` / `File.Delete(...)` pair ではなく `DeleteFile(path)` を優先してください。
- テストクラスに SQLite pool を解放する robust file cleanup wrapper が既にある場合、一時 DB cleanup はその wrapper に通し、DB ではない sidecar は `TestProjectHelper.DeleteFile` に留めてください。
- DB maintenance 系のテストファイルが standalone な `.db` ファイルを直接所有する場合は、`InitializeEmptyDb`、`ReleaseSqlitePools`、`DeleteDbFile` のような薄い local helper を置いて構いません。ただし wrapper は `DbContext`、`SqliteConnection.ClearAllPools()`、`TestProjectHelper.DeleteFile` へ委譲し、pool 解放の意図が見える名前にしてください。
- `SqlitePoolCleanup` は Windows 向け SQLite pool workaround を集約します。テストの生存期間中ずっと一時 SQLite ファイルを所有するテストは、`SqliteConnection.ClearAllPools()` を直接呼ぶ代わりに exclusive owner lease に入り、削除前に冪等に dispose できます。
- `SqliteConnection.ClearAllPools()` を意図的に呼ぶテスト、process-global な環境変数を変更するテスト、プロセスのカレントディレクトリを上書きするテストは、xUnit の non-parallel collection `SQLite pool sensitive` にまとめます。これらのハザードを持つ新しいテストも、この collection に入れて無関係なクラスとの並列実行を避けてください。
- Process-global な環境変数を変更するテストでは `EnvironmentVariableScope.Capture(...)` を使い、setup や assertion が失敗しても単一の disposable cleanup 経路で元の値へ戻してください。

テスト挙動をファイル間・OS間で揃えるため、可能な限りこれらを使ってください。

### `TestDeterminism` と `ManualTimeProvider`

本番コードが `TimeProvider` を受け取れる場合は `ManualTimeProvider` を使い、fixture data の時刻は wall clock から読むのではなく `Advance(...)` で明示的に進めてください。

最終的な条件成立のアサーションや非同期ポーリングには `TestDeterminism.WaitUntilAsync(...)` を使い、周囲のテストが同期処理の場合は `WaitUntil(...)` を使います。どちらも共通 timeout と poll interval を適用し、diagnostic callback を渡せるため timeout failure に観測状態を含められます。十分な作業量に達した場合か遅い host 向け上限に達した場合のどちらでも止めたいループでは `WaitUntilOrTimeoutAsync(...)` を使います。

短い観測時間だけ task が未完了であることを確認する場合は `TestDeterminism.AssertTaskRemainsBlockedAsync(...)`、同期テストで不在や安定性の条件が bounded window の間 true のままであることを確認する場合は `AssertConditionRemainsTrue(...)` を使います。ワーカーを 1 つの gate から同時に解放したい場合は `RunConcurrentlyAsync(...)` を使います。擬似ランダムな fixture data には、ambient または時刻 seed の `Random` を直接作らず `CreateRandom(...)` を使ってください。

### `RepositoryTestPaths`

GitHub workflow、`global.json`、ドキュメントなど、checked-in されたリポジトリ内ファイルを検査するテストでは `RepositoryTestPaths` を使ってください。各テストクラス内で repository-root 探索や `Path.Combine` の連鎖を繰り返す代わりに、`ReadText(...)`、`ReadWorkflow(...)`、`Combine(...)` を優先します。

### `TestConsoleLock`

`Console.Out` や `Console.Error` を差し替えるテストは、必ず `TestConsoleLock.Gate` で lock してください。

これにより、並列実行時のコンソール出力取り込みの衝突を防ぎ、CLI や console UI テストの flaky な失敗を避けられます。

テストクラス自体が non-parallel collection に入っている場合でも、console lock は残してください。process-global な console ハザードを各テストの近くで明示でき、将来そのクラスやヘルパーが collection 外に移った場合の保険にもなります。

## テストの書き方

### 追加・更新が必要なケース

次を変更したら、テストを追加または更新してください。

- CLI の引数解析や出力形式
- DB スキーマ、マイグレーション、クエリ意味論
- シンボル抽出や参照抽出のルール
- インデックスの skip / update / purge 挙動
- MCP ツールの出力や JSON 構造
- コンソールやプログレス表示
- Git / worktree 挙動
- ワークスペース鮮度や trust メタデータ

基本は最も近い既存の `*Tests.cs` を拡張してください。既存ファイルに自然に収まらない場合だけ新しいテストファイルを作ります。

- 複数言語で共有する抽出上限のテストは、切り詰めを発生させる最小値だけ境界を超え、言語ごとに任意の余裕値を足さないでください。

### CLI / コンソール系テスト

- stdout と stderr を明示的にキャプチャする。
- 単純な stdout/stderr capture では `ConsoleCapture` を優先し、直接コンソールを差し替える場合は `TestConsoleLock.Gate` で直列化する。
- 終了コードは `CommandExitCodes` で検証する。
- JSON 出力は生文字列比較ではなく `JsonDocument` で解析して検証する。

### JSON `--json` 出力 snapshot

`JsonOutputSnapshotTests` と `JsonOutputSnapshotHelper` は CLI `--json` 出力の形状ドリフト（キーの rename、削除、トップレベル配列の並び替え、契約更新を伴わない新規キー）を検出する小さなゴールデンファイル harness です。既存の `QueryCommandRunnerTests` 内の絞り込みアサーション形式の JSON テストを置き換えるものではなく、補完するものとして併用してください。

- ゴールデンファイルは `tests/CodeIndex.Tests/golden/<command>.json` に置かれ、ソースツリーに checked in されています。
- `JsonOutputSnapshotHelper` は比較前に揺らぐフィールドを正規化します: `indexed_at` / `latest_modified` などの timestamp 系キー → `<TIMESTAMP>`、`git_head` / `indexed_head_commit` などの commit SHA 系キー → `<COMMIT_SHA>`、`project_root` → `<PROJECT_ROOT>`、`version` → `<VERSION>`、各 result の `score`（BM25、FTS5 実装依存）→ `<SCORE>`、SQLite の `page_count` → `<COUNT>`。テストごとの temp パスは helper の `BuildPathReplacements` で除去されます。
- 形状の変更が意図的な場合は、`UPDATE_SNAPSHOTS=1` を設定して snapshot テストだけを再実行し、生成された差分をレビューしてからコミットしてください:

  ```bash
  UPDATE_SNAPSHOTS=1 dotnet test tests/CodeIndex.Tests/CodeIndex.Tests.csproj \
      --filter "FullyQualifiedName~JsonOutputSnapshotTests"
  git diff tests/CodeIndex.Tests/golden/
  ```

- 意図しない snapshot 差分は契約の回帰として扱ってください: 本番コードを直すか、ゴールデンを schema / docs / changelog と同じ PR で更新するかのどちらかです。
- フィクスチャは最小・決定的に保ってください。新しい `--json` 出力が契約に加わる場合は、同じ変更内で対応する snapshot テストとゴールデンファイルを追加します。

### Git 系テスト

- global の git identity がある前提にしない。
- テストセットアップ内で repo-local の `user.name` と `user.email` を設定する。
- fixture リポジトリでは repo-local の commit/tag signing を無効化し、global signing 設定が非対話実行でプロンプトや失敗を起こさないようにする。
- shell 依存の quoting ではなく、ヘルパーや `ProcessStartInfo.ArgumentList` を使う。
- `dotnet.yml` の SDK setup は一時的な download 失敗に対して条件付きで 1 回だけ再試行します。2 回目の失敗が job を失敗させるよう、最初の `continue-on-error` と失敗 outcome guard を対で維持してください。

### DB 系テスト

- テストごとに分離された一時 DB を優先する。
- 実DB挙動を検証する場合はスキーマ初期化を明示する。
- query plan、page、row count などの境界をまたぐ DB fixture は、その境界を最小限に超えるサイズに留める。実際の大規模 DB が契約そのものではない限り、余裕値で膨らませない。
- 読み取り互換性に触れる変更なら、通常経路に加えて必要な fallback / migration 経路も検証する。

## クロスプラットフォームのルール

- Windows、macOS、Linux すべてで成立するよう `Path.Combine` と相対パスを使う。
- 応答サイズ処理の本番上限が大きい場合は、限定的なテスト専用上限と小さな代表データを使い、process-global な上書きは `finally` で復元して non-parallel suite に置いてください。
- 改行自体が論点でない場合は、改行依存のフィクスチャを正規化して扱う。
- Windows では SQLite 接続やファイル属性の影響で削除が遅れることがあるため、後片付けを甘く見ない。
- shell ツール、パス区切り、プロセス挙動が各 OS で同じとは仮定しない。
- OS 固有の回避策が必要なら、将来の保守者のためにテスト内コメントとこのガイドの両方へ理由を残す。

## テスト変更をコミットする前の確認

次を確認してください。

1. 変更した本番挙動に対して、焦点の合ったテストがある。
2. `dotnet test` が通る。
3. 一時ファイル、git、SQLite の後片付けが堅牢である。
4. 必要なコンソールキャプチャが直列化されている。
5. このドキュメントが現在のテスト構成と規約を正しく反映している。
