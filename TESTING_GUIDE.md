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
- CI runs the test project through `tests/CodeIndex.Tests/CodeIndex.Tests.runsettings`, enables VSTest blame crash and hang collection, applies a 45-minute session timeout plus 60-second xUnit long-running diagnostics, and reruns the suite once after an initial failure. If the retry passes, CI uploads `TestResults/flaky-retry.txt` with the TRX and blame artifacts so the run is treated as suspect instead of silently trusted. TRX telemetry summaries and test-result artifact uploads run only for failed or pass-on-retry lanes, not for clean first-pass success lanes; streamed test output is also written under `TestResults` only after a failed run needs upload/timeout inspection, and that failure-log directory is created only on the failure path. The telemetry summarizer runs with `--configuration Release` and may build its helper during failure diagnostics so every matrix lane can produce a TRX summary. XPlat Code Coverage collection is limited to the `ubuntu-24.04` / `net8.0` lane so every active CI lane still exercises the full suite without paying collector overhead. OS coverage runs on `net8.0`, the production CLI target, while `net9.0` compatibility coverage runs on `ubuntu-24.04` only. Test execution runs with `--no-build` after locked restore and Release build steps: the primary lane restores the full solution for audit and publish coverage, then builds `tests/CodeIndex.Tests/CodeIndex.Tests.csproj` for the matrix framework; non-primary lanes restore only that test project's matrix framework with `RestoreTargetFrameworks` before the same per-framework build. The `net8.0` lanes retain both pinned SDKs because the 9.0 SDK selected by `global.json` builds the project while the 8.0 SDK supplies the test runtime. The `net9.0` compatibility lane installs only `9.0.301`, avoiding its unused 8.0 SDK/runtime download. `CodeIndex.Tests.runsettings` is the single owner of the `TestResults` output directory; local `dev.sh coverage` follows that same ownership instead of passing a second results-directory argument. The `ubuntu-24.04` / `net8.0` lane no longer builds the test project's unused `net9.0` target; `net9.0` build coverage stays in the Ubuntu compatibility lane. It also uses `make lint` as the single formatting verifier. The NuGet cache key is based on `packages.lock.json` and `global.json` instead of every project file, with an OS-scoped restore key for partial cache reuse; locked restore still catches package-input drift, while test-only project edits no longer evict the package cache. The weekly mutation workflow also caches the pinned Stryker global tool and NuGet packages so scheduled mutation runs avoid reinstalling unchanged test tooling.
- The C# CodeQL lane only restores and builds; it installs the pinned 9.0 SDK selected by `global.json` without downloading an unused net8 runtime. Runtime test coverage remains in Build/Test and release workflows.
- Keep the CI initial test run and its single retry routed through one workflow helper so logger, blame, and coverage arguments cannot drift. When a PowerShell helper returns the test exit code, keep streamed test output off the function success stream so assignments capture only the numeric exit code.
- Coverage collection runs only on the initial primary-lane test attempt; the one flaky-classification retry reuses the same test arguments without rerunning the coverage collector.
- Matrix test invocations use both `--no-build` and `--no-restore` because each lane completes its scoped locked restore and Release build before entering the shared test helper.
- Primary-lane publish also uses `--no-build --no-restore`, reusing the production project output and dependency graph built through the Release test project.
- Release cross-compile lanes skip the RID-agnostic solution build because they do not run tests and the self-contained RID publish necessarily performs the real build; native lanes retain the solution build before testing.
- Release cross-compile lanes likewise use a locked production-project restore instead of restoring test and tool projects they never build; native test lanes retain the locked solution restore.
- Release cross-compile lanes install only the repository-selected 9.0 SDK because they publish self-contained binaries and never execute the net8 test host; native lanes retain both pinned SDK lines.
- Release workflow tests use `--no-build --no-restore` after the solution's locked restore and Release build so each runtime lane does not reevaluate dependencies.
- Curated release-note generation caches the changelog tool lock file, performs one conditional locked restore, and runs the tool with `--no-restore`.
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
  Console writer synchronization coverage yields between character writes instead of sleeping per character; use enough whole-line iterations to expose interleaving without adding wall-clock delay.
- `SymbolExtractor*Tests.cs` and `ReferenceExtractor*Tests.cs`
  Extractor coverage is split by language or feature area with partial test classes, while shared helpers remain on the root `SymbolExtractorTests` / `ReferenceExtractorTests` parts.
  When moving repeated extractor scenarios out of a giant suite, keep the new partial file grouped by a readable domain such as language, build-file format, or protocol surface, and prefer small semantic assertion helpers over repeated raw substring or predicate assertions.
  Pattern-config scalar-cap variants reuse one temporary root and overwrite one config file, resetting the global registry between iterations under a single console lock.
  Use `AssertSymbolsContain(...)` when a fixture only needs to verify several symbol names of the same kind across language-specific partials; keep direct predicates for metadata such as line, container, subtype, or return type.
  Likewise, use `AssertReferencesContain(...)` for repeated `(reference kind, container, symbol names)` checks, while retaining predicates for flags, context, line, and other edge-specific metadata.
  Use `AssertReferencesContainInContext(...)` when several reference names share the same kind and exact source context; keep direct predicates when context is only one part of a richer edge contract.
  Use `AssertReferencesDoNotContain(...)` for negative checks over one reference kind; retain direct predicates when the exclusion depends on container, context, line, or other metadata.
  `ReferenceExtractorTests.ExtractSymbolsAndReferences(...)` owns the common symbol-then-reference extraction setup for tests that need both lists; use it instead of repeating the two extractor calls when the fixture does not need a specialized path or workspace symbol setup, and discard the symbol tuple element with `_` instead of keeping an unused `symbols` local when the test only asserts references.
  Dockerfile named-stage reference variants share one multi-stage fixture when ordinary, lowercase, platform-flagged, commented, hyphenated, and dotted forms can be distinguished by exact per-stage call counts; keep external base-image exclusions in that fixture as the negative control.
  Dockerfile `COPY --from` ONBUILD and quoted-stage forms share a fixture with tagged and digest-qualified external-image controls; assert exact call counts for the genuine stages so false positives cannot hide beside positive edges.
  Dockerfile RUN mount coverage keeps ordinary, multiple, quoted, and ONBUILD stage sources beside quoted-text and command-argument negatives in one fixture; exact per-stage counts make the negative controls observable without separate extraction passes.
  Dockerfile ARG expansion coverage places braced, unbraced, conditional, nested, and escaped forms in one fixture with distinct ARG names; assert one reference per declared name so escaped controls cannot add hidden edges.
  Shell and PowerShell reference fixtures keep a declared-function call and a top-level call together when validating function versus synthetic `<script>` containers, so each language is extracted once for both scope contracts.
  Keep Swift's broad representative declaration fixture as the single owner of basic struct/function, macro, precedence-group, and operator presence; do not add narrow methods that repeat those same kind/name assertions without additional metadata.
  Swift attribute and visibility modifiers such as `@available`, `@discardableResult`, and `package` share one declaration fixture when their contracts are independent kind/name recognition checks.
  Swift extension coverage keeps escaped members, generic targets, nested generic conformance targets, and qualified targets with special members in one source when unique target names preserve failure diagnosis.
  SQL MySQL-definer and PostgreSQL return-field extraction shares one mixed-dialect fixture with comment/string false-positive controls when distinct symbol names keep every contract independently assertable.
  SQL qualified-name whitespace coverage keeps procedure, view, enum type, schema, sequence, extension, synonym, and other CREATE/ALTER kinds in one fixture so dot normalization is paid for once across the DDL matrix.
  XAML `x:TypeArguments` coverage keeps scalar, type-markup, nested-generic, and multiline values in one resource dictionary, using distinct wrapped type names to retain failure diagnosis.
  XAML type-object elements, type-property elements, and type markup extensions share one resource dictionary with form-specific type names so one reader traversal covers all three representations without ambiguous assertions.
  XAML common event handlers live in the wrapped search-attribute fixture alongside multiline `x:Name` and `x:Key` values; use exact handler counts so ordinary and wrapped attribute forms share one extraction safely.
  XAML `Binding`, `x:Bind`, `CompiledBinding`, and `ReflectionBinding` paths share one fixture with distinct leaf names; assert source/root and converter-parameter exclusions from the same collected property-name set.
  XAML `ElementName` and `x:Reference` target forms share one Grid fixture; collect property names once and retain inline, object-element, property-element, and ignored-parameter assertions together.
  XAML plain and `x:Static`-derived `x:Key` declarations live with static/dynamic resource lookups, including nested `Member={x:Type ...}` syntax, so key production and consumption share one resource dictionary.
  JavaScript spaced and unspaced chained comparisons before plain templates share one function fixture with distinct operand names, preventing generic-tag false positives in one extraction.
  JavaScript single-line `for...of` and `for await...of` plain-template negatives share synchronous and asynchronous functions in one source, asserting zero phantom `call of` edges once.
  JavaScript multiline `for...of` and `for await...of` plain-template negatives likewise share one source and one phantom-call exclusion.
  JavaScript for-of header negatives share NBSP synchronous/asynchronous and BOM synchronous forms in one source because they assert the same special-whitespace suppression.
  JavaScript reserved-word member tags share one synchronous/asynchronous fixture and exact per-name call counts for `default`, `return`, `finally`, and `await`.
  CSS selector-form coverage shares one fixture for selector lists, descendants, compound classes/IDs, standalone IDs, quoted-attribute lookalikes, and hex-color negatives, using unique names for exact diagnostics.
  CSS animation shorthand and comma-separated `animation-name` coverage share one fixture with distinct keyframe names and a shared `none` exclusion.
  SCSS quoted, URL, bare-URL, and media-qualified imports share one entry-point fixture with parameterized and parameterless mixin includes, asserting import and call edges together.
  TypeScript runtime `typeof` negatives keep multiline assignment and inline arrow-function layouts in one source, excluding both operand names from type references at once.
  TypeScript generic tagged-template coverage keeps ordinary and function-type generic arguments in one source with distinct tags and container assertions.
  App-manifest DTD coverage combines local and external entity declarations in one document, preserving assembly extraction while asserting external targets never enter signatures.
  Solidity's representative declaration/range fixture also owns comment and string false-positive controls on existing lines, avoiding a second extraction without shifting range assertions.
  Python type-introspection helper coverage keeps bare and qualified `cast` / `assert_type` calls beside other helper APIs, using distinct target types and containers in one extraction.
  Python f-string interpolation coverage keeps single-line, multiline, nested-expression, and format-specifier-followed calls in one fixture with exact per-container call sets.
  Swift `.self`, `#selector`, and `#keyPath` expression coverage shares one fixture, asserting qualified roots and excluding instance/member tokens together, including every member segment in a multi-segment key path.
  JavaScript semicolonless blockless-arrow boundaries share one source for following class, expression-plus-class, and CommonJS class-export forms, with distinct hidden/visible names and exact arrow end lines.
  JavaScript direct and wrapper-call blockless-arrow class returns share one source with exact lambda ranges and distinct hidden class/member exclusions.
  JavaScript callable-scope local-class coverage shares class methods, ordinary functions, and CommonJS function expressions in one source, with direct/class-expression forms and unique leak sentinels.
  JavaScript nested local-class coverage shares IIFE, static block, object concise method, getter, and setter scopes in one source, retaining a visible sibling method as the boundary control.
  JavaScript CommonJS class-expression coverage shares named exports, default inline/multiline/parenthesized/conditional assignments, and property exports in one source with unique members and an exact default-class count.
  JavaScript and TypeScript CommonJS object-export APIs share `defineProperty`, `defineProperties`, and `Object.assign` forms in one theory fixture with unique exported, computed, dynamic, and non-export target names.
  JavaScript and TypeScript `import.meta` module discovery shares `import.meta.resolve` and `new URL(..., import.meta.url)` positive/negative forms in one theory fixture with retained line and signature checks.
  JavaScript and TypeScript worker-loading coverage shares `importScripts`, `Worker`, and `SharedWorker` variants in one theory fixture, retaining line/signature checks and dynamic/string/method/constructor negatives.
  JavaScript and TypeScript browser module-registration coverage shares service-worker and worklet APIs in one theory fixture with scoped/options signatures and receiver/dynamic/string negatives.
  JavaScript and TypeScript dynamic-import coverage keeps multiline literals, import attributes/assertions, static templates, and receiver/dynamic/string negatives in one theory fixture with form-specific line and signature checks.
  JavaScript ordinary-string and template-literal brace masking shares one fixture with distinct classes and members, retaining exact container and range checks after each literal.
  JavaScript regex-brace masking keeps direct literals, wrapped `if`/`else if`, plain `else`, `do`/`while`, and `finally` forms in one class fixture with a final sibling boundary and a block-comment method-shape negative.
  JavaScript class-field arrow ASI coverage shares numeric and string field boundaries, computed-member continuation, class-closing literal termination, and final template literals in one fixture with distinct class/member names.
  JavaScript default-class coverage keeps a named class beside anonymous direct-base and mixin-base forms, proving member retention without inventing `extends` as a class name in one extraction.
  TypeScript default-class coverage extends the same shared fixture pattern to named, direct-base, mixin-base, and implements-only forms, retaining the anonymous `default` member container check.
  TypeScript inline-class coverage uses one multi-method declaration for single-member recognition, sibling splitting, exact signatures, and shared class-container metadata.
  TypeScript same-line sibling-class coverage keeps distinct and identical method-name cases in one source, using four class containers to preserve attribution diagnostics.
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
  CLI parsing, command execution, and installer behavior. Index command coverage is split by run mode or feature area, and query command coverage is split by command family with partial test classes so shared console and fixture helpers stay centralized. Keep repeated query-result fixtures, such as overlapping chunk content used by multiple search deduplication tests, in narrow class-level helpers instead of duplicating local builders. `ProgramCliTests.cs` covers top-level entrypoint behavior that must be exercised through a subprocess, while `InstallScriptTests.cs` runs focused bash snippets against `install.sh` in library mode to lock in release-installer regressions without performing real network installs. Installer bundle-generation tests must also verify that `install.sh` is marked generated while every canonical `install_modules/` source remains unmarked.
  Argument-validation variants that only differ by invalid scalar input share one database fixture and iterate within a fact when no per-case state or discovery identity is required.
  Excerpt focus-column validation follows this rule for zero and non-numeric values, reusing one indexed Markdown fixture.
  Inspect path-line exact/enclosing-symbol cases reuse one indexed source fixture and iterate read-only line queries within a fact.
  Definition and symbols exact-mode conflict validation share one empty database and a cross-command flag-pair table.
  Symbols compact flag/alias and summary-only JSON envelopes share one editor-format fixture.
  Symbols JSON array, LSP, quickfix, and SARIF location formats share one editor-format fixture; definition SARIF severity coverage reuses that fixture and asserts informational `note` output separately from warning-level diagnostic output.
  Command-specific output format coverage uses a command/format matrix that checks both parser acceptance and the matching usage line; recognized shared formats without a command implementation need a separate usage-error assertion.
  Unused default-suppression row, JSON count, summary-only, and text count envelopes, including the `--all` count control, share one unused-symbol fixture.
  Unused default-suppressed and `--all` JSON cursor pagination share one unused-symbol fixture.
  Unused full and compact `--by-bucket` JSON envelopes share one taxonomy fixture.
  Unused bucket, minimum-confidence, and actionable confidence-alias JSON filters share one unused-symbol fixture.
  Unused full-summary and bucket-filtered JSON counts share one taxonomy fixture.
  Unused limited-page returned counts and bucket diversification share one taxonomy fixture.
  Unused indexed-path and unsupported-language zero-result schemas share one indexed fixture.
  Outline `size` sorting and its `span` alias share one ranking fixture.
  Outline reference and complexity metric sorting share one derived-ranking fixture.
  Outline kind sorting and default source-order field projection share one ranking fixture.
  Deps JSON and json-graph byte-limit failures share one SQL graph fixture.
  Deps JSON summary output and json-graph summary rejection share one SQL graph fixture.
  References stale-SQL-contract count and result envelopes share one downgraded graph fixture.
  Callers and callees stale-SQL-contract result envelopes share one downgraded graph fixture.
  Assert each MCP SQL graph readiness field and degraded reason once per response.
  MCP analyze-symbol and references stale-SQL-contract checks share one downgraded server fixture.
  Keep MCP mixed and zero-result SQL graph metadata assertions single and non-duplicated.
  MCP deps and symbol-hotspots degraded zero results share one downgraded server fixture.
  MCP unused-symbols and symbol-hotspots kind-filtered clean zero results share one downgraded server fixture.
  CLI unused kind-filtered zero-result and count JSON envelopes share one downgraded SQL fixture.
  CLI hotspots unfiltered degraded and kind-filtered clean zero results share one downgraded SQL fixture.
  Callers mixed-repository pure-C# result and count envelopes share one downgraded graph fixture.
  MCP callers and analyze-symbol pure-C# checks share one mixed-repository downgraded server fixture.
  MCP definition clean metadata and callers/impact degraded metadata share one stale-contract server fixture.
  Deps missing-graph byte-limit and summary-only zero payloads share one read-only fixture.
  Deps JSON-only control rejection across non-JSON formats uses one data-driven theory.
  Unused missing-chunks count and degraded reflection results share one mutated fixture.
  Unused missing-graph JSON schema and human count warning share one empty database fixture.
  Unused JSON confidence taxonomy and human bucket grouping share one unused-symbol fixture.
  Razor directive kind-filter queries share one indexed component fixture and iterate route, implements, attribute, and layout expectations in one fact.
  Symbols literal-query coverage shares one empty database for double-dash and explicit `--query` forms when proving compact-looking text is not expanded.
  Symbols hotspot, references, size, and path ranking queries share one symbol-sort fixture because all are read-only views of the same ranking signals.
  Graph-command kind-semantics warnings reuse one graph-ready database across references, callers, and callees.
  Exact-zero graph hints use one combined Target/Caller fixture per scenario and iterate references, callers, and callees against it.
  C# query-range generic null-comparison regressions place equality and inequality forms in one indexed source when both assert the same absence of leaked enum references.
  C# query-range collision forms for basic selection, directional ordering, keyword-named members, and object initializers share one indexed source when they assert the same empty inspect reference bundle.
  C# inspect brace-range regressions place char-literal, raw-string, and verbatim-string forms in one indexed class and query each following method from that shared fixture.
  C# generic query-range selectors share simple and tuple type arguments in one fixture, while generic type-pattern coverage shares designation and no-designation forms in another fixture.
  C# switch-expression pattern-variable coverage keeps recursive, declaration, guard, and comment-trivia forms in one extractor fixture when their contract is the set of genuine enum-member references.
  C# foreach-shadowing coverage shares embedded, same-line, and dangling-else forms in one source and distinguishes the surviving references by container.
  C# lambda-parameter shadowing coverage shares simple, multiline, after-lambda, and same-line boundary forms in one extraction pass.
  C# query range-variable members named `select` share plain, escaped, and trivia-separated access forms in one extractor source.
  C# statement-pattern shadowing shares switch-case, conditional, recursive, multiline-recursive, and recursive-case forms in one extraction pass.
  C# statement-boundary shadowing shares out-declaration, out-var, catch, and using scopes in one extractor source.
  C# terminal-select generic type-pattern coverage shares designation and no-designation methods in one extraction pass.
  C# terminal-select generic `as` null-comparison coverage places equality, inequality, and a later genuine enum reference in one extractor source.
  C# using-alias coverage places enum and non-enum targets in separate namespaces of one extractor source and asserts only the enum target survives.
  C# property-receiver shadowing shares instance, instance-from-static, and static-property contexts in one extraction pass.
  C# indented lexical shadowing shares local, using-var, and property-accessor containers in one extractor source.
  C# nullable suffix coverage before parenthesized terminal select shares scalar, tuple, and array-rank forms in one extraction pass.
  C# query range-variable order-by coverage shares comma and directional-comma forms in one extractor source.
  C# query range-variable scope coverage shares query-only, after-query, and query-argument boundaries in one extraction pass.
  C# parenthesized keyword-named values before terminal select share parameter and local forms in one extractor source.
  C# terminal-select generic-call coverage shares single, comma-separated, and tuple type arguments in one extraction pass.
  C# order-by ternary coverage shares keyword-named local functions after greater-than, less-than, and bang operators in one extractor source.
  C# awaited order-by coverage shares direct and comment-separated keyword-named local-function calls in one extraction pass.
  C# throw-expression order-by coverage shares `select`- and `group`-named local functions in one extractor source.
  C# parenthesized compound order-by coverage shares ternary and coalesce expressions in one extraction pass.
  C# parenthesized query-terminal argument coverage shares select and group-by forms in one extractor source.
  C# local-shadowing boundary coverage shares later declarations and nested-block exits in one extraction pass.
  Property lexical-shadowing coverage keeps getter-only and getter/setter scope boundaries in the shared indented fixture.
  Lambda parameter shadowing keeps parenthesized same-line and ordinary method-parameter forms in the shared lambda fixture.
  C# lambda-scoped declaration-pattern coverage shares ordinary, nested, and static lambda forms in one extraction pass.
  C# declaration-pattern statement coverage shares single-line if, multiline if, and multiline while forms in one extractor source.
  C# parenthesized terminal-select boundary coverage shares uppercase-constant and generic-close predecessors in one extraction pass.
  C# nested-query order-by coverage shares plain and parenthesized comma boundaries in one extractor source.
  C# casted local-select order-by coverage shares object, simple, multiline, and lowercase-alias forms in one extraction pass.
  Query range-variable order-by coverage keeps anonymous-type and object-initializer comma forms in the shared order-by fixture.
  C# postfix-expression coverage before parenthesized terminal select shares null-forgiving and increment forms in one extraction pass.
  Inspect and references command coverage applies the same switch-expression grouping so each surface builds one graph-ready database and validates results by container.
  Apply the same combined null-comparison fixture to inspect reference-bundle coverage instead of indexing each operator separately.
  Production-runtime switch relational-pattern coverage places less-than and greater-than methods in one source and pays one CLI indexing subprocess.
  Generic switch-arm guard and relational predecessors likewise share one production-runtime fixture and run only on the production `net8.0` target.
  Search language-alias coverage may place distinct language files in one database and iterate alias filters when each filter isolates one expected result.
  Named-query escaping for option-looking literals reuses one indexed Probe fixture across definition, graph, symbols, files, inspect, and impact commands.
  Search alias variants for JavaScript extensions, YAML, batch, and SQL dialects each reuse one language fixture and iterate casing/spelling forms in a fact.
  Raw FTS syntax coverage reuses one indexed source for a valid control query and all invalid query/hint variants.
  Literal and raw FTS complexity bounds reuse one indexed source across length, token-count, NEAR-count, and lowercase-operator controls.
  XAML, Rust, common multi-language, and JavaScript alias sets each build their fixture once and iterate all accepted spellings and casing forms.
  Inline comment-marker exclusion places JavaScript line/block and Python line comments in one index and iterates marker queries.
  Search exact-mode conflict coverage shares one empty database for all pairwise and triple flag sets.
  Search path and exclude-path invalid-glob guards share one empty database and iterate option names before query evaluation.
  Find long-line JSON coverage reuses one text fixture for bounded and zero-width unclamped snippets.
  Excerpt location parsing and focus dependency/range validation reuse one database containing the range and long-line fixtures.
  Search and find canonicalization for C# verbatim qualified names and Java Unicode escapes share one indexed source per language.
  TypeScript and Java language-alias filtering share one multi-language database because unique query markers isolate each language result.
  Kotlin backtick and Java Unicode-escape canonicalization share the path-filtered C# verbatim canonical fixture rather than creating separate databases.
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
  exercise watch-loop startup and shutdown under redirected console output. They call the asynchronous watch core directly without prebuilding an unused index, cancel after its synchronous startup prefix emits the start event, and use a bounded shutdown wait; do not reintroduce index setup, dedicated threads, signal polling, fixed sleeps, or process-wide SQLite pool resets for this path. The temp-project cleanup helper owns any platform-specific SQLite retry, so SQLite pool serialization is unnecessary; redirected process-wide console output still requires the console-sensitive collection.
  The cancellation contract fixture uses only `git init` plus an explicit case-sensitive setting so `path_comparison` has a deterministic expected value without either a second repository-discovery probe after the watch loop exits or the commit-capable Git fixture's unrelated identity and signing setup.
- `BackgroundTaskObserverTests`
  relies on `BackgroundTaskObserver`'s fault-only continuation contract: canceled
  tasks are awaited directly and do not need a post-cancellation fixed sleep to
  prove warnings were suppressed.
- Cancellation-only timeout tests use an infinite cancellable delay so the fixture has no unrelated wall-clock completion path.
- SQL diagnostic truncation tests cross the character cap with a minimal fixed-width suffix instead of constructing thousands of progressively longer `UNION` clauses.
- SQLite scalar-reader diagnostics reuse one open in-memory connection and command across overflow and null-result contracts.
- Oversized file guards create sparse length-only fixtures when content is irrelevant, avoiding a matching managed byte-array allocation.
- Full-scan and scoped-update oversized-file warning tests share `TestProjectHelper.WriteSparseFile(...)` so both execution modes avoid 10 MiB fixture allocations.
- Oversized ignore-file rejection uses the same sparse helper because the size preflight rejects the file before rule contents matter.
- Per-line oversize detection uses four short physical lines; hundreds of repetitions add no boundary coverage because the contract is independent per line.
- Captured-output overflow tests cross the character budget by one character unless a larger excess is itself part of the contract.
- JavaScript and TypeScript same-line class scanning shares one fixture per language for inline members, sibling classes with distinct and repeated method names, statement prefixes, and callable-local class masking. Keep the individual container, signature, and hidden-local assertions in the shared extraction instead of adding a method for each layout.
- Rust attribute reference coverage shares one extraction for direct and `cfg_attr` derive lists, multiline coordinates, and ordinary annotations. Rust mutable-reference coverage likewise keeps type positions, `dyn`/`impl`, and borrow-expression exclusions in one fixture.
- TypeScript and Swift basic type-alias expansion shares heritage, generic-parameter exclusion, and mixed type/value-position coverage in one fixture per language; scope-shadowing fixtures remain separate because alias binding isolation is their contract.
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
- C# reflection-name extraction coverage keeps literal, constant-concatenation, dynamic, comment, and string-decoy cases in one source fixture so those parser boundaries share one symbol/reference pass.
- C# BOM extraction keeps a simple leading-BOM import fixture plus one mixed-newline fixture that simultaneously covers leading and mid-file BOM handling across CRLF, bare CR, and LF boundaries; do not repeat separate extraction passes for newline subsets already present in the mixed fixture.
- C# lambda-capture coverage keeps positive enclosing-local capture, parameter shadowing, and same-named-method isolation in one source fixture; a single capture assertion proves the negative regions did not leak.
- Escaped-brace coverage for regular and verbatim interpolated C# strings shares one extraction fixture because both variants assert the same phantom-call exclusion.
- Direct and nested interpolations inside C# raw strings share one source fixture and one extraction pass while retaining distinct container assertions.
- Generic C# attribute classification and custom type-argument references share one fixture across assembly-targeted, single-line, and multi-line forms.
- Nested generic attribute forms extend that shared fixture instead of running a second parser pass solely for deeper angle-bracket nesting.
- No-argument parameter attributes on methods, delegates, and lambdas share one C# fixture because all three exercise the same section-local parenthesis-depth rule.
- Argument-bearing parameter attributes share one method fixture for inline and line-broken declaration layouts.
- Direct and `global::` static type qualifiers share one C# fixture while retaining per-container reference assertions.
- Static qualifiers in using statements and field access share one consumer fixture and extraction pass.
- Namespace-qualified and Pascal-cased instance-member chains share one qualifier fixture with a rightmost static type reference, so positive and negative qualifier outcomes are checked after one parse.
- Qualified C# type declarations and cast/generic expressions share one fixture because both exclude namespace segments from call references.
- C# doc-cref masking around delimited-comment markers shares one fixture across a raw string after comment close, raw-string content, and verbatim-string content.
- C# constructor-chain coverage shares `this`, `base`, and cross-line initializers in one class hierarchy and distinguishes rewritten targets by constructor container.
- C# record constructor-chain coverage shares inline, multiline, braced, and same-line-body header layouts in one fixture while preserving per-record attribution checks.
- C# 12 primary-constructor coverage shares class, struct, generic, split-line, and same-line-body layouts in one fixture, with distinct base names preserving case-specific diagnostics.
- C# constructor base-target coverage shares generic, interface-list, nested-generic, multiline, and constrained layouts in one fixture and asserts each rewritten edge by its unique terminal type.
- C# multiline member attribution shares expression-bodied method and property layouts, Allman/same-line braces, and intervening block comments in one fixture, using unique member containers for assertions.
- Python `isinstance`/`issubclass` runtime type checks share single-type and tuple forms in one module fixture, with function containers retaining case-level assertions.
- Python class-header type references share single-base, multiple-base, and metaclass forms in one module fixture, distinguished by class container.
- Python annotation coverage shares direct and generic return, parameter, and local-variable forms in one module fixture, with function containers preserving each assertion.
- Python typing-factory coverage shares `TypeAlias`, `NewType`, bounded `TypeVar`, and constrained `TypeVar` declarations in one module fixture with unique target types.
- Python advanced-typing coverage shares multiline/commented `TypeVar`, `ParamSpec`, callable annotations, variadic tuple unpacking, and literal unions in one logical-header fixture with unique type names.
- Python type-introspection helper coverage shares direct/qualified `get_type_hints`, dataclass/attrs field lookup, and Pydantic `TypeAdapter` in one module fixture with unique targets.
- Python exception type-reference coverage shares bare/chained raises, single/tuple except clauses, `pytest.raises`, and `contextlib.suppress` in one module fixture.
- Python f-string masking shares single-line, multiline-literal, and nested-string-brace interpolation forms in one module fixture with distinct call containers.
- Python keyword filtering shares cross-language keyword-like calls with `raise(...)` and `yield` syntax in one module fixture, retaining positive inner-call checks.
- Python class-property symbol coverage shares annotated/assigned attributes plus slots, augmented slots, match args, and annotations dictionaries in one module fixture.
- Python property-decorator symbol coverage shares cached, accessor/setter/deleter, and direct/qualified abstract decorators in one module fixture.
- Python static-import symbol expansion shares aliases, imported names, qualified from-import names, and dotted prefixes in one module fixture.
- Python package `__all__` export coverage shares assignment, append, inline extend, and split-line extend mutations in one qualified `__init__.py` fixture.
- Python package-import coverage shares local/external aliases, current-package imports, relative module members, and parent-package members in one `__init__.py` fixture.
- Python unclosed multiline-import coverage reuses one assertion path for EOF and following-code cases, reducing duplicated test setup while retaining both parses.
- Python basic symbol coverage shares synchronous/asynchronous functions, assigned lambdas, and classes in one module fixture.
- Python triple-quoted-string coverage shares the leading module-docstring heading with double/single/raw fixture masking in one extraction.
- Ruby loading/dependency DSL coverage shares `require`, `require_relative`, `load`, `gem`, and `autoload` in one fixture with container-specific target checks.
- Ruby metaprogramming declaration coverage shares aliases, constant visibility, and `module_function` exports in one fixture while suppressing DSL keywords.
- Ruby Rails model/schema DSL coverage shares enum, table creation, typed attributes, serialization, composed values, and nested attributes in one fixture with target/option separation.
- Ruby exception coverage shares rescue clauses, Rails `rescue_from`, and parenthesized `raise` syntax in one fixture while retaining exception targets and suppressing control/option tokens.
- Ruby Rails command-target coverage shares associations, validation, callbacks, and `class_name` options in one fixture while excluding keyword-option tokens.
- Ruby type-composition coverage shares inheritance, mixin/using targets, contextual control keywords, and refinements in one fixture with container-specific references.
- Ruby call-syntax coverage shares no-parentheses commands, brace blocks, and `do`/`end` blocks in one fixture while retaining container attribution.
- Ruby framework subject DSL coverage shares RSpec `describe` constants and Rails route resource symbols in one fixture while suppressing DSL/option keywords.
- Go composite-literal coverage shares generic, array/slice, and map forms in one function fixture with unique element/key/value types.
- Go type-conversion coverage shares composite, parenthesized pointer/qualified, and parenthesized composite forms in one function fixture with unique converted types.
- Go method-expression coverage shares plain, pointer, qualified, and generic receivers in one fixture while retaining value-expression negatives.
- Go type-set coverage shares interface unions and standalone composite approximations in one fixture while retaining bitwise-expression negatives.
- Go generic type-argument coverage shares called and standalone instantiation forms in one fixture while retaining indexed-value negatives.
- Go struct-field coverage shares embedded, multi-name, generic, and inline fields in one file fixture while retaining field-name negatives.
- Go function-signature coverage shares literal, declared function type, function-valued field/variable, and inline interface forms in one file fixture.
- Go generic-constraint coverage shares function and type declarations in one file fixture.
- Go value-declaration coverage shares multi-name and generic declarations with local inference in one file fixture.
- Go method-signature coverage shares concrete receiver and interface member forms in one file fixture.
- Go runtime-type-check coverage shares direct assertions and type-switch cases in one function fixture while retaining value-switch negatives.
- Go channel/builtin-type coverage shares directional declarations, named channel types, and `make`/`new` allocations in one file fixture.
- Go symbol function coverage shares regular/receiver/generic functions and an assigned function literal in one extraction fixture.
- Go import-symbol coverage shares single/grouped imports, build directives, and cgo classification in one file fixture.
- Go type-symbol coverage shares named, alias, struct, interface, and generic declarations in one file fixture.
- Go declaration-symbol coverage shares grouped types, top-level const/var forms, blank identifiers, and local negatives in one file fixture.
- Go embedded-type symbol coverage shares generic struct fields and interface members in one file fixture with container attribution.
- Go function-like symbol coverage shares qualified receiver containers and branch labels in one file fixture while excluding switch keywords.
- Consolidated Python/Go extractor fixtures retain exact symbol/reference cardinality, including complete per-container result sets, and source-line assertions when those were part of the original regression contract.
- Assembly reference coverage shares direct call/branch forms and tab-separated decorated indirect-target negatives in one exact-cardinality fixture.
- Solidity reference coverage shares inheritance whitespace variants, library/modifier/event/interface edges, and comment/string negatives in one fixture.
- Terraform reference coverage shares resource/module/data traversals and raw `var`/`local` object references in one exact-per-name fixture.
- Language masker copy-on-write coverage pairs unchanged-array reuse and required-clone behavior within the same Lua and Solidity test methods.
- COBOL target-statement coverage places SQL/CICS, report, sort, queue, file, literal, and external-call variants in one program and checks exact grouped edge counts.
- `IndexCommandRunnerTests.RunBackfillFold_PublishedTrimmedBinary_SerializesSuccessAndErrorJson`
  publishes a trimmed RID-specific CLI and runs whichever entry point the SDK emits (`cdidx.dll` through `dotnet` or the native `cdidx`/`cdidx.exe` apphost). Its publish smoke disables NuGet vulnerability auditing because package advisory validation is covered by the normal build/test workflow's package vulnerability check, not by this runtime serialization test. It is reported as skipped on macOS arm64 while SDK/ILLink can crash before exercising `cdidx` (#2586). Do not assume every SDK/runtime pair writes a `cdidx.dll` into self-contained publish output.
- `QueryCommandRunnerTests.RunPublishedTrimmedCli_SerializesQueryJsonAndSupportsRazorAliases`
  uses one trimmed RID-specific publish output for query JSON coverage and both `cshtml` / `razor` C# Razor language aliases, writes publish-specific lock files under the test's temporary intermediate directory, disables NuGet vulnerability auditing for the publish smoke, and runs whichever `cdidx` entry point the SDK emits so the test does not depend on source-tree lock-file mutation, advisory-feed availability, or a DLL-only publish layout. If `dotnet publish` reaches an SDK/ILLink tool that requires an unavailable `Microsoft.NETCore.App` runtime, the test is reported as skipped with that missing-runtime diagnostic instead of failing before it can exercise `cdidx` (#3571). It is also reported as skipped on macOS arm64 because the SDK/ILLink crash happens before the test reaches `cdidx` (#2586).
- `McpServer*Tests.cs`
  MCP JSON-RPC behavior and tool outputs. Large server coverage is split into focused partial suites for tool calls, tool listing, protocol/session handling, and error handling while the root `McpServerTests` part keeps shared seeded fixture state. Request-timeout tests use signal-gated delay hooks instead of fixed sleeps: start the request, confirm the hook has begun, then await the timeout response with a bounded wait so they pay only the configured timeout while still proving in-flight actions drain after the timeout response.
  Stdio response-order tests use the same signal-gated pattern: make the synthetic transport signal the parse-error path instead of sleeping in the response serializer.
  Rate-limit-disabled coverage uses the lightweight `languages` tool for repeated successful calls; do not pay repeated `status` database aggregation cost when the assertion only concerns limiter bypass.
- `DependencyPackageExtractorTests.cs`
  Dependency-lock graph fixtures assert explicit parent-package to child-package references and the absence of synthetic top-level package references. Keep NuGet and npm coverage aligned so shared resolved package sets cannot recreate lock-file-to-lock-file similarity edges while `callers` retains the requiring package as its container.
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
  Keep the converted coverage boolean in a local whose name differs from the case-insensitive `CollectCoverage` string parameter; otherwise PowerShell coerces the boolean back to a string before invoking typed helpers.
  Pass the repository-root `TestResults` directory explicitly to `dotnet test`; runsettings paths can otherwise resolve relative to the test project, separating TRX and coverage output from workflow telemetry and artifact paths.
  Give the initial attempt and flaky-classification retry distinct TRX filenames so a passing retry cannot overwrite the failure evidence that triggered it.
  Collect crash diagnostics on the initial attempt only. The flaky-classification retry reuses that evidence and skips the crash collector, while retaining blame-hang and its five-minute kill bound in case the retry hangs.
  Summarize TRX telemetry only when the test helper reports a failed initial attempt (including pass-on-retry); clean first-pass lanes and jobs that failed before testing should not pay for a second project launch and TRX parse. Keep result and dump artifact uploads failure-gated to avoid unnecessary transfer time.
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
- Console-only test classes do not need the SQLite-sensitive non-parallel collection once every capture/swap window, including writer-disposal checks, is protected by `TestConsoleLock.Gate`.
- Pure i18n resolution, self-locking JSON-envelope capture, and isolated LSP request/budget fixtures should remain outside the SQLite-sensitive collection; owning a temporary DB is not itself process-global state when the context is disposed before helper cleanup.
- Reader issue fixtures with per-instance DB ownership and external-process fixtures with per-instance directories are likewise parallel-safe when their `Dispose` paths release those resources through the shared helpers.
- Schema-constraint fixtures dispose every `DbContext`, connection, command, and reader before directory cleanup; do not add unconditional pool resets that serialize these independent schema checks.
- SQLite connection-string and command-policy tests are parallel-safe when every in-memory connection and command is instance-owned and disposed; referencing `Microsoft.Data.Sqlite` alone is not a reason to join the SQLite-sensitive collection.
- Cross-platform path matrices can run in parallel when cache seeding is guarded by `PathCasingTestLock` and every filesystem/git fixture owns a unique temporary workspace.
- Exception-formatting contract tests should guard their shared console helper with `TestConsoleLock` and remain parallelizable; their in-memory SQLite retry probes do not mutate the process pool.
- Split pure pre-cancelled import and bounded-stream checks from import-replacement fixtures that mutate test-only hooks, so only the hook-owning class remains serialized.
- Release workflow and package-normalizer tests can run outside the SQLite-sensitive collection: xUnit keeps methods in their single class sequential, so its two scoped durability-hook probes cannot overlap each other.
- Database-diff fixtures own independent left/right projects, lock console capture, and keep their sole row-budget override within the same sequential xUnit class; they do not require process-wide SQLite serialization.
- Keep `SuggestionStore` environment-boundary and configured-pruning cases in their small non-parallel fixture; hashing, deduplication, archive, and ordinary persistence cases own isolated directories and should remain parallelizable.
- Separate pure `ProcessStartInfo` construction contracts from subprocess-environment filtering tests; only the latter mutate the parent process environment and require the non-parallel collection.
- Keep freshness diagnostic classification parallel; isolate only the stamped-case probe test that temporarily replaces `GitHelper` and `FileIndexer` test hooks.
- Production source-policy scans for direct environment access are read-only and parallel-safe; isolate the separate `CdidxEnvironment` mutation contract that changes a real process variable.
- The reflection-only SQLite collection registration contract is parallel-safe; keep only fixture lifecycle tests that replace the global pool-clear callback in the sensitive collection.
- Isolate config CLI cases that change current directory or real environment variables; ordinary config parsing and validation uses injected environment readers and independent temporary roots, so the main suite remains parallelizable.
- JSON API-version fixtures use locked console capture and scoped project cleanup; deleting the project before an unconditional pool reset makes that reset both redundant and too late, so keep this contract suite parallelizable.
- Golden JSON snapshot fixtures use the non-parallel console-sensitive collection because empty stderr is part of their contract, while retaining per-instance database cleanup; they should not pay a process-wide pool reset after each of the status, search, references, impact, and excerpt snapshots.
- Extractor plugin-registry fixtures share that console-sensitive collection because rejected pattern configs emit diagnostics through the process-wide stderr writer.
- Suites that capture, replace, or intentionally close process-wide console writers use the same console-sensitive collection; removing SQLite pool cleanup does not make console mutation parallel-safe.
- Temporary repositories and files: create them through `TestProjectHelper` when practical, and do not depend on user-level git config.
- MCP unit fixtures that instantiate a real server should place its database inside a scoped temporary project and delete the project after server disposal instead of leaving a standalone database in the system temp directory.
- MCP audit fixtures should co-locate the database, active log, and rotated logs under one temporary project so one resilient directory cleanup replaces per-file cleanup lists.
- DB lifecycle fixture disposal should call the resilient shared file helper directly after releasing contexts and exclusive pool ownership; do not wrap it in a second catch-all that hides exhausted cleanup retries.
- Workspace active-status transitions should reuse one manifest/config fixture for inactive, active, missing, and stale assertions instead of rebuilding and re-selecting the same workspace for four independent tests.
- Workspace metadata result-shape parity should enrich status, map, and analysis objects from one dirty Git fixture instead of initializing and committing three identical repositories.
- Persisted-HEAD drift and recovery assertions should update metadata within one Git fixture rather than creating a second repository merely to test the matching state.
- Latest-indexed-HEAD precedence should be asserted for status and analysis result shapes from one seeded repository rather than duplicating identical Git and database setup.
- Commits-ahead ancestor and missing-stamp behavior should share the same multi-commit repository; the missing case only requires a fresh result object without `IndexedHeadSha`.
- Shared file-URI escaping and LSP round-trip parity should use one path/root case rather than duplicating equivalent percent-encoding setup in separate tests.
- No-timeout sentinel coverage should exercise zero and infinite budgets in one contract test; both follow the same caller-cancellation path and do not need duplicate scope setup.
- Bounded HTTP private-file success and overflow cases should share one temporary project and use distinct child paths instead of allocating and cleaning two standalone system-temp files.
- Bounded HTTP memory-safety coverage checks huge declared-length allocation avoidance and whole pooled-buffer clearing in one contract test.
- Environment-variable scope restoration should cover present and missing originals in one serialized test, avoiding duplicate process-variable setup and runner cases.
- Pure process-launch builder contracts should verify base flags, invariant arguments, worker payloads, and UTF-8 redirects in one test instead of paying four runner cases for allocation-only assertions.
- SQLite sensitive-fixture initialization, disposal, and boundary clearing should share one replaced callback scope; the boundary assertion can follow lifecycle assertions without rebuilding global hook state.
- SQLite connection policy builder coverage should verify connection strings, command timeout, and status diagnostics in one test; these allocation-only checks do not need three runner cases.
- SQLite command parameter builders should verify primitive types, stable dates, and copied parameter shapes on one command fixture rather than allocating three independent test cases.
- Timeout-origin coverage should exercise timer cancellation and caller cancellation sequentially in one async test so the distinguishing assertion does not duplicate runner setup.
- Bounded in-memory HTTP reads should cover unknown-length success and declared-length rejection in one async contract rather than splitting two adjacent buffer-policy assertions.
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
- Standalone codec/metadata DB tests that dispose their `DbContext` before cleanup should rely on `DeleteFile(path)` instead of resetting every process SQLite pool; this keeps pure codec classes parallelizable while retaining the Windows retry fallback.
- Error-taxonomy tests that only open a standalone corrupt DB through a command boundary follow the same rule; console capture stays protected by `TestConsoleLock`, but it does not require serializing the whole test class.
- Prefer `DeleteFile(path)` for temp DB, lock, metadata, cache, script, HTTP download, audit/metrics log and file-to-directory transitions, filesystem case probes, MCP diagnostics, and outside-fixture file cleanup instead of hand-written `File.Exists(...)` / `File.Delete(...)` pairs.
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
Before adding a test method, check whether the scenario can live in the closest existing method and reuse its setup and execution. Combine related read-only variants when they assert one cohesive contract; keep separate methods when they require isolated mutable state, distinct discovery/skip identity, substantially different setup, or clearer failure diagnosis.
For boundary tests, use the smallest fixture that still crosses the boundary. If the behavior only needs one page, chunk, cache, query-plan row, or offset overflow, do not scale synthetic data far past that point unless the larger size is part of the contract.

### CLI and console tests

- Capture stdout and stderr explicitly.
- Prefer `ConsoleCapture` for simple stdout/stderr capture, and lock direct console mutations with `TestConsoleLock.Gate`.
- Assert exit codes with `CommandExitCodes`.
- For JSON output, parse it with `JsonDocument` instead of asserting raw strings.
- For rejected checkpoint names, assert the usage exit/error code and syntax hint together, and verify that no checkpoint directory was created.

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
- CI は `tests/CodeIndex.Tests/CodeIndex.Tests.runsettings` 経由でテストプロジェクトを実行し、VSTest の blame crash / hang 収集、45分のセッションタイムアウト、60秒の xUnit long-running 診断を有効にします。初回失敗時は suite を1回だけ再実行し、再実行で成功した場合は TRX / blame artifact と一緒に `TestResults/flaky-retry.txt` を upload して、その実行を疑わしい flaky run として扱います。TRX telemetry summary と test-result artifact upload は失敗または retry 成功 lane だけで実行し、初回で clean に成功した lane では実行しません。stream された test output も、失敗後に upload / timeout inspection が必要な場合だけ `TestResults` 配下へ書き、failure log directory もその failure path でだけ作成します。telemetry summarizer は `--configuration Release` で実行し、failure diagnostics 中に必要なら helper を build するため、全 matrix lane で TRX summary を出せます。XPlat Code Coverage の収集は `ubuntu-24.04` / `net8.0` lane に限定し、すべての active CI lane で full suite を実行しつつ collector overhead を避けます。OS coverage は production CLI target の `net8.0` で実行し、`net9.0` compatibility coverage は `ubuntu-24.04` のみに絞ります。テスト実行は locked restore と Release build の後に `--no-build` で走らせます。primary lane は audit / publish coverage のため solution 全体を restore し、その後 `tests/CodeIndex.Tests/CodeIndex.Tests.csproj` を matrix framework 向けに build します。non-primary lane は同じ per-framework build の前に、`RestoreTargetFrameworks` でその test project の matrix framework だけを restore します。`net8.0` lane は、`global.json` が選ぶ 9.0 SDK で project を build し、8.0 SDK が test runtime を供給するため、両方の pinned SDK を維持します。`net9.0` compatibility lane は `9.0.301` だけを導入し、未使用の8.0 SDK/runtime downloadを避けます。`TestResults` 出力ディレクトリは `CodeIndex.Tests.runsettings` だけが管理します。ローカルの `dev.sh coverage` も同じ所有関係に従い、2 つ目の results-directory 引数は渡しません。`ubuntu-24.04` / `net8.0` lane では test project の未使用 `net9.0` target を build しません。`net9.0` build coverage は Ubuntu compatibility lane で維持します。また、formatting verifier は `make lint` だけを使います。NuGet cache key は全 project file ではなく `packages.lock.json` と `global.json` に基づき、OS 単位の restore key で partial cache reuse も許可します。package 入力の drift は locked restore で検出しつつ、テスト用 project だけの変更では package cache を失効させません。weekly mutation workflow も pinned Stryker global tool と NuGet package を cache し、変更のない test tooling を scheduled mutation run で再インストールしないようにします。
- C# CodeQL lane は restore と build だけを行うため、`global.json` が選ぶ pinned 9.0 SDK だけを導入し、未使用の net8 runtime を download しません。runtime test coverage は Build/Test と release workflow で維持します。
- CI の初回テスト実行と1回だけの retry は同じ workflow helper 経由にし、logger、blame、coverage 引数が drift しないようにしてください。PowerShell helper がテストの exit code を返す場合は、stream された test output を関数の success stream に載せず、代入で数値の exit code だけを受け取れるようにします。
- coverage collection は primary lane の初回 test attempt だけで実行し、flaky classification の1回だけの retry では同じ test 引数を再利用しつつ coverage collector を再実行しないでください。
- matrix test invocation は shared test helper の前に各 lane の scoped locked restore と Release build が完了しているため、`--no-build` と `--no-restore` の両方を使ってください。
- primary-lane publish も `--no-build --no-restore` を使い、Release test project 経由で build 済みの production project output と dependency graph を再利用してください。
- release の cross-compile lane は test を実行せず、self-contained RID publish が実 build を必ず行うため、RID 非依存の solution build を省略する。native lane は test 前の solution build を維持する。
- release の cross-compile lane は build しない test / tool project を復元せず、production project だけを locked restore する。native test lane は locked solution restore を維持する。
- release の cross-compile lane は self-contained binary を publish し、net8 test host を実行しないため、repository が選択する9.0 SDK だけを install する。native lane はpinされた両 SDK lineを維持する。
- release workflow の test も solution の locked restore と Release build 後に `--no-build --no-restore` を使い、runtime lane ごとの dependency 再評価を避けてください。
- curated release-note生成はchangelog toolのlock fileをcacheし、conditional locked restoreを1回行ってからtoolを`--no-restore`で実行してください。
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
  console writer synchronization coverageは文字writeごとのsleepではなくyieldを使い、wall-clock delayを追加せずinterleavingを露出できる十分なwhole-line iterationを維持してください。
- `SymbolExtractor*Tests.cs` と `ReferenceExtractor*Tests.cs`
  extractor のカバレッジは言語または機能領域ごとの partial test class に分割し、共有 helper は root 側の `SymbolExtractorTests` / `ReferenceExtractorTests` に残します。
  巨大 suite から繰り返しの extractor シナリオを切り出す場合は、言語、build-file 形式、protocol surface など読みやすい領域ごとの partial file にまとめ、raw substring や predicate assertion の繰り返しより小さな semantic assertion helper を優先してください。
  pattern-config scalar-cap variantは1つのtemporary rootと上書きする1 config fileを再利用し、単一console lock内のiteration間でglobal registryをresetしてください。
  fixture が同じ kind の複数 symbol name だけを検証する場合は、言語別 partial をまたいで `AssertSymbolsContain(...)` を使ってください。line、container、subtype、return type などの metadata を検証する場合は直接 predicate を維持します。
  同様に `(reference kind, container, symbol names)` の繰り返し検証には `AssertReferencesContain(...)` を使い、flag、context、line など edge 固有 metadata の検証には predicate を維持します。
  複数の reference name が同じ kind と完全一致 source context を共有する場合は `AssertReferencesContainInContext(...)` を使い、context がより詳細な edge contract の一部にすぎない場合は直接 predicate を維持します。
  1つの reference kind に対する否定チェックには `AssertReferencesDoNotContain(...)` を使い、container、context、line など他の metadata に依存する除外は直接 predicate を維持します。
  `ReferenceExtractorTests.ExtractSymbolsAndReferences(...)` は symbol 抽出から reference 抽出までの共通 setup を所有します。fixture が特殊な path や workspace symbol setup を必要としない場合は 2 つの extractor 呼び出しを繰り返さずこの helper を使い、reference だけを検証するテストでは未使用の `symbols` local を残さず symbol 側を `_` で捨ててください。
  Dockerfile の named-stage reference variant は、通常、小文字、platform flag、comment、hyphen、dot 形式を stage ごとの厳密な call 数で区別できる場合、1つの multi-stage fixture を共有します。外部 base image の除外も negative control として同じ fixture に残します。
  Dockerfile の `COPY --from` における ONBUILD と quote 付き stage 形式は、tag および digest 付き外部 image の control と fixture を共有します。正しい stage の call 数を厳密に検証し、正例 edge に紛れた false positive を見逃さないようにします。
  Dockerfile の RUN mount coverage は、通常、複数、quote、ONBUILD の stage source と、quote 付き text および command argument の負例を1つの fixture に置きます。stage ごとの厳密な件数により、別々の抽出を行わず negative control を観測可能にします。
  Dockerfile の ARG expansion coverage は、brace、brace なし、conditional、nested、escape 形式を、固有の ARG 名を持つ1つの fixture に置きます。宣言名ごとに1参照を厳密に検証し、escape の control が隠れた edge を追加できないようにします。
  Shell と PowerShell の reference fixture は、function container と synthetic `<script>` container の区別を検証するとき、宣言済み function 内の call と top-level call を同居させ、各言語を両方の scope contract に対して1回だけ抽出します。
  Swift の広範な代表 declaration fixture を、基本的な struct/function、macro、precedence group、operator の存在検証に対する唯一の正本とします。追加 metadata を検証せず同じ kind/name assertion を繰り返す狭いメソッドは追加しません。
  Swift の `@available`、`@discardableResult`、`package` などの attribute / visibility modifier は、契約が独立した kind/name 認識である場合、1つの declaration fixture を共有します。
  Swift extension coverage は、固有の target 名によって失敗診断を維持できる場合、escaped member、generic target、nested generic conformance target、special member を持つ qualified target を1つの source にまとめます。
  SQL の MySQL definer と PostgreSQL return field 抽出は、固有の symbol 名で各契約を独立して検証できる場合、comment/string false-positive control を含む1つの mixed-dialect fixture を共有します。
  SQL qualified-name の空白 coverage は、procedure、view、enum type、schema、sequence、extension、synonym などの CREATE/ALTER kind を1つの fixture に置き、DDL matrix 全体の dot normalization を1回の抽出で検証します。
  XAML の `x:TypeArguments` coverage は、scalar、type markup、nested generic、multiline の値を1つの resource dictionary に置き、wrapped type には固有名を使って失敗診断を維持します。
  XAML の type-object element、type-property element、type markup extension は、形式ごとに固有の型名を持つ1つの resource dictionary を共有し、曖昧な assertion なしで3表現を1回の reader traversal で検証します。
  XAML の common event handler は、multiline の `x:Name` / `x:Key` 値とともに wrapped search-attribute fixture に置きます。handler の厳密な件数を使い、通常属性と wrapped 属性の形式が1回の抽出を安全に共有するようにします。
  XAML の `Binding`、`x:Bind`、`CompiledBinding`、`ReflectionBinding` path は、固有の leaf 名を持つ1つの fixture を共有します。同じ property-name 集合から source/root と converter-parameter の除外も検証します。
  XAML の `ElementName` と `x:Reference` target 形式は1つの Grid fixture を共有します。property 名を一度だけ収集し、inline、object-element、property-element、ignored parameter の assertion をまとめて維持します。
  XAML の plain および `x:Static` 由来の `x:Key` declaration は、nested `Member={x:Type ...}` 構文も含めて static/dynamic resource lookup と同居させ、key の生成と利用を1つの resource dictionary で検証します。
  JavaScript の plain template 前にある空白付き・空白なしの chained comparison は、固有の operand 名を持つ1つの function fixture を共有し、generic tag の false positive を1回の抽出で防ぎます。
  JavaScript の single-line `for...of` / `for await...of` plain-template 負例は、同期・非同期 function を1つの source に置き、phantom `call of` がゼロであることを1回だけ検証します。
  JavaScript の multiline `for...of` / `for await...of` plain-template 負例も1つの source と1つの phantom-call 除外を共有します。
  JavaScript の for-of header 負例は、同じ特殊空白抑制を検証する NBSP 同期・非同期形と BOM 同期形を1つの source で共有します。
  JavaScript の予約語 member tag は、`default`、`return`、`finally`、`await` の名前別 call 数を厳密に検証する同期・非同期 fixture を共有します。
  CSS selector-form coverage は、selector list、descendant、compound class/ID、standalone ID、quoted-attribute の類似文字列、hex-color 負例を、厳密に診断できる固有名付きの1 fixture で共有します。
  CSS animation shorthand と comma-separated `animation-name` coverage は、固有の keyframe 名と共通の `none` 除外を持つ1 fixture を共有します。
  SCSS の quoted、URL、bare-URL、media-qualified import は、引数あり・なしの mixin include と1つの entry-point fixture を共有し、import と call edge を同時に検証します。
  TypeScript runtime `typeof` 負例は、multiline assignment と inline arrow-function の配置を1つの source に置き、両 operand 名を type reference から一度に除外します。
  TypeScript generic tagged-template coverage は、通常型引数と function-type 型引数を固有 tag・container assertion 付きの1 source で共有します。
  App-manifest DTD coverage は local/external entity declaration を1 document にまとめ、assembly 抽出を維持しつつ external target が signature に入らないことを検証します。
  Solidity の代表 declaration/range fixture は既存行上の comment/string false-positive control も担当し、range assertion をずらさず2回目の抽出を省きます。
  Python type-introspection helper coverage は bare/qualified `cast` / `assert_type` を他の helper API と同居させ、固有 target type と container を1回の抽出で検証します。
  Python f-string interpolation coverage は single-line、multiline、nested-expression、format-specifier 後の call を、container 別 call 集合を厳密に検証する1 fixture で共有します。
  Swift の `.self`、`#selector`、`#keyPath` expression coverage は1 fixture を共有し、multi-segment key path の全 member segment を含め、qualified root と instance/member token の除外をまとめて検証します。
  JavaScript の semicolonless blockless-arrow boundary は、後続 class、expression＋class、CommonJS class-export 形を、固有 hidden/visible 名と厳密な arrow end line を持つ1 source で共有します。
  JavaScript の direct/wrapper-call blockless-arrow class return は、厳密な lambda range と固有 hidden class/member 除外を持つ1 source を共有します。
  JavaScript callable-scope local-class coverage は、class method、通常 function、CommonJS function expression の direct/class-expression 形と固有 leak sentinel を1 source で共有します。
  JavaScript nested local-class coverage は IIFE、static block、object concise method、getter、setter scope を1 source で共有し、visible sibling method を境界 control として残します。
  JavaScript CommonJS class-expression coverage は named export、default inline/multiline/parenthesized/conditional assignment、property export を、固有 member と厳密な default-class 件数を持つ1 source で共有します。
  JavaScript/TypeScript CommonJS object-export API は `defineProperty`、`defineProperties`、`Object.assign` 形を、固有 exported/computed/dynamic/non-export target 名を持つ1 theory fixture で共有します。
  JavaScript/TypeScript の `import.meta` module discovery は `import.meta.resolve` と `new URL(..., import.meta.url)` の正負例を、line/signature 検証を維持した1 theory fixture で共有します。
  JavaScript/TypeScript worker-loading coverage は `importScripts`、`Worker`、`SharedWorker` variants を、line/signature 検証と dynamic/string/method/constructor 負例を維持した1 theory fixture で共有します。
  JavaScript/TypeScript browser module-registration coverage は service-worker/worklet API を、scope/options signature と receiver/dynamic/string 負例を持つ1 theory fixture で共有します。
  JavaScript/TypeScript dynamic-import coverage は multiline literal、import attributes/assertions、static template と receiver/dynamic/string 負例を、形式別の line/signature 検証を持つ1 theory fixture で共有します。
  JavaScript の通常文字列と template literal の brace masking は、固有の class/member を持つ1 fixture で共有し、各 literal 後の正確な container/range 検証を維持します。
  JavaScript regex brace masking は、direct literal、wrapped `if`/`else if`、plain `else`、`do`/`while`、`finally` 形式を、末尾 sibling 境界と block-comment method-shape 負例を持つ1 class fixture で共有します。
  JavaScript class-field arrow の ASI coverage は、numeric/string field 境界、computed-member continuation、class-closing literal 終端、末尾 template literal を、固有の class/member 名を持つ1 fixture で共有します。
  JavaScript default-class coverage は、named class と anonymous direct-base/mixin-base 形式を1 extraction にまとめ、member を維持しつつ `extends` を class 名として捏造しないことを検証します。
  TypeScript default-class coverage も同じ共有 fixture 方針で named、direct-base、mixin-base、implements-only 形式をまとめ、anonymous member の `default` container 検証を維持します。
  TypeScript inline-class coverage は、single-member recognition、sibling 分割、正確な signature、共通 class-container metadata を1つの multi-method declaration で検証します。
  TypeScript same-line sibling-class coverage は、distinct/identical method-name case を1 source にまとめ、4つの class container で attribution の診断性を維持します。
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
  CLI の引数解析、コマンド実行、installer 挙動のテスト。Index command coverage は run mode または機能領域ごとの partial suite に分割し、Query command coverage は command family ごとの partial test class に分割して、共有 console / fixture helper は一箇所に保ちます。`ProgramCliTests.cs` はグローバル引数の解釈や完全な CLI 起動フローのように subprocess 経由で確認すべき Program エントリポイント挙動を扱い、`InstallScriptTests.cs` は `install.sh` を library mode で source した bash snippet を実行して、実ネットワーク install を行わずに release installer の回帰を固定する。installer bundle 生成テストでは、`install.sh` が generated と判定される一方、canonical な `install_modules/` source はすべて unmarked のままであることも検証してください。
  invalid scalar input だけが異なる argument-validation variant は、case ごとの state や discovery identity が不要なら1つの database fixture を共有し、fact 内で反復してください。
  excerpt の focus-column validation も zero と non-numeric value にこの規則を適用し、1つの indexed Markdown fixture を再利用してください。
  inspect path-line の exact/enclosing-symbol case は1つの indexed source fixture を再利用し、read-only line query を fact 内で反復してください。
  definition と symbols の exact-mode conflict validation は1つの空databaseとcross-command flag-pair tableを共有してください。
  symbols compact flag/aliasとsummary-only JSON envelopeは1つのeditor-format fixtureを共有してください。
  symbols JSON array、LSP、quickfix、SARIF location format は1つの editor-format fixture を共有し、definition SARIF severity のテストも同じ fixture を再利用して、情報レベルの `note` 出力を warning レベルの診断出力とは分けて検証してください。
  コマンド別の出力形式 coverage は command / format matrix で parser の受理と対応する usage line の両方を検証してください。共通 parser が認識してもコマンド側に実装がない形式には、別途 usage error の assertion が必要です。
  unused default-suppressionのrow、JSON count、summary-only、text count envelopeは、`--all` count controlも含めて1つのunused-symbol fixtureを共有してください。
  unusedのdefault-suppressed JSON cursor paginationと`--all` JSON cursor paginationは1つのunused-symbol fixtureを共有してください。
  unusedのfull JSONとcompact `--by-bucket` JSON envelopeは1つのtaxonomy fixtureを共有してください。
  unusedのbucket、minimum-confidence、actionable confidence-alias JSON filterは1つのunused-symbol fixtureを共有してください。
  unusedのfull-summary JSON countとbucket-filtered JSON countは1つのtaxonomy fixtureを共有してください。
  unusedのlimited-page returned countとbucket diversificationは1つのtaxonomy fixtureを共有してください。
  unusedのindexed-pathとunsupported-languageのzero-result schemaは1つのindexed fixtureを共有してください。
  outlineの`size` sortとその`span` aliasは1つのranking fixtureを共有してください。
  outlineのreference metric sortとcomplexity metric sortは1つのderived-ranking fixtureを共有してください。
  outlineのkind sortとdefault source-order field projectionは1つのranking fixtureを共有してください。
  depsのJSONとjson-graphのbyte-limit failureは1つのSQL graph fixtureを共有してください。
  depsのJSON summary outputとjson-graph summary rejectionは1つのSQL graph fixtureを共有してください。
  referencesのstale SQL contract count envelopeとresult envelopeは1つのdowngraded graph fixtureを共有してください。
  callersとcalleesのstale SQL contract result envelopeは1つのdowngraded graph fixtureを共有してください。
  MCP SQL graph readiness fieldとdegraded reasonはresponseごとに1回だけassertしてください。
  MCP analyze-symbolとreferencesのstale SQL contract checkは1つのdowngraded server fixtureを共有してください。
  MCP mixed/zero-result SQL graph metadataのassertは重複させず1回にしてください。
  MCP depsとsymbol-hotspotsのdegraded zero resultは1つのdowngraded server fixtureを共有してください。
  MCP unused-symbolsとsymbol-hotspotsのkind-filtered clean zero resultは1つのdowngraded server fixtureを共有してください。
  CLI unusedのkind-filtered zero-resultとcount JSON envelopeは1つのdowngraded SQL fixtureを共有してください。
  CLI hotspotsのunfiltered degraded zero resultとkind-filtered clean zero resultは1つのdowngraded SQL fixtureを共有してください。
  callersのmixed-repository pure-C# result envelopeとcount envelopeは1つのdowngraded graph fixtureを共有してください。
  MCP callersとanalyze-symbolのpure-C# checkは1つのmixed-repository downgraded server fixtureを共有してください。
  MCP definitionのclean metadataとcallers/impactのdegraded metadataは1つのstale-contract server fixtureを共有してください。
  deps missing-graphのbyte-limitとsummary-only zero payloadは1つのread-only fixtureを共有してください。
  depsのJSON-only controlをnon-JSON formatで拒否する検証は1つのdata-driven theoryにしてください。
  unusedのmissing-chunks countとdegraded reflection resultは1つのmutated fixtureを共有してください。
  unusedのmissing-graph JSON schemaとhuman count warningは1つのempty database fixtureを共有してください。
  unusedのJSON confidence taxonomyとhuman bucket groupingは1つのunused-symbol fixtureを共有してください。
  Razor directive kind-filter query は1つの indexed component fixture を共有し、route、implements、attribute、layout の期待値を1つの fact 内で反復してください。
  symbols literal-query coverage は、compact風のtextが展開されないことを確認するdouble-dash形式と明示的`--query`形式で1つの空databaseを共有してください。
  symbols の hotspot、references、size、path ranking query は同じranking signalのread-only viewなので、1つのsymbol-sort fixtureを共有してください。
  graph-command kind-semantics warning は references、callers、callees 全体で1つの graph-ready database を再利用してください。
  exact-zero graph hint はscenarioごとに1つのTarget/Caller統合fixtureを使い、references、callers、calleesを反復してください。
  C# query-range generic null-comparison regression は、どちらもenum reference漏えいがない同じ契約ならequalityとinequality形式を1つのindexed sourceに併置してください。
  C# query-range collision の basic selection、directional ordering、keyword 名 member、object initializer は、同じ空の inspect reference bundle を検証する場合は1つの indexed source を共有してください。
  C# inspect の brace-range regression は char literal、raw string、verbatim string を1つの indexed class に併置し、それぞれの後続 method を共有 fixture から query してください。
  C# generic query-range selector は単純型引数と tuple 型引数を1つの fixture で共有し、generic type-pattern coverage は designation 有無を別の1 fixture で共有してください。
  C# switch-expression pattern-variable coverage は、真の enum-member reference 集合を同じ contract とする recursive、declaration、guard、comment-trivia 形式を1つの extractor fixture で共有してください。
  C# foreach shadowing coverage は embedded、same-line、dangling-else 形式を1つの source で共有し、残る reference を container で区別してください。
  C# lambda parameter shadowing coverage は simple、multiline、after-lambda、same-line boundary 形式を1回の extraction pass で共有してください。
  C# query range-variable の `select` 名 member は plain、escaped、trivia-separated access 形式を1つの extractor source で共有してください。
  C# statement-pattern shadowing は switch-case、conditional、recursive、multiline-recursive、recursive-case 形式を1回の extraction pass で共有してください。
  C# statement-boundary shadowing は out-declaration、out-var、catch、using scope を1つの extractor source で共有してください。
  C# terminal-select generic type-pattern coverage は designation あり / なしのmethodを1回の extraction pass で共有してください。
  C# terminal-select generic `as` null-comparison coverage は equality、inequality、後続する真の enum reference を1つの extractor source に併置してください。
  C# using-alias coverage は enum / non-enum target を1つの extractor source 内の別 namespace に置き、enum target だけが残ることを検証してください。
  C# property-receiver shadowing は instance、instance-from-static、static-property context を1回の extraction pass で共有してください。
  C# indented lexical shadowing は local、using-var、property-accessor container を1つの extractor source で共有してください。
  C# parenthesized terminal select 前のnullable suffix coverage は scalar、tuple、array-rank 形式を1回の extraction pass で共有してください。
  C# query range-variable order-by coverage は comma / directional-comma 形式を1つの extractor source で共有してください。
  C# query range-variable scope coverage は query-only、after-query、query-argument boundary を1回の extraction pass で共有してください。
  C# terminal select 前のparenthesized keyword-named value は parameter / local 形式を1つの extractor source で共有してください。
  C# terminal-select generic-call coverage は single、comma-separated、tuple type argument を1回の extraction pass で共有してください。
  C# order-by ternary coverage は greater-than、less-than、bang operator 後のkeyword-named local function を1つの extractor source で共有してください。
  C# awaited order-by coverage は direct / comment-separated のkeyword-named local-function call を1回の extraction pass で共有してください。
  C# throw-expression order-by coverage は `select` / `group` 名のlocal function を1つの extractor source で共有してください。
  C# parenthesized compound order-by coverage は ternary / coalesce expression を1回の extraction pass で共有してください。
  C# parenthesized query-terminal argument coverage は select / group-by 形式を1つの extractor source で共有してください。
  C# local-shadowing boundary coverage は後続 declaration と nested-block exit を1回の extraction pass で共有してください。
  property lexical-shadowing coverage は getter-only と getter/setter のscope boundaryを共有のindented fixtureに併置してください。
  lambda parameter shadowing は parenthesized same-line と通常のmethod-parameter 形式を共有のlambda fixtureに併置してください。
  C# lambda-scoped declaration-pattern coverage は ordinary、nested、static lambda 形式を1回の extraction pass で共有してください。
  C# declaration-pattern statement coverage は single-line if、multiline if、multiline while 形式を1つの extractor source で共有してください。
  C# parenthesized terminal-select boundary coverage は uppercase-constant / generic-close predecessor を1回の extraction pass で共有してください。
  C# nested-query order-by coverage は plain / parenthesized comma boundary を1つの extractor source で共有してください。
  C# casted local-select order-by coverage は object、simple、multiline、lowercase-alias 形式を1回の extraction pass で共有してください。
  query range-variable order-by coverage は anonymous-type / object-initializer comma 形式を共有のorder-by fixtureに併置してください。
  C# parenthesized terminal select 前のpostfix-expression coverage は null-forgiving / increment 形式を1回の extraction pass で共有してください。
  inspect / references command coverage も同じ switch-expression の統合方針を適用し、各 surface で1つの graph-ready database を構築して container ごとに結果を検証してください。
  inspect reference-bundle coverageにも同じnull-comparison統合fixtureを適用し、operatorごとの個別indexingを避けてください。
  production-runtime switch relational-pattern coverage はless-thanとgreater-thanのmethodを1 sourceに置き、CLI indexing subprocessを1回だけ実行してください。
  generic switch-arm のguardとrelational predecessorも同様に1つのproduction-runtime fixtureを共有し、production `net8.0` targetだけで実行してください。
  search language-alias coverage は、各filterが期待結果を1件に分離できる場合、異なる言語fileを1 databaseに置いてalias filterを反復してください。
  option風literalのnamed-query escapingは、definition、graph、symbols、files、inspect、impact command全体で1つのindexed Probe fixtureを再利用してください。
  JavaScript extension、YAML、batch、SQL dialectのsearch alias variantは、それぞれ1つのlanguage fixtureを再利用し、casing/spelling形式をfact内で反復してください。
  raw FTS syntax coverage はvalid control queryと全invalid query/hint variantで1つのindexed sourceを再利用してください。
  literalとraw FTSのcomplexity boundはlength、token count、NEAR count、lowercase operator control全体で1つのindexed sourceを再利用してください。
  XAML、Rust、common multi-language、JavaScriptのalias setはそれぞれfixtureを1回だけ構築し、全accepted spelling/casing形式を反復してください。
  inline comment-marker exclusionはJavaScript line/block commentとPython line commentを1 indexに置き、marker queryを反復してください。
  search exact-mode conflict coverageは全pairwise/triple flag setで1つの空databaseを共有してください。
  search path/exclude-pathのinvalid-glob guardは1つの空databaseを共有し、query評価前にoption nameを反復してください。
  find long-line JSON coverageはbounded snippetとzero-width unclamped snippetで1つのtext fixtureを再利用してください。
  excerpt location parsingとfocus dependency/range validationは、range fixtureとlong-line fixtureを含む1 databaseを再利用してください。
  C# verbatim qualified nameとJava Unicode escapeのsearch/find canonicalizationは、言語ごとに1つのindexed sourceを共有してください。
  TypeScriptとJavaのlanguage-alias filteringは、固有query markerで各言語結果を分離できるため1つのmulti-language databaseを共有してください。
  Kotlin backtickとJava Unicode-escape canonicalizationは別databaseを作らず、path-filtered C# verbatim canonical fixtureを共有してください。
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
  リダイレクトした console 出力の下で watch loop の起動と停止を検証する。未使用の index を事前構築せず非同期 watch core を直接呼び、同期的な起動 prefix が start event を出力した後に cancel して bounded shutdown wait を行う。この経路に index setup、専用 thread、signal polling、固定 sleep、process-wide SQLite pool reset を再導入しないこと。platform 固有の SQLite retry は temp-project cleanup helper が所有するため SQLite pool による直列化は不要だが、redirect した process-wide console 出力には console-sensitive collection が引き続き必要である。
  cancellation contract fixture は `git init` と明示的な case-sensitive setting だけを使い、watch loop 終了後の2回目の repository-discovery probe と、commit 対応 Git fixture の無関係な identity/signing setup の両方を避けながら `path_comparison` の期待値を決定的にする。
- `BackgroundTaskObserverTests`
  は `BackgroundTaskObserver` の fault-only continuation 契約に依存します。canceled
  task は直接 await し、warning が抑止されたことを示すための cancellation 後の固定 sleep
  は不要です。
- cancellation だけを検証する timeout test は、fixture に無関係な wall-clock 完了経路を持たせないよう infinite cancellable delay を使います。
- SQL diagnostic truncation test は、長さが増え続ける数千個の `UNION` 句を組み立てず、最小の固定幅 suffix で文字数 cap を超えます。
- SQLite scalar-reader diagnostic は、overflow と null-result の契約で 1 つの open 済み in-memory connection と command を再利用します。
- 内容が契約に無関係な oversized file guard は sparse な length-only fixture を作り、同サイズの managed byte array 割り当てを避けます。
- full-scan と scoped-update の oversized-file warning test は `TestProjectHelper.WriteSparseFile(...)` を共有し、両方の実行モードで 10 MiB fixture 割り当てを避けます。
- oversized ignore-file rejection も同じ sparse helper を使います。size preflight が rule 内容を読む前に拒否するためです。
- per-line oversize detection は 4 本の短い物理行を使います。契約は各行で独立しており、数百回の反復は境界 coverage を増やしません。
- captured-output overflow test は、より大きな超過量自体が契約でない限り、文字数 budget を 1 文字だけ超えます。
- JavaScript と TypeScript の same-line class scan は、inline member、異名/同名 method を持つ sibling class、statement prefix、callable-local class masking を言語ごとに1つの fixture で共有します。layout ごとに method を追加せず、個別の container、signature、hidden-local assertion を共有 extraction 内に維持してください。
- Rust attribute reference coverage は direct / `cfg_attr` derive list、multiline 座標、通常 annotation を1回の extraction で共有します。Rust mutable-reference coverage も type position、`dyn` / `impl`、borrow-expression 除外を1つの fixture に維持します。
- TypeScript と Swift の基本 type-alias expansion は、heritage、generic parameter 除外、type/value position 混在 coverage を言語ごとに1つの fixture で共有します。alias binding の分離自体が契約である scope-shadowing fixture は独立したまま維持します。
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
- C# reflection-name 抽出 coverage は、literal、定数連結、dynamic、comment、string decoy を1つの source fixture にまとめ、これらの parser boundary で1回の symbol/reference pass を共有します。
- C# BOM 抽出は、単純な先頭 BOM import fixture と、CRLF・bare CR・LF 境界で先頭/mid-file BOM を同時に扱う1つの混在改行 fixture を維持します。混在 fixture に含まれる改行 subset ごとに抽出 pass を重複させないでください。
- C# lambda capture coverage は、外側 local の正例、parameter shadowing、同名 method 間の分離を1つの source fixture にまとめます。capture が1件だけである assertion により、negative region からの漏れも同時に検証します。
- C# interpolated string の escaped-brace coverage は、通常形式と逐語形式で同じ phantom call 除外を検証するため、1回の抽出 fixture を共有します。
- C# raw string 内の direct interpolation と nested interpolation は1つの source fixture と抽出 pass を共有し、container assertion は個別に維持します。
- generic C# attribute の分類と custom type-argument reference は、assembly target、single-line、multi-line の各形式で1つの fixture を共有します。
- nested generic attribute 形式も同じ共有 fixture に含め、山括弧の深い入れ子だけのために2回目の parser pass を実行しません。
- method、delegate、lambda の no-argument parameter attribute は、同じ section-local parenthesis-depth 規則を通るため1つの C# fixture を共有します。
- 引数付き parameter attribute は、inline と改行された declaration layout で1つの method fixture を共有します。
- direct と `global::` の static type qualifier は、container ごとの reference assertion を維持しながら1つの C# fixture を共有します。
- using statement と field access の static qualifier は、1つの consumer fixture と抽出 pass を共有します。
- namespace qualifier と PascalCase の instance-member chain は rightmost static type reference と1つの qualifier fixture を共有し、1回の parse 後に正例と call 除外を検証します。
- qualified C# type の declaration と cast/generic expression は、どちらも namespace segment を call reference から除外するため1つの fixture を共有します。
- C# doc-cref masking の delimited-comment marker coverage は、comment close 後の raw string、raw-string content、verbatim-string content で1つの fixture を共有します。
- C# constructor-chain coverage は `this`、`base`、cross-line initializer を1つの class hierarchy で共有し、書き換え後の target を constructor container で区別します。
- C# record constructor-chain coverage は inline、multiline、braced、same-line body の各 header layout を1つの fixture で共有し、record ごとの帰属検証を維持します。
- C# 12 primary constructor coverage は class、struct、generic、split-line、same-line body を1つの fixture で共有し、base 名を分けて case ごとの診断精度を維持します。
- C# constructor の base-target coverage は generic、interface list、nested generic、multiline、constraint 付き layout を1つの fixture で共有し、一意な末尾型ごとに書き換え後の edge を検証します。
- C# multiline member 帰属テストは expression-bodied method/property、Allman/same-line brace、途中の block comment を1つの fixture で共有し、一意な member container ごとに検証します。
- Python の `isinstance`/`issubclass` runtime type check は単一型と tuple 形式を1つの module fixture で共有し、function container ごとの検証を維持します。
- Python class header の type reference は単一 base、複数 base、metaclass 形式を1つの module fixture で共有し、class container で区別します。
- Python annotation coverage は direct/generic の return、parameter、local variable 形式を1つの module fixture で共有し、function container ごとの検証を維持します。
- Python typing factory の coverage は `TypeAlias`、`NewType`、bound 付き `TypeVar`、constraint 付き `TypeVar` を一意な target type を持つ1つの module fixture で共有します。
- Python advanced typing coverage は multiline/comment 付き `TypeVar`、`ParamSpec`、callable annotation、variadic tuple unpack、literal union を一意な型名を持つ1つの logical-header fixture で共有します。
- Python type introspection helper coverage は direct/qualified `get_type_hints`、dataclass/attrs field lookup、Pydantic `TypeAdapter` を一意な target を持つ1つの module fixture で共有します。
- Python exception の type-reference coverage は bare/chained raise、single/tuple except、`pytest.raises`、`contextlib.suppress` を1つの module fixture で共有します。
- Python f-string masking は single-line、multiline literal、nested string brace の interpolation 形式を一意な call container を持つ1つの module fixture で共有します。
- Python keyword filtering は他言語の keyword に似た call と `raise(...)`/`yield` 構文を1つの module fixture で共有し、内側の正しい call 検証を維持します。
- Python class property symbol coverage は annotated/assigned attribute、slots、augmented slots、match args、annotations dictionary を1つの module fixture で共有します。
- Python property decorator symbol coverage は cached、accessor/setter/deleter、direct/qualified abstract decorator を1つの module fixture で共有します。
- Python static import の symbol expansion は alias、imported name、qualified from-import name、dotted prefix を1つの module fixture で共有します。
- Python package の `__all__` export coverage は assignment、append、inline extend、split-line extend を1つの qualified `__init__.py` fixture で共有します。
- Python package import coverage は local/external alias、current-package import、relative module member、parent-package member を1つの `__init__.py` fixture で共有します。
- Python の未閉鎖 multiline import coverage は EOF と後続コードありの case で1つの assertion path を共有し、両方の parse を保ったまま test setup の重複を減らします。
- Python basic symbol coverage は synchronous/asynchronous function、assigned lambda、class を1つの module fixture で共有します。
- Python triple-quoted string coverage は先頭 module docstring の heading と double/single/raw fixture masking を1回の extraction で共有します。
- Ruby loading/dependency DSL coverage は `require`、`require_relative`、`load`、`gem`、`autoload` を1つの fixture で共有し、container ごとの target を検証します。
- Ruby metaprogramming declaration coverage は alias、constant visibility、`module_function` export を1つの fixture で共有し、DSL keyword を抑止します。
- Ruby Rails model/schema DSL coverage は enum、table creation、typed attribute、serialization、composed value、nested attribute を1つの fixture で共有し、target と option を区別します。
- Ruby exception coverage は rescue clause、Rails `rescue_from`、parenthesized `raise` 構文を1つの fixture で共有し、exception target を保ちながら control/option token を抑止します。
- Ruby Rails command-target coverage は association、validation、callback、`class_name` option を1つの fixture で共有し、keyword option token を除外します。
- Ruby type composition coverage は inheritance、mixin/using target、contextual control keyword、refinement を1つの fixture で共有し、container ごとの reference を検証します。
- Ruby call syntax coverage は parenthesis なし command、brace block、`do`/`end` block を1つの fixture で共有し、container 帰属を維持します。
- Ruby framework subject DSL coverage は RSpec `describe` constant と Rails route resource symbol を1つの fixture で共有し、DSL/option keyword を抑止します。
- Go composite literal coverage は generic、array/slice、map 形式を一意な element/key/value type を持つ1つの function fixture で共有します。
- Go type conversion coverage は composite、parenthesized pointer/qualified、parenthesized composite 形式を一意な converted type を持つ1つの function fixture で共有します。
- Go method expression coverage は plain、pointer、qualified、generic receiver を1つの fixture で共有し、value expression の negative assertion を維持します。
- Go type-set coverage は interface union と standalone composite approximation を1つの fixture で共有し、bitwise expression の negative assertion を維持します。
- Go generic type-argument coverage は call と standalone instantiation の形式を1つの fixture で共有し、indexed value の negative assertion を維持します。
- Go struct-field coverage は embedded、multi-name、generic、inline field を1つの file fixture で共有し、field name の negative assertion を維持します。
- Go function-signature coverage は literal、declared function type、function-valued field/variable、inline interface の形式を1つの file fixture で共有します。
- Go generic-constraint coverage は function declaration と type declaration を1つの file fixture で共有します。
- Go value-declaration coverage は multi-name と generic declaration、および local inference を1つの file fixture で共有します。
- Go method-signature coverage は concrete receiver と interface member の形式を1つの file fixture で共有します。
- Go runtime-type-check coverage は direct assertion と type-switch case を1つの function fixture で共有し、value-switch の negative assertion を維持します。
- Go channel/builtin-type coverage は directional declaration、named channel type、`make`/`new` allocation を1つの file fixture で共有します。
- Go symbol function coverage は regular/receiver/generic function と assigned function literal を1つの extraction fixture で共有します。
- Go import-symbol coverage は single/grouped import、build directive、cgo classification を1つの file fixture で共有します。
- Go type-symbol coverage は named、alias、struct、interface、generic declaration を1つの file fixture で共有します。
- Go declaration-symbol coverage は grouped type、top-level const/var、blank identifier、local negative を1つの file fixture で共有します。
- Go embedded-type symbol coverage は generic struct field と interface member を container attribution 付きの1つの file fixture で共有します。
- Go function-like symbol coverage は qualified receiver container と branch label を1つの file fixture で共有し、switch keyword を除外します。
- 統合した Python/Go extractor fixture でも、container ごとの完全な result set を含む symbol/reference の厳密な件数と、元の回帰契約に含まれていた source line の assertion を維持します。
- Assembly reference coverage は direct call/branch と tab 区切り decorated indirect-target の negative case を、厳密な件数を持つ1つの fixture で共有します。
- Solidity reference coverage は inheritance whitespace variant、library/modifier/event/interface edge、comment/string negative を1つの fixture で共有します。
- Terraform reference coverage は resource/module/data traversal と raw `var`/`local` object reference を、name ごとの厳密な件数を持つ1つの fixture で共有します。
- Language masker の copy-on-write coverage は、変更不要時の配列再利用と mask 必要時の clone を Lua/Solidity それぞれ同じテストメソッド内で検証します。
- COBOL target-statement coverage は SQL/CICS、report、sort、queue、file、literal、external-call variant を1つの program に配置し、group ごとの edge 件数を厳密に検証します。
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
  変換後のcoverage booleanは、大文字小文字を区別しない`CollectCoverage` string parameterとは異なる名前のlocalに保持してください。同名だとPowerShellがtyped helper呼び出し前にbooleanをstringへ戻します。
  repository rootの`TestResults` directoryを`dotnet test`へ明示的に渡してください。そうしないとrunsettingsのpathがtest project相対で解決され、TRX/coverage outputがworkflowのtelemetry/artifact pathから分離することがあります。
  初回attemptとflaky classification retryには別々のTRX filenameを付け、成功したretryが、その契機となったfailure evidenceを上書きしないようにしてください。
  crash diagnostics は初回 attempt だけで収集する。flaky classification retry は初回の evidence を再利用して crash collector を省略する一方、retry 自体が hang した場合に備えて blame-hang と5分の kill bound は維持する。
  TRX telemetry summary は test helper が初回 attempt の失敗を報告した場合（retry 成功を含む）だけ実行する。clean first-pass lane と test 開始前に失敗した job は、2回目の project 起動と TRX parse を支払わない。不要な転送時間を避けるため、result / dump artifact upload も failure-gated のままにする。
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
- console だけを扱う test class は、writer disposal check を含むすべての capture / swap 期間が `TestConsoleLock.Gate` で保護されていれば、SQLite-sensitive non-parallel collection に入れる必要はない。
- pure i18n resolution、内部で lock する JSON-envelope capture、独立した LSP request / budget fixture は SQLite-sensitive collection の外に保つ。一時 DB を所有するだけなら、context を helper cleanup 前に dispose している限り process-global state ではない。
- instance ごとに DB を所有する reader issue fixture と、instance ごとに directory を所有する external-process fixture も、`Dispose` で共有 helper を通して resource を解放する限り parallel-safe である。
- schema-constraint fixture は directory cleanup 前にすべての `DbContext`、connection、command、reader を dispose する。独立した schema check を直列化する無条件 pool reset を追加しないこと。
- SQLite connection-string / command-policy test は、各 in-memory connection と command を test instance が所有して dispose する限り parallel-safe である。`Microsoft.Data.Sqlite` を参照するだけでは SQLite-sensitive collection に入れる理由にならない。
- cross-platform path matrix は、cache seed を `PathCasingTestLock` で保護し、各 filesystem / git fixture が一意な temporary workspace を所有する限り parallel 実行できる。
- exception-formatting contract test は共有 console helper を `TestConsoleLock` で保護して parallel 実行可能に保つ。in-memory SQLite retry probe は process pool を変更しない。
- pure な事前 cancellation import / bounded-stream check は test-only hook を変更する import replacement fixture から分離し、hook を所有する class だけを直列化する。
- release workflow / package-normalizer test は SQLite-sensitive collection の外で実行できる。xUnit は単一 class 内の method を直列に保つため、scope された2件の durability-hook probe は互いに重ならない。
- database diff fixture は独立した left / right project を所有し、console capture を lock し、唯一の row-budget override を同じ xUnit class の直列実行内に閉じるため、process-wide な SQLite 直列化は不要である。
- `SuggestionStore` の environment boundary / configured pruning case は小さな non-parallel fixture に隔離する。hash、deduplication、archive、通常の persistence case は独立 directory を所有し、parallel 実行可能に保つ。
- pure な `ProcessStartInfo` construction contract と subprocess environment filtering test を分離する。parent process environment を変更して non-parallel collection が必要なのは後者だけである。
- freshness diagnostic classification は parallel 実行し、`GitHelper` / `FileIndexer` test hook を一時的に置き換える stamped-case probe test だけを隔離する。
- production source の direct environment access policy scan は read-only で parallel-safe である。実 process variable を変更する別の `CdidxEnvironment` mutation contract だけを隔離する。
- reflection だけを行う SQLite collection registration contract は parallel-safe である。global pool-clear callback を置き換える fixture lifecycle test だけを sensitive collection に残す。
- current directory または実 environment variable を変更する config CLI case を隔離する。通常の config parse / validation は注入された environment reader と独立 temporary root を使うため、main suite は parallel 実行可能に保つ。
- JSON API-version fixture は locked console capture と scoped project cleanup を使う。project 削除後の無条件 pool reset は冗長なうえ遅すぎるため、この contract suite は parallel 実行可能な状態を保つ。
- golden JSON snapshot fixture は空の stderr 自体が契約なので non-parallel な console-sensitive collection を使い、instance ごとの database cleanup を維持する。status、search、references、impact、excerpt の各 snapshot 後に process-wide pool reset を支払わないこと。
- extractor plugin-registry fixture も、reject された pattern config が process-wide stderr writer 経由で診断を出すため、同じ console-sensitive collection を共有する。
- process-wide console writer を capture、差し替え、または意図的に close する suite は同じ console-sensitive collection を使う。SQLite pool cleanup を外しても console mutation は parallel-safe にはならない。
- 一時 repo / file: 可能な限り `TestProjectHelper` 経由で作り、user-level の git config に依存しない。
- real server を生成する MCP unit fixture は、system temp directory に単独 DB を残さず、scoped temporary project 内へ DB を置き、server dispose 後に project を削除する。
- MCP audit fixture は database、active log、rotated log を同じ temporary project 配下へ置き、file ごとの cleanup list を1回の resilient directory cleanup に置き換える。
- DB lifecycle fixture の dispose は context と exclusive pool ownership を解放後、resilient shared file helper を直接呼ぶ。cleanup retry の枯渇を隠す二重の catch-all で包まない。
- workspace active-status transition は inactive、active、missing、stale assertion で1つの manifest / config fixture を再利用し、同じ workspace を4つの独立 test で再構築・再選択しない。
- workspace metadata の result-shape parity は1つの dirty Git fixture から status、map、analysis object を enrich し、同一 repo の initialize / commit を3回繰り返さない。
- persisted HEAD の drift / recovery assertion は1つの Git fixture 内で metadata を更新し、matching state のためだけに2つ目の repo を作成しない。
- latest indexed HEAD の優先順位は1つの seed 済み repo から status / analysis result shape の両方で検証し、同一 Git / DB setup を重複させない。
- commits-ahead の ancestor / missing-stamp behavior は同じ multi-commit repo を共有する。missing case は `IndexedHeadSha` のない新しい result object だけで検証できる。
- shared file-URI escaping と LSP round-trip parity は1つの path / root case で検証し、同等の percent-encoding setup を別 test で重複させない。
- no-timeout sentinel coverage は zero / infinite budget を1つの contract test で検証する。どちらも同じ caller-cancellation path に従うため scope setup を重複させない。
- bounded HTTP private-file の success / overflow case は1つの temporary project と別々の child path を共有し、standalone system-temp file を2回確保・cleanup しない。
- bounded HTTP の memory-safety coverage は、巨大な declared length の allocation 回避と pooled buffer 全体の clear を1つの contract test で検証する。
- environment-variable scope restoration は present / missing original を1つの serialized test で検証し、process-variable setup と runner case の重複を避ける。
- pure process-launch builder contract は base flag、invariant argument、worker payload、UTF-8 redirect を1つの test で検証し、allocation-only assertion のために4つの runner case を使わない。
- SQLite sensitive fixture の initialize、dispose、boundary clear は1つの replaced callback scope を共有する。boundary assertion は global hook state を再構築せず lifecycle assertion に続けて検証できる。
- SQLite connection policy builder coverage は connection string、command timeout、status diagnostics を1つの test で検証する。allocation-only check に3つの runner case は不要である。
- SQLite command parameter builder は primitive type、stable date、copied parameter shape を1つの command fixture で検証し、3つの独立 test case を割り当てない。
- timeout origin coverage は timer cancellation / caller cancellation を1つの async test で順に検証し、区別の assertion のために runner setup を重複させない。
- bounded in-memory HTTP read は unknown-length success / declared-length rejection を1つの async contract で検証し、隣接する buffer-policy assertion を分割しない。
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
- standalone な codec / metadata DB test が cleanup 前に `DbContext` を dispose する場合、process 全体の SQLite pool reset ではなく `DeleteFile(path)` に委譲してください。Windows retry fallback を保ったまま pure codec class を parallel 実行できます。
- command 境界から standalone な corrupt DB を開くだけの error-taxonomy test も同じ規則に従います。console capture は `TestConsoleLock` で保護しますが、test class 全体を直列化する必要はありません。
- temp DB、lock、metadata、cache、script、HTTP download、audit / metrics log と file-to-directory transition、filesystem case probe、MCP diagnostics、outside fixture file の cleanup では、手書きの `File.Exists(...)` / `File.Delete(...)` pair ではなく `DeleteFile(path)` を優先してください。
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
テストメソッドを追加する前に、最も近い既存メソッドへ同居させて setup と実行を再利用できないか確認してください。同じ一貫した契約を検証する read-only variant は統合し、mutable state の分離、個別の discovery / skip identity、大きく異なる setup、または failure diagnosis の明瞭さが必要な場合は別メソッドを維持します。

- 複数言語で共有する抽出上限のテストは、切り詰めを発生させる最小値だけ境界を超え、言語ごとに任意の余裕値を足さないでください。

### CLI / コンソール系テスト

- stdout と stderr を明示的にキャプチャする。
- 単純な stdout/stderr capture では `ConsoleCapture` を優先し、直接コンソールを差し替える場合は `TestConsoleLock.Gate` で直列化する。
- 終了コードは `CommandExitCodes` で検証する。
- JSON 出力は生文字列比較ではなく `JsonDocument` で解析して検証する。
- 拒否される checkpoint 名では usage の終了コード / error code と構文 hint を併せて検証し、checkpoint directory が作成されていないことも確認する。

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
