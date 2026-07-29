# Developer Guide

> **[日本語版はこちら / Japanese version](#開発者ガイド)**

## Build & Test

Core commands:

| Command | Use |
|---|---|
| `dotnet build` | Build the solution with the default local configuration. |
| `dotnet test` | Run the default test set. |
| `dotnet format CodeIndex.sln --verify-no-changes` | Verify repository formatting before a PR. |
| `dotnet test tests/CodeIndex.Tests/CodeIndex.Tests.csproj --settings tests/CodeIndex.Tests/CodeIndex.Tests.runsettings --blame-crash --blame-hang --blame-hang-timeout 5m` | Run the main test project with the repository runsettings and VSTest crash/hang blame collection. |
| `dotnet run --project src/CodeIndex -- <command> [options]` | Exercise the CLI from source. |

Top-level task wrappers:

| Wrapper | Use |
|---|---|
| `make build` | Build through the repository wrapper. |
| `make test` | Run tests through the repository wrapper. |
| `make lint` | Run formatting/lint validation. |
| `make coverage` | Run coverage workflow. |
| `make mcp-smoke` | Run the MCP smoke workflow. |

Use `FRAMEWORK=net9.0 make test` to match the net9 CI lane. On systems without
`make`, run the same tasks as `./dev.sh build`, `./dev.sh test`, and so on.

Development contracts:

| Area | Contract |
|---|---|
| Formatting and warnings | CI enforces repository formatting with `.editorconfig` and treats compiler warnings as errors through `Directory.Build.props`, so local changes should pass the format check before opening a PR. Existing trim-analysis warnings are explicitly listed in `WarningsNotAsErrors` until they are fixed without blocking ordinary compiler-warning enforcement, and ILLink keeps reporting trim warnings without failing trimmed publish smoke tests. |
| CLI help | `cdidx --help` stays brief, `cdidx --help-all` prints the full command/flag/example reference, `cdidx --help-flags` prints only shared flag tables, and `cdidx <command> --help` prints one command's usage line. Keep new commands visible in the brief summary only when they are a primary user workflow; every command must remain present in the full help and command-specific usage table. |
| `cdidx validate` | This is the user-facing integrity scan for indexed content issues such as replacement characters, BOMs, NUL bytes, mixed line endings, UTF-16 BOMs, and likely non-UTF8 content. Keep its CLI usage, README entry, and help summary in sync when adding validation issue kinds or filters. |
| `cdidx doctor` | This is the copy-pasteable environment summary for support requests. Keep it redacted by default: secret-like `CDIDX_*` values must not be printed, and new diagnostic fields should be stable enough for issue triage. Full environment inventory filters (`--env-domain`, `--env-category`, and `--env-sensitivity`) use case-insensitive exact values and compose with AND; filtered JSON summaries describe the returned inventory rather than the global catalog. `--max-json-bytes` is valid only with `--json --env-inventory=full`, counts the serialized UTF-8 document plus its newline, and returns a structured usage error rather than an oversized successful document. The `github` block reports `proxy_default_credentials` as `enabled` / `disabled` and the bounded `max_request_timeout_s`; never print proxy credential material or raw secret values. `license --json` returns the versioned `license`, `commercial_use`, `trademark`, and controlling `documents` contract. |
| Exception diagnostics | User-facing CLI, JSON, MCP, file-issue, and local diagnostic output must not echo raw `ex.Message` directly. Route exception prose through `CommandErrorWriter.FormatSanitizedExceptionMessage`, `DiagnosticSanitizer.ForMessage`, or an existing bounded `DiagnosticRedactor` helper, and use stable error codes/categories when the message is not needed for recovery. Intentional broad catches should match the `risky-code/broad-exception-catch` taxonomy and normalize to bounded diagnostics, private best-effort suppression, or a documented fallback. |
| Shell completions | Generated shell completion scripts include a comment with the `cdidx` version that produced them. When command or flag schema changes, update completion tests and keep the README guidance that installed completions should be regenerated after upgrades. |
| Target frameworks | The production CLI and NuGet tool packaging target `net8.0`. The test project multi-targets `net8.0;net9.0`, and CI runs the test suite on both frameworks across Linux, Windows, and macOS. Use a .NET SDK that can restore and run both target frameworks when validating the full CI-equivalent test matrix. |
| SDK selection | `global.json` pins the repository SDK to `9.0.301` with `rollForward` disabled. CI installs both `8.0.413` and `9.0.301` explicitly: `8.0.413` provides the `net8.0` runtime lane, while `9.0.301` is the selected SDK for restore, build, test, publish, and changelog validation. When rolling SDKs, update `global.json`, every `actions/setup-dotnet` version list, the Docker build image, and this guide together. |
| GitHub Actions policy | Workflows pin hosted runners to versioned labels (`ubuntu-24.04`, `windows-2022`, `macos-14`), keep the top-level `contents` permission read-only by default, limit `continue-on-error` to failure-path diagnostic artifact upload, give every upload artifact explicit retention, bound every artifact download by pattern and path, and scope cache keys to workflow + runner OS + `packages.lock.json` / `global.json` without broad restore-key fallbacks. `CiWorkflowTests.GitHubActionsWorkflows_FollowRunnerArtifactCacheAndContinueOnErrorPolicy` enforces this checklist. |
| Test diagnostics | CI uses `tests/CodeIndex.Tests/CodeIndex.Tests.runsettings` plus VSTest blame crash/hang collection and a bounded one-time retry to distinguish repeatable failures from pass-on-retry flakes. The Build and Test workflow splits `ubuntu-24.04` / `net8.0` into complementary coverage shards; Windows and macOS use the same complementary net8 split without coverage overhead, while the Ubuntu net9 compatibility lane runs the full suite. For test suite structure, shared helpers, state-isolation rules, timeout diagnostics, and test-writing conventions, see [TESTING_GUIDE.md](TESTING_GUIDE.md). |
| Mutation testing | The weekly `Mutation testing` workflow runs Stryker.NET against `src/CodeIndex/Database/DbWriter.cs` using `stryker-config.json`. Keep this scope focused on transaction, savepoint, rollback, and batch-write behavior unless the runtime budget is intentionally expanded. The workflow caches the pinned `dotnet-stryker` 4.14.0 tool and NuGet packages, updates the tool only on cache misses, and keeps mutation score gates at high 75, low 70, and break 65 so changes that weaken rollback or savepoint coverage fail outside the regular PR test path. |

## CI / Artifact Distribution

| Workflow | Command or setting | Notes |
|---|---|---|
| Read-only database queries | `cdidx status --db /artifacts/codeindex.db --read-only --json`; `cdidx search AuthService --db /artifacts/codeindex.db --immutable` | Query commands accept `--read-only` (alias `--immutable`) to open an existing CodeIndex database through SQLite's immutable read-only URI mode. Use this for CI artifacts, mounted caches, and sandboxes where creating or updating `codeindex.db-wal` / `codeindex.db-shm` sidecars is not allowed. |
| Mutating commands | `index`, `backfill-fold`, `optimize`, `vacuum` | These require writable storage and reject read-only database opens. |
| Reusable index artifact | `cdidx export codeindex.cdidx.zip`; `cdidx import codeindex.cdidx.zip --db <path>`; `cdidx import codeindex.cdidx.zip --dry-run --json` | Run export after indexing and upload the archive. Export refuses an existing destination unless `--overwrite` is explicit, publishes atomically from an owner-only temporary file, and verifies POSIX mode `0600`. Successful export JSON adds final archive byte size and SHA-256 plus the complete immutable manifest while retaining the prior result fields. Consumers import before query commands, or use `--dry-run` / `--check` to validate the archive without replacing the destination DB. Use `--prune-paths` when the archive comes from another checkout and the restored DB should advertise the import target project root; imports targeting `.../.cdidx/codeindex.db` use the sibling project directory, while other DB paths fall back to the process current directory. The archive contains only `manifest.json` plus `codeindex.db`; import validates ZIP entry names through `ZipArchiveSafetyPolicy` and rejects absolute, parent-directory, backslash, NUL, non-canonical, duplicate, and extra entries before extraction. The manifest carries bounded summary/readiness metadata including row counts, readiness bits, writer/indexed-head metadata, schema contract stamps, and unknown-extension summary when available. Import validates manifest format, manifest `user_version`, `database_sha256`, present summary counts, and the embedded SQLite file as a CodeIndex database before replacing the destination DB. Import rejects archive `codeindex.db` entries whose compressed or uncompressed metadata exceeds 8 GiB, and the extraction stream is also capped at 8 GiB. |
| Maintenance checkpoint and managed rollback | `cdidx db checkpoint <name> [--dry-run]`; `cdidx db checkpoints --list|--delete <name>|--prune --keep <n> [--dry-run]`; `cdidx db restore <name> [--dry-run] [--no-backup]`; `cdidx db restore-backups --list|--prune --keep <n>|--restore <id> [--dry-run] [--no-backup]` | Checkpoint snapshots `codeindex.db` plus existing WAL/SHM sidecars before risky maintenance. Import and both restore forms create a consistent, verified managed SQLite rollback snapshot before replacing an existing DB unless `--no-backup` is explicit. Managed directories use `<db>.restore-backup-<id>/` and contain a bounded manifest plus one standalone database payload; the manifest records SHA-256, byte count, supported `user_version`, provenance, and an optional source identifier, but no local absolute source path. `restore-backups --list` exposes the ID and provenance while retaining legacy directory metadata; existing prune retention remains compatible. `restore-backups --restore <id>` revalidates the directory boundary, manifest, payload hash, schema, and combined staging/rollback free space, then performs an atomic replacement with transient rollback-on-failure. Its `--dry-run` reports every validation and planned backup without mutation. Checkpoint delete/prune and restore-backup prune require an explicit mutation action, and checkpoint prune skips all deletion if its bounded 1,000-directory scan is truncated. Checkpoints live under `<db>.checkpoints/<name>/`. `backfill-fold` creates an automatic checkpoint before row mutation unless `--no-checkpoint` is passed. |
| Binary compatibility | [COMPATIBILITY.md](COMPATIBILITY.md) | Database compatibility across `cdidx` binary upgrades and downgrades is documented there. Keep that policy updated whenever readiness bits, `codeindex_meta` contract stamps, or rebuild requirements change. |
| Fold backfill preview and recovery | `backfill-fold --dry-run`; MCP `backfill_fold` with `dry_run: true` or `force: true` | Dry-run previews folded-key rows without mutating the DB or stamping FoldReady. MCP accepts the same preview and can force rewriting all folded keys when an operator needs to recover from suspicious fold metadata or row state even though the stored version/fingerprint appears current. Non-dry-run row rewrites are resumable after interruption: completed row updates remain durable, and final FoldReady metadata is stamped only after verification succeeds. MCP responses include `progress.rows_done`, `progress.rows_total`, and `progress.fraction` so clients can report and retry long backfills. |

## Filesystem Permissions

| Artifact | POSIX permission and behavior |
|---|---|
| `.cdidx/` | Created with mode `0700`. |
| `codeindex.db` plus WAL/SHM sidecars | Mode `0600` is applied when the files exist. Enforcement defaults to best-effort and can be made strict with `CDIDX_DB_PERMISSION_POLICY=strict`. |
| `suggestions-*.json` suggestion stores | Written atomically with owner-only mode `0600` on POSIX. |
| Portable export archives | Written atomically through the `Sensitive` profile and verified as owner-only mode `0600` on POSIX because they contain indexed source text. Existing destinations require explicit `--overwrite`. |
| Indexed workspace source reads | Source-file content and checksum reads use `FileShare.ReadWrite | FileShare.Delete`, long-path normalization, the configured max-file byte cap, and modified-time retry checks so indexing can inspect files that build tools keep open without allowing unbounded growth. |
| Atomic file writes | `AtomicFileWriter` writes to a sibling temp file, applies the requested POSIX mode before replacement, flushes file contents, renames over the target, and fsyncs the parent directory on Unix. Callers must use the `Sensitive` write profile for local state, caches, suggestions, checkpoints, portable archives, and other private payloads; user-requested reports use the default `Public` profile unless their content is explicitly private. If the parent directory flush fails after replacement, the command fails explicitly so callers know the file was replaced but directory durability was not confirmed. Windows skips directory fsync because the helper only promises it on supported Unix platforms. |
| Index locks, watch sub-run spools, staged hook scripts, lock metadata sidecars, and active workspace `active.json` | Created or written as owner-only files (`0600`) before contents are exposed, and read through small bounded buffers where applicable so stale or corrupted diagnostics cannot expose local paths more broadly or force unbounded allocation. |
| Checkpoint roots, snapshot directories, manifest files, copied DB/WAL/SHM snapshots, and restore staging/backup directories | Forced owner-only on POSIX. |
| `status --json` | Reports `data_dir_mode`, `db_file_mode`, the effective `database_permission_policy`, and support-safe `database_permission_diagnostics` when Unix mode operations fail. |

`CDIDX_DB_PERMISSION_POLICY` accepts `best_effort` (the default) or `strict`. In
best-effort mode, `IOException`, `UnauthorizedAccessException`, and
`NotSupportedException` from database/WAL/SHM mode changes or database mode reads do
not make an otherwise usable SQLite database inaccessible. cdidx emits a stable
`database_permission_hardening_failed` warning and records the operation, logical
target, stable reason, message, and remediation without exposing the database path.
Strict mode instead fails with a structured `CodeIndexException` carrying the same
error code and a remediation hint. Windows and explicit SQLite file URIs skip this
POSIX-only enforcement.

### Destructive Filesystem Operation Audit

Production `File.Delete`, `Directory.Delete`, and `File.Move` call sites are allowed only for owned CodeIndex state or caller-approved outputs. Re-run the audit with the local binary when changing these areas:

```bash
dotnet ./src/CodeIndex/bin/Debug/net8.0/cdidx.dll search File.Delete --path src/ --exclude-tests --exact-substring --count-by file --limit 80
dotnet ./src/CodeIndex/bin/Debug/net8.0/cdidx.dll search Directory.Delete --path src/ --exclude-tests --exact-substring --count-by file --limit 80
dotnet ./src/CodeIndex/bin/Debug/net8.0/cdidx.dll search File.Move --path src/ --exclude-tests --exact-substring --count-by file --limit 80
dotnet ./src/CodeIndex/bin/Debug/net8.0/cdidx.dll search --recipe filesystem-mutation-boundaries --format count --limit 80
```

| Surface | Ownership and boundary policy | Cleanup or rollback policy |
|---|---|---|
| `AtomicFileWriter` file delete/move helpers | Used for caller-selected output paths after the caller has accepted or validated the destination. Temp files are generated as collision-resistant siblings of the target, so replacements stay on the same filesystem boundary and do not follow a separate temp-root policy. | Writes flush the temp file, rename over the target, and flush the parent directory on Unix. Pre-move temp cleanup is best-effort; post-replace parent-directory flush failures are explicit command failures because the target has already changed. |
| `cdidx import` / `cdidx export` temp databases and sidecars | Import temp DBs are either hidden siblings in the destination DB directory or owner-only `codeindex-import-*` temp directories for dry-run. Export snapshots live in owner-only `codeindex-export-*` temp directories. Destination DB replacement rejects overlapping export outputs and rolls back through backup sidecars. | Temp DB, WAL, SHM, and empty temp-directory cleanup failures are warnings that do not hide the import/export error. Replacement failure reports residual diagnostics so operators can inspect the destination state. |
| `cdidx db` checkpoint restore staging and restore-backup pruning | Checkpoints, restore staging, and restore backups are derived from the resolved DB path. Recursive cleanup uses `FileSystemBoundary.TryValidateDirectoryCleanupTarget` with the DB parent as safe root and an expected `codeindex.db.restore-*` style prefix. Checkpoint payload files must be regular files, not symlinks, reparse points, or devices. | Restore creates backups before replacement and attempts rollback on failure. Temporary-directory cleanup and restore-backup prune failures become bounded diagnostics or warnings without deleting outside the validated root. |
| Upgrade installer script and temp directory | Upgrade downloads use owner-only `cdidx-install-*` directories under `Path.GetTempPath()`. Recursive directory cleanup validates the temp root, required prefix, and symlink/reparse/device status before deletion. Install-directory write probes run only after install-directory validation rejects roots, symlinks/reparse points, and unsafe POSIX modes. | Installer script and temp-directory cleanup failures are warnings. The install operation reports its own result separately from secondary cleanup failures. |
| `.cdidx` write probes and case-sensitivity probes | Write probes are freshly generated files under the already resolved install directory, `.cdidx` directory, or `.cdidx/probes` directory. Probe directories are created owner-only and are under the workspace data directory. | Probe files are deleted after the check. Case-sensitivity probe-directory cleanup revalidates the workspace data/probe root, expected directory name, and symlink/reparse/device status before deleting created empty probe directories; rejected cleanup records bounded diagnostics and suggests removing stale `.cdidx/probes` entries when no `cdidx` process is running. |
| Legacy scan checkpoints | Full scans neither create nor consume `.cdidx/scan-checkpoint.json`. After the first immutable scan-input barrier, successful and partial runs delete any legacy file without parsing it. | Delete failures are warnings in human output and `CliJsonMessage` entries in JSON output; indexing continues without relying on stale HEAD-only state. |
| Git hook staging | Hook installation first classifies the operation as create, managed replacement, custom-hook chain, or exact no-op. `hooks install --dry-run` returns that plan and the managed script before directory creation or staging, while a real mutation writes a private staged hook script inside the repository hook directory and then replaces the hook file through `File.Replace` with a backup path when needed. | Exact executable UTF-8/no-BOM managed-hook reruns return `already_installed` without rewriting. Other encodings and non-executable managed hooks are replaced so Git can execute them. If a staged script was not moved into place, cleanup is best-effort and recorded as hook warnings. Failure to delete a managed hook is a command error because that is the requested mutation. |
| Index and MCP lock metadata sidecars | Lock files and `.info` sidecars live next to the resolved DB or MCP index lock path and are created with owner-only permissions. | Disposing a lock deletes only the metadata sidecar. Cleanup failures are logged through `GlobalToolLog` and optional test sinks; stale lock files rely on OS lock release rather than recursive cleanup. |
| Search audit recipes | `SearchAuditRecipes` contains literal recipe strings such as `Directory.Delete` and `File.Move`; these are search metadata, not filesystem mutations. | No cleanup policy applies. |

Best-effort cleanup catches must stay narrow to secondary cleanup for owned temp, probe, lock, or metadata artifacts. They should emit a bounded warning or diagnostic when an operator can act on the residue, and they must not suppress the primary operation result. The exception is durability confirmation after an atomic replacement: if the target has already been replaced and the parent directory cannot be flushed on Unix, the command fails explicitly so callers know the filesystem state changed but durability was not confirmed.

## Release Distribution Checklist

When preparing a release, verify every supported distribution channel documented
in [DISTRIBUTION.md](DISTRIBUTION.md):

| Channel or area | Verify |
|---|---|
| `install.sh` | Latest install, explicit-version install, `--doctor`, and local mirror self-test. |
| NuGet global tool | Install/update on a clean .NET 8 tool environment. |
| NuGet trusted publishing | GitHub Actions variable `NUGET_TRUSTED_PUBLISHING_USER` is set to the NuGet.org username that created the trusted publishing policy; this can differ from the package owner. |
| Release assets | Published asset exists for every advertised RID. |
| Release workflow privilege split | `.github/workflows/release.yml` keeps `prepare-release-files` and `verify-release-install` on `contents: read`, hands only the short-lived `release-payload` artifact to `create-release`, removes temporary GPG material before publishing continues, and scopes Windows signing secrets to the signing step. |
| GHCR container image | Published `linux/amd64` and `linux/arm64` images run `cdidx --version`, omit runtime `git`, and expose provenance/SBOM attestations. |
| Package metadata | License, repository URL, tags, and runtime prerequisites are correct. |
| Documentation links | README, USER_GUIDE, and package metadata links resolve to the intended docs. |

### NuGet lock files

| Contract area | Requirement |
|---|---|
| Lock-file participation | `Directory.Build.props` sets `RestorePackagesWithLockFile=true`, so every project under this solution writes a `packages.lock.json` next to its `.csproj`. The lock file pins exact resolved versions and `contentHash` for every direct **and transitive** package, including the native-bearing `SQLitePCLRaw.bundle_e_sqlite3` that ships under `Microsoft.Data.Sqlite`. This keeps builds reproducible across machines, CI lanes, and release artifacts, and turns a silent transitive bump or downgrade attack into a loud, build-breaking diff. |
| Package source boundary | The repository root `nuget.config` clears machine-wide package sources, allows only `https://api.nuget.org/v3/index.json`, maps every package ID to that source, and requires signed packages. Trusted signers are limited to the NuGet.org repository-signing certificates and the author-signing certificates needed by the currently locked package graph, so restore rejects unsigned packages, packages from unconfigured feeds, and packages signed by unknown authors. When NuGet.org or an approved package author rotates a signing certificate, update `nuget.config` in the same change as the restore validation. |
| CI and Docker locked restore | CI (`.github/workflows/dotnet.yml`, `release.yml`, `codeql.yml`) uses `--locked-mode` on the narrowest dependency graph each lane needs: primary build and CodeQL lanes restore the solution, compatibility build lanes restore the test project for their matrix framework, native release lanes restore the test project for `net8.0`, and cross-compile release lanes restore the production project. The test-project restores include the production dependency graph through `ProjectReference`, so any drift between the committed lock files and the selected resolution graph fails the build instead of slipping into artifacts. The Docker build restores `src/CodeIndex/CodeIndex.csproj` with `--locked-mode` and publishes with `--no-restore`; keep the committed project lock file populated for the Docker musl RIDs before changing those container restore/publish flags. NuGet package caches in the build, release, and mutation workflows restore only by workflow-scoped exact lockfile-derived keys and do not fall back to broad OS-level cache prefixes. Local development restores normally; the lock file is enforced in CI and Docker builds. |
| Deterministic package metadata | The `CodeIndex` package project opts into deterministic builds and publishes repository metadata for Source Link. On GitHub Actions it also sets `ContinuousIntegrationBuild=true` and embeds untracked source inputs so PDBs and `.snupkg` artifacts can map back to the repository without local machine paths. Build metadata uses the Git commit date when available instead of the wall-clock build date so repeated builds of the same commit do not drift by timestamp. `Microsoft.SourceLink.GitHub` is a build-only dependency (`PrivateAssets=All`), not a runtime dependency. |
| Vulnerability checks | The normal build/test workflow runs `dotnet list src/CodeIndex/CodeIndex.csproj package --vulnerable --include-transitive` after locked restore and fails on any High or Critical NuGet advisory in direct or transitive runtime packages. If an advisory lands in the native SQLite graph and `Microsoft.Data.Sqlite` still permits a vulnerable minimum, add a direct `SQLitePCLRaw.bundle_e_sqlite3` reference to the patched bundle version and refresh the committed lock files so CI, Docker, and release restores all resolve the same patched native payload. Dependabot is configured for weekly NuGet and GitHub Actions update PRs in `.github/dependabot.yml`, so security fixes and routine dependency/action bumps are proposed before they become release surprises. |
| Release publish/pack restore | The release `dotnet publish` per-RID and `dotnet pack` NuGet packaging steps intentionally do **not** set `RestoreLockedMode=true`. Those steps run runtime-specific restores that legitimately add lock entries that did not exist at the preceding locked-restore boundary, such as `net8.0/<rid>` runtime sections and `Microsoft.NET.ILLink.Tasks` for trimming. They still consume locked versions because `RestorePackagesWithLockFile=true` from `Directory.Build.props` forces every restore on the machine to resolve through the lock file. The `Microsoft.NET.ILLink.Tasks` direct reference must stay on the .NET 8 package line while the CLI targets `net8.0`, even though release and container builds run under the repository-pinned .NET 9 SDK; Dependabot ignores major updates for that package so 9.x/10.x ILLink tasks cannot reintroduce a runtime/tool mismatch. The supply-chain guarantee for `Microsoft.Data.Sqlite` and its `SQLitePCLRaw.*` graph is enforced by the lane-appropriate locked restore that runs first: native release validation restores the `net8.0` test project, whose `ProjectReference` includes the production graph, while cross-compile release validation restores the production project directly. |

Package normalization runs after `dotnet pack` and before validating, hashing,
or publishing NuGet artifacts:

```bash
dotnet run --project tools/CodeIndex.PackageNormalize -- nupkg/*.nupkg nupkg/*.snupkg
```

For release diagnostics, inspect without rewriting and request a bounded
summary across all candidate packages:

```bash
dotnet run --project tools/CodeIndex.PackageNormalize -- --dry-run --summary nupkg/*.nupkg nupkg/*.snupkg
dotnet run --project tools/CodeIndex.PackageNormalize -- --dry-run --json --continue-on-error nupkg/*.nupkg nupkg/*.snupkg
```

`install.sh` is generated from focused fragments under `install_modules/`.
The generated bundle carries an `@generated` provenance marker whose canonical
sources are those fragments. Keep the marker in generated output so default
definition and graph queries do not double-count the copied implementations;
use `--include-generated` only when the bundled copy itself must be audited.
After editing installer, doctor, self-test, reinstall, uninstall, or dispatch
logic, regenerate the checked-in one-file installer before testing:

```bash
bash tools/build-install-sh.sh
```

| Normalizer rule | Detail |
|---|---|
| Reproducible OPC metadata (#2756) | NuGet's OPC package writer generates a random `package/services/metadata/core-properties/*.psmdcp` part name on each pack run. The normalizer rewrites that part to `package/services/metadata/core-properties/core-properties.psmdcp`, updates the matching content-type and relationship references, and gives ZIP entries stable timestamps. This is the package reproducibility boundary for `.nupkg` and `.snupkg` archives. |
| Rewrite durability (#3961) | Package normalization writes through collision-resistant `.cdidx-normalize-*.tmp` files beside the package, flushes the completed temp file, replaces the package, and flushes the parent directory on Unix so post-replace durability failures are reported explicitly. Cancellation is checked between ZIP entries and stream chunks, and created temp files are cleaned up best-effort. |
| Work bounds (#2892) | Before rewriting, the normalizer rejects packages with more than 4096 ZIP entries, any entry above 128 MiB uncompressed, total uncompressed content above 512 MiB, or XML reference text above 16 MiB so crafted packages cannot force unbounded normalization work. |
| Unsafe ZIP names (#2894, #4174) | ZIP entry-name validation is shared through `ZipArchiveSafetyPolicy`. Before creating the destination archive, the normalizer rejects absolute paths, Windows drive roots, backslash separators, NUL characters, empty path segments, parent-directory segments, empty normalized names, and destination names that collide after path normalization. `cdidx import` uses the same policy and accepts only exact `manifest.json` / `codeindex.db` entries, rejecting duplicate and extra entries before reading payloads. |
| Unsafe ZIP attributes (#3552) | Before copying entries, the normalizer rejects POSIX symlink/device/special-file types and unsafe DOS attributes, then writes normalized entries with scrubbed deterministic external attributes instead of preserving source permission bits. |
| Failure diagnostics (#3458) | The CLI accepts at most 1024 package paths per run, reports bounded package path and ZIP entry diagnostics instead of raw path-heavy exception text, and emits cleanup deletion failures as per-package `warnings` in JSON output. |
| Temp-file replacement policy (#3996) | Before rewriting, the normalizer deletes a stale legacy `.normalize-tmp` sidecar only after it can open that file exclusively, and aborts when the file is locked or inaccessible to avoid racing another normalizer. Replacement archives still use collision-resistant same-directory `.cdidx-normalize-*.tmp` sidecars that are flushed, moved over the package, and followed by a parent-directory flush on Unix. |

When you intentionally update a dependency (or add a new direct `PackageReference`), regenerate the lock files locally and commit the diff in the same change:

```bash
dotnet restore CodeIndex.sln --force-evaluate
git status --short -- '**/packages.lock.json'
```

| Situation | Required response |
|---|---|
| CI fails with `NU1004 The packages lock file is inconsistent with the project dependencies` | Rerun `dotnet restore --force-evaluate` locally, review the lock-file diff, and commit it. Do **not** delete the lock files to "make CI pass" because that re-opens the supply-chain hole this contract closes. |
| Project has zero direct `PackageReference` entries, such as `tools/CodeIndex.Changelog/` | Keep the intentionally empty `net8.0: {}` dependency map. That is normal and proves the project is participating in the locked-mode contract. |

## Architecture

| Area | Key files | Responsibility |
|---|---|---|
| CLI entry | `Program.cs` | Thin command entry point and routing. |
| CLI runners | `Cli/IndexCommandRunner.cs`, `Cli/QueryCommandRunner.cs` | Indexing commands, search/definition/reference/caller/callee/symbol/file/map/inspect/outline/status commands, and argument parsing. |
| CLI support | `Cli/CliCommandCatalog.cs`, `Cli/CliFlagSchema.cs`, `Cli/ConsoleCompletionRenderer.cs`, `Cli/ConsoleUi.cs`, `Cli/CommandExitCodes.cs`, `Cli/SearchSnippetFormatter.cs`, `Cli/LineWidthFormatter.cs` | Shared command, subcommand, and flag metadata; generated command help and completions; user-facing output, exit codes, focused snippet formatting, and line-width clamping. |
| Workspace resolution | `Cli/DbPathResolver.cs`, `Cli/GitHelper.cs`, `Cli/IndexFreshnessChecker.cs`, `Cli/WorkspaceMetadataEnricher.cs`, `Cli/GlobalToolLog.cs` | DB path resolution, git-aware refresh inputs, DB/worktree freshness checks, workspace metadata, and persistent install logs. |
| SQLite storage | `Database/DbContext.cs`, `Database/DbConnectionFactory.cs`, `Database/DbPragmaPolicy.cs`, `Database/SqliteConnectionPolicy.cs`, `Database/SqliteCommandPolicy.cs`, `Database/SqliteIdentifier.cs`, `Database/DbWriter.cs`, `Database/DbReader.cs` | WAL-backed SQLite schema, shared connection-string/read-only URI/command-timeout policy, retry and pragma policy, typed SQLite parameter/scalar helpers, quoted identifier SQL helpers, batch writes, stale-file cleanup, FTS5 search, symbol/reference lookups, excerpts, outlines, inspect bundles, status, and dependency queries. |
| Repository map | `Database/RepoMapBuilder.cs` | Repo-level overview for `map`: file stats, likely entrypoints, hotspots, and module grouping. |
| File scanning | `Indexer/Scanning/FileIndexer.cs`, `Indexer/Scanning/FileIndexer.LanguageDetection.cs`, `Indexer/Scanning/ChunkSplitter.cs` | Shared full/update path filtering, ignore handling, language detection, file records, and 80-line chunks with 10-line overlap. Ambiguous `.h` detection reuses the bounded C/C++ lexical masker, streams lexical and preprocessor state through skipped bytes, and scores either the full header or head/middle/tail ranges within a 48 KiB UTF-8 byte budget. Its source/confidence metadata is retained on loaded records and exposed by `index --dry-run --json` under `language_detections`. |
| Symbol extraction | `Indexer/Symbols/SymbolExtractor.cs`, `Indexer/Symbols/SymbolExtractor.Lisp.cs`, `Indexer/Symbols/CSharpSymbolNameNormalizer.cs` | Hybrid symbol extraction across supported languages plus C# persisted-name canonicalization. |
| Reference orchestration | `Indexer/References/ReferenceExtractor.cs` | Dispatches regex/state-machine reference extraction across graph-supported languages. |
| Reference support | `Indexer/References/Support/*.cs` | Shared masking, type-position scanning, trailing-lambda handling, JVM method references, and SQL name resolution. |
| Language extractors | `Indexer/References/Languages/*ReferenceExtractor.cs` | Language-specific reference extraction for C#, Java, Python, SQL, Rust, Swift, Terraform, and other graph-supported languages. |
| MCP server | `Mcp/McpServer.cs` | JSON-RPC 2.0 server for AI coding tools, including batch queries. Transport is pluggable via `IMcpTransport`. |
| MCP transports | `Mcp/IMcpTransport.cs`, `Mcp/StdioMcpTransport.cs`, `Mcp/HttpMcpTransport.cs` | Stdio (default) and optional HTTP `POST /` transports for the MCP server (#1558). |
| DTOs | `Models/FileRecord.cs`, `Models/ChunkRecord.cs`, `Models/SymbolRecord.cs`, `Models/ReferenceRecord.cs` | Records shared by indexing, storage, query, and MCP layers. |
| Tests | `tests/CodeIndex.Tests/*Tests.cs`, `TestProjectHelper.cs`, `TestConsoleLock.cs` | Focused unit/integration coverage for chunking, extraction, DB reads/writes, CLI behavior, MCP behavior, git helpers, and shared test harness utilities. |

`CliCommandCatalog` owns command and nested-subcommand names, including whether
a nested verb is optional and parent flags must remain available, while
`CliFlagSchema` owns primary flags, aliases, placeholders, descriptions, command
applicability, canonical value domains and value aliases, and safety/scope
classification. Command usage placeholders, command and shared flag help,
output-format validation, search origin/result-kind validation, and every shell
completion renderer consume those shared definitions. Dedicated or nested
parsers retain exact usage-specific syntax until their subcommand metadata is
complete. Add or change CLI options and accepted values in the schema first;
do not add parallel help, validation, or completion value lists. This keeps
runtime validation, help, completion, and generated next-step flags aligned
without emitting partial option lists. Every verb
listed by `CliCommandCatalog.CommandSubcommands` must also resolve to a hidden,
verb-specific `ConsoleUi` usage entry with its constraints, side effects, and
an example; aggregate usage lines must enumerate accepted public flags.

Full scans no longer create or consume the legacy `.cdidx/scan-checkpoint.json` resume state because a HEAD-bound directory list cannot prove in-place, untracked, or configuration freshness. After the first immutable scan-input barrier, both successful and partial runs delete any legacy file; a delete failure is a bounded warning and never changes the indexing result.

Large command and extractor files have a tracked decomposition plan in
[docs/large-file-decomposition-plan.md](docs/large-file-decomposition-plan.md).
Use that plan when splitting `QueryCommandRunner`, `SymbolExtractor`,
`LanguageReferenceExtractionSupport`, `McpToolHandlers`, or `FileIndexer`
ownership boundaries so behavior changes remain reviewable and testable.

### Workspaces

`cdidx.workspace.json` and `.cdidx-workspace.json` declare monorepo members without adding a YAML dependency. Workspace manifests are capped at 64 KiB, 16 JSON nesting levels, 1024 members, 4096 characters per member path, and 255 characters for `default_db_name`. The supported schema is additive: `members` is an array of member paths that must be relative to and resolve under the manifest directory, `index_strategy` is `per_member` or `single` with unknown values rejected, `default_db_name` is a plain file name that overrides `codeindex.db`, and `shared_ignores` is reserved for shared ignore policy. Invalid `members` entries are rejected with bounded diagnostics, and valid entries are normalized and deduplicated with the workspace path casing policy before DB paths are materialized. `cdidx workspace list` and `cdidx workspace status` report member DB paths. `workspace status` also reports each member's database existence, probe status and reason, schema compatibility, exact workspace freshness, timestamps, index completeness, and graph readiness. It probes at most 64 distinct existing member databases per invocation, reuses a probe when members share a database under the `single` strategy, and marks later members as `not_checked` with a top-level truncation summary. In JSON mode, invalid manifest schema or safety failures are returned as a structured `workspace_manifest_invalid` error instead of falling through to the top-level crash handler.

`cdidx workspace use <name-or-relative-path>` writes an existing manifest member or `default` workspace to the per-user config directory and rejects missing manifest members. A directory name remains a shorthand when it identifies exactly one member; repeated directory names remain ambiguous. A manifest-relative path selects the exact normalized member, accepts either slash spelling, and stores the canonical forward-slash relative path in active workspace state. Manifest-member selections also persist `manifest_member: true`, so members named `default` or `env` remain distinguishable from the reserved non-manifest states. Active workspace names share the manifest member path's 4096-character bound. `cdidx workspace clear` (also available as `workspace deactivate`) removes that persisted selection instead of rebinding `default` to the current directory. When `CDIDX_ACTIVE_WORKSPACE` is set, clear reports that the environment override must be unset because it takes precedence over persisted state. Query DB resolution keeps existing precedence: explicit `--db`, then explicit `--data-dir` / `CDIDX_DATA_DIR`, then active workspace state, then ancestor/CWD discovery.

### Observability

CodeIndex exposes an opt-in `ActivitySource` named `CodeIndex`. MCP JSON-RPC frames create `mcp.request` server spans and SQLite commands routed through tracked database helpers create `db.query` spans. MCP callers can pass W3C trace context as `params._meta.traceparent`; when present, the MCP span uses that trace as its parent. No exporter dependency is bundled, so spans are emitted only when the host process installs an OpenTelemetry/Diagnostics listener.

Set `CDIDX_SLOW_QUERY_MS=<milliseconds>` to write slow SQLite command diagnostics to stderr. Query commands also accept `--profile` for a JSON profile block and `--slow-query-ms <milliseconds>` for command-scoped profiling. Slow-query SQL diagnostics are single-line, length-bounded, and redact SQL string/blob/numeric literals before they reach stderr or the global tool log; the logged SQL is intended for operation/shape debugging, not value recovery.

### Resource-Boundary Contracts

| Path | Contract |
|---|---|
| Worker protocol JSON | Isolated worker stdin frames are read through `BoundedLineReader`. The symbol-worker client serializes requests directly to UTF-8 and writes newline-framed bytes to the process stream; the worker serializes responses directly to its stdout stream, and the client reads each bounded response frame as UTF-8 bytes for direct deserialization. This avoids an additional UTF-16 JSON string and encoding pass in each direction for every source file. The default frame cap is 32 MiB for both characters and UTF-8 bytes. When a larger `--max-file-bytes` setting needs JSON-escaping headroom, the protocol frame cap may expand up to `WorkerProtocolLineLimits.MaxExtendedLineUtf8Bytes` (384 MiB), never to `int.MaxValue`. `WorkerProtocolJsonValidator` rejects payloads over the negotiated character/UTF-8 byte cap before `JsonDocument.Parse`, parses with `DefaultMaxJsonDepth` (32), rejects more than 1,000,000 object properties, and rejects strings longer than the frame cap. |
| User regex find | `find --regex` keeps the classic .NET regex engine for lookaround/backreference compatibility, adds `RegexOptions.CultureInvariant`, adds `IgnoreCase` unless `--exact` is set, and uses `BoundedRegex.DefaultMatchTimeout` per match. Timeouts surface as `E014_REGEX_MATCH_TIMEOUT` / `regex_timeout` in CLI JSON, and human output includes the same recovery hint. `find --all` additionally applies candidate-file and line-scan caps before walking the whole index. |
| Shared regex construction | Production regex construction is centralized through `BoundedRegex`, `RegexRegistry`, or `RegexTimeoutPolicy`. Use `BoundedRegex` for extractor patterns and bounded static regex APIs, `RegexRegistry` for raw BCL regex factories that must preserve timeout exceptions (`find --regex`, ignore glob regexes, generated-code path patterns), and `RegexTimeoutPolicy` for diagnostic/redaction surfaces. `RegexRegistry` owns the named ignore-glob timeout (100 ms), generated-code pattern timeout (50 ms), and find-regex factory using `BoundedRegex.DefaultMatchTimeout`. Search-audit recipes treat only `BoundedRegex` aliases and `RegexRegistry.cs` as centralized positive evidence, so new production raw constructors require a deliberate factory or generated-regex entry plus tests. |
| Filesystem traversal helpers | `FileSystemTraversalPolicy` keeps top-directory-only enumeration explicit (`IgnoreInaccessible=false`, no implicit recursion) and exposes opt-in `CancellationToken` / entry-budget options. Expected traversal failures are classified centrally so command diagnostics share the same permission, I/O, invalid-path, unsupported-path, path-too-long, and budget-exceeded taxonomy. |
| `MaxValue` sentinels | `int.MaxValue` may be used only as an internal sentinel when the next operation clamps before SQL limits, allocation, traversal, payload sizing, or timeout conversion. User-influenced values must be reduced to named practical constants before multiplication, buffer sizing, protocol framing, or query expansion. |

### Indexing pipeline

```
Directory scan / shared path filter (built-in skip lists + `.gitignore` / `.cdidxignore` + directory symlink policy + reparse/Windows Hidden/System attribute pruning)
  → Parallel extraction workers (`--parallelism`, `CDIDX_INDEX_PARALLELISM`; default CPU count capped at 8, explicit maximum 16) read UTF-8, split chunks, extract symbols/references, and validate content
  → Single SQLite writer checks unchanged-file reuse, UPSERTs file records, runs post-extraction hooks, and inserts chunks + symbols + references + issues in per-file transactions
  → Populate FTS5 index
```

The single writer reuses multi-row chunk, symbol, reference, and reference-line
commands by row-count shape through the bounded `PreparedCommandCache`. SQL text
and typed parameter schemas are created only on a cache miss; each execution
reassigns parameter values by ordinal. Keep new bulk-write paths on this bounded
cache so large indexes do not rebuild equivalent SQLite commands per file.

Repository-wide incremental scans load stat-reuse candidates with one SQLite
statement before the C# contract prepass and parallel extraction. Each candidate
is still compared with a fresh filesystem size and UTC modification time, and
language extractor versions, extraction caps, stale issue metadata, and generated
code suppression must all remain part of the snapshot eligibility contract. Do
not replace this snapshot with per-file database probes in either CLI or MCP.
Rows with missing or invalid legacy stat values are excluded so normal checksum
reuse or reindexing can repair them, and CLI/MCP cancellation must interrupt the
snapshot query as well as the later extraction pipeline.

`FileIssue` rows may include nullable `origin` and `severity` metadata.
For `replacement_char`, `origin: source_literal` means the file contains a
valid encoded U+FFFD literal, while `origin: decode_replacement` means the
decoder inserted U+FFFD for invalid bytes. `severity: info` is used for source
literals, and `severity: warning` is used for likely encoding damage.

Scoped `--files` / `--commits` refreshes reuse the same path filter as full scans. Before scanning a nested project root, `FileIndexer` loads ignore files from the resolved ignore-rule root through each existing ancestor directory down to the project root's parent, then loads the project directory's own rules during the normal walk. Within each directory, `FileIndexer` loads `.gitignore` before `.cdidxignore`, appends both rule sets in that order, and honors later `!` patterns as re-includes. If an ancestor ignore directory cannot be read, scanning fails closed with a scan error instead of silently skipping those rules; `ScanFilesResult.AncestorIgnoreDirectories` records the resolved ancestor list for troubleshooting. If a commit-scoped refresh includes `.gitignore` or `.cdidxignore` changes, `IndexCommandRunner` falls back to a full scan so newly ignored files are purged safely. Malformed ignore lines are reported as scan errors and skipped instead of aborting the whole run. Symlinks default to `--follow-symlinks none`; `internal` follows file and directory targets that resolve under the workspace root, and `all` follows all resolvable targets. Discovery, dry-run, C# workspace preflight, and content loading use the same resolved file target identity, so a stable allowed external file target is indexed while a link retargeted after preflight is rejected as source drift. Dangling symlinks are counted and warned separately; index dry-run reports them through `warnings_total` / `warnings`, matching execution severity and successful exit behavior. Permission failures resolving directory targets are also reported as scan warnings. On Windows, files and directories with Hidden or System attributes are rejected before language detection; clear those attributes before indexing project-owned sources because ignore rules cannot re-include them.

Incremental refreshes that mutate `fts_chunks` increment both `codeindex_meta.fts_incremental_writes_since_merge` and `codeindex_meta.fts_incremental_writes_since_optimize`. When the merge counter reaches 25 writes, index runners issue `INSERT INTO fts_chunks(fts_chunks, rank) VALUES('merge', -1000)`: 1,000 pages is a minimum work target, and SQLite's complete-segment granularity may process more pages. The merge resets only its dedicated counter, while the optimize counter continues to support the `cdidx optimize --dry-run` recommendation. Full CLI scans and MCP refreshes switch to trigger-free bulk rewrite, FTS rebuild, and full optimize when dirty source bytes are at least three-fifths of known workspace source bytes; fresh indexes and explicit rebuilds always use that path. Dirty bytes include the larger of the current and persisted sizes for each file that will be rewritten, plus persisted byte sizes for indexed rows planned for deletion, including the old side of a rename. The comparison total includes known readable current-workspace bytes, those planned-deletion bytes, and the positive persisted-minus-current excess for rewritten files that shrank, so both sides describe the same pre-update footprint. A scan error, invalid persisted size, or byte-count overflow makes the estimate incomplete and conservatively keeps trigger synchronization. Stale-file IDs are planned without mutation before selecting the FTS policy, then deleted inside the selected bulk guard. The plan keeps IDs ascending so the C# static-interface workspace prepass can skip purge-planned rows with a binary search and no duplicate deletion set; the reusable-stat snapshot instead loads the IDs into an indexed temporary SQL filter before running eligibility subqueries. This prevents a path that reappears between MCP planning and scanning from reusing a row that the same run will purge. The separate pre-purge contract-presence query runs only for a non-empty plan and still forces C# re-extraction when deletion may remove implicit implementation references. When such contracts exist, MCP also invalidates the C# symbol-name contract at its first mutation; if a scan error leaves an implementer unprocessed after the purge, the next clean run disables stat reuse, repairs its implicit references, and only then restamps the contract. The batched delete transaction checks cancellation throughout and rolls back every delete if cancellation arrives before commit; if cancellation arrives after a committed bulk purge, guard abandonment rebuilds FTS from the surviving chunks and restores its triggers before the run exits. Full scans and MCP refreshes filter reusable-row snapshots with a current-target path set only when non-purged indexed rows unused by the current target set outnumber the current targets; other runs use sorted-ID exclusion without duplicating every current path in a second large set. Scoped `--files` / `--commits` refreshes stay on trigger synchronization and incremental merge maintenance. `cdidx optimize --db <path>` and `cdidx index <projectPath> --optimize` still run an explicit full optimize, reset both counters, and stamp `fts_last_optimized_at`; this may briefly hold the writer lock on large indexes.

The C# pre-purge contract preflight probes sorted planned IDs in uncached batches of at most 500 SQLite parameters and applies managed keyword-boundary validation before forcing re-extraction. A prior symbol-kind policy that could have omitted a contract-member kind makes an interface declaration conservative evidence. For scoped updates, a false source-evidence marker is authoritative, so the hot path does not restore a repository-wide exact-member scan; transition-path probes instead start from exact indexed `files(path)` rows, join through `symbols(file_id, kind)`, and remain cancellation-aware and cache-neutral in batches of at most 500 paths. Plain interfaces and LIKE-only decoys therefore do not invalidate reusable C# rows. Persisted workspace materialization likewise starts from `files(lang)` and probes only member-capable `symbols(file_id, kind)` rows, validates exact signatures in managed code, then loads interface declarations only for retained contract container names through bounded, cache-neutral `symbols(name)` batches. A negative or LIKE-decoy-only read stops after the first phase and never materializes the repository's plain interfaces. A tri-state source-evidence marker observes built-in C# symbols before post-extraction hooks, kind filters, and row caps, so a hook-hidden contract still forces safe implicit-reference refreshes. Full CLI and MCP scans preserve either authoritative true or false source evidence without rereading source only when the prior index is explicitly complete, GraphReady was already stamped, symbols-only omission and filter/version/root/hotspot contracts remain compatible, no persisted C# path changed language, and every C# target is stat-reusable. This strict known-evidence no-op also skips persisted C# symbol loading and workspace-lookup construction; missing legacy completeness or readiness metadata deliberately falls back to a raw workspace prepass.

Full scans neither create nor consume HEAD-only directory checkpoints because they cannot prove that in-place, untracked, or configuration inputs remained stable between runs; after the first immutable scan-input barrier, successful and partial runs delete any legacy checkpoint file without parsing it. Each enumerated, non-ignored directory records one modification baseline before membership-configuration probes and recursive listing. Existing ignore, language-map, pattern, and submodule inputs are additionally bound to content, metadata, and file identity, while missing external inputs and nested-repository markers use explicit states. Consumers validate that single scan-input snapshot immediately before the first domain/index-state mutation after schema initialization and again immediately before readiness stamps, without retaining the former per-directory after-traversal stat map. C# source-file stat snapshots independently bind workspace materialization and final readiness. Input instability at the first barrier leaves prior rows, trust, evidence, purge state, and FTS recovery state unchanged; drift after mutations begin leaves the run partial and source evidence unknown for a clean retry. A C# file change detected while the workspace is still read-only can instead promote the run to one complete raw prepass and an all-C# refresh. Fatal discovery errors under prior positive or unknown evidence likewise defer C# writes and stale cleanup, while source contracts first observed after the immutable prepass leave the marker unknown so the next clean run repairs unchanged implementers.

Scoped updates group caller targets by unique checksum and same-directory stem, query C#-restricted candidates once per key, plan exact sorted stale IDs before the workspace prepass for one-sided renames and C#-filtered `--changed-between` cleanup, then reread those IDs by primary key in batches of at most 500 immediately before applying the immutable plan. If a planned database path has reappeared on disk, C# cleanup and writes are deferred, the run is partial, and source evidence remains unknown until a clean retry; the only exception is a live old spelling that is not an exact retained target and whose filesystem file identity matches a retained caller target in that same case-fold bucket. Binary and oversized C# skip records are fresh-stat validated inside their nested transaction before cleanup or upsert, so drift rolls back both the row update and its batch marker. Cleanup planning uses the indexed `files(path COLLATE NOCASE)` lookup and managed folding only as bounded alias-candidate filters. A live candidate bypasses the existence guard only when its exact spelling is not retained and its identity belongs to the matching case-fold bucket, so distinct leaf-case, ancestor-case, Unicode-folded, and cross-target hardlink paths survive on case-sensitive and mixed-policy directory trees. Git commit/range discovery requests NUL-delimited name-status output, preserving non-ASCII, tab, and newline paths; exact persisted rename sources are matched through lazily materialized identity buckets without hashing live files or turning pathological fold variants into quadratic work. A `--changed-between` run with prior authoritative false evidence retains the historical missing-file reconciliation; if that run discovers a contract or incomplete C# evidence, its late reconciliation excludes C# rows. Prior positive or unknown evidence stays on the pre-workspace C#-only stale plan and does not turn the scoped delta into an unrelated all-language walk. Unchanged caller-selected paths reuse their persisted checksum through an indexed stat-matched point lookup; only new or stat-changed rename candidates receive an extra streaming checksum read, keeping work proportional to the delta rather than the full index. If a later live cleanup still encounters an unplanned C# row, it reports workspace drift and leaves source evidence unknown instead of silently committing an authoritative result.

Expanded C# targets are normalized back to their repository `IndexPath` before the exact retained-path reappearance guard runs. This keeps absolute paths introduced by contract expansion in the same namespace as persisted cleanup-plan paths, so an exact path that reappears cannot be mistaken for a removable case-fold alias before extraction succeeds.

File-purge transaction-gate acquisition observes the caller cancellation token as well as cancellation during the delete batch. A purge waiting behind another writer therefore exits promptly without deleting files, chunks, or FTS rows.

Bulk-guard setup and abandonment also keep cleanup failures recoverable. If trigger suspension fails after establishing the process-owned marker—including a partial trigger drop—or if later trigger restoration or the FTS rebuild throws, the writer best-effort replaces that marker with owner-independent `true` before rethrowing the original exception. A later request in the same process can therefore restore all synchronization triggers, rebuild searchable FTS state, and clear the marker; a secondary marker-write failure never masks the original cleanup error.

Process-owned FTS state keeps its primary marker exactly `pid:<pid>` so older readers continue to parse it. A separate owner-generation value repeats that PID and records the process start-time generation when the platform exposes it, with a per-process token fallback. Persistent insert/update/delete cleanup triggers clear the association whenever an older writer mutates only the primary marker. New readers load both values and the trigger-set status in one SQLite snapshot, then trust the generation only when the complete cleanup-trigger set exists and its PID matches the primary marker; missing, malformed, mismatched, and unverifiable foreign generations stay on the conservative PID-only behavior. Bulk guards also capture the writer's durable-commit generation after suspending triggers. A SQLite commit is published before post-commit bookkeeping and passive WAL checkpoint work, so either `Complete` or abandonment rebuilds FTS after a later exception even if the caller had not yet updated its mutation flag. The transaction scope remains in its finalizing state until those post-commit actions finish or fail, and a concurrent `Dispose` waits beyond the diagnostic contention timeout rather than releasing transaction resources or the writer gate while a finalizer still uses them. Rollback similarly detaches the completed SQLite transaction before publishing its terminal state, so an old finalizer cannot clear a successor transaction's cached reference after the gate changes owners.

Successful writer sessions attempt `PRAGMA wal_checkpoint(TRUNCATE)` before closing a writable `DbContext`, so large WAL files are reclaimed after index, backfill, optimize, prune, and other DB-writing commands. `cdidx db schema [--json]` dumps `sqlite_master` entries plus `PRAGMA user_version` for schema inspection and accepts `--summary-only`, `--type`, `--name`, `--limit`, `--max-sql-chars`, and `--exclude-internal` when automation needs bounded diagnostics. `cdidx db prune --dry-run|--apply [--json]` counts or deletes orphaned `symbol_references`, `reference_lines`, and `symbols` rows before running `PRAGMA optimize` on apply.

### Metadata invariants

`DbWriter.SetMeta` participates in the caller's writer transaction when one is
active. When no writer transaction is active, it wraps the metadata UPSERT in a
SQLite savepoint so standalone stamps still have a commit boundary and calls
from raw SQL transactions do not attempt a nested `BEGIN`. Dependent metadata
and row rewrites that must succeed or fail together should be placed inside the
same `DbWriter.BeginTransaction()` scope; do not stamp readiness or schema
trust metadata before the dependent rows are written.

`MarkFoldReadyWithResult` is the single validation-and-stamp path used by CLI and
MCP when carried fold metadata is current. It checks NULL folded columns and
current folded values once under `BEGIN IMMEDIATE`, returns a precise failure
category, and stamps readiness without a caller-side full-table pre-scan. When
carried metadata is stale, callers may run the cheaper NULL-only check solely to
prioritize the legacy-backfill degradation reason.

### Extending the indexer

Out-of-tree post-extraction hooks can implement `CodeIndex.Indexer.Hooks.IPostExtractionHook` in a `.dll` placed under `~/.config/cdidx/hooks/` (or the directory named by `CDIDX_HOOKS_DIR`). Hook discovery validates every directory ancestor, rejects unsafe ownership or group/world-writable modes and symlink/reparse-point ancestors, examines at most `CDIDX_HOOK_DISCOVERY_MAX_DLLS` DLL candidates (default: 128), rejects non-regular or symlink/reparse-point candidates, requires each candidate to be no larger than `CDIDX_HOOK_DISCOVERY_MAX_BYTES` bytes (default: 67108864), then processes the bounded candidate set in path order. Assembly loading, module initialization, `GetTypes`, and constructor validation occur only in a deadline-, memory-, and output-bounded discovery worker that returns a bounded manifest. After flushing its response, the worker remains alive on the parent input pipe so the parent can terminate the live discovery process tree, including descendants, before accepting the manifest; hook constructors and callbacks execute in isolated callback workers. Hooks are called after built-in symbol extraction and again after built-in reference extraction, before rows are persisted. Hooks receive a `FileContext` plus mutable `IList<SymbolRecord>` / `IList<ReferenceRecord>` values, so they can annotate extracted records, add synthetic symbols, or add domain-specific references.

Plugin marker and API compatibility checks use PE metadata only and run before executable loading. The marker admission check requires the exact attribute constructor signature, a complete value blob, and one marker. Metadata-referenced managed sibling dependencies cross the same filesystem boundary and are staged recursively under count and aggregate-byte caps. Plugin assembly loading, type inspection, construction, and symbol/reference extraction then run in a dedicated worker with a 5-second wall-clock deadline, 256 MiB working-set limit, and bounded line protocol. The parent registers manifest-backed proxies and never loads plugin executable content. Failed fingerprints are cached only until the source bytes change, allowing a repaired partial copy to be retried when project initialization or status starts an explicit serialized refresh. Hot-path language and extractor lookups reuse the current registrations without re-enumerating or restaging unchanged DLLs. A successful replacement atomically updates registrations and disposes the previous path-keyed worker and staging, keeping counts bounded. Hook discovery and callbacks likewise retain no parent load context. `status --json` reports both isolation lifecycles and exposes each hook's stable `id`.

Plugin and hook discovery use the same executable-content filesystem boundary for default and overridden directories. After validating ownership, mode, every ancestor, containment, and the regular-file leaf, cdidx hashes and copies accepted DLL bytes into a process-private read-only staging directory. On Windows, the equivalent checks validate owners and reject write-capable DACL entries for untrusted principals; staging is created with protected, current-user-only inheritance. Assembly loads use only that staged path, closing the validation-to-load rename-swap window; staging is removed when the owning registry or hook runner is disposed, including read-only dependency files.

`CDIDX_HOOKS_DIR` is a trust boundary override. Point it only at a local directory controlled by trusted users because hook assemblies execute extension code in isolated workers. `status --json` and MCP `status` report sanitized `hook_diagnostics[]` when the override is accepted or rejected and reject missing directories, unsafe Unix ownership/modes, or symlink/reparse-point ancestors. Hook diagnostics include a bounded `category` machine code so callers can distinguish override, discovery, assembly load, constructor, callback, and timeout failures without parsing human-readable messages.

Hook failures are isolated to that hook invocation: assembly load, construction, and callback exceptions are captured as diagnostics with sanitized categories and indexing continues. Each loaded hook runs in an isolated worker process, and callbacks run against scratch copies with a bounded wall-clock budget controlled by `CDIDX_HOOK_CALLBACK_BUDGET_MS` (default: 5000 ms). The first callback budget covers worker startup and callback execution. A timed-out callback kills the worker process tree, contributes no mutations, emits an index warning, and disables only that assembly-qualified hook ID for the remainder of the current index run. The stable ID hashes the staged assembly fingerprint, normalized source-path identity, and full type name, preventing equal `Type.FullName` values from colliding across DLLs. `status --json` and MCP `status` expose loaded hooks under `hooks` with `id`, `name`, `assembly_path`, `type_name`, and `callback_budget_ms`; hook-specific diagnostics carry the same value as `hook_id`.

### Ignore file parsing

`.gitignore` and `.cdidxignore` parsing follows Git's whitespace rules for pattern lines: leading unescaped spaces and tabs are ignored before comment/pattern parsing, `#` starts a comment only when it is the first unescaped character after that trim, and unescaped trailing spaces or tabs are trimmed. Escape a leading, trailing, or `#` character with `\` when it is part of the filename pattern.

Ignore-file reads avoid `File.Exists` / `File.ReadLines` time-of-check/time-of-use races: the scanner attempts the UTF-8 read directly, treats missing files as absent rules, treats permission-denied ignore files as warnings while preserving inherited ancestor rules, and treats other I/O failures as unavailable rules so callers can avoid indexing with stale or unknown local ignore state. Ignore patterns are capped at 512 tokens and compiled with the non-backtracking regex engine plus a match timeout so malformed or untrusted ignore files cannot stall a scan with excessive regex work.

Bracket expressions follow Git-compatible glob behavior: both `[!a]` and `[^a]` are treated as negated character classes when `!` or `^` appears immediately after `[`. A caret elsewhere in the class is literal (`[a^b]`), and a literal leading caret must be escaped (`[\^a]`).

### CLI recoverable error format

Recoverable command errors in human output use the canonical line shape below. Include only non-null lines, but every error must include a recovery hint:

```text
Error: <message>
Hint: <actionable recovery path>
Usage: <command shape>
```

When an error code is available, the first line is `Error [<code>]: <message>`. Use `CommandErrorWriter` for new CLI parse, validation, and filesystem preflight errors so `ProgramRunner`, `IndexCommandRunner`, and query runners keep the same format. JSON error payloads continue to use `CommandErrorJsonResult`.

For JSON mode, recoverable non-database failures use the same versioned
`CommandErrorJsonResult` envelope. It requires `api_version`, `status`,
`message`, `hint`, `error_code`, `category`, `command`, `exit_code`, and
`usage`; command-specific fields may be merged only after paths, warnings, and
previews are sanitized and bounded. JSON failures write the envelope to stdout
and leave stderr empty. Human failures write the matching coded `Error`,
`Hint`, and `Usage` lines to stderr and leave stdout empty.

| Failure class | Exit code | Error code | Category |
|---|---:|---|---|
| Usage / invalid arguments | 1 or 7 | `E010_USAGE_ERROR` | `usage` |
| Missing outline path | 2 | `E019_FILE_NOT_FOUND` | `not_found` |
| Invalid configuration | 1 | `E024_CONFIG_INVALID` | `configuration` |
| Hook platform or filesystem failure | 9 | `E025_HOOK_OPERATION_FAILED` | `platform` |
| Hooks outside a Git repository | 2 | `E026_NOT_GIT_REPOSITORY` | `not_found` |
| Other recoverable command failure | command-specific | `E023_COMMAND_FAILED` | stable writer classification |

### Process launch policy

All production subprocess launch sites must use `ProcessStartInfo.ArgumentList` and must leave `UseShellExecute` disabled. Start-info construction belongs in `ProcessLaunchPolicy` or a nearby purpose-specific helper that calls it, so git, isolated workers, hook callbacks, installer dispatch, and other subprocesses share the same argument, encoding, and no-shell defaults.

Environment inheritance is opt-in. Use `SubprocessEnvironmentPolicy` for allowlisted subprocess environments: git keeps only base/proxy/certificate/git knobs and disables terminal prompts, isolated workers keep only base/.NET runtime values plus `CDIDX_TEST_` variables for tests, and installer handoff keeps only the documented installer/proxy/certificate variables. Do not add broad `CDIDX_*`, token, credential, or shell environment forwarding without documenting the trust boundary and adding tests.

Every subprocess wait must have an explicit cancellation or timeout path. Captured stdout/stderr must be bounded before user-visible diagnostics, and diagnostics should be sanitized through the existing safe-formatting helpers.

### CLI output encoding and terminal controls

CLI JSON output must be machine-clean: redirected stdout is written as UTF-8 without a BOM, and JSON-mode commands must not emit ANSI escape sequences even when `--color=always` or `CLICOLOR_FORCE=1` would color human output. Keep JSON-safe styling suppression close to shared formatting helpers such as `ConsoleUi.ColorizeKind` so future query output paths inherit the invariant.

Stdout is owned by the command payload. In human mode that payload is the human-readable result; in JSON mode it is only the documented JSON object, array, NDJSON stream, envelope, or external format selected by `--format`. Warnings, progress, slow-query messages, lifecycle logs, worker diagnostics, and recoverable-error prose belong on stderr or the private global tool log. Commands that create artifacts while also reporting JSON, such as `report` and export commands, must keep stdout parseable as JSON and describe the artifact through the JSON summary instead of mixing human text into stdout. MCP stdio follows the same separation at the protocol level: stdout carries JSON-RPC frames only, while server diagnostics and telemetry go to stderr.

JSON serialization sites are split by contract domain. Public CLI JSON uses
`ProgramRunner.CreateDefaultJsonOptions()` and `CliJsonSerializerContext`: field
names are snake_case, nulls are omitted, audited public top-level event/result
DTOs carry `api_version`, and DOM-built `JsonObject` payloads may add only
sanitized, bounded fields. Add `api_version` when introducing or auditing a
public top-level CLI JSON DTO. MCP JSON-RPC uses `McpServer`'s camelCase options for the
protocol envelope while tool structured content keeps its documented
machine-readable keys. Every object-shaped tool `structuredContent` envelope carries
root-level `api_version`, injected by `CreateToolResult`; sanitize/redact values before mutating `JsonObject` /
`JsonNode` instances. LSP, quickfix, and SARIF outputs follow their external
schemas rather than the CLI snake_case contract. GitHub/report helpers and
worker/private storage paths use their own bounded serializers because they are
either API clients, persisted local state, or process-internal protocols. The
`LocalJsonlJsonWriterOptions` relaxed encoder is intentionally limited to
private append-only JSONL diagnostics and must not be reused for public CLI,
MCP, LSP, HTTP, or embeddable JSON.

`cdidx export ctags --json` follows the same contract: stdout contains only a
single JSON summary or structured error, while the tag file itself remains the
artifact. The summary includes resolved output/database paths, tag/emitted/
skipped counts, filters, and advertised metadata field names so editor
integrations can validate filtered exports without parsing human output.
`skip_reason_counts` is a bounded object with stable reason keys; every skipped
candidate contributes to exactly one reason, and its values sum to
`skipped_count`. Generated files follow the query-command contract: exclude by
default, opt in with `--include-generated`, and report `unavailable` without
referencing `files.generated` when a legacy database lacks that column.

Interactive terminal controls are allowed only when stdout is not redirected or captured, terminal capability hints are present, and the environment has not opted out. Treat `TERM=dumb`, truthy `CI`, missing Unix terminal hints, `NO_COLOR`, and `CLICOLOR=0` as reasons to suppress ANSI/progress controls unless an explicitly human-facing override is documented for that control.

### C# / .NET integration

`SolutionProjectResolver` parses the plain-text `.sln` `Project(...) = "...", "...csproj"` entries with a non-regex parser and resolves C# / F# / VB project files. Project entries that normalize outside the active workspace root are ignored before filesystem probing or path-filter evaluation. Solution parsing rejects `.sln` files above 8 MiB, lines above 16,384 characters, and more than 4096 .NET project references with clear diagnostics. Automatic root-level `.sln` discovery samples at most 128 candidates before sorting and reports a clear error when that cap is exceeded, so callers should pass `--solution <path>` in solution-heavy workspaces. If automatic solution discovery cannot enumerate the workspace root, it appends a bounded traversal diagnostic with the explicit `--solution <path>` recovery hint instead of surfacing a raw filesystem exception. When exactly one `.sln` exists at the workspace root within that cap, `--project <name|path>` uses it automatically; otherwise callers can pass `--solution <path>`. Fallback project discovery caps traversal at 4096 directories and 65,536 files with a clear `--solution <path>` recovery hint. Fallback project discovery and project-file expansion use long-path-safe per-directory enumeration, skip unreadable subtrees, and include bounded traversal diagnostics when a project filter cannot be resolved.

Query commands that accept path filters (`search`, `definition`, `references`, `callers`, `callees`, `symbols`, `files`, `find`, `map`, `inspect`, `deps`, `impact`, `unused`, `hotspots`, and `validate`) expand `--project` into the matching project directory glob before hitting `DbReader`, so all existing SQL path predicates keep working. When the indexed project root cannot be resolved and project expansion falls back to the process current directory, CLI query context and MCP structured payloads include `project_filter_root` and `project_filter_root_fallback_reason`. `index --project` expands to the files under the selected project directory and reuses the existing `--files` update path, but rejects expansions above 65,536 files for one project or 131,072 unique files across all requested projects with an explicit-files recovery hint.

`cdidx batch` is a CLI-side query loop for editor integrations and scripts that need several query commands against the same DB without spawning `cdidx` repeatedly. Each newline-delimited stdin record may use the established JSON string-array form or the validated `{"command": "...", "args": [...]}` object form. Object input rejects duplicate/unknown properties, missing or blank commands, non-array `args`, non-string values, and the same argument count/length violations as array input. Serial mode opens one `DbContext` / `DbReader`; `--parallel <n>` requires `--json-summary`, is capped at 16 workers, and opens one isolated query-only context per active worker. Every form dispatches only commands in the side-effect-free allowlist owned by `CliCommandCatalog`. That schema includes query and read-only discovery surfaces such as `goto` and `audit`; adding a top-level command or a dispatcher arm alone cannot cross the batch safety boundary.

The default input budget remains 1,024 lines and is configurable through `--max-input-lines <n>` up to 65,536. Each decoded string argument remains capped at 8,192 characters. The JSON-summary output budget defaults to 10,485,760 characters and `--max-output-chars <n>` accepts 4,096 through 67,108,864. Immediate EOF with no commands remains exit 0 with no output by default; `--json-summary` appends a final JSON object with `commands_processed`, `line_errors`, `command_failures`, and `exit_code` for non-interactive callers that need an explicit empty-input signal.
By default, child query commands stream their normal stdout/stderr directly. In
`--json-summary` mode, every non-blank stdin line must instead emit one
machine-readable batch envelope before the final summary: parsed commands use
`record: "batch_result"` and include `line`, `command`, `arguments`,
and `exit_code`. The requested command/output format, rather than output-text
sniffing, selects the projection: successful
single-document JSON is embedded as typed `result`, while successful NDJSON is
embedded as a stable typed `results` array even when it has one row. Successful
text remains `stdout`, while every failure uses one typed `error` object with a
stable `error_code`, `category`, safe `message` / `hint`, and `scope`.
Malformed or over-limit input lines use `record: "batch_error"` and the same
typed error serializer. Failed records omit captured child stdout/stderr by
default; `--include-raw-streams` explicitly adds them under a bounded
`raw_streams` object. Child output must not be written directly beside batch
metadata in this mode. The entire
serialized stream—including envelopes, arguments, escaping expansion, terminal
errors, and the final summary—uses the configured `--max-output-chars` budget
(default 10,485,760; maximum 67,108,864). An item that exhausts it retains its
exit/error metadata with `error.scope: "batch"`. The
final `record: "batch_summary"` retains `commands_processed`, `line_errors`,
`command_failures`, and `exit_code`, and publishes `output_chars`,
`output_char_limit`, `input_line_limit`, `parallelism`, and input/output limit
state for empty-input, failure, and budget accounting. Parallel workers route
stdout/stderr through per-command bounded writers, keep a separate read-only
SQLite connection and thread-local batch reader, and buffer only the active
worker window. `ScopedConsoleOutput` keeps nested JSON-envelope capture on the
current worker's routed stdout instead of replacing another worker's process-wide
writer. Completed records are committed to the shared output writer in input
order; an ordinary item failure remains isolated. Caller cancellation is
serialized as `batch_cancelled` for a consumed input item and in the final
summary before batch processing stops. Parallel input waits remain
cancellation-aware even while stdin is blocked, and cancellation during
database setup still emits the typed final summary.

Editor integrations can request standard location shapes directly. `definition`, `references`, `search`, `find`, and `validate` accept `--format <text|json|lsp|qf|sarif>`; `lsp` emits LSP `Location` arrays, `qf` emits Vim quickfix lines, and `sarif` emits SARIF 2.1.0. `goto <symbol>` returns the single unambiguous definition as one LSP `Location`, while `goto --all <symbol>` returns all matching locations without applying the default or environment-provided query limit. An explicit `--limit` or `--top` still bounds the returned location array.

The `cdidx lsp` server advertises full text document synchronization and keeps
open document text in a bounded in-memory cache only. Position-based providers
must read that live cache before disk so unsaved editor buffers can identify the
requested token. Provider results remain conservative and index-backed except
that document symbols for an indexed document may be structurally re-extracted
from the bounded live buffer through the normal language extractor and container
pipeline, using the indexed file's authoritative language rather than
re-detecting it from the path. Live extraction stops at the document-symbol
materialization bound and falls back to indexed symbols when that bounded
extractor is unavailable. Numeric document-version tombstones remain bounded
across live-text eviction and are cleared by `didClose`, so an evicted newer
version cannot be replaced by a stale change. Other providers return empty
arrays or null when the database cannot answer safely instead of inventing
language-server analysis.
`LspServer` uses one lock-protected lifecycle state machine across ordinary
dispatch, the cancellation fast path, and queue-overload responses. Its phases
are before-initialize, initializing, running, shutdown, and exited. Only the
first `initialize` request can enter initialization. The transport reserves each
frame's lifecycle action in receive order. Under the output gate, the serialized
initialize response and the transition to running share one publication boundary:
the state changes immediately before the frame write starts. Shutdown changes
phase before waiting for active dispatches, then disposes an owned query context
and reader exactly once.
After shutdown, requests receive `-32600`, notifications are ignored, and only
the `exit` notification completes the normal lifecycle.
Disk-backed position-line caching must enforce its 4 MiB input limit while
streaming, not only through a pre-read `Length` check. Bytes beyond the limit
must never reach text decoding, including when a shared file grows concurrently,
and the bounded failure reason remains `position_file_too_large`.

LSP references resolve an indexed definition or call-site target through the same
identity-scoped candidate path as CLI symbol analysis; `includeDeclaration` adds
only that selected declaration. Document-symbol selections and workspace-symbol
locations cover the identifier. The stored column is the lookup anchor; when the
indexed source line is readable, the server confirms the identifier on that line
to tolerate older imprecise columns. If source text is unavailable, it falls back
to the stored column, then to character zero when no column was indexed. Type
inlay hints are emitted only when the indexed return type is not already written
before the declaration identifier, so explicit method return, local, and field
types remain suppressed.

Document/workspace symbol providers advertise work-done support and honor
bounded string/integer `partialResultToken` and `workDoneToken` values. Partial
results preserve the provider's deterministic order and use `$/progress`
notifications capped at 100 items and 64 KiB of JSON body each; document-symbol
partial results deliberately use flat `SymbolInformation` items, while the
token-free response keeps the existing hierarchical `DocumentSymbol` contract.
That hierarchy materializes every symbol before resolving parents from indexed
container names, container kinds, enclosing ranges, and same-line selection
columns. Same-range members such as positional record properties therefore stay
beneath their declaring type regardless of deterministic presentation order,
while a later same-named container on the line cannot capture an earlier member.
Live document symbols use the same extractor, normalization, and hierarchy
builder as indexed symbols, so a full-text change updates both ranges and
containers together. Numeric document versions must increase; an older or equal
change cannot replace the newest accepted live text.
The stdio reader and the single response worker are separated by a bounded
queue, so `$/cancelRequest` can cancel an active or queued symbol request without
making database-backed request processing concurrent. Cancellation IDs retain
their JSON type, cancelled requests end work-done progress and return `-32800`,
and every result/progress cap is surfaced through the work-done end message or a
bounded `window/logMessage` warning. Progress notifications drain through a
separate two-item channel while symbol items are enumerated, so notification
memory stays bounded and work-done `begin` reaches the client before symbol
materialization completes. A full request queue rejects excess requests with
`-32000` (`Server busy`), but document-sync and control notifications wait for
ordered processing instead of being rejected. Rejection responses use a bounded
output channel; capacity keeps cancellation readable, and output backpressure
pauses input rather than dropping a response. An output-worker failure cancels
the pending input read before propagating.

### Extractor performance contract

Symbol and reference extractors run during `cdidx index`, so language-specific
helpers must assume they will see generated files, very large methods, and
thousands of declarations or references in one syntactic scope. Avoid helper
shapes that rescan the same body, line range, or accumulated result list once
per candidate. If scope or delimiter information is needed for many candidates,
precompute the ranges once per file, function, or block and reuse that structure
for the per-candidate lookup.

The same rule applies to extracted-symbol membership. When a later per-line or
per-match decision repeatedly asks whether a class, property, import alias, or
other symbol exists, build a dictionary or set once and reuse it. A helper that
hides `symbols.Any(...)`, LINQ enumeration, or signature parsing inside the
candidate loop is still a repeated scan. Keep uncommon language/feature lookups
lazy, and preserve source-order or first-match semantics when replacing
`Distinct`-based scans.

Container ownership follows the same contract. If references repeatedly resolve
an extracted declaration by name and source range, index candidates by name once
and scan only that name's ordered range list. Preserve first-candidate behavior
for duplicate names and keep the index local to one extraction call.
For C#, both symbol assignment and reference resolution prefer the narrowest
active callable range, including `test.method` and nested local functions, before
an enclosing type. Named lambdas without a complete body range attach to that
enclosing callable and do not become reference containers.
Symbol container assignment keeps one reusable path buffer for the sorted
per-symbol walk. Enumerate the active stack into that buffer and reverse it to
outer-to-inner order; do not materialize both a stack array and a fresh path list
for every member in a deeply nested generated file.

Duplicate detection in hot extraction loops should use a `HashSet` or another
constant-time structure keyed by the full emitted record identity. Do not add
`List.Any(...)`, `List.Contains(...)`, nested regex scans, or repeated string
joins to loops that can run once per local variable, parameter, call site, type
reference, or pattern match in a large generated file.
Reference deduplication specifically uses `ReferenceDedupeSet` with a value-type
identity. Do not replace it with concatenated string keys: candidate names and
container names can be long, and the key is created before duplicate rejection.

For delimiter-only parsing in hot extractors, prefer index/span walks when the
consumer needs only one item at a time or validation can stay on the source
string. `string.Split` creates an array and substrings for every import,
dependency, path segment, or declaration item. Preserve the original empty-item,
trimming, quote, and first-separator semantics when replacing it.
`DelimitedSpanEnumerable` is the shared allocation-free walker for single
delimiters; repository metadata, application manifests, VHDL declarations and
package paths, and CUDA parameter headers use it instead of split arrays.
Likewise, exclusion-range checks inside dense match loops must use indexed
helpers rather than capturing LINQ predicates. Erlang, OCaml, and Raku share
`ContainsFunctionalSpan` / `OverlapsFunctionalSpan` for remote, qualified,
quoted-atom, and type-reference suppression.
The same rule applies to hardware languages: Verilog / SystemVerilog / VHDL
shadow scopes and CUDA / GLSL / HLSL / Metal / WGSL binding and resource scopes
use direct indexed loops so every identifier does not allocate a predicate
closure.
Repository metadata character validation uses `SpanCharacterSearch` instead of
predicate-based enumeration, and application-manifest dependency ownership is
tracked as XML depth rather than rescanning an ancestor stack for every
`assemblyIdentity`.
State-machine sentinel checks must also stay on spans. Erlang specification and
callable terminators plus Raku heredoc terminators trim views of the original
line without materializing padded copies.
`SpanCharacterSearch.EndsWithAfterTrim` is the shared suffix primitive for
these sentinels and for CSS selector continuations plus C# / Java body-less
declaration termination.
Functional-language exclusion span lists are lazy. Erlang quoted/remote calls,
OCAML type/qualified calls, and Raku qualified/method calls must not allocate an
empty list on every source line when no corresponding match exists.
Functional-language regex loops enumerate matches on demand and stop as soon as
the bounded reference list is full. Keep this contract across Clojure, Elixir,
Erlang, OCAML, and Raku so one dense line cannot force unused match objects.
JVM-family reference scanners follow the same rule. Java, Kotlin, Scala, and
Gradle/Groovy multi-match loops use `BoundedRegex.EnumerateMatches`; loops that
own the bounded reference list stop immediately at its cap.
Python reference scanners stream decorators, annotations, runtime type checks,
typing factories, dataclass/attrs integrations, and dynamic imports. The
start-offset overload of `BoundedRegex.EnumerateMatches` keeps decorator
argument scans demand-driven without rescanning the decorator prefix. The
no-offset overload preserves the regex instance's default start position,
including reverse source order for `RegexOptions.RightToLeft`.
Dynamic-language scanners for PHP, Ruby, R, and Perl stream multi-match
attributes/types, DSL targets, namespace/member/resource references, and arrow
calls. Any loop that writes directly to a bounded reference list must exit at
the cap instead of walking the rest of a dense line.
Prolog goal scanning keeps its per-line call list lazy. When calls are present,
update directive metadata in that same list before storing it; do not allocate
an empty list for call-free rules or project a populated list into a second one.
Secondary reference scanners also keep Fortran, Visual Basic, F#, Pascal,
Objective-C, Haskell, Elixir, Smalltalk, Lua, Dart, Razor, JSON, JavaScript,
GitHub Actions, and C++ compound-requirement matches demand-driven. Static
pattern enumeration accepts the extraction timeout explicitly so these paths
do not trade streaming for a different timeout contract. Regex loops that own
a reference list use `ReferenceExtractor.EnumerateReferenceMatches` so bounded
lists stop before requesting the next match, and the shared per-line pipeline
  checks the same cap between type, infrastructure, SQL, call, member, metadata,
  Razor, Python, and R phases.
Symbol and dependency extractors follow the same streaming rule across
scientific/native, Pascal/Ada, SQL, Python, Swift, GraphQL, markup/XAML, shell,
Ruby, Perl, Elixir, CSS, HDL, C++, and manifest parsing. When only a total is
needed, use `BoundedRegex.CountMatches`; it preserves the prior all-or-nothing
timeout result without retaining a `MatchCollection`. A scanner that needs the
first match for classification and the rest for parsing must keep one enumerator
instead of materializing or rescanning the input.
XAML supplemental symbol phases share the structured-data symbol budget. They
retain at most one overflow marker for diagnostic replacement, then return
through `TrimStructuredDataSymbols` immediately; do not accumulate an
unbounded temporary symbol list and trim it only after every XAML phase.
JSON symbol and reference byte-offset mapping shares `Utf8LineStarts`. It counts
newlines first and allocates the final offset array at exact capacity; do not
grow a `List<int>` and retain it beside a copied array for dense JSON files.
Systems-language scanners stream C/C++ construction and template groups, Rust
calls and value/signature types, Swift property wrappers, Go concurrency and
composite/signature types, plus shared scientific/native call groups. Preserve
source-order emission and stop owned bounded lists at capacity.
SQL reference scanners stream statement, source, target, generated-column,
window-clause, procedure-call, and temporary-object matches. Helpers that accept
multiple SQL matches must keep the sequence demand-driven, and loops that emit
references must stop consuming it when the bounded list reaches capacity.
Infrastructure and markup scanners stream CSS, XAML, HTML/GraphQL/Markdown,
HDL, MSBuild, Dockerfile, shell, and PowerShell match groups. Keep state-only
scans demand-driven as well, while applying bounded-list exits only where the
scanner owns the reference list.
Core reference scans stream shared calls, C# attributes/types/patterns/locals,
JSX elements, JVM documentation links, and Solidity references. A match set
that is intentionally consumed in multiple passes may remain materialized;
single-pass emitters must stay demand-driven and stop owned bounded lists.
All line-based symbol and reference extractors share `SourceLineSplitter`.
It counts newline boundaries once, allocates the exact result array, and then
materializes only the line strings that downstream scanners require; do not
restore separator-index arrays through `string.Split`.

When structural masking turns a source line into whitespace, do not materialize
a trimmed copy merely to discover that the line has no references. Preserve
documentation handling and the build-automation and markup paths that inspect
the original line, but let ordinary C#, Java, and JavaScript / TypeScript code
paths skip masked multiline payloads before creating a reference context.
Classify prepared-line whitespace once per core-loop iteration and reuse that
result for both special-line dispatch and the ordinary empty-line path; masked
payload lines can be thousands of characters long.

C / C++ header disambiguation operates on bounded lexical samples. Walk those
samples with spans and newline indices; splitting a sample into a line array
temporarily duplicates every sampled line and scales poorly across large
repositories with many ambiguous `.h` files.

The C# value-receiver path is the reference example: local receiver scopes are
derived from precomputed block spans for the containing function, and duplicate
receiver records are tracked with a hash set. Regressions in this area should
have a focused correctness test for the scoping rule and a large-fixture runaway
guard that would fail before users see multi-hour indexing stalls.

### Extractor concurrency contract

`SymbolExtractor` and `ReferenceExtractor` must be safe to call concurrently for different files or repeated calls on the same file content. Shared `Regex` instances and static lookup tables are initialized once by the CLR and treated as immutable after type initialization. Per-extraction state belongs in local variables, method parameters, caller-owned collections, or language-specific state objects created for that extraction call.

Do not add mutable static caches, shared `StringBuilder` instances, reused `MatchCollection` enumerators, or singleton scanner state to extractor code. If a future extractor needs cross-call memoization, use an explicit thread-safe collection and add a targeted parallel regression test that proves deterministic output under concurrent calls.

### Symbol Kind Taxonomy

`symbols.kind`, `symbols.container_kind`, and `symbol_references.container_kind` use the public symbol kind taxonomy below. New extractors must register new kind values in `SymbolKindCatalog` before writing them so schema checks, writer validation, CLI filters, and downstream JSON consumers stay aligned.

| Kind | Current producers / meaning | Graph behavior |
|---|---|---|
| `accessor` | Accessor declarations when extracted separately from their owning property | Search/filter symbol |
| `add` | Dockerfile `ADD` destination paths | Dependency/file-flow search symbol |
| `anchor` | Explicit Markdown HTML anchors, preserving exact ID case and punctuation after HTML entity decoding | Definition target for path-scoped Markdown fragment references |
| `annotation` | Annotation declarations or annotation-like language constructs | Metadata/search symbol |
| `async_function` | JavaScript/TypeScript async function declarations | Callable definition; participates in callers/callees through reference rows |
| `async_generator` | JavaScript/TypeScript async generator declarations | Callable definition; participates in callers/callees through reference rows |
| `attribute` | Razor attributes and metadata-like declarations | Context/search symbol; not a call edge by itself |
| `associatedtype` | Swift associated type declarations | Type-like definition target |
| `base_image` | Dockerfile `FROM` image names | Container image search symbol |
| `build_arg` | Dockerfile `ARG` names | Variable/search symbol; participates in Dockerfile variable references |
| `class` | Class declarations across object-oriented languages | Definition target and container |
| `class_hook` | Python class hook methods such as dunder hooks reclassified from functions | Callable/search symbol |
| `code` | Markdown fenced or structured code blocks | Search/outline symbol |
| `constant` | Constant declarations where the language distinguishes them | Search/filter symbol |
| `copy` | Dockerfile `COPY` destination paths | Dependency/file-flow search symbol |
| `delegate` | C# / F# delegate declarations | Callable type definition and container-like target |
| `enum` | Enum declarations | Definition target and container |
| `environment` | Dockerfile `ENV` variable names | Variable/search symbol; participates in Dockerfile variable references |
| `event` | Event declarations | Search/filter symbol |
| `expose` | Dockerfile `EXPOSE` ports | Container runtime search symbol |
| `field` | Field declarations where distinct from properties; C# const and static readonly fields share the tuple-aware type grammar used by ordinary fields | Search/filter symbol |
| `file_module` | File-scoped module/package declarations | Namespace-like context symbol |
| `function` | Functions, methods, constructors, delegates, tasks, and callable bindings that do not have a narrower kind | Primary callable definition; participates in callers/callees through reference rows |
| `generator` | JavaScript/TypeScript generator declarations | Callable definition; participates in callers/callees through reference rows |
| `heading` | Markdown headings and language section markers such as C# regions, Python module docstrings, and JavaScript/TypeScript `@module` docblocks | Outline symbol; Markdown headings are definition targets for path-scoped fragment references |
| `hook` | JavaScript/TypeScript React custom hook bindings | Callable-like search/filter symbol |
| `implements` | Razor `@implements` directives | Context/search symbol |
| `import` | Imports, using directives, aliases, and package includes | Search/filter symbol |
| `interface` | Interface declarations | Definition target and container |
| `lambda` | Named lambda/arrow bindings | Callable definition; participates in callers/callees through reference rows |
| `label` | Dockerfile `LABEL` keys | Metadata/search symbol |
| `layout` | Razor layout directives | Context/search symbol |
| `method` | Languages or hooks that explicitly distinguish methods from functions | Callable definition; participates in callers/callees through reference rows |
| `module` | Module declarations | Definition target and container |
| `namespace` | Namespace declarations | Definition target and container |
| `operator` | C# operator overload and conversion operator declarations | Callable definition; participates in callers/callees through reference rows |
| `object` | Object-literal/object container context used by nested extracted symbols | Container context |
| `package` | Package declarations | Namespace-like context symbol |
| `property` | Properties, property-like fields, and GraphQL input fields | Definition target; not treated as a call edge by itself |
| `procedure` | Procedure declarations in languages such as Fortran | Callable definition |
| `program` | Program block declarations in languages such as Fortran | Definition target and container |
| `protocol` | Protocol declarations in languages that distinguish protocols from interfaces | Definition target and container |
| `protocol_impl` | Elixir `defimpl` protocol implementation declarations | Definition target and container for implementation blocks |
| `reference` | Secondary extracted symbolic references, such as HTML classes, metadata keys, or GraphQL union variants | Search/filter symbol |
| `rule` | CSS/SCSS rule container context used by nested references | Container context |
| `route` | Razor route directives | Context/search symbol |
| `run` | Dockerfile `RUN` command bodies | Container build-step search symbol |
| `service` | Service declarations in IDL/protobuf-like languages | Definition target and container |
| `shell` | Dockerfile `SHELL` executables | Container runtime search symbol |
| `specialization` | C++ template specialization declarations | Definition target for specialized type/function forms |
| `stage` | Dockerfile named build stages | Build-stage definition; participates in Dockerfile stage references |
| `stopsignal` | Dockerfile `STOPSIGNAL` values | Container runtime search symbol |
| `struct` | Struct declarations | Definition target and container |
| `submodule` | Fortran submodule declarations | Namespace/module-like definition target |
| `subroutine` | Fortran subroutine declarations | Callable definition |
| `test.method` | Test methods detected by test-aware extraction | Callable definition; participates in callers/callees through reference rows |
| `trait` | Trait declarations in languages that distinguish traits from interfaces | Definition target and container |
| `type` | Type declarations where a narrower class/interface/struct/enum kind is not available | Definition target |
| `type_parameter` | Python `TypeVar`, `ParamSpec`, and `TypeVarTuple` declarations | Type-like definition target for declared type parameters |
| `typealias` | Type alias declarations | Definition target for alias names |
| `union` | Union declarations | Definition target and container |
| `user` | Dockerfile `USER` values | Container runtime search symbol |
| `block data` | Fortran block data declarations | Definition target |
| `variable` | Variable bindings | Search/filter symbol |
| `volume` | Dockerfile `VOLUME` paths | Container storage search symbol |
| `workdir` | Dockerfile `WORKDIR` paths | Container filesystem search symbol |

`SymbolKindCatalog.CompatibilityKindFamilies` maps both `typealias` and
`type_parameter` to the broad `type` family for consumers that only understand
the older coarse taxonomy. The persisted `kind` remains semantic, and `--kind`
filters remain exact, so `--kind import` does not include local type declarations.

`symbol_references.reference_kind` uses this separate reference taxonomy:

| Reference kind | Meaning |
|---|---|
| `annotation` | Annotation usage in languages that distinguish annotations from attributes |
| `attribute` | Metadata/attribute usage |
| `augmentation` | TypeScript declaration/interface merge edge |
| `call` | Function, method, operator, macro, or command call |
| `capture` | Captured callback/delegate relationship used by impact analysis |
| `column_reference` | SQL column reference in a statement-specific context |
| `consumes_hook` | React hook consumption relationship |
| `const_assertion` | TypeScript `as const` assertion edge |
| `const_generic_reference` | Rust const generic argument reference |
| `copy_from` | Dockerfile `COPY --from=<stage>` stage dependency |
| `cte_body_reference` | SQL common table expression body reference |
| `decorator` | Python decorator usage |
| `extends` | Inheritance or type-extension relationship |
| `from` | Dockerfile `FROM <stage>` dependency |
| `friend` | C++ friend declaration relationship |
| `generic_type_argument` | Generic type argument attached to an explicit invocation |
| `implement` | Interface implementation relationship |
| `implicit_implementation` | C# implicit interface implementation relationship |
| `import` | Import/include/reference through a module system |
| `instantiate` | Constructor or object creation |
| `join_condition_reference` | SQL join/merge condition column reference |
| `lifetime_reference` | Rust/C#-style lifetime or lifetime-like type reference |
| `metadata` | Metadata-only reference |
| `reference` | Generic persisted reference row used by fixtures or extractors without a narrower edge kind |
| `razor_event_binding` | Razor event binding relationship |
| `stage` | Build-stage relationship |
| `subscribe` | Event subscription relationship |
| `type_reference` | Type annotation, generic constraint, or other type-position reference |
| `unsubscribe` | Event unsubscription relationship |
| `use` | Generic usage relationship when no narrower reference kind applies |

### Status freshness age threshold

`status --check` keeps the DB/worktree checksum comparison in `IndexFreshnessChecker`, but the user-facing age hint threshold is resolved in `QueryCommandRunner`: CLI `--stale-after <duration>` wins over `CDIDX_STALE_AFTER`, which wins over `.cdidxrc.json`'s `stale_after`, then the 24-hour default. Supported duration suffixes are `m`, `h`, and `d`. A valid CLI `--stale-after` implies the workspace check. Check-mode JSON includes the top-level `stale_after_seconds` and `index_age_seconds` fields plus `query_context.check_mode` (`explicit` or `implied_by_stale_after`) and `query_context.stale_after_seconds`, so clients can audit both the activation path and the effective threshold without inferring them from text. Ordinary status JSON omits these check-only fields.

`status --json` emits structured readiness guidance whenever any trust field is degraded. The top-level `degraded_root_cause` is a stable machine-readable primary code, while `readiness_degradations[]` lists every degraded field with `root_cause`, human `degraded_reason`, `recommended_action`, and `alternative_action`. `migration_in_progress` is set from the active batch marker so clients can distinguish a temporary writer/migration window from a permanently degraded index. `issues_table_available` means the physical `file_issues` table exists; `file_issues_data_current` is the freshness/trust bit consumers should use before treating validate rows as authoritative.

Index-generation completeness is computed by one persisted-readiness reader and
reused by the successful full/update index response, immediate status and
workspace status, and MCP indexing/status responses. Persisted omission evidence
from symbols-only runs, `file_too_large`, `symbol_count_exceeded`,
`reference_count_exceeded`, extractor failures, and reference safety caps makes
`index_complete=false` with stable `index_incomplete_reasons`.
`reference_graph_complete` additionally requires an available, current graph
generation and repeats graph-specific stable reasons. A legacy database without
the completeness metadata keeps the compatibility default unless its persisted
rows prove that work was omitted.

Reference extraction publishes its fixed safety limits through CLI
`languages --json` / `status --json` and the corresponding MCP responses:
50,000 lookup symbols, 20,000 lookup lines, 512 names per
line, and 20,000 container candidates. Cap diagnostics are persisted as
per-file `file_issues`, aggregated for the current generation in
`reference_extraction_cap_hits`, and snapshotted into
`last_index_run.reference_extraction_cap_hits`. Any hit makes
`reference_graph_complete=false` and `graph_data_current=false`; callers,
CLI/MCP callers, callees, deps, and impact responses repeat the bounded summary and stable diagnostic
kinds so consumers cannot mistake an incomplete zero for an authoritative
absence.

### Workspace version pinning

On startup, `cdidx` walks up from the current directory looking for `.cdidx-version`. The first non-empty line is treated as the required CLI version for that workspace. The pin file is read with a 4096-byte cap; `cdidx` skips at most 16 leading blank lines, and each scanned line must be at most 256 characters. If those limits are exceeded, the pin is ignored with a warning. A mismatch prints a warning and continues by default; `--strict-version` or `CDIDX_STRICT_VERSION=1` turns the mismatch into exit code `64` (`EX_USAGE`). This check is advisory and does not rewrite the file. Use it to keep teams on the same binary when index contracts or query behavior differ between releases.

### Release freshness and upgrade checks

`cdidx --check-updates` and `cdidx status --check-updates` query the GitHub latest-release endpoint through `UpdateChecker`, using the same 24-hour cache and `CDIDX_DISABLE_UPDATE_CHECK=1` opt-out as the `--version` hint. `cdidx upgrade --check-only` reuses that check. `cdidx upgrade` is intentionally a thin wrapper around the signed release installer: it downloads `sha256sums.txt` and `install.sh` into a private temporary directory, independently verifies both exact files with `gh attestation verify` pinned to `github.com/Widthdom/CodeIndex/.github/workflows/release.yml` and `refs/tags/<selected-version>`, and only then trusts the manifest checksum and starts the installer. Missing or failed provenance blocks execution by default; `CDIDX_VERIFY_POLICY=compat` is the explicit audited opt-in. Upgrade JSON distinguishes the mechanism from the observed result through `verification_policy`, `manifest_provenance_verified`, `installer_provenance_verified`, `installer_verification_status`, and `provenance_audit_code`; check-only reports `not_attempted`, success reports `verified`, a strict blocked failure reports `verification_failed`, and a compat bypass reports `compat_bypass` plus `compat_provenance_bypass`. Invalid policy values return the normal structured usage-error JSON when `--json` is selected. After verification, the command checks that the current binary directory is writable, sets `CDIDX_INSTALL_DIR` to that directory, and runs the selected release installer.

Upgrade installer and git subprocesses scrub the inherited process environment
before launch. They forward only the shared subprocess allowlist needed for
PATH/home/temp/proxy/certificate behavior plus tool-specific knobs such as
`CDIDX_INSTALL_DIR`, installer verification variables, and selected `GIT_*`
controls. `CDIDX_TEST_*` variables are not a public runtime contract; they are
forwarded only to isolated worker processes so repository tests can exercise
worker-only hooks without exposing the rest of the host environment.

`cdidx upgrade --json` has a stdout contract suitable for automation. Check-only
and no-update results use the update-check fields
(`current_version`, `latest_version`, `update_available`, `from_cache`,
`error`, `error_category`, `error_hint`) plus release-selection fields (`selected_version`,
`selected_channel`, `selection_source`, `include_prerelease`). When an update is
installed, installer stdout/stderr is captured so stdout remains one JSON
document, with `install_attempted`, `install_exit_code`, and
`install_succeeded` added to the update-check fields. Windows handoff responses
also include `handoff_command`, `handoff_url`, `handoff_asset`, and
`handoff_asset_url`.

### Degradation reason codes

Readiness degradation reason codes are centralized in `DegradationReasonCodes`. Add new codes there with human text, a recommended action, and an alternative action before emitting them from readers, CLI, or MCP payloads.

Current stable codes and triggers:

| Code | Trigger | Recovery |
|---|---|---|
| `missing_fold_backfill` | legacy rows do not have folded-name values | `cdidx backfill-fold` or full rebuild |
| `stale_fold_key_version` | folded rows were stamped with an older fold-key version | `cdidx backfill-fold` or full rebuild |
| `stale_fold_key_fingerprint` | folded rows were stamped under an older runtime fingerprint | `cdidx backfill-fold` or full rebuild |
| `fold_rows_not_restamped` | fold metadata is current but one or more folded rows were not restamped | `cdidx backfill-fold` or full rebuild |
| `fold_ready_bit_set_but_rows_incomplete` | row-level verification found NULL folded-name values even though the fold-ready bit is set | `cdidx backfill-fold` or full rebuild |
| `fold_ready=false` | aggregate fold readiness bit is degraded | `cdidx backfill-fold` or full rebuild |
| `sql_graph_contract_ready=false` | SQL graph rows do not match the current call-column / qualified-name contract | `cdidx index <projectPath>` |
| `hotspot_family_ready=false` | one or more hotspot-family languages lack current authoritative family stamps | `cdidx index <projectPath> --rebuild` |
| `hotspot_family_marker_fingerprint_incomplete` | hotspot-family marker fingerprint traversal hit a safety cap, so family trust was not stamped authoritative | reduce generated/ignored marker trees or raise the cap in code, then run `cdidx index <projectPath> --rebuild` |
| `partial_family_key_population` | hotspot-family metadata is stamped but some indexed symbols still have NULL `family_key` values | `cdidx index <projectPath> --rebuild` |
| `graph_table_available=false` | `symbol_references` is missing or not graph-ready | `cdidx index <projectPath>` |
| `symbols_only_graph_omitted` | the last symbols-only generation intentionally omitted reference-graph rows | run `cdidx index <projectPath>` without `--symbols-only` |
| `reference_graph_complete=false` | the graph generation is unavailable/stale, a symbols-only run omitted it, or persisted file/extractor/cap evidence makes the index generation incomplete | address the reported stable reasons, then run `cdidx index <projectPath>` |
| `index_complete=false` | a symbols-only run or persisted file-size, symbol-count, reference-count, extractor-failure, or safety-cap evidence proves that indexing work was omitted | address `index_incomplete_reasons`, then run `cdidx index <projectPath>` |
| `issues_table_available=false` | `file_issues` is missing or not issue-ready | `cdidx index <projectPath>` |
| `csharp_symbol_name_ready=false` | C# canonical symbol-name stamps are stale | `cdidx index <projectPath>` |
| `csharp_metadata_target_ready=false` | C# metadata-target stamps are stale | `cdidx index <projectPath>` |
| `csharp_metadata_target_missing_column` | `symbols.is_metadata_target` is missing | `cdidx index <projectPath> --rebuild` |
| `csharp_metadata_target_stamp_outdated` | C# metadata-target version stamps are missing or stale | `cdidx index <projectPath>` |
| `index_newer_than_reader=true` | the DB was written with a newer persisted contract than this reader understands | use a current `cdidx` binary or rebuild with this version |

### SQLite WAL durability policy

| Area | Policy |
|---|---|
| Writable open pragmas | `DbContext` opens writable indexes in WAL mode, applies connection performance pragmas through `DbPragmaPolicy`, sets `PRAGMA auto_vacuum=INCREMENTAL` before schema creation for new empty databases, sets `PRAGMA application_id=0x43444958` (`CDIX`), sets `PRAGMA synchronous=NORMAL`, and pins `PRAGMA wal_autocheckpoint=1000`. |
| Open intent | Every `DbContext` caller declares `QueryOnly`, `WriteIndex`, `Migration`, or `Repair`. `QueryOnly` uses unpooled connections and skips persistent pragmas, migrations, metadata writes, and repair work. WAL databases are read from private temporary snapshots: checkpointed databases copy the main file and open that copy through an immutable read-only URI, while non-empty WAL databases copy the stable main/WAL pair so committed WAL content remains visible. Before and after copying, database-header and WAL-generation fingerprints must match; otherwise the bounded copy retries and ultimately refuses the open instead of returning stale data or modifying source `-wal` / `-shm`. Attached cross-database snapshots are cleaned up only after the attaching connection closes. Missing source/WAL files may retry as generation churn, while persistent copy I/O failures report `query_only_snapshot_copy_failed` with temporary-storage remediation. Long-lived MCP and LSP sessions refresh a detached snapshot when its source generation changes. `RepairIncompleteBatchReadiness` is guarded by `Repair` intent. |
| Application id | The application id lets file-type detection tools distinguish cdidx databases from generic SQLite databases. |
| Maintenance error contract | `vacuum`, `backfill-fold`, `optimize` / `index --optimize`, and `db integrity` route failures through `MaintenanceDatabaseErrorClassifier` version `1` and one JSON/human writer. SQLite primary codes `5`/`6`, `8`, `11`, and `26` classify locked/busy, not-writable, corrupt, and not-a-database failures without inspecting exception wording. The shared response carries a stable error code/category, conditional recovery hint, redacted path metadata, and optional primary/extended SQLite codes. Absolute paths are redacted by default; `--show-paths` is the explicit diagnostic opt-in. |
| Durable WAL file set | When WAL is active, the durable SQLite index is the `.db` file plus sibling `.db-wal` and `.db-shm` files. Backups, diagnostics bundles, and manual copies must include all three files when the siblings exist, or use SQLite's `.backup` command/API from a live connection. Copying only `codeindex.db` can produce a stale snapshot because committed pages may still live in `codeindex.db-wal`. |
| `synchronous=NORMAL` | Under WAL, `NORMAL` avoids per-commit fsync pressure during 500-row indexing batches while preserving database consistency after crashes. |
| Checkpointing | `DbWriter` runs `PRAGMA wal_checkpoint(PASSIVE)` after each outer transaction commit, and SQLite may also checkpoint automatically after the configured 1000-page threshold. Both checkpoint paths are opportunistic: active readers are not blocked, and an uncheckpointed WAL is expected state rather than corruption. |
| Checkpoint result contract | Explicit `PRAGMA wal_checkpoint(TRUNCATE)` paths execute a reader and return a structured result containing SQLite's `(busy, log, checkpointed)` values. Non-zero `busy` or positive remaining pages is unsuccessful with a bounded machine reason. `(0, -1, -1)` is SQLite's successful non-WAL no-op. Instance checkpointing, the static read-only-fallback preflight, query diagnostics, top-level status, and nested connection-policy status preserve the same result and counts. Raw exception text and paths must not enter diagnostics. |
| Crash recovery | If the process is killed after SQLite has committed a transaction but before checkpointing, the next normal opener rolls the WAL forward; no manual recovery step is required. If the process dies before a transaction commits, SQLite rolls that transaction back. |
| Migration transaction and foreign-key ownership | `TryMigrateForRead` checks SQLite's autocommit state before opening a transaction: an active transaction is treated explicitly as caller-owned, so cdidx neither commits nor rolls it back, while unrelated `BEGIN` failures propagate instead of being mistaken for nesting. Rebuild migrations that require disabled foreign keys set and read back `PRAGMA foreign_keys=OFF` before their owned transaction begins, assert the effective disabled mode inside nested rebuild helpers, and restore plus verify the caller's original mode after the transaction is disposed on both success and failure. |
| Schema discovery cache | `DbReader` schema discovery uses a process-level cache keyed by the normalized DB path. Column and index results are stored and returned as immutable `FrozenSet` snapshots, so callers cannot mutate schema decisions shared by other readers. Path states are reference-counted by live `DbSchemaCache` owners, are removed when the final owning `DbContext` is disposed, and are never evicted while an owner remains active. The cache checks `PRAGMA schema_version` before serving a lookup so SQLite DDL performed by cdidx or an external `sqlite3` session invalidates stale snapshots. Manual schema edits outside cdidx are still unsupported operationally; run `cdidx validate` after such edits before trusting query output. |
| Batch trust marker | Index write batches stamp `codeindex_meta.batch_in_progress=true` before starting a mutation transaction and clear it inside the transaction that commits the matching rows and readiness metadata. If the indexer crashes after the marker is written but before the commit clears it, every later open reports `Last batch did not complete; run cdidx index --rebuild to re-index from a known clean state.` without changing readiness metadata. The explicit `index --rebuild` repair path alone demotes readiness before rebuilding. Gracefully handled per-file errors clear the marker after rollback; orphaned markers are reserved for interrupted or crashed batches whose trust metadata should not be treated as clean. |
| Read-only opens and fallback | Query-only commands open with SQLite `Mode=ReadOnly` from the first attempt, retain WAL visibility, and never use writable setup or opportunistic migrations. A write-capable intent may still fall back to read-only when writable journal/WAL setup fails; an explicitly supplied `immutable=1` URI is the opt-in stale-snapshot escape hatch. If a WAL is present and must be observed from storage that cannot expose its sidecars, copy `.db`, `.db-wal`, and `.db-shm` together to a readable location or use a SQLite backup from an environment that can open the full WAL set. |
| Status pragma diagnostics | `status --json` exposes the selected read-only connection under `sqlite_connection_policy` (`active_mode=read_only`, `open_mode=read_only`) and resolved connection values under `db_pragma_settings` (`journal_mode`, `synchronous`, `wal_autocheckpoint`, `busy_timeout_ms`, `page_count`, `freelist_count`, `page_size`, `auto_vacuum`). It also exposes prepared-command cache counters under `prepared_command_cache` (`count`, `capacity`, `hit_count`, `miss_count`, `eviction_count`) for automation and support diagnostics. `maintenance_guidance` derives `wal_state`, `freelist_ratio`, `freelist_state`, `estimated_*_reclaimable`, `auto_vacuum_mode(_name)`, `recommended_command`, and `post_maintenance_follow_up` from those raw metrics without changing the raw values. `status --check --json` adds `repair_commands[]` entries with `name`, `args`, `reason`, and `safety_notes` so clients do not parse prose remediation strings. `last_failed_or_partial_index_run` exposes bounded failed/partial index context (`status`, `mode`, timings, counts, stable error code, reason, `progress_persisted`, and bounded `recovery_hint`) and must not include raw exception text or file paths. |
| Maintenance thresholds | WAL guidance flips to `checkpoint_recommended` at `CDIDX_MAINTENANCE_WAL_WARN_BYTES` (default 64 MiB). Freelist guidance flips to `vacuum_recommended` at `CDIDX_MAINTENANCE_FREELIST_WARN_RATIO` (default `0.20`). Invalid or out-of-range env values fall back to defaults. |
| Vacuum | `cdidx vacuum` runs `PRAGMA incremental_vacuum` against writable incremental-auto-vacuum DBs, and performs a one-time `PRAGMA auto_vacuum=INCREMENTAL` plus full `VACUUM` conversion for legacy no-autovacuum DBs. `cdidx vacuum --dry-run --json` estimates reclaimable pages/bytes and returns the same maintenance guidance without executing vacuum pragmas. Real `cdidx vacuum --json` also reports before/after DB and WAL byte samples; `wal_checkpoint_timing_note` explains that `wal_size_bytes_after` is measured before connection cleanup, so later `status --json` output may show a smaller WAL after checkpoint/truncation. |
| FTS optimize preview | `cdidx optimize --dry-run` opens a `QueryOnly` snapshot, probes an existing lockfile without creating or acquiring it, and never runs write PRAGMAs, schema setup, FTS control inserts, or metadata writes against the source DB/WAL/SHM set. JSON reports size/freelist/readiness indicators, the write-threshold recommendation, and planned operations, including the real command's repair-mode schema initialization or migration check. Object sizes use `dbstat` page bytes when available and a labeled logical-payload fallback otherwise. A real optimize records its elapsed milliseconds so later previews can expose `estimated_duration_ms`. |
| Size and process diagnostics | `status --json` also reports `db_size_bytes`, `wal_size_bytes`, capped `symbol_kinds` / `symbols_by_language` kind maps with `symbol_kind_*` and `symbols_by_language_kind_*` overflow metadata when caps apply, current `process` heap/GC/working-set metrics, `last_index_run` metadata from successful CLI and MCP index runs, and `last_workspace_freshened_at` as the latest successful index/update timestamp. `last_index_run.bytes_read_skipped_file_count` and `bytes_read_incomplete` report whether unreadable files were omitted from the `bytes_read` total, while `last_index_run.diagnostics`, `diagnostic_count`, and `diagnostics_truncated` carry bounded warnings for best-effort index metadata writes that failed after the index data itself was successfully written. `indexed_at` still comes from indexed file rows, so partial or no-op updates can freshen the workspace without moving `indexed_at`. |
| Memory tracing | `index --json --memory-trace` adds a `memory_timeline` block to the CLI index result and persists peak working-set MB into `last_index_run`; dry-run results also emit live `start`, `snapshot`, `scan`, and `finalize` samples but never persist run metadata. `index --dry-run --rebuild` bypasses destructive confirmation because it does not delete or rewrite the index. `CDIDX_MEM_WARN_MB=<mb>` prints a warning when the sampled working set crosses that threshold. |
| Newer schema protection | Writable opens reject databases whose `PRAGMA user_version` contains readiness bits outside the current binary's `CurrentSchemaVersion` mask. Read-only status/query paths may still surface `index_newer_than_reader=true` as a degraded audit signal, but write-capable paths must fail with `E003_SCHEMA_TOO_NEW` so an older cdidx cannot silently rewrite a DB stamped by a newer one. |

### Data directory resolution

When `--db <path>` is omitted, cdidx resolves the SQLite location from a data directory and appends `codeindex.db`. The precedence chain is:

1. `--data-dir <dir>`
2. `CDIDX_DATA_DIR`
3. `XDG_DATA_HOME/cdidx/<workspace-hash>` when `XDG_DATA_HOME` is set
4. `<workspace>/.cdidx`

`--db <path>` remains the most explicit override and bypasses data-directory resolution. `status --json` reports the effective directory as `data_dir` and the selected source as `data_dir_source` (`flag`, `env`, `xdg`, or `workspace`) so automation can audit where the index lives.

### SQLite performance tuning

Every `DbContext` connection sets `PRAGMA cache_size=-65536` (64 MiB), `PRAGMA temp_store=MEMORY`, and on 64-bit processes `PRAGMA mmap_size=268435456` (256 MiB). These are connection-scoped query-performance knobs; they do not alter the on-disk schema and are skipped only where SQLite cannot apply them.

Operators can override the defaults with environment variables:

| Variable | Default | Meaning |
|---|---:|---|
| `CDIDX_SQLITE_CACHE_KB` | `65536` | Positive cache size in KiB, up to `1048576`; cdidx applies it as a negative SQLite `cache_size` value so SQLite interprets it as KiB. Invalid or oversized values fall back to the default. |
| `CDIDX_SQLITE_MMAP_BYTES` | `268435456` | Non-negative memory-map window in bytes on 64-bit processes, up to `1073741824`. Use `0` to disable mmap. Invalid or oversized values fall back to the default. |
| `CDIDX_SQLITE_BUSY_TIMEOUT_MS` | `5000` | Non-negative SQLite busy timeout in milliseconds, up to `3600000`. Use a higher value for slow disks or concurrent MCP/index workflows; invalid or oversized values fall back to the default. |
| `CDIDX_PREPARED_COMMAND_CACHE_CAPACITY` | `64` | Positive prepared SQLite command cache capacity per connection, up to `512`. Invalid or oversized values fall back to the default. |

After a successful `cdidx index` run, the writer refreshes SQLite planner statistics so large repositories do not rely on default selectivity estimates for `search`, `references`, `callers`, and related joins. A brand-new index database runs full `ANALYZE` once after the initial population; later successful index runs use SQLite's lighter `PRAGMA optimize`. This maintenance is best-effort and never changes the schema contract.

### MCP request correlation

Each JSON-RPC MCP request gets a server-generated `correlation_id` in addition to the client-controlled JSON-RPC `id`. Successful MCP responses include it under `result._meta.correlation_id`, and error responses include it in `error.data.correlation_id` or tool-error `result.structuredContent.correlation_id`. The serialized JSON-RPC id is echoed as `request_id` in the same metadata when one exists. `batch_query` assigns child correlation IDs to each slot by suffixing the parent value with `.1`, `.2`, and so on.

MCP stderr diagnostics are prefixed with `[rid=<opaque-token> rid_type=<id-type> rid_length=<decoded-value-length> cid=<correlation-id>]` when a request context has an id. Every `tools/call` also emits one structured JSON line with `event: "mcp.tool.invocation"`, the same opaque `request_id` / `request_id_type` / `request_id_length` tuple, the tool name, elapsed milliseconds, status, result count when available, error metadata, argument keys, and argument lengths. Neither the raw request id nor argument values are logged in this telemetry.

### MCP query pagination

MCP `search` responses include `result_stable_at`, copied from the index freshness timestamp for the database snapshot used by that call. Clients that page through search results should compare `result_stable_at` across calls; if it changes, an intervening index mutation may have shifted the result set and the client should restart pagination.

Non-empty `search` responses also include `next_cursor`. Passing that value back as the `cursor` argument with the same query and filters continues after the last returned `(score, chunk rowid)` anchor. The cursor is an opaque response value; clients should not construct or edit it.

The high-volume discovery tools `symbols`, `files`, and `validate` also accept an opaque `cursor`. Every non-count response reports `returned_count`, `total_count`, `total_count_authoritative`, `remaining_count`, `cursor_offset`, `page_limit`, `has_more`, `result_stable_at`, and `next_cursor`; the final or empty page returns `has_more: false` and `next_cursor: null`. Totals are authoritative for `symbols` and `files`, and for `validate` while `file_issues_data_current` is true. If validation data is unavailable or not current, `validate` reports `total_count_authoritative: false` together with `issues_table_available` and `file_issues_data_current` instead of presenting its synthetic zero as a clean result. Each page reads its generation, total, and rows in one SQLite snapshot, and deterministic ordering lets clients enumerate all rows without gaps or duplicates. The generation includes the persisted monotonic indexed-file write counter, so separately committed indexing batches invalidate older tokens even when they update existing files within the same timestamp second.

Pass `next_cursor` back to the same tool with the exact same filters, `format`, and `limit`. These tokens are stateless and bound to both that normalized query and the index generation. Invalid tokens return `cursor_malformed`, changed arguments return `cursor_query_mismatch`, out-of-range offsets return `cursor_offset_out_of_range`, and an intervening index generation returns `cursor_stale` with the `index_stale` category. In all four cases, discard the token and restart without `cursor`. `countOnly: true` and `format: "count"` do not accept a cursor. The `status` tool publishes the token input bound as `mcp.limits.max_query_cursor_characters`.

### MCP health probes

The MCP JSON-RPC `ping` method returns a structured health object with `status`, `uptime_s`, `last_request_at`, `db_open`, `last_db_check_at`, and `transport_ready`. HTTP MCP transports expose the same object at `GET /healthz` on the existing listener. If the HTTP transport is protected by a bearer token, `/healthz` uses the same `Authorization: Bearer <token>` requirement as POST and `/events`.

`db_open` is a lightweight `SELECT 1` probe against the configured SQLite DB. A failed probe reports `status: "degraded"` and includes a sanitized `db_error` exception type instead of raw filesystem or SQLite details.

HTTP health objects also include transport observability counters for request-log drops, response cleanup failures, SSE event-stream drops (`http_event_stream_drop_count`, `http_event_stream_write_failure_drop_count`, `http_event_stream_last_drop_reason`), and bearer auth denial classes (`http_auth_denial_*`). These are internal diagnostics: bearer auth failures still return the generic 401 body unless unsafe debug logging is explicitly enabled.

### MCP keep-alive notifications

HTTP MCP `/events` streams can emit opt-in server-initiated `notifications/keep_alive` JSON-RPC notifications. Set `CDIDX_MCP_KEEP_ALIVE_INTERVAL_S` to a finite value from `1` to `300` seconds to enable them; unset, non-finite, or out-of-range values keep the default off behavior and emit a warning. Stdio sessions do not emit keep-alive notifications by default because the parent process owns liveness for that transport.

Each keep-alive notification includes `server_time` and `uptime_s` under `params`. The notification is best-effort: disconnected SSE clients are removed from the stream registry, and keep-alive write failures must not terminate the MCP server.

## Database schema

Persisted SHA-256 hashes are lowercase hexadecimal strings. New hash emitters
must format bytes with lowercase hex and comparisons must use ordinal
case-sensitive equality so format drift is visible instead of silently accepted.

### Tables

```sql
-- File metadata
files (
    id          INTEGER PRIMARY KEY AUTOINCREMENT,
    path        TEXT NOT NULL UNIQUE,       -- relative path from project root, slash-normalized and Unicode NFC
    lang        TEXT,                       -- detected language (e.g. "python")
    size        INTEGER,                    -- file size in bytes
    lines       INTEGER,                    -- line count
    checksum    TEXT,                       -- SHA256 over file bytes with CRLF/CR collapsed to LF (BOM bytes preserved); cross-OS clones match while BOM add/remove still triggers re-index
    modified    DATETIME,                   -- file modification time (UTC)
    generated   INTEGER NOT NULL DEFAULT 0, -- generated-code marker from filename/header detection
    indexed_at  DATETIME DEFAULT CURRENT_TIMESTAMP
)

-- Content chunks for full-text search
chunks (
    id          INTEGER PRIMARY KEY AUTOINCREMENT,
    file_id     INTEGER NOT NULL REFERENCES files(id) ON DELETE CASCADE,
    chunk_index INTEGER NOT NULL,           -- 0-based chunk position
    start_line  INTEGER,                    -- 1-based start line
    end_line    INTEGER,                    -- 1-based end line (inclusive)
    content     TEXT,
    UNIQUE(file_id, chunk_index)
)

-- Extracted symbols (functions, lambdas, classes, imports, namespaces)
symbols (
    id              INTEGER PRIMARY KEY AUTOINCREMENT,
    file_id         INTEGER NOT NULL REFERENCES files(id) ON DELETE CASCADE,
    kind            TEXT,                    -- "function", "lambda", "class", "import", "namespace", ...
    sub_kind        TEXT,                    -- language-specific subtype such as kotlin_value_class
    name            TEXT,
    line            INTEGER,                 -- 1-based anchor line
    start_line      INTEGER,                 -- definition start line
    end_line        INTEGER,                 -- definition end line
    body_start_line INTEGER,                 -- body start line when known
    body_end_line   INTEGER,                 -- body end line when known
    signature       TEXT,                    -- trimmed declaration/signature line
    container_kind  TEXT,
    container_name  TEXT,
    container_qualified_name TEXT,           -- qualified enclosing path (namespace/type stack)
    family_key      TEXT,                    -- authoritative cross-file family key when known
    visibility      TEXT,
    return_type     TEXT
)

-- Indexed references such as call sites
symbol_references (
    id              INTEGER PRIMARY KEY AUTOINCREMENT,
    file_id         INTEGER NOT NULL REFERENCES files(id) ON DELETE CASCADE,
    symbol_name     TEXT,                    -- referenced symbol name
    reference_kind  TEXT,                    -- "call", "instantiate", "generic_type_argument", "subscribe", "razor_event_binding", "friend", "attribute", "annotation", "decorator", "type_reference", "implicit_implementation"
    line            INTEGER,                 -- 1-based line number
    column_number   INTEGER,                 -- 1-based column number
    context         TEXT,                    -- trimmed source line
    container_kind  TEXT,
    container_name  TEXT,
    source_symbol_id INTEGER,                -- enclosing definition when resolved
    target_symbol_id INTEGER,                -- single resolved target definition
    target_symbol_key TEXT,                  -- stable language/path/container/name family key
    target_qualifier TEXT,                   -- stable type-like receiver qualifier when extracted
    resolution_state TEXT,                   -- resolved / resolved_group / ambiguous / unresolved
    resolution_candidate_count INTEGER NOT NULL DEFAULT 0
)

-- Best-scope candidates retained for resolved groups and explicit ambiguity
symbol_reference_candidates (
    reference_id INTEGER NOT NULL,
    symbol_id    INTEGER NOT NULL,
    scope_rank   INTEGER NOT NULL,
    PRIMARY KEY(reference_id, symbol_id)
)

-- FTS5 virtual table mirroring chunks.content
fts_chunks USING fts5(content, content='chunks', content_rowid='id')

-- Query-semantic readiness metadata
codeindex_meta (
    key         TEXT PRIMARY KEY NOT NULL,
    value       TEXT
)
```

### Indexes

```sql
idx_files_lang      ON files(lang)
idx_files_modified  ON files(modified)
-- idx_files_path is not needed: the UNIQUE constraint on path creates an implicit index
idx_chunks_file     ON chunks(file_id)
idx_symbols_name    ON symbols(name)
idx_symbols_file    ON symbols(file_id)
idx_symbols_file_kind ON symbols(file_id, kind)
idx_files_lang_modified ON files(lang, modified)
idx_symbol_refs_name      ON symbol_references(symbol_name)
idx_symbol_refs_file      ON symbol_references(file_id)
idx_symbol_refs_container ON symbol_references(container_name)
idx_symbol_refs_name_kind ON symbol_references(symbol_name, reference_kind)
idx_symbol_refs_name_file ON symbol_references(symbol_name, file_id)
idx_symbol_refs_name_nocase_kind ON symbol_references(symbol_name COLLATE NOCASE, reference_kind)
idx_symbol_refs_name_nocase_file ON symbol_references(symbol_name COLLATE NOCASE, file_id)
idx_symbol_refs_container_nocase_kind ON symbol_references(container_name COLLATE NOCASE, reference_kind)
idx_symbol_refs_symbol_name_folded_kind ON symbol_references(symbol_name_folded, reference_kind)
idx_symbol_refs_symbol_name_folded_file ON symbol_references(symbol_name_folded, file_id)
idx_symbol_refs_container_name_folded_kind ON symbol_references(container_name_folded, reference_kind)
idx_symbol_refs_source_symbol ON symbol_references(source_symbol_id)
idx_symbol_refs_target_symbol ON symbol_references(target_symbol_id)
idx_symbol_ref_candidates_symbol ON symbol_reference_candidates(symbol_id, reference_id)
```

### Query planner expectations

Hot graph aggregations that constrain `symbol_references.symbol_name` and a
small `reference_kind IN (...)` set must stay indexable through
`idx_symbol_refs_name_kind`. Regression coverage uses `EXPLAIN QUERY PLAN`
before and after `ANALYZE` so this compound index remains the expected plan for
`GROUP_CONCAT(DISTINCT r.reference_kind)` summaries instead of falling back to a
single-column symbol-name probe plus row-by-row kind filtering (#1922).

Language + symbol-kind definition queries intentionally keep `lang` on
`files` instead of denormalizing it into `symbols`. Query builders express that
filter as `s.file_id IN (SELECT id FROM files WHERE lang = @lang)` so SQLite can
use `files(lang)` to resolve candidate files and then probe
`idx_symbols_file_kind (file_id, kind)`. Avoid changing those queries back to
`JOIN files f ... WHERE f.lang = @lang AND s.kind = @kind`; on large indexes
that shape can start from `idx_symbols_kind` and scan every symbol of the
requested kind before checking file language (#1933).

Large filtered CTEs must project only the columns consumed by downstream CTEs
instead of using `SELECT *`. SQLite may materialize multi-use CTEs, so carrying
unused symbol columns through hotspot-style query chains inflates temp rows and
join width on large indexes (#1928).

### FTS5 sync triggers

```sql
-- Keep fts_chunks in sync with chunks table automatically
fts_chunks_ai   AFTER INSERT ON chunks  -- insert into FTS
fts_chunks_ad   AFTER DELETE ON chunks  -- delete from FTS
fts_chunks_au   AFTER UPDATE ON chunks  -- delete old + insert new in FTS
```

### Entity-Relationship

```
files 1──N chunks 1──1 fts_chunks (content mirror)
files 1──N symbols
files 1──N symbol_references
symbol_references 1──N symbol_reference_candidates N──1 symbols
```

Identity-aware reads are enabled only when `codeindex_meta` contains the current
`reference_identity_contract_version`. Adding the columns and candidate table through a
legacy read migration is not sufficient readiness: a normal, no-op, or deletion-only index
run refreshes reference resolution and stamps the marker atomically. C# references with an
unqualified name receive a global candidate only when that name is unique in the applicable
symbol set. Otherwise they remain `ambiguous` or `unresolved`, and dependency queries do not
fall back to a same-name edge.

C# `type_reference` candidates are filtered before qualifier and namespace ranking. A candidate
must be a type-like symbol (`class`, `struct`, `record`, `interface`, `enum`, or `delegate`), and
when the reference's generic arity can be recovered from its normalized line context, that arity
must match the declaration. Arity recovery skips valid block-comment trivia and treats the original
source column as an upper bound after the persisted context has been trimmed, so a later same-name
generic on the line cannot steal the reference. Non-type name collisions, ordinal-case mismatches,
and mismatched generic declarations never
participate in resolution; if no compatible candidate remains, the reference stays `unresolved`.
An uppercase property receiver such as `Name.Trim()` is rewritten to a qualified member reference
before graph resolution, including when the property is declared in another partial-class file or
an indexed base class. The closest property in the inheritance chain is selected with ordinal name
matching, so the type-only filter does not discard or ambiguously retarget a real member dependency.
This is a general candidate-compatibility rule rather than a framework-type blacklist, and it
does not change Java reference resolution.
The persisted reference-identity contract is versioned for this rule, so indexes written before
the compatibility filter are treated as non-authoritative until a normal index refresh rebuilds
their candidates.

Reference finalization computes candidate count, minimum symbol ID, distinct target-family
count, and stable target key in one correlated aggregate per reference. Keep these four
resolution fields on the row-value assignment path; separate scalar subqueries multiply the
candidate-index and symbol/file lookup work on large graphs. The language/name families that
are globally unique are aggregated once into the connection-local
`temp.reference_unique_symbol_families` table and reused by non-C#, C#, and C# attribute
fallbacks. Create that temp table in a separate prepared command before preparing the refresh;
SQLite resolves referenced tables while preparing every statement in a command batch.

`inspect` / MCP `analyze_symbol` treats each returned definition as a separate identity
bundle. Candidate selectors expose the persisted symbol ID plus qualified/container name,
signature, language, kind, path, and line. Identity-scoped reference/caller/callee queries
join `symbol_reference_candidates` or `source_symbol_id`; with multiple candidates,
top-level graph arrays are labeled `primary_candidate` and mirror only the first prioritized
bundle, never an unlabeled aggregate across definitions. When neither a language filter nor
a definition supplies the graph language, `analyze_symbol` infers it only from one consistent
language across reference/caller/callee evidence. `graph_language_source`,
`graph_language_confidence`, `graph_language_candidates`, and `graph_language_conflict`
distinguish authoritative filter/definition decisions from consistent inference and keep
mixed-language evidence unresolved.
Path/line resolution must select `symbols.id` and enter the same candidate-bundle builder as
name resolution; do not hand graph loaders only the display name. Each bounded references,
callers, and callees section computes its own stable-order page and authoritative total.
`graph_sections` reports `total`, `returned`, `offset`, and `truncated`, while CLI `inspect`
and MCP `analyze_symbol` attach query-, effective-page-size-, and index-generation-bound
cursors independently to truncated sections. Caller/callee ordering ends with every grouped
identity field so offset pages cannot skip tied rows. Candidate selectors are part of cursor
scope so paging one overload or partial-family representative cannot borrow graph rows from
another candidate. Treat a path/line locator separately from graph path filters: the locator
selects the persisted symbol, but its inbound references and callers may come from any indexed
file permitted by explicit graph filters. Shared cursor parsing may retain inspect cursors, but
every non-inspect command must reject that cursor family before execution.

### Reference taxonomy

`symbol_references.reference_kind` stores raw extractor labels. Default call-graph surfaces (`callers`, `callees`, inspect/analyze caller and callee bundles, and their JSON/MCP fields) expose the canonical public vocabulary `call`, `instantiate`, and `subscribe`. The primary `reference_kind`, `reference_kinds`, and `reference_kind_counts` keys use that same vocabulary. Use `--raw-kinds` on `callers` / `callees`, or `references --kind <raw-kind>`, when debugging raw extractor output.

`ReferenceRecord.SpanLength` and `symbol_references.span_length` persist the physical matched-token width rather than deriving it from the resolved symbol name; this matters for constructor-chain tokens such as `base`, `super`, and `this`. `DbReader.GetCallees` preserves that span while aggregating counts: it selects the smallest `(line, column_number)` among rows with a stored column, exposes that 1-based pair as `first_line` / nullable `first_column`, and carries the same row's nullable width as `first_length`; `reference_count` remains the independent aggregate. When every contributing legacy row has `column_number IS NULL`, the reader retains the minimum line and a null column. A migrated row can also retain a column with a null span length. CLI/MCP location adapters degrade either case without fabricating precision.

Reference extraction deduplicates only within the same indexed file and language context. When adding extractor paths, include the file id and language hint in shared `seen` keys so same line/column/name edges from polyglot workspaces do not collapse across Java, Rust, C#, SQL, or other language-specific normalization contexts.

| Raw kind | Logical graph kind | Notes |
|---|---|---|
| `call` | `call` | Direct executable invocation edges. |
| `instantiate` | `instantiate` | Constructor / construction edges. |
| `goroutine_spawn` | `goroutine_spawn` | Go `go f()` async spawn edges; the ordinary `call` edge is also emitted for the invoked function. |
| `channel_send`, `channel_receive` | raw label | Go channel communication edges for send and receive expressions; excluded from default invocation graphs. |
| `razor_event_binding` | `subscribe` | Razor `@on...="Handler"` event bindings from markup to C# handler names. |
| `subscribe`, `unsubscribe` | `subscribe` | Event wiring edges kept visible in default call-graph queries. |
| `generic_type_argument`, `friend`, `capture`, `consumes_hook`, `project_reference` | raw label | Dependency/metadata edges excluded from default `callers` / `callees`; available through `references`, an explicit kind filter, and the applicable dependency/impact surfaces. |
| `binding`, `resource_reference`, `import` | raw label | GPU/shader binding declarations, resource uses, and statically visible includes; excluded from invocation graphs and available through `references --kind <kind>` and raw reference exports/queries. |
| `system_variable` | raw label | SQL execution-context variables such as T-SQL `@@ROWCOUNT` / `@@IDENTITY` and MySQL `@@session.sql_mode` / `@@global.max_connections`; intrinsic variables have no definition site. |
| `attribute`, `annotation`, `type_reference`, `implicit_implementation` | raw label | Dependency/reference-only metadata, type-position edges, and compiler-synthesized implementation edges such as C# async iterator `GetAsyncEnumerator` / `MoveNextAsync`; excluded from default call-graph rows. |

TypeScript decorators emit `annotation` rows for the decorator name and must not hide the decorated declaration's type-position edges. For example, `constructor(@Inject() svc: Service)` records `Inject` as `annotation` and `Service` as `type_reference`, and `@Input() profile: UserProfile` records both the decorator and field type.

C# named-argument labels such as `overwrite:` are syntax, not type positions, and must not emit `type_reference` rows. The declaration-type scanner skips a leading single-colon label inside an argument fragment, including comma-terminated multiline argument lines, while preserving expression references, named `out` declaration types, explicitly typed lambda and anonymous-method parameters, and typed LINQ range variables in the argument value. Multiline property subpatterns likewise keep the type after their property label. Alias-qualified names (`Alias::Type`), statement and `case` labels, nullable types, and ternary expressions remain distinct colon-bearing constructs.

### GPU and shader reference extraction

CUDA, GLSL, HLSL, Metal, and WGSL use a request-scoped, stateless reference
extractor. Ordinary helper and entry-point invocations, including CUDA
`kernel<<<...>>>(...)` launches, emit `call` edges and therefore participate in
default call graphs. Statically visible includes, bindings, resource uses, and
user-defined type uses emit `import`, `binding`, `resource_reference`, and
`type_reference` metadata rows. Included-file type declarations can be supplied
through the bounded workspace-symbol snapshot. The extractor masks comments, suppresses
attribute/binding syntax as phantom calls, caps tracked names and per-line
scans, and intentionally does not perform macro expansion, binding validation,
function-pointer resolution, or semantic data-flow analysis. Registration in
the built-in extractor registry is the readiness contract exposed as
`reference_extraction` and `graph_queries` by `languages --json`.

### Python symbol taxonomy

Python extraction uses `function` for ordinary functions and methods, `class` for class declarations and dynamic class factories, `property` for class attributes, `@property` descriptors, accessor decorators, `Final` constants, and walrus-assigned names, and `class_hook` for lifecycle dunder hooks such as `__init_subclass__`, `__class_getitem__`, `__set_name__`, and `__class_subclasses__`. `SubKind` refines Python property accessors as `getter` / `setter` / `deleter`, walrus assignments as `walrus`, and class hooks as `dunder`.

### Scala symbol taxonomy

Scala extraction uses `class` for `class` / `case class` declarations and `object` for singleton `object`, `case object`, and sealed-object declarations. `implicit def` / `implicit val` / `implicit var` / `implicit class` declarations use `implicit`, and Scala 3 `given` declarations use `given`; their source, target, and evidence types are emitted as `type_reference` rows. `for`-comprehension generators also emit call edges for their generator sources. When a top-level `object X` appears in the same file as a top-level `class X`, `SubKind` records `companion_object` on the object and `has_companion_object` on the class so inspect/outline consumers can show the companion relationship without treating the singleton as an instantiable class (#1823, related taxonomy tracking in #1772).

### Extending reference extraction

Reference extraction is routed through `IReferenceExtractor` instances keyed by normalized language strings. Use `ReferenceExtractor.TryGetExtractor(language, out var extractor)` when a caller needs to invoke a language extractor directly; aliases such as `vue` / `svelte` normalize to `typescript`, and `razor` / `blazor` / `cshtml` normalize to `csharp`. The public `ReferenceExtractor.Extract(...)` method remains the compatibility entry point and delegates through the same registry.

Extractor inputs are passed as a `ReferenceExtractionContext`. Implementations must be stateless per call: keep mutable parse state in local variables or request-scoped helper objects, and treat shared regexes or lookup tables as immutable after initialization. New language extractors should register exactly one normalized language key and preserve existing reference-kind taxonomy (`call`, `instantiate`, `type_reference`, metadata kinds, and language-specific raw labels) so database readers and MCP output keep their contracts.

### TypeScript type-graph extraction

TypeScript extraction emits `type_reference` edges from type-only constructs as dependency metadata, not executable call-graph edges. Type aliases, mapped types, indexed access types, conditional types, template literal type holes, and `infer` clauses are scanned for referenced identifiers while TypeScript type operators such as `keyof`, `in`, `as`, `extends`, and `infer` are suppressed as keywords. For example, ``type Getters<T> = { [K in keyof T as `get${Capitalize<K>}`]: () => T[K] }`` records references to `T`, `K`, and `Capitalize`; `type Unwrap<T> = T extends Promise<infer U> ? U : never` records `T`, `Promise`, and `U`.

## Why a database instead of grep?

On small projects, `grep` works fine. But as a codebase grows to tens of thousands of files, `grep` becomes a bottleneck — especially when an AI agent calls it repeatedly. cdidx solves this by **reading every file once at index time** and building a search structure so that queries never need to touch the original files again.

`grep -r "keyword" .` performs a brute-force linear scan: it opens every file, reads every line, and checks for a match. The tenth search costs the same as the first. cdidx shifts the expensive work to a one-time indexing step, and subsequent searches are cheap lookups into the pre-built database.

| Factor | `grep -r` | cdidx (SQLite FTS5) |
|---|---|---|
| **Search algorithm** | Linear scan of every file, every time | Token lookup in inverted index |
| **Repeated searches** | Same full cost each time | Near-instant after initial index; `status --check` can verify whether the DB still matches the workspace before reindexing |
| **Startup cost** | None | One-time indexing (incremental updates after) |
| **What is stored** | Nothing — reads files on the fly | Source text in chunks + inverted index of tokens |
| **Structured queries** | Text matching only | Filter by language, path, symbol kind, line range |
| **Symbol awareness** | None — just raw text | Knows function/class/import names and locations |
| **AI token cost** | Returns raw lines — noisy, high token usage | Returns precise chunks with file path and line numbers |

### When to use which

| Scenario | Recommended |
|---|---|
| Quick one-off search in a small project | `grep` |
| Repeated searches across a large codebase | **cdidx** |
| AI agent performing multiple code lookups | **cdidx** |
| Finding all usages of a function by name | **cdidx** (`symbols` table) |
| Searching binary files or non-code content | `grep` |

## Why SQLite?

Given that a database is the right approach, why SQLite specifically rather than PostgreSQL, DuckDB, LiteDB, or a dedicated search engine like Tantivy?

**The short answer: SQLite is the only option that keeps cdidx a zero-configuration, single-file CLI tool with exactly one production dependency.**

### Alternatives considered

| Alternative | Strength | Why it doesn't fit cdidx |
|---|---|---|
| **PostgreSQL / MySQL** | Concurrency, scalability, advanced FTS | Requires a running server. Users would need to install and manage a database before using cdidx — this destroys the `dotnet tool install -g cdidx` experience. |
| **DuckDB** | Fast analytical (OLAP) queries, columnar storage | No built-in full-text search. cdidx's workload is OLTP (insert + keyword search), not analytics. .NET bindings are less mature than `Microsoft.Data.Sqlite`. |
| **LiteDB** | .NET-native embedded NoSQL, schema-free | No FTS. The relational structure of symbols → references → callers/callees is a natural fit for SQL joins, not document queries. |
| **Tantivy / Lucene** | Purpose-built full-text search with superior ranking | Handles only the search side. Relational data (symbols, references, file metadata) would need a separate store, creating a two-storage sync problem. |
| **Vector DBs** (Qdrant, Chroma) | Semantic / embedding-based search | Requires an embedding model (adds a large dependency or API calls). Keyword and structural queries are weak. Could complement SQLite in the future but cannot replace it. |

### What makes SQLite the right fit

1. **Zero configuration** — No server process, no connection strings, no ports. `cdidx index .` just works.
2. **Single-file database** — The entire index lives in `.cdidx/codeindex.db`. Copy, delete, or move it like any file.
3. **Cross-platform** — Identical behavior on Windows, macOS, and Linux without platform-specific setup.
4. **One production NuGet dependency** — `Microsoft.Data.Sqlite` is the only production/runtime dependency in `src/CodeIndex`. Test-only packages may still exist in `tests/CodeIndex.Tests/`, but they do not ship with the product and do not weaken that rule. This minimizes supply-chain risk and binary size.
5. **FTS5 built-in** — Full-text search is a native SQLite extension with inverted indexes, phrase queries, and ranking — no external search engine required.
6. **Relational + FTS in one engine** — Symbols, references, chunks, and file metadata live alongside the FTS index in the same database. Joins, triggers, and transactions keep everything consistent without cross-system synchronization.
7. **WAL mode** — Write-Ahead Logging allows concurrent reads during indexing and supports the MCP server serving queries while a background index runs.
8. **Incremental by nature** — SQLite transactions, `ON CONFLICT DO UPDATE`, and timestamp comparison make incremental indexing straightforward.

### When SQLite would not be enough

- **Massive monorepos (1M+ files):** SQLite's single-writer model could become a bottleneck. Sharding by project (cdidx already uses per-project databases) mitigates this, but true parallel writes would need a server database.
- **Semantic search:** Embedding-based similarity search would benefit from a vector index. The `sqlite-vec` extension could add this without leaving SQLite, or a hybrid architecture (SQLite + external vector store) could be considered.

For the current use case — a local CLI tool that indexes a single project for keyword search and symbol navigation — SQLite hits the sweet spot of simplicity, performance, and capability.

## FTS5 full-text search

[FTS5](https://www.sqlite.org/fts5.html) (Full-Text Search 5) is a SQLite extension that provides an **inverted index** for full-text search: it maps each token (word) to a list of documents containing it, enabling O(1) lookups by keyword rather than scanning every row.

FTS5 works through a **virtual table** — a table that looks and behaves like a normal SQLite table but stores its data in a specialized format optimized for text search.

Search result ordering must remain deterministic for identical inputs and an unchanged index. Ranking `ORDER BY` clauses should finish with stable persisted keys after user-visible relevance keys, so ties in FTS rank, file timestamp, and path never fall through to SQLite's implementation-defined row order. The chunk search `ORDER BY` ends with `f.path, c.id ASC` for this reason (#1731).

Chunk search ranking uses symbol structure before falling back to bare BM25 ties. Exact and prefix symbol-name boosts distinguish a chunk that overlaps the matching symbol definition from a different chunk in the same file, then fall back to file-level symbol presence. When text relevance ties, chunks with query hits in overlapping symbol names/signatures, higher-value symbol kinds (class/interface/struct/enum, then function/method/property), and shallower overlapping symbol scopes rank ahead of comment-only or deeply nested matches. This keeps scope-root and definition-like results ahead of redundant inner or unstructured mentions while preserving deterministic fallback ordering.

### What is an inverted index?

An inverted index maps each word (token) to the list of documents (or rows) that contain it — like the index at the back of a textbook.

For example, suppose three chunks contain the following code:

| Chunk ID | Content (simplified) |
|---|---|
| 1 | `handleRequest(ctx)` |
| 2 | `sendResponse(ctx)` |
| 3 | `handleRequest(req); sendResponse(res)` |

The inverted index built by FTS5 would look like:

| Token | Chunk IDs |
|---|---|
| `handleRequest` | 1, 3 |
| `sendResponse` | 2, 3 |
| `ctx` | 1, 2 |
| `req` | 3 |
| `res` | 3 |

When you search for `handleRequest`, FTS5 reads the entry for that token and immediately returns chunk IDs `{1, 3}` — no scanning required.

### How it differs from B-tree indexes

A B-tree (balanced tree) is the default index structure in SQLite. It organizes values in a sorted, tree-shaped hierarchy — similar to how a phone book is sorted alphabetically:

```mermaid
flowchart TD
    root["go | python"]
    left["csharp"]
    middle["java, kotlin"]
    right["rust, typescript"]

    root --> left
    root --> middle
    root --> right
```

B-tree indexes are good for exact matches (`WHERE lang = 'csharp'`), range queries (`WHERE modified > '2025-01-01'`), and sorting. However, they cannot efficiently answer "which rows contain the word `handleRequest` somewhere in a text column?" — that requires FTS5.

| | B-tree index | FTS5 inverted index |
|---|---|---|
| **Use case** | Exact match, range, prefix on a single column | Natural language keyword search across text |
| **Lookup** | `WHERE path = 'foo.py'` | `WHERE fts_chunks MATCH 'authenticate'` |
| **Structure** | Sorted tree of column values | Token → document ID posting lists |
| **Ranking** | N/A (returns exact matches) | BM25 relevance scoring |
| **Used on** | `path`, `lang`, `modified`, `file_id`, `name` | `chunks.content` (code text) |

These two index types complement each other. A typical query might use FTS5 to find matching chunks and then use B-tree indexes to filter by language or file path.

### The `fts_chunks` virtual table

```sql
CREATE VIRTUAL TABLE IF NOT EXISTS fts_chunks USING fts5(
    content,
    content='chunks',
    content_rowid='id'
);
```

| Parameter | Meaning |
|---|---|
| `USING fts5(...)` | Use the FTS5 engine to manage this virtual table |
| `content` | The column to index — corresponds to `chunks.content` (the actual code text) |
| `content='chunks'` | **External-content table** — `fts_chunks` does not store a copy of the text. It references `chunks`. |
| `content_rowid='id'` | The `rowid` of each FTS5 entry matches `chunks.id` for direct row lookup |

### Content sync

`fts_chunks` is a **content-external** FTS5 table (`content='chunks'`). It does not store the original text; instead, it points to `chunks.id` via `content_rowid`. This avoids doubling storage. cdidx keeps the FTS index in sync via database triggers (`fts_chunks_ai`, `fts_chunks_ad`, `fts_chunks_au`) that fire on insert, delete, and update of the `chunks` table.

### Query syntax

FTS5 supports advanced query syntax:

```sql
-- Single term
WHERE fts_chunks MATCH 'authenticate'

-- Phrase (exact sequence)
WHERE fts_chunks MATCH '"handle request"'

-- Boolean operators
WHERE fts_chunks MATCH 'auth AND token'
WHERE fts_chunks MATCH 'auth OR login'
WHERE fts_chunks MATCH 'auth NOT oauth'

-- Prefix search
WHERE fts_chunks MATCH 'auth*'

-- Column filter (only one column here, but useful in multi-column FTS)
WHERE fts_chunks MATCH 'content:authenticate'
```

### How the search works

Literal-safe `search` queries are bounded in the reader before FTS5
sanitization: maximum 1000 characters and 128 whitespace terms. Keep this guard
in `DbReader` so CLI, MCP, and direct reader callers share the same failure mode;
raw `--fts` queries continue to use the raw FTS complexity limits instead.

When you run:
```sql
SELECT f.path, c.start_line, c.content
FROM fts_chunks fc
JOIN chunks c ON c.id = fc.rowid
JOIN files f ON f.id = c.file_id
WHERE fts_chunks MATCH 'handleRequest'
LIMIT 20;
```

1. FTS5 looks up `handleRequest` in its inverted index → gets a list of matching chunk `rowid`s directly
2. Joins back to `chunks` to get the 80-line code block with start/end line numbers
3. Joins to `files` to get the file path and language

No files are opened. No directories are scanned. The entire search runs inside SQLite.

## Chunking strategy

Files are split into **80-line chunks with 10-line overlap**. The overlap ensures that a symbol definition or code block spanning a chunk boundary will appear in full in at least one chunk.

```
Lines   1-80   → Chunk 0
Lines  71-150  → Chunk 1  (10-line overlap with chunk 0)
Lines 141-220  → Chunk 2  (10-line overlap with chunk 1)
...
```

The step size is `80 - 10 = 70` lines. A file with N lines produces `ceil((N - 80) / 70) + 1` chunks (minimum 1).

## Symbol extraction

Extractor strategy by language surface:

| Surface | Extraction strategy |
|---|---|
| Most languages | Use **compiled regex patterns**, matched one line at a time for functions, classes, and sometimes imports. Named capture groups still extract identifiers for regex-driven paths. |
| JavaScript / TypeScript core shapes | Add a lightweight lexer/state machine on top of the regex pass for class-body bare methods, computed and modifier-prefixed methods, scope-aware synthetic class expressions, and JS/TS-specific range resolution that line-oriented regex cannot handle reliably. |
| Swift | Stays on the regex path for `func`, `class`, `struct`, `protocol`, `enum`, `indirect enum`, stored properties including `private(set)` / `fileprivate(set)` and backtick-escaped names, and enum cases. A small dedicated trailing-lambda pass keeps idiomatic `name { ... }` call sites visible to the graph. |
| Kotlin | Stays on the regex path. |
| Scala | Uses a separate block-call pass for `name { ... }` / `name { x => ... }` forms so idiomatic calls such as `foreach {}`, `Try {}`, and `synchronized {}` stay visible. |
| Common Lisp / Racket | Use a lightweight S-expression scanner that masks strings, line comments, and `#| ... |#` block comments before extracting definitions and function ranges. |
| HTML | Uses a dedicated character-level state machine instead of the regex pattern loop. It walks tag openers, quoted/unquoted attribute values including multi-line values, and masks `<script>` / `<style>` / `<textarea>` / `<title>` bodies plus `<!-- ... -->` comments so attribute-lookalike strings inside those regions do not leak phantom symbols. |
| JSON / JSON Lines | JSON emits `object`, `array`, `property`, and bounded primitive-array `value` symbols with indexed paths. `.jsonl` and `.ndjson` parse each non-empty physical line independently, prefix symbols with a stable zero-based record path such as `[0].result.path`, and emit repository-local path references from each valid record without flattening malformed neighbors. |
| TOML / repository metadata | TOML tables and keys, EditorConfig sections and keys, Git/Docker ignore rules, Git attribute rules/attributes, and `.rules` blocks/keys are emitted as bounded structural symbols. References are limited to repository-local paths or globs; remote URLs, absolute filesystem paths, and parent traversal are suppressed. |
| Windows application manifests | Manifest element paths, assembly identities, execution levels, and supported-OS values remain structural symbols. Dependent assembly identities emit `dependency` references, while local `file`, `codeBase`, and probing paths emit `project_reference` edges. |
| XML / NuGet.config | Generic XML emits bounded element and attribute paths. NuGet.config additionally promotes package sources, source mappings, signature validation mode, trusted signer names, certificate fingerprints, and `allowUntrustedRoot` values to semantic `property` symbols with `nuget.*` subkinds. |

JavaScript and TypeScript export/reference details:

| Area | Behavior |
|---|---|
| Barrel re-export surfaces | Recent JS/TS additions cover commented, multiline, namespace, minified, TypeScript type-only-star, and `with {}` / `assert {}` import-attribute forms while preserving the corresponding source-module `import` rows. |
| CommonJS exports and typed arrows | Direct CommonJS named export assignments and their same-line / multiline parenthesized wrappers are covered, as are multiline / constrained / async TypeScript generic-arrow RHS forms. |
| Function/class discrimination | Prefix-safe function/class discrimination is preserved. |
| Object-literal exports | Exported object-literal alias and shorthand properties are indexed. |
| Destructured named exports | `export const { foo, renamed: localName } = source` is indexed by emitted binding names. |
| Namespace and dynamic import aliases | TypeScript namespace aliases from `export * as NS from "./module"`, `import * as NS from "./module"`, named import aliases, and `const NS = await import("./module")` produce a `reference` edge back to the module specifier when later qualified usage such as `NS.Member()` appears. A later local declaration with the same alias name conservatively stops that module-linking range; dynamic import aliases are limited to their local brace scope. |
| Discriminant string guards | JavaScript/TypeScript comparisons such as `shape.type === "circle"` emit queryable `type_tag` references. These rows describe narrowing metadata and stay outside runtime call graphs. |
| React hooks | Detection is name-based: JavaScript/TypeScript function symbols matching `use[A-Z]...` are reclassified as `hook`, and calls to `use[A-Z]...()` are emitted as `consumes_hook` references. |
| Module specifier resolution | Module specifiers collected as `import` symbols consult the nearest `tsconfig.json` or `jsconfig.json`, follow relative `extends` chains, and apply `compilerOptions.baseUrl` / `paths` mappings before falling back to the literal specifier. Alias matches only rewrite to project-relative paths when a concrete file exists, trying TypeScript/JavaScript extensions and `index.*` candidates. |

SQL-specific symbol extraction:

| SQL surface | Behavior |
|---|---|
| MySQL backtick identifiers | Backtick-quoted identifiers are treated as case-preserving symbol names. |
| Unquoted identifiers | Continue through existing case-insensitive lookup paths. |
| MySQL `DEFINER=user@host` | Emits `definer` symbols. |
| PostgreSQL `RETURNS TABLE(...)` / `OUT` parameters | Emit function-scoped `field` symbols for synthetic result columns. `RETURNS TABLE` column lists are scanned with bounded, quote-aware parenthesis balancing so nested type modifiers and unsupported trailing syntax cannot abort the file extractor. |

Supported symbol kinds by language:

| Language | Main symbol kinds | Import / reference surfaces | Graph |
|---|---|---|:---:|
| Python | functions, classes, `@property`, PEP 695 generic functions | `from` / `import`, `type NAME = ...` | yes |
| JavaScript | functions, arrows, methods, class expressions, class-field arrows, export-surface properties | ESM imports/re-exports, CommonJS named exports, tagged-template calls | yes |
| TypeScript | JavaScript coverage plus typed arrows, generic methods, interfaces, enums, type aliases | ESM/TS imports/re-exports, CommonJS named exports, declaration type references | yes |
| C# | methods, constructors, operators, conversion operators, indexers, properties, fields, events, delegates, classes, records, structs, interfaces, enums, enum members, `#region` | `using`, `using` alias, `extern alias`, XML-doc `cref`, type-position references | yes |
| Java | methods, compact constructors, enum constants, classes, records, interfaces, annotations, enums, record components | imports, sealed `permits`, and `module-info.java` directives | yes |
| Kotlin | functions, extension functions, secondary constructors, classes, objects, interfaces, enum classes, enum entries, properties | imports, type aliases, trailing-lambda calls | yes |
| Go | functions, methods, test/benchmark/fuzz/example/init roles, type aliases, structs, interfaces | imports, type-position references, goroutine spawns, channel sends/receives | yes |
| Rust | functions, macros, `const`, `static`, impl/type aliases, structs, unions, traits, enums, inline modules, file modules | `use`, turbofish, structural type-position references, associated-type bindings, and explicit lifetime references | yes |
| Swift | functions, classes, actors, structs, protocols, enums, stored properties | imports and trailing-closure calls | yes |
| Ruby | `def`, Rails DSL, block calls, classes, modules, attributes | `require` | yes |
| Perl | packages, subroutines, constants | `use`, `require`, `parent` / `base`, arrow method calls | yes |
| C / C++ | functions, macros, structs, C++ classes, enums, enum classes; balanced C++ callable declarators including constructors, destructors, operators, and trailing-return functions | `#include`, type-position references | yes |
| PHP | functions, constants, enum cases, classes, interfaces, traits, enums | `use`, `require`, `include` | yes |
| Scala | `def`, `implicit` declarations, `given`, classes, objects, traits, enums | imports, type aliases, block calls, `for` generators, implicit conversion types, `given` / `using` evidence types | yes |
| Elixir | `def`, `defp`, modules, protocols | `import`, `alias`, `use`, `require` | yes |
| Common Lisp / Racket | packages/modules, functions/macros, classes, structs, variables | S-expression call heads, `#'name` function references, `make-instance` instantiation | yes |
| Clojure / Erlang / OCaml / Raku | namespaces/modules, functions, records/types/classes, protocols/roles | bounded imports, aliases, calls, and type/protocol/behaviour relationships | yes |
| SQL | procedures/functions/triggers, DDL objects, schemas, enum types, extensions | source/target dependencies, procedure calls, temp-object tracking | yes |
| Verilog / SystemVerilog / VHDL | modules/entities/architectures, packages, interfaces, classes, functions/tasks/processes, types, signals/parameters | syntax-visible instantiations, package/import/use relationships, architecture/entity links, bounded same-file signal/type references | yes |
| Shell | function declarations | command-style calls, sources, aliases | -- |
| Terraform | variables, outputs, locals, resources, data sources, modules, dotted dependencies | same-file `var.*`, `local.*`, `module.*`, `data.*`, `TYPE.NAME` references | -- |
| Protobuf / GraphQL | protobuf RPC/messages/services/enums; GraphQL operations/types/interfaces/enums | protobuf imports | -- |
| Gradle / Makefile | Gradle tasks/defs; Makefile targets and `.PHONY` metadata | Gradle plugin declarations; Makefile target prerequisites and `.PHONY` target lists | -- |
| Dockerfile | named stages and base-image/stage dependencies | `FROM`, `COPY --from` | -- |
| Lua / R / Haskell / F# | language-specific functions/types/modules/signatures | imports/requires/opens where supported | mixed |
| VB.NET | subs/functions, classes/modules, structures, interfaces, enums, properties, events | namespaces/imports, `AddressOf`, `Handles` | yes |
| Zig / PowerShell / CSS-SCSS / Batch / Assembly / HTML | language-specific functions, labels, selectors, stages, Web Components, properties, imports | language-specific references where implemented | mixed |
| XML | bounded element/attribute paths plus NuGet.config security-policy values | XAML references where implemented | mixed |

C# parameter and argument-list modifier tokens (`out`, `ref`, `in`, `params`, `this`, and `scoped`) are excluded only when parsed in modifier positions. A following concrete or generic type retains its `type_reference`, while `out var` emits neither the modifier nor the implicit `var` as a type. Do not implement this as a global keyword blacklist because contextual keywords can remain legal identifiers outside modifier positions.

Shell and PowerShell files also expose a synthetic `<script>` function symbol spanning the file. Top-level call references use this scope as their graph container, while references inside declared functions retain the declared function container.

Type aliases are indexed as `import` symbols in Rust, TypeScript, Swift, Go, F# and Scala. In F#, record declarations map to `struct`, discriminated unions map to `enum`, and constructor-style `type` declarations remain `class`.

For C#, the `Graph = yes` column covers callable references, event subscriptions, type-position dependencies, XML-doc `cref`, enum-member references, generic constructor/method calls, pattern heads, and verbatim identifiers. `unused` still has a narrower C# enum-member limitation and reports degraded scopes where that matters.

For JavaScript / TypeScript, reference extraction also captures tagged template literal call sites such as `` gql`...` ``, `` styled.button`...` ``, `` sql`...` ``, and generic-tagged `` html<User>`...` ``. Member-access tags attribute to the last segment (`styled.button` -> `button`).

HDL reference extraction is deliberately syntax-based and bounded by the published reference lookup limits. It masks comments and strings, emits hierarchy/package/architecture edges without requiring a matching local declaration, and limits signal, type, and function references to known symbols from the current file and their applicable lexical/design-unit scopes. It does not elaborate generates, preprocessor macros, parameterized hierarchy, or signal data flow. A successful full scan stamps `hdl_graph_contract_version`; when the stamp is missing or stale, graph readiness degrades and a normal `cdidx index .` refreshes unchanged Verilog, SystemVerilog, and VHDL files before restoring trust.

SQL also emits `namespace` symbols for `CREATE SCHEMA`, but the summary table above does not have a dedicated namespace column. SQL graph extraction emits `reference` edges for named source/target forms such as `FROM`, `JOIN`, `INSERT INTO`, `UPDATE`, `TRUNCATE TABLE`, `DELETE FROM`, `DELETE ... USING`, and `MERGE ... USING`; procedure and table-valued-function calls stay on the `call` path.

Languages without a registered symbol extractor remain available for text search; use `languages --json` to inspect the current symbol, reference, and graph capability flags.

VB.NET container patterns use `RegexOptions.IgnoreCase` plus `VisualBasicEnd`-based range tracking, so `Partial` spelling differences and multi-file type families still receive stable definition ranges and hotspot-family metadata.

Regex-based extraction is intentionally simple. Speed and portability are prioritized over AST-level accuracy.

### Line number contract

`files.lines`, `symbols.line`, symbol range fields, and `symbol_references.line` use 1-based physical source line numbers from the decoded file content before line-ending normalization and line-leading invisible stripping. CRLF, LF, and bare CR each count as one line separator, and a final line separator does not create an extra empty line. Extractors may operate on LF-normalized content, but persisted symbol anchor rows (`line` and `start_line`) must stay within the physical line range for the original file. `end_line` and body ranges may point one line past EOF to represent empty trailing ranges. Indexing validates extracted symbol ranges before writing them, and unchanged-file reuse is rejected when the stored line count differs from the current file even if the normalized checksum still matches.

## Incremental indexing

By default, cdidx compares each file's `modified` timestamp (UTC) against the stored value in the database. If unchanged, the file is skipped entirely.

When a file is re-indexed:
1. Old chunks and symbols for that file are deleted (FTS entries are cleaned up automatically by triggers)
2. The file record is upserted (`INSERT ... ON CONFLICT DO UPDATE`, preserving the row ID)
3. New chunks and symbols are inserted (FTS entries are populated automatically by triggers)

### Stale file purge

Before indexing begins, cdidx queries all file paths from the database and checks each against the filesystem. Files that no longer exist on disk (e.g., after a branch switch or deletion) are removed along with their chunks and symbols.

| Situation | What happens |
|---|---|
| File unchanged across branches | Skipped (instant) |
| File content changed | Re-indexed |
| File deleted after checkout | Purged from DB |
| File added after checkout | Indexed as new |

```mermaid
flowchart LR
    A[git checkout branch-B] --> B[cdidx .]
    B --> C{Per-file check}
    C -->|Unchanged| D[Skip]
    C -->|Changed| E[Re-index]
    C -->|Deleted| F[Purge from DB]
    C -->|New| G[Index as new]
    D & E & F & G --> H[DB = branch-B in sync]
```

### Partial update mode

Use `--commits`, `--changed-between`, or `--files` to update only specific files instead of scanning the entire project:

```bash
cdidx ./myproject --commits abc123 def456   # files changed in these commits
cdidx ./myproject --changed-between main feature
                                             # files changed between two refs
cdidx ./myproject --files src/app.cs        # specific files only
```

`--commits` uses `git diff-tree --no-commit-id -r --name-only` to resolve changed file paths.
`--changed-between` uses `git diff --name-status -M <old-ref> <new-ref>` and includes both old and new rename paths so stale indexed paths can be purged.

Watch batches reuse this partial-update runner with the top-level cancellation token. Ctrl-C, SIGTERM, or an embedding-host token remains active after the initial scan, so it interrupts idle watch waits, active extraction, FTS recovery/rebuild/optimization, and SQLite planner maintenance instead of waiting for a sub-run to finish. Watch does not install a second console handler, preserving the top-level first-Ctrl-C cooperative / second-Ctrl-C force-exit contract. A cancelled bulk FTS completion restores synchronization triggers and leaves an owner-independent recovery marker when its transaction did not roll back the marker. Long-lived MCP write contexts register each request token to suppress dispose-time planner maintenance after cancellation. Sub-run JSON is routed through `CommandOutputWriter`'s async-local scope into the watch capture writer; the watch loop never replaces `Console.Out`, so other commands and embedding hosts retain their own stdout.

Source membership is shared through `FileIndexer`: full scan, workspace freshness checks, and watch all exclude the `.cdidx` namespace. Watch classifies ignore files plus `.cdidx/patterns/**` and `.cdidx/plugins/**` before applying that source filter, so those non-source inputs remain debounced reconciliation events while ordinary `.cdidx` sidecars stay excluded. Subdirectory watches add non-recursive, ignore-file-only watchers for each ancestor directory through the repository rule root. The resulting `--files` sub-run recognizes extractor inputs, refreshes the process registry generation, and falls back to a full scan that disables unchanged-file reuse so every retained source row is extracted with the new generation. Refresh unloads file-discovered workspace plugins and patterns before loading the current generation, while retaining extractors explicitly registered by an embedding host, so edits and deletions cannot leave extension membership or persisted rows stale.

The watcher is enabled before its startup reconciliation scan. `FileChangeBatcher.TryDrainImmediately` closes the buffered startup generation without waiting for the normal debounce interval; those paths are applied before the `watching` event, while events arriving after the snapshot remain queued as normal live updates. `watching` is emitted only when every startup reconciliation sub-run succeeds; a failed generation returns its non-zero exit instead of discarding the batch and declaring readiness. This generation boundary prevents both the initial-scan subscription gap and unbounded readiness delay on a continuously changing workspace.

## AI integration

For the AI agent search-rule template, see [AI Integration](USER_GUIDE.md#ai-integration) in the User Guide.

### Output format

Bounded projection fields are defined only in `ProjectionFieldRegistry`.
Runtime validation, `--fields list` discovery, compact defaults, alias
resolution, and command help all consume that registry. Field names are
case-sensitive; unknown values use the versioned `E010_USAGE_ERROR` command
error when JSON is requested, and discovery runs before query or database
access.

| Output mode | Contract |
|---|---|
| Human-readable default | Query commands (`search`, `definition`, `references`, `callers`, `callees`, `symbols`, `files`, `excerpt`, `map`, `inspect`, `outline`, `suggestions`) default to **human-readable output**. |
| `--json` | Emits JSON lines output, one JSON object per line, designed for easy parsing by AI agents. |
| `definition --json` miss | A default-format definition lookup that finds no matching symbol emits the shared versioned `E018_QUERY_NOT_FOUND` command-error object and exits `2`, with or without `--body`; it never succeeds with empty stdout. Bounded-envelope controls move the object to `metadata.error` and keep `results` empty instead of projecting it as a location row. The object is preflighted against `--max-json-bytes`; an impossible cap returns a usage error without oversized stdout. `--count` still returns its structured zero-count object, and explicit location formats retain their existing format-specific empty-result output. |
| Raw discovery JSON shape | `symbols` and `files` build each result row through the same DTO path for array, NDJSON, and envelope output. `symbols --json=array` therefore preserves `exact_index_available` just like NDJSON. Every cardinality and `--max-json-bytes` path keeps the selected flat shape: zero-result NDJSON is an empty stream, `--json=array` is always an array, and byte-capped output omits whole trailing rows without changing the top-level type. Bounded projections keep rows in `results`, pagination facts in `metadata`, and exact-query readiness in `metadata.response_context`; they never reuse a result row as response context. Use `--format compact` or `--json-envelope` when truncation and freshness metadata must accompany the results. |
| Generated-code filtering metadata | DB-backed discovery `query_context` always reports `include_generated`, `generated_code_policy`, and `generated_file_filter_available`. The `files --count --json` and every JSON `map` summary (including `issue-drafts`) also report `generated_file_count_excluded` and `generated_file_count_excluded_authoritative`. The excluded count is `0` when generated files are included. For a legacy DB without `files.generated` when filtering is requested, the policy is `unavailable`, the count is `null`, and the authoritative/available flags are `false` rather than claiming that an unavailable filter ran; explicit `--include-generated` remains `include` with an authoritative excluded count of `0`. Byte-capped and uncapped raw discovery arrays retain SQLite trust diagnostics even when the query returns no result rows. |
| Map scope, depth, and freshness | `map --depth <n>` applies path, language, test, generated-code, and exclusion filters before aggregating modules by the requested prefix depth. Scoped map output excludes the workspace-global decomposition plan. Workspace HEAD metadata is read in one query from the same SQLite snapshot as the map and remains explicit under `head_freshness`: `scope=workspace`, `indexed_head_source=latest_index` for the current successful index stamp (or `legacy_full_scan` only when it is the fallback), and `legacy_full_scan_head` for the separately labeled compatibility stamp. `issue-drafts` evaluates every scoped file for its thresholds, so `candidate_source=evaluated_scoped_candidates`, candidate counts, group totals, omitted counts, and `truncation.issue_draft_candidates` are candidate-based even though candidate details remain bounded; `truncation.largest_files` is a labeled compatibility alias only. |
| `test-extractor` JSON | Machine-readable `test-extractor` success uses a versioned `{"api_version":"1","symbols":[...]}` envelope; the nested symbol objects retain their established property names. `--json` failures use the shared versioned command-error contract. |
| Compact location envelope | CLI `--format compact` location output uses a versioned envelope with `api_version`, returned `count`, conservative limit-based `truncated` / `truncation` metadata, applied `query_context`, and lightweight `results` rows. |
| Grouped search totals | `search --format grouped` derives `total_matches` / `matched_count`, `total_groups`, and `total_files` from the complete bounded query rather than the displayed page. `grouped_match_count` counts rows supplied to returned groups, `emitted_match_count` counts rows left after per-file grouping limits, and `omitted_match_count`, `truncated`, `has_more`, and `continuation_action` describe incomplete output. |
| Bounded high-volume responses | `search`, `definition`, `find`, `status`, `hotspots`, `references`, `callers`, `callees`, `symbols`, `files`, `languages`, `impact`, and `map` accept shared bounded-response controls where their schema exposes them. Newly emitted opaque `--cursor <response:v2:...>` values bind the offset to the command/query/filter selection and index generation; legacy `response:v1:<offset>:<fingerprint>` cursors remain accepted for transition. Reuse with changed selection or generation fails with restart-required guidance. `search --format compact`, `symbols --format compact`, and `files --format compact` auto-select the bounded contract, while `search --json=array --json-envelope` provides the opt-in array envelope and `languages --json` selects it when paging or `--max-json-bytes` is requested. Existing compact roots and location rows remain compatible while adding shared metadata. Metadata reports `returned_count`, authoritative `total_count` where available, `omitted_count`, `remaining_count`, `cursor_offset`, `page_limit`, `has_more`, `next_cursor`, `result_stable_at`, `pagination_window_limit`, and `pagination_window_exhausted`. The safety window is 10,000 rows; exhaustion suppresses `next_cursor` rather than returning a cursor that the next request would reject. Pageable commands pass the cursor offset into their database/scan layer instead of serializing an `offset + limit` prefix. `find --all` partial scans encode the next path/line in the opaque cursor so replay continues after the last scanned line. `hotspots` and `impact` page their active primary nested collection as `results`, identify it with `metadata.primary_collection`, and retain scalar/container evidence in `metadata.response_context`; dotted fields such as `callers.path,callers.depth` select that collection and project its rows. The final newline is included in `--max-json-bytes`, and trailing whole rows are removed until the complete envelope fits. `definition` remains metadata-only by default; explicit `--body` content is retained for `body`, `body_content`, or `all`, and suppressed when the projection excludes it. `map --sections` remains its section-level projection, while dotted bounded fields page a selected array section with section-specific totals and scalar projections skip unused ranked arrays. |
| Bounded response edge cases | `impact` applies the cursor offset only to the selected nested collection so definition pages do not repeat or alter caller/fallback mode. Plain `map --compact` preserves its established section arrays and truncation payload; a collection projection is rejected when `--summary-only` or an excluding `--sections` filter would remove it. Explicit definition body fields override compact defaults. Profile and verbose records are moved into `metadata.stream_control_records`, and parser/capture failures emit an error envelope only when it fits the active hard byte cap. |
| `--count --json` envelope | Count-only JSON for `search`, `definition`, `references`, `callers`, `callees`, `symbols`, `files`, `find`, `impact`, and `unused` is a single automation-oriented object. It always includes `count`, applied `query_context`, freshness metadata (`indexed_file_count`, `indexed_at`, `freshness_available`), and trust flags `degraded` / `authoritative_count`; commands with matched-file totals also include `files` and the older `file_count` compatibility alias. `file_count` carries the same value as `files`, remains for compatibility, and is not scheduled for removal before the next major release. `unused --count --json` also includes `returned_bucket_counts`, `returned_contract_domain_counts`, and `summary.by_bucket` / `summary.by_confidence` / `summary.by_contract_domain`. `authoritative_count=false` means a readiness or graph/exact trust signal made the count non-authoritative, while the freshness fields describe the indexed snapshot used for the count. |
| Search row selection | Row-producing plain-search and recipe paths share `ApplySearchOutputSelection`: `--first-per-file` and fixed-seed deterministic `--sample` run before the effective per-query / remaining total limit. Sample fetch envelopes are sized from at least the requested sample target. Aggregate/compact query DTOs, plain compact roots, run summaries, issue-draft source DTOs, NDJSON terminals, and bounded array-envelope stream terminals expose `source_total`, `selected_total`, `returned`, `selector_omitted_count`, and `limit_omitted_count`; `source_total_authoritative` / `source_total_lower_bound` distinguish complete populations from bounded observations. Guard filters, origin/facet post-filters, exhausted candidate windows, and recipe file-reject post-filters force lower-bound authority. Their ordered `selectors` entries preserve each stage's input/output/omission counts plus sample size, mode, and seed, while nullable `selection_reason` / `selection_omitted_count` remain compatibility summaries. Bounded plain-search selection is computed once and its selected page is reused by compact/envelope serialization. Search `query_context.row_selectors` records the applied selector configuration. Selection-only omission updates matched/omitted lower bounds without setting `truncated`, `has_more`, or `next_cursor`. When a later limit truncates selected rows, limit truncation remains visible but `next_cursor` is suppressed because raw database cursors cannot preserve selector state; incoming `--cursor` values are rejected with either selector for the same reason. Generated compact and issue-draft replay commands retain the selector. Count, aggregation, named-query, recipe-list, results-only, metadata-free array, unsupported formatted, and summary-only compact shapes reject `--first-per-file` / `--sample`, while every recipe shape rejects grouped-only `--per-file-limit`. |
| Search selection edge cases | Issue-draft roots independently retain per-query `selection_accounting`, including zero-draft and exhausted-total-limit queries. Byte-bounded compact and array envelopes rewrite `returned` to the emitted row count while preserving logical `limit_omitted_count`; hard-cap omissions remain separate in `metadata.byte_limit_omitted_count`. |
| Ad-hoc search SARIF | `search <query> --format sarif` stores completion metadata on each SARIF run. The run and its single `queries[]` summary report `source_result_count`, `source_result_count_authoritative`, emitted `result_count`, the applied `limit_per_query` / `result_limit`, conservative `minimum_omitted_result_count`, and `truncated` state. Source and emitted counts use the final SARIF result/location unit, including exact-search occurrence expansion. Guarded searches retain their bounded candidate budget instead of failing during a completion recount; their source count is an explicitly non-authoritative lower bound and their truncation state remains conservative. Facet-filtered exact searches use an exhaustive source count rather than the display candidate window. Ad-hoc search does not expose a continuation cursor, so `cursoring_available` is `false` and `next_cursor` is null; a shell-quoted `replay_command` preserves option-like queries and active search controls. The completion vocabulary intentionally matches recipe SARIF, and empty runs carry the same fields with zero counts. |
| Ad-hoc issue-draft selection | `search --format issue-drafts` reads the complete filtered ad-hoc population, then applies `--first-per-file`, deterministic `--sample`, and `min(--limit, --total-limit)` in that order. Guarded searches retain their finite candidate inspection contract: `source_total_count` is omitted, `source_minimum_count` reports the observed lower bound, `source_total_count_authoritative=false`, `source_fetch_limit` reports the bounded fetch, and `truncated=true` preserves incomplete-population state. Existing `result_count`, `result_limit`, `omitted_count`, and `truncated` fields describe the returned selection accurately; additive `source_total_count`, `returned_count`, `limit_per_query`, `total_limit`, `first_per_file`, and `sample` fields make the applied contract auditable. Replay commands are serialized from normalized parsed options, use POSIX-safe single-quote escaping, and retain raw/exact/prefix modes, path/language/facet/guard filters, selection controls, evidence formatting, duplicate preflight, and issue hints. |
| Recipe SARIF | `search --recipe <name> --format sarif` emits one result per bounded recipe result. Rule IDs use `recipe/query`; standard `fingerprints.cdidx/v1` values are derived from the normalized source location; result properties preserve recipe/query identity, severity, confidence, and per-query truncation; run properties preserve scope, applied result limits, aggregate counts, and conservative omitted-result metadata. Bound SARIF with `--limit` / `--total-limit`; row selectors such as `--sample`, `--first-per-file`, and `--per-file-limit` are rejected instead of being silently ignored. Recipe severity maps `critical` / `high` to `error`, `medium` to `warning`, and `low` / `info` to `note`. |
| Recipe classifier output | Recipe run JSON may add `audit_classifications` to individual `CompactSearchResult` rows when a recipe classifier can classify the hit, and query/count payloads may add `classifier_counts` when classified rows are present. These fields are additive; use them to separate triage domains such as DTO/result-wrapper `.Result` properties versus Task/ValueTask blocking waits without changing the raw search query. |
| NDJSON terminal records | Default NDJSON for `search`, `symbols`, and `files` appends one final `terminal_record` after result rows; search also emits it for zero-result responses, while raw `symbols` and `files` keep zero-result NDJSON empty. Recipe/audit search row streams share the same writer. Terminals report returned and observed total counts, `total_count_authoritative` / `total_count_lower_bound`, selection or interruption reason, applied limits, omitted rows, and recovery guidance. `--max-json-bytes` covers the complete stdout stream, including newlines and this terminal record; when additive selector-accounting fields prevent the terminal from fitting, the writer omits those optional fields before declaring the terminal impossible. A cap that still cannot fit the terminal fails before stdout. Capped output rejects `--profile`, `--verbose`, and `--json-envelope`. Byte-cap partial output exits with `CommandExitCodes.PartialResult` (`11`) unless `--allow-partial` explicitly opts into exit `0`. `--results-only` is the explicit terminal-record opt-out for these NDJSON row streams and is rejected with array, compact, summary, or count output. |
| `outline` / `unused` cursor binding | `outline --json` accepts `--kind <kind[,kind]>`, `--limit` / `--top`, opaque `--cursor <next_cursor>`, and `--outline-fields <csv>` for bounded machine output. Controlled outline responses keep the normal envelope and add `total_symbol_count`, `returned_symbol_count`, `cursor_offset`, `next_cursor`, `has_more`, and `result_stable_at`, plus `kind_filter` and `selected_fields` when active. `outline` and `unused` cursors bind their offset to the normalized path/scope, filters, ordering, and index generation; reuse after changing those inputs or refreshing the index fails with explicit restart-required guidance. Legacy `outline:<offset>` / `unused:<offset>` inputs remain accepted for transition, but every newly emitted cursor is opaque and bound. |
| `hotspots --json` grouping semantics | `hotspots` and MCP `symbol_hotspots` emit `grouped_by`, `grouping_unit`, `count_kind`, `limit_applies_to`, `score_fields`, `ranking_fields`, and matching `query_context` fields. `--limit` applies to returned symbols, files, name/kind groups, or SQL statements; `--count` ignores `--limit` and reports total groups. Explicit `statement` grouping is SQL-only (`--lang sql` / `lang: "sql"`). |
| `--json-envelope` commands | Applies to `search`, `definition`, `references`, `callers`, `callees`, `symbols`, `files`, `find`, `excerpt`, `map`, `inspect`, `outline`, `status`, `validate`, `languages`, `impact`, `deps`, `unused`, and `hotspots`. |
| `--json-envelope` shape | Wraps the per-line `--json` stream into a single `{"metadata": {...}, "results": [...]}` document. Stream terminal records are excluded from `results` and preserved as `metadata.stream_terminal`, while zero-result prelude/control records are preserved as `metadata.stream_control_records`; therefore `result_count` counts result rows only. A `find --all --count` object is both the count result and terminal scan metadata, so it remains in `results` and is also copied to `metadata.stream_terminal`. `metadata` also carries `api_version`, `command`, `cdidx_version`, `elapsed_ms`, `db_path`, `exit_code`, and, when applicable, `query_normalized` and `indexed_at_head_sha`. `indexed_at_head_sha` maps to the persisted latest-successful `indexed_head_sha` used by status and MCP output after full, `--files`, `--commits`, and `--changed-between` refreshes; failed/rolled-back refreshes do not advance it, and legacy DBs without that key fall back to full-scan-only `indexed_head_commit`. The bounded high-volume commands above support `--json-envelope --max-json-bytes` by measuring the final serialized document; other envelope/byte-cap combinations remain rejected. |
| Indexed-HEAD envelope snapshot | A present `indexed_head_sha` row is authoritative even when its value is NULL, so current-format databases with an unavailable Git HEAD omit `indexed_at_head_sha` instead of falling back to the legacy baseline. Regular and bounded envelopes capture the resolved value in `ResponseSnapshot`, verify the generation again after the inner query, and reuse the snapshot while serializing; a generation change returns restart guidance instead of mismatched rows. Bounded metadata, projected rows, `result_stable_at`, and cursors therefore remain on one database generation. |
| Envelope migration | `--json-envelope` implies `--json`, so callers do not need to pass both. The default output remains the legacy NDJSON / array form for one release; the envelope will become the default in the next major release, when the flat form becomes opt-in via `--json-flat`. |
| `find --all` scan summary | `find` requires either repeatable `--path <glob>` filters or explicit `--all`. `--all` cannot be combined with `--path`; safe case-insensitive ASCII literals of at least three characters use the external-content trigram FTS index to select files, then re-run the established line matcher over every selected file. Regex, `--exact` normalization, short/non-ASCII literals, legacy databases without the trigram table, missing trigram synchronization triggers, and active FTS bulk-load rebuilds use the bounded line-scan fallback, preventing false negatives from unsupported tokenization, normalization, or stale index state. Writable initialization rebuilds an existing trigram table when any synchronization trigger is missing, repairing artifacts retained across older-writer rebuilds. Both JSON and human summaries expose `search_strategy` plus optional `search_fallback_reason`; `candidate_files` remains the total scoped file count, while `files_scanned` / `lines_scanned` count post-index verification work. Default JSON rows end with a terminal record carrying `scan_complete`, `authoritative_rows`, returned/scanned counts, active caps, truncation/continuation fields, and recovery guidance. Count JSON carries the same terminal scan state in its single object and uses `authoritative_count`. Row formats that cannot represent the terminal metadata (JSON array, compact, CSV/TSV, LSP, quickfix, and SARIF) are rejected with `--all`; conflicting JSON/text flags are rejected or normalized to NDJSON independent of option order. Candidate-file or line-scan truncation exits with partial-result code `11` unless `--allow-partial` opts into `0`; ordinary result-limit early stops remain successful but set `scan_complete=false` and `result_limit_reached=true`. Human stderr summaries include the strategy, active caps, scan/authority state, continuation action, and recovery guidance and use the same partial exit semantics. |

#### JSON output API version contract

| Contract area | Rule |
|---|---|
| DTOs carrying `api_version` | Every top-level CLI/MCP JSON success or failure DTO carries an `api_version` string stamped from `JsonOutputContract.ApiVersion`. Primary query DTOs include `StatusResult`, `RepoMapResult`, `SymbolAnalysisResult`, `ImpactAnalysisResult`, `OutlineResult`, `FileExcerptResult`, `CompactSearchResult`, `SymbolResult`, `DefinitionResult`, `UnusedSymbolResult`, `ReferenceResult`, `CallerResult`, `CalleeResult`, `FileResult`, and `FileFindResult`. Audited utility DTOs implement the shared `IVersionedJsonResult` contract, including command errors, update/upgrade checks, database restore/backup maintenance, diff results, hooks, workspace/config, language/version inventory, test-extractor results, and index run/watch results. |
| Envelope metadata | The same `api_version` value is mirrored on the `--json-envelope` `metadata` block. |
| Binary version separation | `api_version` describes the JSON output contract, not the cdidx binary version. The binary version remains surfaced through `version.json` and `cdidx --version`. |
| When to bump | Bump `JsonOutputContract.ApiVersion` only on **breaking** shape changes: renames, removals, or type changes of an existing field. Additive changes such as new optional fields, new readiness flags, or new enum values keep the version stable so older consumers continue to parse the payload. Strict downstream consumers should pin against the major value and degrade gracefully when it changes. Issue #1555. |

`status --json` trust contract:

| Field group | Fields |
|---|---|
| Readiness and graph trust | `fold_ready`, `fold_ready_reason`, `graph_table_available`, `graph_data_current`, `reference_extraction_limits`, `reference_graph_complete`, `reference_graph_incomplete_reasons`, `reference_extraction_cap_hits`, `index_complete`, `index_incomplete_reasons`, `issues_table_available`, `file_issues_data_current`, `migration_in_progress`, `sql_graph_contract_ready`, `sql_graph_contract_degraded_reason`, `hotspot_family_ready`, `hotspot_family_degraded_reason`, `language_readiness`, `csharp_symbol_name_ready`, `csharp_metadata_target_ready`, `csharp_metadata_target_degraded_reason`. |
| Workspace and HEAD freshness | `indexed_head_commit`, `worktree_head_changed`, `indexed_head_sha`, `indexed_head_branch`, `indexed_head_timestamp`, `commits_ahead_of_indexed_head`, `head_freshness`. |
| Version and forward compatibility | `index_writer_version`, `index_newer_than_reader`, `index_newer_than_reader_reason`. |
| Unknown-extension and runtime diagnostics | `unknown_extension_file_count`, `unknown_extension_files`, `unknown_extension_files_truncated`, `unknown_extension_file_path_limit`, `unknown_extension_extension_counts`, `unknown_extension_category_counts`, `unknown_extension_groups`, `extractors`, `hooks`, `hook_diagnostics`, `trust_overrides`, `path_case_sensitive`, `data_dir_mode`, `mac_profile`, `mac_profile_diagnostics`, `stale_after_seconds`, `index_age_seconds`, `query_context.check_mode`, `query_context.stale_after_seconds`, `last_index_run.reference_extraction_cap_hits`, `last_failed_or_partial_index_run`, `last_failed_or_partial_index_run.progress_persisted`, `last_failed_or_partial_index_run.recovery_hint`, `last_failed_or_partial_index_run.file_errors`. |
| Database maintenance | `db_size_bytes`, `wal_size_bytes`, `db_pragma_settings` (`journal_mode`, `synchronous`, `wal_autocheckpoint`, `busy_timeout_ms`, `page_count`, `freelist_count`, `page_size`, `auto_vacuum`), `prepared_command_cache` (`count`, `capacity`, `hit_count`, `miss_count`, `eviction_count`), `maintenance_guidance`. |
| Remediation fields | `degraded_root_cause`, `degraded_reason`, `recommended_action`, `alternative_action`, `readiness_degradations`, `repair_commands`. |
| MCP-only session diagnostics | `mcp_session`, `mcp_session.metrics`, `mcp_session.audit_log`, `mcp.rate_limit.bucket_limit`, and `mcp.rate_limit.bucket_limit_rejection_count`. `mcp_session` is session-scoped diagnostics rather than persisted DB state. It contains `log_level`, bounded `roots`, optional `client_info`, bounded optional `client_capabilities`, an always-present `metrics` object, and `audit_log` when audit emission is enabled. When advertised roots are capped, `roots_truncated`, `root_count`, `root_limit`, and `root_uri_length_limit` describe the truncation. When client capabilities are capped, `client_capabilities_truncated`, `client_capabilities_truncation_reason`, `client_capabilities_serialized_bytes`, `client_capabilities_byte_limit`, and `client_capabilities_depth_limit` describe the retained diagnostic subset. `mcp_session.metrics` is `{"enabled":false}` when unconfigured. An enabled metrics sink contains `enabled`, `path`, `max_bytes`, `bytes_written`, `disposed`, `degraded`, `queue_capacity`, `queue_depth`, `queued_event_count`, `written_event_count`, `dropped_event_count`, `queue_full_drop_count`, `serialization_failure_count`, `write_failure_count`, `rotation_failure_count`, `batch_flush_count`, `consecutive_failure_count`, and `recovery_count`, plus optional `next_retry_at`, `last_recovery_at`, and `last_failure`. MCP ping always mirrors the metrics object as `metrics`; metrics degradation is intentionally excluded from its top-level liveness result. The audit status fields and their health semantics are defined in [MCP audit log emission](#mcp-audit-log-emission). `mcp.rate_limit.bucket_limit` is the configured process-local cap across normalized `(partition, caller)` buckets: every direct call uses one fixed caller-wide coarse partition, canonical known tools additionally use secondary per-tool partitions, and unknown `batch_query` slots share one fixed invalid-slot partition per caller. `mcp.rate_limit.bucket_limit_rejection_count` counts calls denied because creating a new bucket would exceed that cap. |
| Documentation sync | Keep this list synchronized with `README.md` and `AGENT_GUIDE.md`; `DocumentationStatusContractTests` fails when any required field is missing from one of those docs. |

`head_freshness` is a compact summary for machine consumers. `state=fresh`
requires a successful complete `status --check` workspace comparison,
`state=fresh_but_incomplete` separates matching-workspace freshness from failed-file coverage, and `state=head_current`
only proves the runtime HEAD matched the `indexed_head` selected by
`indexed_head_source`, and
`indexed_head_source` tells consumers whether `indexed_head` came from the latest
index stamp (`indexed_head_sha`) or the legacy full-scan-only stamp
(`indexed_head_commit`).

Per-file full-scan or scoped-update failures commit their own successful file transactions and restore
the graph-presence bit so persisted edges remain queryable. They do not restore issue,
SQL graph, hotspot, C#, or fold currentness stamps. Instead they persist
`index_completeness=incomplete`, bounded structured file errors, and recovery metadata;
the next complete run clears that state. Full-scan JSON reports extracted and persisted
counts separately, uses committed database counts for primary totals, and returns exit
`11` unless `--allow-partial` explicitly selects exit `0`.

Runtime diagnostic subcontracts:

| Surface | Contract |
|---|---|
| `hotspot_family_degraded_reason` stable codes | `hotspot_family_support_not_indexed`, `hotspot_family_metadata_stale`, `hotspot_family_disabled_at_index_time`, `partial_family_key_population`, and `hotspot_family_marker_fingerprint_incomplete`. |
| `partial_family_key_population` | Some indexed symbols still lack family keys and need a rebuild/restamp. |
| `hotspot_family_marker_fingerprint_incomplete` | Marker fingerprint traversal hit safety caps during the last index run; narrow or ignore generated/vendor marker trees before rebuilding. |
| `extractors` | Reports runtime extractor plugin and pattern-config health, including loaded plugin assembly and pattern counts, symbol/reference extractor counts, the zero parent load-context count and isolated-worker lifecycle, skipped file counts, and a bounded diagnostic list for incompatible or malformed files. Diagnostic paths and messages are sanitized before output. |
| `hooks[]` / `hook_diagnostics[]` | Includes worker-discovered hook manifests with stable assembly-qualified `id`, `callback_budget_ms`, and worker-only load-context lifecycle. `hook_diagnostics[]` reports sanitized bounded-discovery diagnostics, including `hook_id` when a concrete hook is known. Index runs discard timed-out callback mutations because hooks run on a scratch copy before their results are applied. |
| `trust_overrides[]` | Reports accepted extension trust-boundary environment overrides, including `kind`, `environment_variable`, sanitized `value`, optional sanitized `path`, and `message`. Current entries cover accepted workspace plugin discovery via `CDIDX_TRUST_WORKSPACE_PLUGINS` and accepted hook directory overrides via `CDIDX_HOOKS_DIR`. |

`references` already prefixes each human-readable row with `reference_kind`, and `callers` does the same for its grouped caller rows. When one grouped container mixes kinds (for example `call` and `subscribe` on the same event member), the human-readable label joins the distinct kinds with `+` (for example `call+subscribe`) instead of collapsing to a single preferred label, and the reference-kind column widens dynamically to fit the longest label in the batch so mixed rows do not overrun the neighbouring column. JSON output for `callers` and `callees` keeps the scalar `reference_kind` for back-compat (it reports the preferred summary kind `instantiate` > `subscribe` > `MIN(call)`) and adds a sorted `reference_kinds` array plus a `has_mixed_reference_kinds` bool so consumers can detect mixed containers without trusting a single collapsed label. This lets terminal users distinguish `call` / `instantiate` / `subscribe` / mixed without re-running the command with `--json` and lets AI clients answer mixed-kind questions without chasing a second `--exact` query.

`ReferenceResult` includes `is_self_reference` and `is_mutual_recursion`; `CallerResult` includes `has_self_reference` and `has_mutual_recursion`. These fields identify self-recursive edges and direct two-symbol cycles without removing valid recursive calls from default graph results. Reader APIs that need a non-recursive view can opt into self-reference exclusion.

MCP tool calls return structured JSON in `structuredContent` plus a short summary in `content`, so clients can consume typed data directly. Text content blocks include `mimeType`: `application/json` when a structured payload is present and `text/plain` otherwise. Tool input schemas also carry common JSON Schema constraints (`minimum` / `maximum` for limits and line counts, `maxLength` for free text, `pattern` for workspace-relative path filters, and `enum` for common kind values) so MCP-aware clients can reject invalid requests before dispatch.

Exact-match flag compatibility is documented in [USER_GUIDE.md](USER_GUIDE.md#flag-compatibility-and-migrations). Keep MCP schemas aligned with that table: `search.exact` is the legacy alias for `exactSubstring`, while name-based tools use `exact` as the legacy alias for `exactName`. Do not add new exact-match aliases without updating the compatibility table, CLI help, MCP descriptions, and changelog fragment together.

`search`, `definition`, `references`, `callers`, `callees`, `symbols`, and `files` also share path-aware narrowing via `--path`, repeatable `--exclude-path`, and `--exclude-tests`. The read layer ranks source files ahead of tests and docs, and `search` further boosts exact symbol-name and path matches so AI clients are more likely to land on implementation files first.

`search --json` and MCP `search` project full chunks into compact match-centered snippets with `chunk_start_line`, `chunk_end_line`, `snippet_start_line`, `snippet_end_line`, `snippet`, `match_lines`, `highlights`, `context_before`, `context_after`, `truncated_line_count`, `dropped_match_line_count`, and `truncation_context`. Compact CLI rows and MCP search results also echo effective output options with `snippet_lines` / `snippetLines`, `max_line_width` / `maxLineWidth`, `exact`, `raw_fts` / `rawFts`, `literal_highlights_available` / `literalHighlightsAvailable`, and optional `literal_highlight_warning` / `literalHighlightWarning`. `--snippet-lines` caps the snippet length up front (default: 8, max: 20), and `--max-line-width` (CLI) / `maxLineWidth` (MCP) clamps each individual snippet line around the first match token via the shared `LineWidthFormatter.ClampLine` contract used by `find` / `references` / `excerpt` / `inspect` (default: 512, max: 4096) so a single match inside a minified / transpiled / generated single-line file no longer returns hundreds of KB per hit. Clamped lines surface `...(+N)...` markers inside the snippet and expose `truncation_context.char_counts`, `truncation_context.total_chars`, `highlights[].truncated`, `highlights[].original_line_length`, and `highlights[].truncated_char_counts` so AI clients can detect clamping and quantify omitted characters. `highlights[].terms` remains a distinct term list for compatibility; `highlights[].term_occurrences` records every matched occurrence with `term`, 1-based `line`, 1-based `column`, `length`, plus `visible`, `visible_column`, and `visible_length` for the portion still present in the returned snippet text after line clamping. Exact substring search also adds `highlights[].literal_terms` and `highlights[].literal_term_occurrences` (camelCase in MCP) so clients can render only the requested literal phrase while preserving the broader diagnostic token list; raw FTS rows set `literal_highlight_warning` / `literalHighlightWarning` to `literal_highlights_unavailable_raw_fts` because FTS syntax can no longer be mapped to one literal phrase. Non-exact punctuation-heavy code-phrase searches add `exact_substring_hint` to CLI JSON compact results and `recovery_hint` to MCP `search` responses so clients can retry with exact substring semantics when FTS tokenization is likely to hide punctuation. `focus_mode`, `focus_line`, `focus_column`, and `focus_reason` describe the match window selected for the snippet, while `dropped_match_line_count` and optional `next_match` report match lines omitted because they fell outside that selected snippet window.

Default `quality` snippet focus treats a single query that begins with a letter or underscore and otherwise contains only letters, digits, and underscores as identifier-shaped. When a result mixes matching code with earlier comments or strings, the first `code`-origin occurrence supplies both the preferred snippet line and column, including when a literal and executable occurrence share one long line; automatic occurrence focus scans the complete valid chunk even when its text exceeds the bounded line-only preferred-focus probe. Space-delimited phrase queries and explicit `leftmost` / `proximity` focus modes retain their existing selection. Explicit origin filters refocus on the first retained facet's line and column so focus metadata, visibility, and line clamping describe the filtered result. The selected occurrence remains auditable through the existing focus and origin metadata, `dropped_match_line_count` is computed from the final returned window, and `next_match` continues forward from that window.

When the match line falls inside an indexed symbol range, `search --json` and MCP `search` also include optional `enclosing_symbol_name`, `enclosing_symbol_kind`, `enclosing_symbol_start_line`, `enclosing_symbol_end_line`, and `enclosing_container_name`.

`find --json` remains line-delimited for repeated matches and adds bounded match-span/truncation metadata to each row: `length` reports the 1-based `column` span length, `original_line_length` reports the source line length before any line-width clamp, and `snippet_truncation_context.line_count` / `char_counts` / `total_chars` / optional `reason` describe snippet clamping. `reason` is `line_width` when `--max-line-width` elides one or more snippet lines.

`excerpt --json` includes `semantic_tokens`, a lightweight range list with 1-based source start/end positions, token `type`, and `modifiers`, so IDE and LLM clients can render or post-process excerpt spans without reparsing the raw `content` string. C# excerpts and LSP `textDocument/semanticTokens/full` share the same source classifier for keywords/modifiers, namespaces and types, methods and properties, parameters, variables and fields, and declaration modifiers. Excerpt classification uses indexed-source context, applies the output token budget only after filtering to visible source lines, and falls back to classifying the visible content when a bounded source scan cannot reach it; narrow or late-file excerpts therefore retain context when available without becoming empty because earlier tokens exhausted the output budget. Excerpt range mapping and LSP delta encoding only translate coordinates and do not choose semantic kinds. `semantic_token_coordinate_space` is `source`; when `--max-line-width` clamps returned content, `content_line_spans` maps each returned content line and visible content-column span back to the matching source line and source-column span, while clamp markers remain unmapped and are not emitted as semantic tokens. Excerpt rows also expose `requested_start_line`, `requested_end_line`, `effective_start_line`, `effective_end_line`, `content_truncation_reasons`, and optional `content_recovery` so clients can tell when `--max-line-width` caused `line_width_cap` and replay the omitted text. Body-bearing JSON rows use matching `body_requested_*`, `body_effective_*`, and `body_content_truncation_reasons` fields; body reasons include `body_line_cap` for snippet/body line caps and `body_byte_cap` for definition body byte caps.

`content_recovery` and `body_content_recovery` use `argv` as their primary machine-readable contract. Shared CLI JSON and MCP responses redact machine-specific absolute apphost, assembly, source, and database paths by default with the structured path sanitizer, before rendering `command`; they never regex-rewrite the rendered shell string. SQLite file-URI query segments are processed independently so safe controls such as `mode=ro` remain visible while path-valued or sensitive query values are sanitized. The database option is located only after the known source-argument position, so an option-like source name such as `--db` cannot bypass DB-path redaction. Default metadata reports `paths_redacted: true`, `command_display_only: true`, and `requires_local_path_substitution: true` when any argument was replaced. Root-level paths beginning with `-` retain the supported `--` end-of-options marker, and `command_shell` (`posix-sh` or `powershell`) identifies the escaping contract. CLI `definition`, `references`, `callers`, `callees`, `excerpt`, `inspect`, and `impact` accept `--redact-paths` as the explicit default and `--show-paths` as the local-only opt-in. `--show-paths` emits the resolved apphost or `dotnet` plus running assembly, source, and database arguments, sets `paths_redacted: false` and `command_display_only: false`, and produces a safely quoted command for the declared shell. MCP remains support-safe and emits the equivalent camelCase metadata.

`status --config` follows the same policy: `db_path`, `data_dir`, and `global_tool_log_dir` are redacted by default, including path-valued and sensitive SQLite file-URI query segments. The top-level `redaction.paths_redacted` field records the path mode, while secrets remain redacted in both modes. `--show-paths` is the only local opt-in for resolved path values. `--redact-paths` makes the default explicit; either path-display flag is rejected by plain `status` without `--config`.

`inspect` and MCP `analyze_symbol` bundle the primary definition, nearby symbols from the same file, references, callers, callees, file metadata, workspace freshness/git metadata, and graph-support metadata into one response. When those bundled graph sections actually depend on SQL-backed reads, the payload also mirrors `sql_graph_contract_ready` / `sql_graph_contract_degraded_reason` (plus the existing camelCase aliases on MCP responses); mixed-language bundles that only return C# / JS / etc. graph rows omit the SQL trust signal entirely. This is intended for symbol-oriented AI workflows that would otherwise need several back-to-back calls. Call graph sections remain language-aware: for unsupported languages, clients can now distinguish "unsupported" from "no hits" via `graphSupported` / `graphSupportReason`, and should prefer `search` instead of assuming graph data will exist.

The direct MCP graph tools (`references`, `callers`, `callees`) also emit `graphLanguage`, `graphSupported`, and `graphSupportReason` when a language filter is supplied, so unsupported-language queries do not look identical to zero-hit supported-language queries. CLI/MCP graph and dependency payloads mirror `sql_graph_contract_ready` / `sql_graph_contract_degraded_reason` only when SQL-backed rows actually contribute, instead of degrading unrelated non-SQL hits just because the same filtered workspace also contains stale SQL files.

```json
{"path":"src/auth.py","lang":"python","chunk_start_line":1,"chunk_end_line":80,"snippet_start_line":1,"snippet_end_line":6,"snippet":"def authenticate(user):\n    token = issue_token(user)\n    return token","match_lines":[2],"highlights":[{"line":2,"text":"    token = issue_token(user)","terms":["token"]}],"context_before":1,"context_after":3,"score":-1.5}
```

## Release Workflow

The version string has a single source of truth: `version.json` at the repository root.
Official GitHub Release, NuGet publishing, and Homebrew tap update jobs are
restricted to the canonical `Widthdom/CodeIndex` repository. Do not reuse the
official release workflow, NuGet package ID, Homebrew tap/formula, or
cdidx/CodeIndex branding for derivative distributions without a separate
written agreement.

### Version flow

1. **Build time.** `src/CodeIndex/CodeIndex.csproj` reads `version.json` and sets `<Version>`, so the NuGet package and self-contained binaries are stamped automatically.
2. **Runtime.** The same project file copies `version.json` next to the published binary. `ConsoleUi.LoadVersion()` reads it from `AppContext.BaseDirectory`, which keeps `cdidx --version`, MCP `serverInfo.version`, and `status --json` aligned.
3. **Install time.** `install.sh` and the generated Homebrew formula place `version.json` and the native SQLite library beside `cdidx`. If `version.json` is missing, `cdidx --version` falls back to `v0.0.0`; if the native SQLite library is missing, SQLite-touching commands fail before indexing starts.

There are no version constants in C#. Outside `version.json`, the only expected version strings are release headings and compare links in `CHANGELOG.md`.

### Maintainer checklist

The version numbers below use `1.9.0` only as an example. Replace them with the version you are actually releasing.

0. **Triage every unmerged branch and open PR before bumping the version.**
   Run `git fetch --all --prune`, then list all unmerged branches with `git branch -a --no-merged main` and all open PRs with `gh pr list --state open --limit 1000`. Do not pre-filter by branch name. For each entry, either merge it before release or explicitly note in the release PR description why it is deferred.
1. Run the release changelog tool from the latest release branch:
   `dotnet run --project tools/CodeIndex.Changelog -- prepare --version 1.9.0 --date YYYY-MM-DD`.
   The tool validates `changelog.d/unreleased/`, aggregates bilingual fragments into `CHANGELOG.md`, preserves any legacy direct `[Unreleased]` content, resets both `[Unreleased]` sections, updates `version.json`, removes consumed fragments while keeping `.gitkeep`, and updates the compare-link footer.
2. Review the generated `CHANGELOG.md`, `version.json`, and `changelog.d/unreleased/` diff. Do not hand-edit release headings or compare links unless you are fixing a tool bug; fix fragments or the tool input and rerun `prepare` instead.
3. Run the release validation from `.codex/workflows/release-changelog.md` (`restore`, `build`, `test`, and `pack` in Release configuration).
4. Commit the generated release prep, for example `Prepare release v1.9.0`.
5. After the release PR merges, tag the merge commit `v1.9.0` and push the tag. `.github/workflows/release.yml` triggers on `v*` tags, builds the per-platform tarballs, publishes the NuGet package, and bumps the `Widthdom/homebrew-tap` `codeindex` formula with `HOMEBREW_TAP_TOKEN`.
6. After the release is published, run the one-liner installer and the Homebrew install path on a clean machine and verify `cdidx --version` prints the released version before announcing it.

If a clean install reports `cdidx v0.0.0`, treat it as a release regression: either the tarball did not bundle `version.json`, or `install.sh` did not copy it next to the binary. Use `CLOUD_BOOTSTRAP_PROMPT.md` for the clean-install smoke path.

### Legacy package handling after a licensing change

README changes on `main` do not retroactively change licenses that shipped
with older source archives, tags, branches, or NuGet package versions. For old
NuGet versions that predate the current license policy, use nuget.org's
package-management UI to:

1. Deprecate each legacy version with a clear message that it predates the
   current license and trademark policy.
2. Unlist legacy versions where reducing new discovery is appropriate.
3. Leave the replacement version as the latest listed version after verifying
   its `.nupkg` contains `LICENSE`, `COMMERCIAL_LICENSE.md`, and
   `TRADEMARKS.md`.

Unlisting does not make an exact version impossible to restore. It is a
discovery-reduction step, not a retroactive license change or deletion.

## AI Feedback Implementation

The `suggest_improvement` MCP tool allows AI agents to report gaps or errors.

### Source files

| File | Purpose |
|------|---------|
| [`src/CodeIndex/Models/SuggestionRecord.cs`](src/CodeIndex/Models/SuggestionRecord.cs) | Suggestion data model (DTO) |
| [`src/CodeIndex/Cli/SuggestionStore.cs`](src/CodeIndex/Cli/SuggestionStore.cs) | Local JSON storage with exact-hash and fuzzy description dedup |
| [`src/CodeIndex/Cli/SourceCodeDetector.cs`](src/CodeIndex/Cli/SourceCodeDetector.cs) | Heuristic source code leak prevention |
| [`src/CodeIndex/Cli/GitHubIssueReporter.cs`](src/CodeIndex/Cli/GitHubIssueReporter.cs) | GitHub Issues API client (best-effort) |
| [`src/CodeIndex/Mcp/McpToolHandlers.cs`](src/CodeIndex/Mcp/McpToolHandlers.cs) | `ExecuteSuggestImprovement` handler |
| [`src/CodeIndex/Mcp/McpToolDefinitions.cs`](src/CodeIndex/Mcp/McpToolDefinitions.cs) | Tool schema definition |
| [`src/CodeIndex/Cli/SuggestionsCommandRunner.cs`](src/CodeIndex/Cli/SuggestionsCommandRunner.cs) | Local suggestion listing, audited lifecycle transitions, bounded atomic export, issue-draft generation, and open-issue duplicate preflight |

### What is sent (when GitHub token is configured)

- Category (one of 14 fixed values: `symbol_extraction`, `reference_extraction`, `search_ranking`, `language_support`, `output_format`, `crash_report`, `unexpected_error`, `security`, `performance`, `bug`, `cleanup`, `documentation`, `feature_request`, `other`)
- Language name (e.g. `typescript`)
- Description text (natural language, validated by SourceCodeDetector)
- Context text (natural language, validated by SourceCodeDetector)
- Optional repository-relative evidence paths supplied by the caller. These are path strings only; file contents are never read for the payload.
- cdidx version string
- Attribution metadata: `created_by_agent`, `session_id`, `client_version`, `mcp_client_name`, `mcp_client_version`, and optional `tool_invocation_context`
- Immutable suggestion ID and the SHA256 revision of the submitted editable content

### Suggestion identity and revisions

`SuggestionRecord.Id` is an opaque immutable public identity used by CLI resolution, mutation, export, MCP responses, and GitHub retry idempotency. New IDs are generated independently of suggestion content. `RevisionHash` is the SHA256 digest of every editable field and changes whenever that content changes; updates and deletes compare the revision read by the caller with the revision under the store lock before mutating a draft. Exact duplicate detection uses a separate normalized content hash. A legacy record containing only `hash` migrates in memory by preserving that value as `Id`, computing `RevisionHash`, and continuing to persist `hash` as an alias of the stable ID. Consequently, an ID copied before migration or an edit remains valid, while deduplication follows current normalized content without reusing content-derived IDs.

Suggestion sidecars use `DataDirectorySecurity.ResolveSensitiveSidecarDirectoryForDatabase`. A database in a private directory retains colocated JSON/archive/lock files. On POSIX, a database whose direct parent is group- or other-writable instead uses a path-derived scope below `ResolveSensitiveTempFallbackDirectory`; `CreateSensitiveDirectory` enforces `0700`, and sensitive file writers enforce `0600`. CLI filesystem failures are caught at the suggestion-store boundary and emitted as `E021_SUGGESTION_STORE_UNAVAILABLE` with `FileSystemBoundary.ClassifyProbeFailure` in `category`. The MCP path uses `permission_denied` plus `error_code` and `filesystem_category` in structured content rather than leaking an unhandled exception.

Before suggestion text is persisted, typed redaction replaces named credentials, AWS access keys, bearer values, known structured credential formats (including GitHub, Stripe, GitLab, Slack, and OpenAI prefixes), and opaque mixed-character tokens. The high-entropy fallback is identifier-aware: structured PascalCase and snake_case code/test identifiers, including embedded numeric components, leading-underscore identifiers, and slash/hyphen recipe IDs with multiple word boundaries remain available as reproducibility evidence. Single-block or alternating-case opaque tokens and known token formats remain redacted as `[REDACTED:high_entropy_token]`.

### Deduplication

`SuggestionStore` first checks a normalized SHA256 content hash, then compares the candidate against the most recent suggestions in the same category and language using normalized-token Jaccard similarity. The default fuzzy threshold is `0.85`; `cdidx mcp --suggestion-dedup-threshold`, `CDIDX_SUGGESTION_DEDUP_THRESHOLD`, or `.cdidxrc.json` `suggestion_dedup_threshold` can override it with a value from `0` to `1`. Fuzzy matches are returned as duplicates before GitHub submission and log the matched immutable ID plus score to stderr for auditability.

Local suggestion retention is bounded by `CDIDX_SUGGESTION_MAX_AGE_DAYS` and `CDIDX_SUGGESTION_MAX_COUNT`, also available as `.cdidxrc.json` `suggestion_max_age_days` and `suggestion_max_count`. The built-in defaults are 365 days and 5000 records; accepted values are capped at 3650 days and 100000 records. Non-positive, non-numeric, overflowing, or larger environment values fall back to the defaults, while larger config-file values are rejected during config validation.

### Local lifecycle fields

Local suggestion records use the `status` lifecycle field instead of a binary submitted flag. New records start as `draft`; successful GitHub submission moves them to `submitted_pending_triage` and stamps `upstream_url`, `upstream_issue_number`, and `last_synced_at` when known. Every GitHub submission attempt also stamps `last_submit_attempt`, increments `submit_attempt_count`, and records `last_submit_error` on failure; success clears the last error. GitHub rate-limit responses also stamp `next_retry_at`, and duplicate unsubmitted suggestions are not retried until that timestamp has passed. Older records containing `submitted_to_github` / `github_issue_url` are normalized on read to the new lifecycle fields.

`SuggestionStore.TryTransitionStatus` is the atomic manual-transition boundary used by `suggestions update <id> --status <state>`. `submitted_pending_triage` is automatic-only. `open_in_upstream` and `resolved_in_upstream` require existing upstream evidence; `draft` requires the absence of upstream evidence; and `wont_fix`, `duplicate`, or `superseded` are local maintainer dispositions. Local dispositions suppress automatic duplicate resubmission without setting `AlreadySubmitted` or an upstream-submission response flag. Same-state transitions and transitions during an active submission reservation fail closed. The store rechecks the expected revision under its file lock, stamps the latest `previous_status`, UTC `status_changed_at`, bounded/redacted `status_changed_by`, and optional bounded/redacted `status_change_reason`, updates `resolved_at` for `resolved_in_upstream`, and recomputes `revision_hash`. Full audit values are redacted before a surrogate-safe final cap so a credential crossing the cap boundary cannot evade redaction. Content edits and lifecycle transitions are separate CLI operations so one audit event has one unambiguous meaning.

`suggestions export --format markdown|issue-drafts --output <path>` renders the bounded payload in memory, rejects payloads over 16 MiB before writing, and refuses the selected database or suggestion-store path. For existing files it compares filesystem identities as well as normalized path spelling, so symlinked parents, mount aliases, and hard links cannot bypass source protection. Existing destinations are rejected unless `--overwrite` is explicit. Publication uses a sibling temporary file, flushes its contents, and performs a same-filesystem no-overwrite move or atomic replacement; failed publication cleans the temporary file. The writer emits UTF-8 without a BOM, creates missing parent directories, and keeps JSON-format suggestion exports on stdout. Tests cover the store transition/revision contract, CLI validation and filtering, source-target alias rejection, no-overwrite race safety, replacement, and temporary-file cleanup.

### GitHub retry idempotency

`SuggestionStore.TryAddAndSubmit` keeps local read/write and submission reservation under the suggestion-store file lock, but invokes the GitHub callback after releasing the lock. The reservation stamps `last_submit_attempt`, increments `submit_attempt_count`, and sets a six-minute `next_retry_at` guard covering the maximum configured production callback timeout. While that guard identifies an active attempt, updates and deletes return `submission_in_flight`; the callback result is persisted by re-taking the lock briefly after the remote call completes. Finalization also compares the reserved revision with the current one and does not mark a revision changed after an expired reservation as submitted by an older callback result.

Before creating an upstream Issue, `GitHubIssueReporter` checks whether an Issue with the same immutable suggestion ID already exists. It first queries GitHub Search for the ID in issue bodies, then falls back to listing Issues with the existing repository labels cdidx applies (`enhancement` for ordinary suggestions, `bug` for crash/error reports) and matching the ID in each body. The fallback avoids GitHub Search indexing latency, so a retry immediately after a lost create response can still find the just-created Issue and avoid a duplicate POST. Lookup failures fail closed: if GitHub search, labeled issue listing, or response parsing is indeterminate, the reporter records a sanitized `last_submit_error` and does not create a possible duplicate Issue.

The shared GitHub HTTP client uses an explicit 10-second submission timeout by default, configurable up to 300 seconds with `CDIDX_GITHUB_SUBMIT_TIMEOUT_SECONDS`, and the platform default proxy (`HTTPS_PROXY`, `HTTP_PROXY`, `ALL_PROXY`, and `NO_PROXY` through .NET default proxy handling). Non-positive, non-numeric, and larger timeout values fall back to the 10-second default. GitHub API requests apply `User-Agent: cdidx`, `Accept: application/vnd.github+json`, and `X-GitHub-Api-Version: 2022-11-28` through the shared request helper; bearer tokens are added only when a non-empty token is available, and suggestion submission only sources that token from `CDIDX_GITHUB_TOKEN`. Create failures mention proxy environment variables in their diagnostic hint. `429` responses and `403` responses with `x-ratelimit-remaining: 0` are treated as rate limits; `Retry-After` wins, then `x-ratelimit-reset`, then a one-minute fallback retry window. Parsed retry dates are bounded to the current time through one hour in the future, and invalid or out-of-range reset headers are ignored before falling back.

Outbound HTTP/GitHub egress is intentionally narrow:

| Purpose | Path and bounds |
|---|---|
| Update check | `UpdateChecker` reads GitHub release metadata with a 2-second request scope, `ResponseHeadersRead`, a 64 KiB response cap, JSON depth 16, private cache writes, and sanitized failure categories. |
| Release installer/download verification | `ProgramRunner` release download helpers use release-download headers without GitHub API media types, a 256 KiB checksum cap, a 1 MiB installer script cap, private script-file writes, and bounded release-asset diagnostics. |
| GitHub issue creation | `GitHubIssueReporter` posts only scrubbed structured suggestion fields after duplicate lookup succeeds; success JSON is capped at 256 KiB/depth 16 and API error bodies are capped at 4 KiB with sensitive JSON fields redacted. |
| Duplicate preflight | `IssueDuplicatePreflight` loads at most 1000 open issues and 1000 repository labels, caps GitHub page bodies at 8 MiB/depth 32, skips pull requests, truncates issue/title/body/label scalars, and sanitizes recoverable diagnostics. |
| Suggestions/reporting exports | `SuggestionStore`, `SuggestionsCommandRunner`, search issue-draft output, and report/query output are local-only unless the caller explicitly requests live GitHub duplicate preflight or `suggest_improvement` has `CDIDX_GITHUB_TOKEN`; exported descriptions/context/tool invocation text are bounded with `[truncated]` markers. |
| MCP exposure and diagnostics | MCP `suggest_improvement` stores accepted suggestions locally first; doctor diagnostics expose GitHub proxy/default-credential state and maximum request timeout without token values, while submission failures store sanitized categories/details instead of raw response bodies. |

### What is NOT included in the payload by design

- Source file contents from the user's project
- Any data from the indexed SQLite database
- Any data from `.cdidx/codeindex.db`
- Operating system or environment information

### Heuristic source code guard (not a security boundary)

The description, context, and optional tool invocation context fields pass through `SourceCodeDetector` before storage and optional GitHub submission. This heuristic rejects common pasted code patterns (multi-line blocks, backtick or tilde fenced code, import runs, function definitions) but intentionally allows short inline code examples so gap descriptions remain useful. Rejections return a bounded `source_code_rejection` object with the rejected field name, stable primary `reason_code`, and `reason_code_counts` diagnostics for the heuristics that matched; they do not echo the rejected text. It is **not a security boundary** or data-loss-prevention boundary — a determined agent could bypass it, and encoded or obfuscated code-like text can be a false negative. The guard is a best-effort filter to catch accidental code inclusion, not a guarantee that no code-like text will ever be transmitted.

### SourceCodeDetector design

`SourceCodeDetector` uses six independent heuristics to reject text that looks like pasted source code. Each heuristic is implemented as a clearly named private method with detailed comments explaining what it detects and why, and maps to a stable reason code such as `statement-ending`, `indented-code-lines`, `block-structure`, `repeated-imports`, `function-definition`, or `fenced-code-block`. Detection evaluates every heuristic so diagnostics can report matched reason-code counts while preserving the first matched reason as the primary `reason_code`. The class is designed for readability: anyone reviewing the open-source code can verify the detection logic and understand the false-negative tradeoff.

The detector intentionally allows short inline code examples (e.g. `` `const foo = () => {}` ``) and only rejects multi-line code blocks. False negatives (missing some code) are acceptable; false positives (rejecting valid descriptions) are not.

## Exit codes

See [Exit codes](USER_GUIDE.md#exit-codes) in USER_GUIDE.

## Error code taxonomy

Process exit codes are coarse (`0` success including valid zero-row queries, `1` usage, `2` not-found or strict zero-row query, `3` db, `4` feature-unavailable, `5` stale, `6` transient db, `7` invalid argument, `8` signal cancellation, `9` install/upgrade installer failure, `99` unhandled exception). Query commands return `0` for genuine zero-row results by default and reserve `2` for missing indexed data or callers that opt into `--strict-not-found`. Scripts, oncall runbooks, and AI agents that need to react to *which* failure happened — not just the bucket — should read the stable `Exxx_NAME` taxonomy emitted on every CLI / MCP error path. The user-facing table lives in [Error codes](USER_GUIDE.md#error-codes); this section captures the developer contract.

**Where it surfaces**

- Human stderr is prefixed with the bracketed code: `Error [E001_DB_NOT_FOUND]: database not found at <path>`. The prose after the prefix can change between releases; the code cannot.
- `--json` envelopes (`CommandErrorJsonResult`) gain an additive optional `error_code` field. The field is `null`-omitted via `JsonIgnoreCondition.WhenWritingNull`, so existing JSON consumers see no schema break — they only observe the field once a runner attaches a code.
- The taxonomy is owned by `src/CodeIndex/Cli/CommandErrorCodes.cs`. All emitters route through `WriteCommandError(... , string? errorCode = null)` in `DbCommandRunner` / `IndexCommandRunner` and the equivalent stderr writers in `QueryCommandRunner.WithDb`.

**Stability contract**

- Codes are append-only. Once a code is published in a tagged release, it must not be renamed, renumbered, or reused for a different failure shape, even if the implementation site moves.
- Retiring a code means stopping new emissions but leaving the constant in `CommandErrorCodes.cs` so old log archives stay decodable.
- When adding a new code, allocate the next free `Exxx` slot, document its trigger condition in the XML doc-comment on the constant, wire it through every relevant `WriteCommandError` call, mirror the entry into both the English and Japanese tables in `USER_GUIDE.md`, and add a regression test under `tests/CodeIndex.Tests/CommandErrorCodesTests.cs` (one assertion for the bracketed stderr prefix and one for the JSON `error_code` field).
- `E003_SCHEMA_TOO_NEW` is intentionally reserved with no current emission site — the equivalent forward-compatibility condition is surfaced softly via `status --json index_newer_than_reader`. A future binary that decides to hard-fail on an unknown schema stamp at open time must emit `E003`.

## Design decisions

- **Language capability patterns remain typed at the integration boundary** — CLI/MCP `languages` rows expose suffix-only `extensions`, literal `exact_filenames`, and `<suffix>`-rendered `filename_prefix_patterns`. `legacy_patterns` preserves the former combined list during deprecation, and `pattern_provenance` identifies built-in, plugin/pattern, and language-map override ownership. Round-trip tests feed every advertised typed pattern back through `FileIndexer.DetectLanguage` (#4617).
- **Ambiguous source extensions stay explicit** — `.m` and `.pl` are not assigned to Objective-C and Perl by default. `FileIndexer` checks an authoritative recognized shebang, then a 64 KiB bounded prefix for strong mutually exclusive Objective-C/MATLAB or Perl/Prolog markers, then at most 256 entries per ancestor directory for conservative project markers. Conflicting or weak evidence is indexed as `ambiguous_m` / `ambiguous_pl`; unresolved `.m` files run the bounded MATLAB and Objective-C symbol/reference paths after a shared position-preserving comment mask, while Prolog and `ambiguous_pl` advertise conservative reference/graph support and the ambiguous `.pl` bucket uses union symbol/reference rules without changing content-based classification (#4612, #4738, #4746).
- **Dynamic reference-graph readiness follows extractor contracts** — when indexed Crystal, Groovy, Tcl, Prolog, or `ambiguous_pl` rows have a missing or stale symbol-extractor version stamp, status reports `dynamic_reference_graph_contract_stale` and keeps `reference_graph_complete` / `graph_data_current` false until a normal index refresh rewrites those rows (#4746).
- **Hotspot marker fingerprints share one bounded tree traversal** — full/update CLI and MCP indexing compute C#, VB, F#, and MSBuild marker fingerprints together instead of walking the directory tree once per language. Each distinct marker glob retains the platform filesystem's matching behavior and is enumerated once per visited directory, while child directories are enumerated once; marker sets, budgets, truncation sentinels, and warning order remain isolated per language. The single-language API delegates to the same engine, preserving ignore rules, nested-repository/submodule boundaries, and MCP authorized-read failures.
- **Lock-file dependency graphs model package relationships** — `packages.lock.json`, `package-lock.json`, and `npm-shrinkwrap.json` keep package declarations as symbols, but emit `dependency` references only for explicit parent-package to child-package entries. NuGet lock symbols and references preserve the current file, target/RID, parent package, and exact JSON property span; candidate resolution stays file-local, while file-level `deps` suppresses cross-file package-name inference. Normal index updates invalidate the prior dependency-lock extraction and reference-identity contracts, so `callers` identifies the requiring package without connecting unrelated lock files or collapsing repeated declarations to the first matching line (#4409, #4845).
- **Dependency-cycle audits separate analysis from display** — CLI `deps --cycles` and MCP `deps` with `cycles=true` analyze a deterministic, path-ordered edge set up to the independent `--graph-budget` / `graphBudget` before computing and stably ranking strongly connected components. `--limit` / `limit` only paginates that ranked SCC set, and opaque cursors are bound to the filters, graph budget, and indexed graph that produced them. Machine-readable responses expose `analysis_complete`, graph edge count/budget, stable ranking mode, authoritative total-cycle status, and continuation metadata; exhausting the graph budget is reported as an explicitly incomplete analysis rather than a complete cycle audit (#4731).
- **No ORM** — Raw `Microsoft.Data.Sqlite` with parameterized queries. Keeps dependencies minimal and control explicit.
- **Batch commits** — 500 records per transaction for write performance. Reduces fsync overhead.
- **Partial batch failures** — `DbWriter` keeps the fast multi-row `INSERT` path for normal chunk and symbol batches. If SQLite rejects a batch, the writer rolls that batch back, retries rows under per-row `SAVEPOINT`s, commits the valid rows, skips only the failing rows, increments `BatchRowsSkipped`, and emits a warning containing the row identifier and SQLite error. This keeps one corrupt extracted row from discarding the rest of a large indexing batch (#1754).
- **WAL mode + busy_timeout** — Write-Ahead Logging for concurrent read/write access and crash safety. 5-second busy timeout avoids immediate SQLITE_BUSY errors.
- **Content-external FTS5 with triggers** — Avoids doubling storage by pointing to `chunks` table instead of storing a copy. Database triggers keep the FTS index in sync automatically.
- **Reader snapshot isolation for bundled multi-statement reads** — Any read entry point that runs more than one SQL statement per call (`DbReader.GetStatus`, `DbReader.AnalyzeSymbol` for CLI `inspect` / MCP `analyze_symbol`, and `RepoMapBuilder.Build` for CLI `map` / MCP `repo_map`) wraps its body in a single `BEGIN DEFERRED` transaction so every sub-query resolves against the same WAL snapshot. Without this, a writer committing between two `COUNT(*)` statements can let a concurrent reader observe impossible mixed states (issue #180 exposed this as `files=836, refs=0` against a steady-state 44k-ref index). `DEFERRED` acquires only a `SHARED` lock on the first SELECT, so it does not block other writers, and the transaction is committed explicitly at the end to release that lock promptly. Sub-queries that open their own `SqliteDataReader` must scope the reader in an inner block so the handle closes before the outer `Commit()` — `SqliteTransaction.Commit()` fails if a reader on the same connection is still open. New multi-statement reader entry points should follow the same pattern; single-statement queries do not need it (SQLite auto-commit already gives statement-level snapshot isolation).
- **Git-style ignore awareness** — `FileIndexer` keeps the always-on `SkipDirs` / `SkipFiles` baseline for non-repo directories (including the `._*` AppleDouble resource-fork pattern so macOS-side metadata files never reach the indexability/language probe — #1583), then layers user `.gitignore` and optional `.cdidxignore` rules directory-by-directory while scanning. Git-managed workspaces resolve case-sensitivity from `core.ignorecase` instead of an OS-name heuristic, even when the indexed project root is a subdirectory inside the repository; repo-root and other ancestor `.gitignore` files above that subdirectory are preloaded before scanning, while non-Git trees fall back to a best-effort filesystem probe. `--commits` also normalizes Git's repository-root-relative paths back to the indexed project root before update-mode filtering, and `**` is only treated specially in Git's path-form globstar cases. Unreadable ignore files fail closed for that directory scope so full scans skip the subtree and scoped refreshes avoid mutating the index with incomplete rules. Last-match-wins negation allows users to keep secrets, generated code, fixtures, and build output out of the index without changing cdidx defaults for non-Git trees.
- **Literal-safe search by default** — Search uses token-by-token quoting by default to avoid FTS syntax errors, while double-quoted spans such as `search "\"new Regex\""` stay single FTS5 phrase tokens instead of widening to independent token matches. Raw FTS5 syntax is opt-in via `--fts` or MCP `rawQuery`. Prefix expansion is also opt-in: appending `*` to a single token (`search auth*`) promotes that token to an FTS5 prefix phrase, and the `--prefix` flag (MCP `prefix`) promotes every token. Without an opt-in, `search 計算` matches only the indexed token `計算` and does not widen to `計算する` (issue #1519) — unicode61 keeps adjacent CJK codepoints as one token, so users who need that widening pass `--prefix` or append `*` explicitly.
- **Exact regex spans in `find`** — `find --regex` preserves the regex engine's match length in JSON and MCP results, including `length: 0` for insertion-point anchors such as `^` and `$`. Display-oriented formats that require a visible range may still render a one-character span without changing the machine-readable match length (#4473).
- **Path-aware narrowing and ranking** — `search`, `definition`, `references`, `callers`, `callees`, `symbols`, and `files` share path include/exclude filters plus `--exclude-tests`. Read queries prefer source files over tests/docs, and full-text search boosts exact symbol-name and path matches to surface likely implementation files first.
- **Compact search snippets for AI** — `search --json` and MCP `search` return match-centered snippets with explicit snippet ranges, match lines, highlights, context counts, `truncated_line_count`, and `truncation_context` instead of whole chunks. `truncation_context.char_counts` and `truncation_context.total_chars` expose the omitted character counts behind each clamped snippet line, while truncated highlights also carry `truncated_char_counts`. `--snippet-lines` lets clients trade recall for smaller payloads, and `--max-line-width` (CLI) / `maxLineWidth` (MCP) routes each snippet line through the same `LineWidthFormatter.ClampLine` contract used by `find` / `references` / `excerpt` / `inspect` so hits inside minified / transpiled / generated single-line files no longer return hundreds of KB per result unless the caller explicitly sets `0`; clamped lines carry `...(+N)...` markers and `highlights[].truncated` / `highlights[].original_line_length`.
- **Repo map for first-pass orientation** — `map` aggregates languages, modules, top files, file hot spots, and likely entrypoints from indexed data so AI clients can decide where to look before issuing precise queries. Entrypoint inference now falls back to known top-level entry files when symbol extraction does not produce an explicit `Main`-style symbol.
- **Freshness metadata for trust decisions** — `status` exposes whole-workspace freshness and git state, plus trust metadata such as `sql_graph_contract_ready` / `sql_graph_contract_degraded_reason`, `hotspot_family_ready` / `hotspot_family_degraded_reason`, forward-compatibility audit fields (`index_writer_version`, `index_newer_than_reader`, `index_newer_than_reader_reason` — see "Forward-compatibility readiness audit"), and fold remediation fields (`fold_ready_reason`, `degraded_reason`, `recommended_action`, `alternative_action`) so AI clients can tell up front whether SQL graph/dependency/impact answers, duplicate-name hotspot families, and Unicode `--exact` are authoritative. CLI `status --json` and MCP `status` both populate those fold remediation fields when `fold_ready=false`. It also carries `unknown_extension_file_count`, a capped `unknown_extension_files` path sample, `unknown_extension_files_truncated`, and `unknown_extension_file_path_limit` after a current full-repository scan so extension-table coverage gaps are visible and actionable even when those files were excluded from indexing. When those fold remediation fields are derived from an explicit read-only `file:` DB URI, they are normalized back to a writable filesystem path for both absolute (`file:///...?...`) and relative (`file:codeindex.db?...`) forms instead of echoing the read-only URI into commands that would fail. `cdidx index` JSON/human readiness output also surfaces the same trust bits, keeping the post-index readiness summary aligned with `status`. `impact` / MCP `impact_analysis` also mirror the SQL graph-contract signal in JSON so stale SQL rows do not masquerade as authoritative zero-impact answers. `inspect` / MCP `analyze_symbol` and `references` / MCP `references` now mirror that same SQL graph-contract signal whenever SQL-backed graph reads contribute to their payloads, so stale SQL rows do not look like authoritative hits or zero-result answers there either. `map` keeps `indexed_at` / `latest_modified` scoped to the filtered result set and also exposes `workspace_indexed_at` / `workspace_latest_modified` for whole-workspace freshness. `inspect` mirrors those whole-workspace timestamps and git fields so symbol-oriented AI flows can make trust decisions without a separate `status` call. `files` exposes per-file checksum plus modified/indexed timestamps. File-column migrations are applied opportunistically for older DBs, and read paths are designed to avoid crashing if in-place migration is unavailable. CLI and MCP zero-result JSON responses for `search`, `files`, `symbols`, `definition`, `references`, `callers`, `callees`, `deps`, `unused`, `hotspots`, and `impact` include `indexed_file_count`, `indexed_at`, and `freshness_available`. `indexed_at:null` with `freshness_available=true` means the index is empty, while `freshness_available=false` means a legacy/read-only DB could not expose freshness timestamps and `freshness_degraded_reason` explains why. **HEAD-aware staleness signal**: every successful `cdidx index` full scan now stamps the captured `git HEAD` into `codeindex_meta` so subsequent runs can compare it against the workspace HEAD. When they differ and the user did not pass `--rebuild`, the CLI emits a `head_changed` warning recommending `cdidx index <projectPath> --rebuild` and exposes `head_changed` / `prior_indexed_head_commit` / `current_head_commit` / `head_change_notice` in `index --json`. `status --check` mirrors the same comparison through `workspace_check.head_changed` (alongside `indexed_head_commit` / `workspace_head_commit` when they differ), so AI clients that already gate on freshness can refuse to trust a default incremental scan after `git switch <branch>` without a separate query. `--commits` / `--files` partial updates deliberately preserve the captured HEAD so the staleness signal survives until a real full scan reindexes the worktree. Non-git workspaces and legacy DBs that never captured a HEAD skip the comparison instead of false-positive flagging.
- **Folded-key upgrade without reparse** — `backfill-fold` and MCP `backfill_fold` recompute `name_folded` / `*_folded` directly from existing DB rows, then stamp `FoldReadyFlag` once verification confirms no required folded values remain NULL. This gives AI clients and users a low-cost upgrade path from pre-#86 DBs without re-reading every source file, and it also rewrites all folded rows when `fold_key_version` is missing or mismatched so future `NameFold.Version` bumps cannot silently restamp stale keys.
- **Bundled symbol analysis** — `inspect` and MCP `analyze_symbol` return definition, nearby symbols, references, callers, callees, file metadata, workspace trust metadata, and graph-support metadata in one request so AI clients can answer common symbol questions with fewer round-trips.
- **Language-aware reference extraction** — `references`, `callers`, `callees`, and `impact` are backed by an indexed reference table built only for languages where regex-based call/reference extraction is meaningful. Unsupported languages intentionally fall back to text search instead of returning low-confidence pseudo-graph data. When a language is removed from graph support, `PurgeUnsupportedReferences` deletes its stale `symbol_references` rows on the next indexing run, and graph read paths additionally filter by supported languages to prevent stale edges from surviving between index runs. Shell is intentionally excluded because its command-style invocations (`foo arg1 arg2`) cannot be detected by the parenthesized-call regex. **Nested generic call sites**: C#/Java constructor calls like `new Dictionary<string, List<int>>()` and C# generic method calls like `Helper.DoWork<List<int>>()` are recovered by a depth-aware fallback scanner so the outer target still reaches the reference table even though the flat regex fast-path cannot balance `>>`. **JS/TS no-paren constructors**: JavaScript / TypeScript zero-argument constructor calls that legally omit `()` — for example `new Foo;`, `new Date;`, qualified targets like `new Demo.Provider;`, and one-level generic TypeScript forms like `new Box<number>;` — are emitted as `instantiate` edges via a dedicated language-gated path, while next-line `.bar()` / `[0]` continuations are suppressed so a line-ended `new Foo` does not become a phantom standalone instantiation. **Constructor chain calls**: C# `: this(...)` / `: base(...)` initializers and Java `this(...)` / `super(...)` first-statement calls are detected separately from the generic call regex and rewritten so the reference target is the real constructor (enclosing class/record for `this`, the parsed base type from the class signature for `base` / `super`). Cross-line C# initializers are attributed to the owning constructor rather than the enclosing class. Base-type parsing strips generics, record primary-ctor args, `where` constraints, and `global::` / dotted namespace qualifiers; Java `super.method()` stays a normal method call. **Type-position dependency edges**: C#/Java base lists, declaration types, generic constraints, `throws`, `is`/`as`/`instanceof`, and real C# XML-doc `cref` sites are indexed as `type_reference` rows so `references` / `impact` can see compile-time rename dependencies without polluting the default dynamic call graph exposed by `callers` / `callees`. C# XML-doc `cref` extraction accepts declaration-attached XML-doc comments from both `///` lines and delimited `/** ... */` blocks, including declarations that begin later on the same physical line after the closing `*/` only when no unrelated same-line code or declaration intervenes, while ordinary `//` / `////` comments, non-documenting block comments, method-body XML-doc comments that merely precede a later declaration, brace-free field/property initializer continuations, brace-free expression lambdas, intervening top-level executable statements, same-line non-target code after `*/`, other nested executable continuations, and multiline raw/verbatim string content whose line happens to start with `/**` stay excluded. Non-doc code or string content after the closing `*/` on the same physical line is still outside the doc-comment slice. Even though the regex now runs against that narrower slice, the extractor preserves `symbol_references.column` relative to the original physical source line. On the C# read path, `using static` constant-pattern suppression is token-aware around `is` / `case`, reconstructs an anchor-aware indexed multi-line window when the anchor lives on a previous line, and keeps trivia-bearing forms such as `value is/*comment*/Red`, `value is\n    Red or Blue`, `value is\n    // comment\n    Red`, `case\n    // comment\n    Point:`, long `case` / `or` chains, and `case\tRed:` filtered or rescued correctly. Qualified constant/member patterns stay qualifier-driven on that exact-name read path, so an unrelated same-name type such as `class Red {}` no longer cancels suppression for `case Color.Red or Color.Blue:` just because the leaf name matches. The extractor-side pending type-pattern carry now also survives trivia-only separator lines, standalone continuation-line `not`, and multiline `case` heads/logical continuations, so comment-only or `not`-only continuation lines no longer drop the later type head before the real token arrives. Non-type `case` labels such as `case > 0:` and `case not > 0:` do not arm that pending carry, so the next-line call/identifier token stays out of `type_reference`. Same-name type rescue also honors `file` visibility so file-local types only rescue references from the same physical file; inherited protected/public/internal nested types from real base classes rescue derived-class pattern heads only after the base reference is normalized through active type and namespace aliases, and alias-expanded constructed generic bases are canonicalized again before containing-type lookup so `AliasBase = Probe.Base<int>` resolves the same way as `Probe.Base`; implemented interfaces do not contribute inherited nested-type rescue; and same-file `using Namespace;`, project-wide `global using Namespace;`, and active type aliases all participate in the rescue set. The extractor deliberately leaves ambiguous unqualified `using static` heads such as `value is Red` in the DB, because file-local parsing alone cannot know whether another file in the same namespace declares the real `Red` type; the workspace-aware read path is responsible for suppressing the pure constant-only cases. **SQL qualified-name alignment**: SQL definitions still persist their schema-qualified symbol name (`dbo.fn_X`), but graph/`deps`/unused/hotspot readers now resolve each SQL reference row through its stored source-line context, recorded call column, and enclosing container before they compare it to definitions, so qualified `references` / `callers` / `impact` queries stay schema-scoped even when one line contains multiple qualified calls or the lookup is non-exact. Those readers fall back to the bare leaf only when the source site itself is genuinely unqualified, which keeps `deps`, `unused`, and `hotspots` aligned with qualified SQL calls without regressing bare-call support or double-counting `EXEC dbo.fn_Target; EXEC sales.fn_Target;`. Once a row already has a recorded call column, those downstream readers no longer whole-line-upgrade that row to a later qualified token, so trailing comments, string literals, or a second qualified call cannot steal the earlier unqualified edge. Exact SQL graph/dependency readers also preserve the resolved segment count, so a quoted single identifier containing a dot such as `"sales.fn_Target"` stays distinct from the real qualified name `sales.fn_Target` across exact `references` / `callers` / `impact` and aggregate `deps` / `unused` / `hotspots`. SQL CTE body source rows use the raw `cte_body_reference` kind, so `references --kind cte_body_reference` can distinguish anchor/recursive-member internals from outer-query table references. Qualified SQL `callees` queries also keep leaf fallback disabled unless the caller query itself is unqualified, so `callees sales.Caller` no longer widens to `dbo.Caller`. SQL extractors also accept optional whitespace around qualified-name dots, so definitions/calls such as `[sales] . [fn_Target]` and `[dbo] . [fn_Target]` keep their full qualified identity instead of truncating at the first segment. The same SQL no-parens extractor now preserves ANSI / PostgreSQL double-quoted call targets such as `CALL "sales"."proc_name"` and `EXEC "dbo"."fn_Target"` instead of stripping them as string literals, while true single-quoted SQL string literals remain masked. Definition-oriented readers also canonicalize quoted qualified SQL names (`[dbo].[fn_X]` → `dbo.fn_X`) before matching, and they only fall back to the leaf identifier for unqualified queries so exact qualified lookups do not widen to sibling schemas that merely share the same leaf name. Exact SQL definition matching also preserves segment count, so a quoted single identifier that contains a dot (`"sales.fn_Target"`) does not collide with a real qualified name (`sales.fn_Target`). SQL exact graph leaf fallback also stays on the Unicode folded exact path, and both quoted qualified and unqualified Unicode exact definition lookups now use the folded normalized path, so queries such as `dbo.Äpfel` / `dbo.äpfel` and bare `Äpfel` / `äpfel` keep matching leaf call/reference rows such as `äpfel` plus stored definitions such as `[dbo].[Äpfel]` or `dbo.Äpfel` instead of silently degrading to ASCII-only `NOCASE`. Exact multi-name SQL `symbols --count` lookups also bind the folded leaf parameters on that same `_foldReady` path, so Unicode leaf query sets no longer fail with missing-parameter database errors.
- **Transitive impact analysis** — `impact` and MCP `impact_analysis` compute the transitive caller chain of a symbol using BFS. Design constraints refined through adversarial review: caller matching uses case-insensitive exact match (`lower() = lower()`) to avoid both substring expansion and case-sensitivity brittleness; symbol names are pre-resolved through definitions with exact-case preference; the read path filters to graph-supported languages to prevent stale edges from removed languages; the definition set used for heuristic fallback must also respect active `--lang` / `--path` / `--exclude-path` / `--exclude-tests` filters and graph-supported languages so out-of-scope or unsupported duplicates do not suppress in-scope hints; fallback eligibility is keyed off class-like definitions only, so same-name namespace/import siblings do not block a single resolved class / struct / interface target, while pure non-callable `namespace` / `import` queries surface `non_callable_symbol_kind` guidance; heuristic file-level hints still return a successful result and encode their non-authoritative status via `impact_mode`, `heuristic`, `hint_count`, and `truncated`; caller rows include `result_kind: "graph"` and heuristic `file_impacts` rows include `result_kind: "file_heuristic"` so clients can distinguish authoritative hop-depth graph results from boundary fallback hints without inferring from list position or depth values; when `truncated` is `true`, the JSON / MCP payload also exposes `truncated_reason` so callers can distinguish actionable cases from runaway-graph cases — `user_limit` means the caller-supplied `--limit` was reached and raising `--limit` will return more results, while `safety_cap` means an internal per-symbol BFS fetch-iteration cap fired (the graph is likely pathological / cyclic and raising `--limit` alone will not help). `impact` / MCP `impact_analysis` also expose `termination_reason` (`completed`, `max_depth_reached`, `cycle_detected`, `row_limit_truncated`, `safety_cap`, or `cancelled`), `cycle_detected`, and `cycles` so caller cycles are distinguishable from natural traversal completion or limit/depth termination (#1883). `safety_cap` outranks `user_limit` whenever both are encountered, and the heuristic file-level hints path is `user_limit`-only because hint truncation is always driven by the caller's `--limit`. The field is omitted whenever `truncated` is `false`. (#1533) `count` / `file_count` now describe the visible returned set while `confirmed_count` / `confirmed_file_count` preserve symbol-level caller totals for heuristic-success payloads, and `impact --json --count` uses the same `*_count` field names as the full payload; to reduce general-name collisions, a file only qualifies for type fallback if it both references one of the candidate member names and also exposes same-file evidence anchoring the source/target pair — either a `call` / `instantiate` reference to the resolved target name (the call-graph itself authoritatively pins the relationship, so this path runs before the metadata-attribute bypass and does not depend on the looser ambiguity guard) or structured type evidence through indexed symbol metadata such as signatures or return types — rather than raw comment/string text matches. The call/instantiate anchor matches the resolved name exactly with no suffix-strip alias, because callable references already carry the authoritative identifier and applying the C# `[Foo]` → `FooAttribute` alias there would let unrelated `Foo()` method calls falsely anchor `impact FooAttribute` (#1881); the metadata bypass keeps the C# `Attribute` suffix alias because attribute use sites legitimately abbreviate the target name. The signature evidence path is Unicode-aware so fullwidth/accented identifiers are tokenized consistently with exact-name resolution; hint `reference_count` reflects the real number of matching reference rows while the symbol list stays deduplicated; only multiple class-like definitions are treated as fallback ambiguity, even when they share one file; and `PurgeUnsupportedReferences` runs in all three indexing paths (CLI full scan, CLI update mode, MCP index).
- **Impact cycle identity** — On a current reference-identity graph, `impact` carries resolved source/target symbol IDs through every BFS hop and detects cycles only from actual traversed directed edges between those canonical IDs. A repeated or folded display name is never a zero-hop cycle by itself; direct recursion remains a real singleton cycle because its persisted edge has the same source and target ID. Unresolved or ambiguous name-matched edges and non-unique `resolved_group` overload candidates remain in conservative traversal output but cannot enter the canonical cycle graph, and mixed same-name target identities aggregate into one caller row without inventing a callee ID. Structured caller rows expose `caller_symbol_id` / a uniquely resolved `callee_symbol_id`, `--with-paths` nodes expose `symbol_id` only for unique identities, and cycle rows expose `member_identities` while retaining the display-only `members` list for compatibility. Legacy graphs without the current identity contract keep the name-keyed compatibility path (#4847).
- **Extractor regex backtracking policy** — Built-in symbol and reference extractors must not use unbounded regular expression matching on repository-controlled file content. Backtracking regexes use `BoundedRegex.DefaultMatchTimeout`, while `RegexOptions.NonBacktracking` is allowed for patterns that are compatible with the non-backtracking engine. Patterns that deliberately remain backtracking-only, such as lookaround-heavy or balancing-group extractors, are acceptable only because the shared timeout audit covers them. If a future extractor must use `System.Text.RegularExpressions.Regex` directly, it must pass an explicit timeout and document why `BoundedRegex` or `NonBacktracking` is not suitable.
- **Hybrid symbol extraction** — No AST parsers and no heavyweight language-specific dependencies. Most languages still use compiled regex patterns, while JavaScript/TypeScript add a lightweight lexer/state machine for class-body method extraction, private-scope filtering, synthetic class-expression binding detection, and JS/TS-specific range resolution that regex alone could not handle reliably. The trade-off still favors speed and portability over full parser accuracy, but the index stores richer symbol metadata such as definition ranges, optional body ranges, signatures, enclosing symbols, qualified container paths, authoritative family keys, visibility, and return types when the language patterns or JS/TS state machine can infer them. Visual Basic patterns also treat `Namespace ... End Namespace` as a real container and allow implicit-visibility declarations plus leading modifiers (`Shared`, `Overrides`, `Partial`, etc.), so VB projects expose the same top-level orientation and member coverage that other class-based languages already get. Visual Basic container patterns use case-insensitive `VisualBasicEnd` range tracking so cross-file partial families still get stable body ranges and can participate in hotspot-family grouping. **Pattern externalization**: Language patterns are currently defined inline in `SymbolExtractor.cs` using compiled `Regex` objects. This keeps the extraction pipeline self-contained and allows compile-time validation, but means adding a new language requires a code change and rebuild. A future iteration could externalize patterns to JSON/TOML files (loaded at startup), which would lower the barrier for community contributions and enable hot-reload during development. The trade-off is losing compile-time safety and slightly increasing startup cost. If externalized, patterns should include: language name, kind (function/class/import/namespace), regex string, body style (brace/indent/ruby-end/none), and optional capture group names for visibility and return type.
- **Nested C# interpolation state** — The C# lexical masker keeps immutable parent frames when an interpolated regular, verbatim, or raw string starts inside another interpolation hole. Closing the nested string restores the complete outer mode, delimiter, dollar-count, and brace-depth state; expression-bodied property calls remain excluded from declaration patterns, and C# extractor contract bumps force existing indexes to refresh affected files.
- **C# static-lambda declaration gating** — The declaration scanner treats a candidate name inside a confirmed `static`, `static async`, or `async static` lambda header as expression context, including when multiline property-header composition prepends call arguments. Real static members, local functions, and assigned-lambda symbols remain eligible. C# extractor contract v6 makes a normal index refresh re-extract stale C# symbols (#4830; regression of #4453).
- **Authoritative hotspot-family trust** — `hotspots` only promotes duplicate-name families back to codebase-wide counts when the persisted `symbols.container_qualified_name` / `symbols.family_key` were produced under the current per-language `hotspot_family_version_*` contract. These readiness stamps and marker fingerprints live in `codeindex_meta`, so legacy, mixed, or partially refreshed DBs degrade explicitly instead of silently reusing stale cross-file family identities.
- **Authoritative C# metadata-target trust** — `deps` / `impact` metadata-attribute edges (linking `[Foo]` usage to the defining `FooAttribute` class) are promoted from a signature-shape heuristic to an authoritative resolver whenever `is_metadata_target` is persisted under the current `metadata_target_version_csharp` contract. The resolver walks C# class base lists with fixed-point transitive resolution through same-DB class rows and falls back to the BCL `Attribute` suffix convention only for unresolved external bases. Readiness lives in `codeindex_meta`, and the reader uses a three-way branch: (1) ready → `is_metadata_target = 1`; (2) column present but not stamped (legacy row) → `signature LIKE '%: %'`; (3) column missing → naming-only fallback. This fixes non-attribute impostors (`class FooAttribute : BaseService`) silently dropping edges when they shared names with real `FooAttribute : Attribute` classes (#435).
- **Human-readable default** — All commands default to human-readable output. `--json` for AI/machine consumption.
- **Structured MCP responses** — MCP tool calls return typed JSON in `structuredContent` and keep `content` concise for compatibility.
- **MCP pre-validation rate limiting and bucket eviction** — Every direct `tools/call` consumes one fixed caller-wide coarse bucket before detailed tool-name, enablement, and argument validation. Canonical known tool names additionally retain secondary `(tool, caller)` buckets; missing, malformed, empty, oversized, case-variant, and unknown names create no name-derived buckets, while unknown `batch_query` inner-slot names share one fixed invalid-slot partition per caller. `CDIDX_MCP_RATE_LIMIT_BUCKET_IDLE_SECONDS` defaults to 900 seconds. At the process-local cap, expired buckets are pruned immediately. Layered acquisition evaluates both partitions under one lock; if the coarse token was charged before secondary denial, `retry_after_ms` covers every required token-refill and capacity boundary. This prevents cross-tool malformed-call burst multiplication and untrusted-name cardinality growth, lets legitimate creation recover at the advertised time, and avoids retaining historical caller identities for the process lifetime (#2824 / #4547).
- **MCP envelope response cap** — `CDIDX_MCP_RESPONSE_MAX_BYTES` defaults to 10 MiB and clamps at 64 MiB. Invalid values fall back to the default; values above the cap are clamped with a stderr warning so operators cannot accidentally disable the JSON-RPC response guard.
- **MCP `batch_query` response cap** — `batch_query` estimates the UTF-8 JSON size of aggregate slot results and stops appending once the response would exceed `CDIDX_MCP_BATCH_RESPONSE_MAX_BYTES` (default: 1 MiB / 1,048,576 bytes; maximum: 10 MiB). Truncated responses include `truncated: true`, `truncated_queries`, and byte-limit metadata so clients can split the batch or lower per-slot limits without parsing prose (#1416). Invalid values fall back to the default, values above the maximum are clamped with a stderr warning, and MCP `status` exposes the effective value under `mcp.limits.batch_response_bytes`.
- **HTTP MCP response and stream caps** — `HttpMcpTransport` caps ordinary JSON response bodies with `CDIDX_MCP_HTTP_MAX_RESPONSE_BYTES` (default: 1,000,000 bytes; maximum: 16,777,216 bytes). Oversized JSON-RPC responses return HTTP 500 with a bounded text diagnostic instead of streaming the payload, while request-loop diagnostics record `request_body_length_unknown` for unknown-length request bodies and `request_body_limit_exceeded` for bodies that cross the request cap. Response and SSE write timeouts use stable diagnostics such as `timeout:http_response_write` and `timeout:sse_write`, and timeout expiry actively aborts the HTTP response so a non-cooperative output stream cannot hold the request or SSE gate indefinitely. JSON-RPC request timeouts carry `timeout_category: "mcp_request"` so callers can distinguish them from caller cancellation.
- **HTTP initialize delivery is fail-closed** — `McpServer` commits initialize state after server-side JSON-RPC serialization, and `HttpMcpTransport` publishes the session before HTTP delivery. If `CDIDX_MCP_HTTP_MAX_RESPONSE_BYTES` rejects that already serialized initialize response, the transport returns HTTP 500 with the new `Mcp-Session-Id` and retains the committed session so no second client can inherit the server state (#4539). This transport limit is intentionally distinct from the pre-commit `CDIDX_MCP_RESPONSE_MAX_BYTES` fallback (#4540).
- **HTTP MCP aggregate request-body budget** — `CDIDX_MCP_HTTP_MAX_IN_FLIGHT_REQUEST_BYTES` bounds process-wide request-body reservations across reads, queued frames, MCP execution, and response completion. It defaults to 64 MiB, is capped at 1 GiB, must be at least the individual request cap, and rejects saturation with classified HTTP 429 instead of allowing queue depth multiplied by per-request size to become the memory bound (#4548).
- **MCP bounded queues and concurrency gates** — HTTP request queue slots are acquired before `TryWrite`; once full, requests are rejected with HTTP 429, `Retry-After: 1`, `X-Cdidx-Mcp-Rejection: request_queue_limit`, and `http_request_queue_rejection_count` rather than blocking an HTTP handler. The request-log queue is best-effort and increments `http_request_log_queue_full_drop_count` / `http_request_log_dropped_count` on saturation. POST handlers and long-lived event streams use independent admission semaphores, report `concurrent_handler_limit` and `event_stream_limit` separately, and expose their effective capacities plus `http_separate_event_stream_handlers` through HTTP health. Limit environment variables use defaults only when absent; a present malformed or out-of-range value fails before listener startup. Transport-owned queue and handler gates are disposed only after bounded shutdown observes every acquired slot returned, because a late handler may still finish after listener teardown. Frame-loop gates are disposed only after EOF drain observes all request tasks, because bounded drain can intentionally leave late tasks running.
- **MCP pagination offset cap** — `references`, `callers`, and `callees` clamp `offset` to 10,000 before executing SQL queries. `tools/list` advertises the maximum in each offset schema, and MCP `status` mirrors it under `mcp.limits.max_pagination_offset`.
- **MCP compact discovery and status projection** — `tools/list` keeps its complete response as both the default and explicit `format: "full"` contract. Opt-in `format: "compact"` replaces long descriptions, schemas, examples, and catalog metadata with bounded summaries and an on-demand full-definition recipe; `names` filters exact enabled tool names only and is capped at 24 names of 128 characters. Opaque continuation cursors preserve compact/name-filtered controls, and name-filtered full responses mark the returned list as scoped while deriving capability metadata from all enabled tools. The compact schema is deliberately non-authoritative and says so in `_meta`. MCP `status.fields` projects exact top-level fields after `format` and optional diagnostic attachments are built, while `api_version` remains part of every structured result. Projection inputs are capped at 32 names, 128 characters each, and 2,048 characters total; unknown names and nested paths fail as `invalid_argument`. Any new discovery or status mode must preserve the no-argument response byte-for-byte and add size/compatibility regression tests.
- **MCP resource-list cursor stability** — `resources/list` emits a fixed-size opaque keyset cursor that binds the last consumed file id to a persisted indexed-file generation and the canonical discovery filters. The reader resolves that id back to the existing source/test/docs bucket plus path ordering inside the same SQLite snapshot. Any file insertion, deletion, or update changes the generation; a later page then returns `-32011` / `index_stale` with `restart_required: true`. Changing `path`, `lang`, or `includeGenerated` between pages returns `-32602` / `resources_list_filters_changed` with the same restart requirement. In either case, the client must omit `params.cursor` to restart instead of continuing across mixed snapshots or filters. Writable legacy databases install the generation row and triggers through the normal read migration before a cursor is issued. A mutable read-only legacy database that cannot prove generation tracking returns `resources_list_generation_unavailable` with `migration_required: true`; a canonical, unambiguous `immutable=1` legacy URI (optionally paired with `mode=ro`) may safely use connection-local generation zero because it cannot change between pages. Encoded, case-variant, whitespace-padded, duplicated, conflicting, or extra query parameters are not trusted as that immutable guarantee. The legacy decimal zero remains a first-page upgrade input, but nonzero decimal offsets cannot prove their source generation and therefore return the same restart-required error; decimal cursors are never emitted. Version-1 opaque cursors remain valid only with the default unfiltered view.
- **MCP file resource discovery** — `resources/templates/list` advertises `cdidx://file-path/{path}` so a client that already knows an exact repository-relative path can construct a `resources/read` URI without paging the repository inventory. Simple URI-template expansion percent-encodes separators and reserved filename characters such as `?` and `#`; the template-only resolver decodes the value once, rejects absolute paths, traversal, backslashes, empty segments, queries, and fragments, then returns the canonical `cdidx://file/<path>` identity. Canonical resource URIs continue to reject encoded separators. `resources/list` accepts `path` as one string or at most 100 strings of at most 1024 characters and 128 wildcard operators each, using the same anchored directory/glob semantics as file queries, plus an exact normalized `lang` filter and `includeGenerated` (default `false`). Generated files also require `includeGenerated: true` for direct reads.
- **MCP resource-list response budget** — `resources/list.params.maxBytes` accepts 4,096 through 1,000,000 bytes and defaults to 1,000,000, matching the default HTTP response-body cap. The effective budget is the minimum of that request, the server-wide MCP envelope cap, and the active HTTP transport response-body cap (when applicable), so a lower configured HTTP cap shapes a valid page instead of rejecting it with HTTP 500. The server measures the complete JSON-RPC envelope, keeps 200 as the candidate ceiling, and stops before the next resource would cross the effective byte budget. For HTTP JSON-RPC batches, the active transport budget covers the complete response array, including brackets and commas, and is divided fairly among response-bearing items; notifications consume no response slot. Each `resources/list` item honors its current share and preserves its request ID in a canonical budget error if even a bounded page cannot fit. State-changing and other non-resource outcomes are never relabeled as retry-safe; an aggregate overflow after execution reports an unknown completion state and forbids automatic retry. `_meta.response_controls` reports the requested/effective budgets, consumed and returned counts, `omitted_resource_count`, bounded reason counts (`resource_uri_too_long` / `resource_exceeds_max_bytes`), `byte_budget_reached`, and `continuation_reason` (`byte_budget`, `item_limit`, or `completed`). A continuation cursor anchors the last consumed database row; a valid resource that did not fit remains unconsumed for the next page, while a resource that cannot fit even on an empty page is consumed and counted so pagination cannot livelock.
- **MCP array argument bounds** — MCP string-array filters such as `path`, `project`, `excludePaths`, and mixed `names` arrays reject invalid entries instead of silently dropping them. Arrays are capped at 100 entries and each entry is capped at 4096 characters; `batch_query` reports these validation failures per slot with `request_index` and `ok: false`.
- **MCP schema lock-down** — Every tool `inputSchema` includes `additionalProperties: false`, and `tools/call` mirrors that contract by rejecting unknown argument names with `-32602` / `invalid_argument` instead of silently defaulting misspelled fields.
- **MCP stability markers and naming** — Every tool advertises `x-stability` (`stable`, `experimental`, or `deprecated`). MCP structured payload keys use snake_case, matching the CLI JSON contract; do not add camelCase aliases for new fields.
- **MCP language-support clauses** — Every advertised MCP tool description ends with a `Language support:` clause generated through `McpServer.CreateToolDefinition`. Graph tools enumerate `ReferenceExtractor.GetSupportedLanguages()`, symbol tools enumerate `SymbolExtractor.GetSupportedLanguages()`, and file/content tools point at the detected-language catalog used by `cdidx languages`, so `tools/list` stays aligned with the runtime registries instead of carrying hand-maintained prose.
- **MCP tool annotations** — All tools emit `annotations` with `readOnlyHint`, `destructiveHint`, `idempotentHint`, and `openWorldHint` per the MCP spec, so AI clients can auto-approve safe read-only queries.
- **MCP server instructions** — The `initialize` response includes an `instructions` string with tool-selection guidance so AI clients can choose the right tool on first connection.
- **Per-deployment MCP tool enablement** — `cdidx mcp` honors two environment variables so operators can narrow the exposed tool surface without a code change (#1561). `CDIDX_MCP_TOOLS_ALLOW=<csv>` is a strict allowlist; if set, only those tools appear in `tools/list` and are dispatched by `tools/call`. `CDIDX_MCP_TOOLS_DENY=<csv>` removes individual tools from the default-all-enabled set. Allow wins over deny when both are set. The single source of truth for known tool names is `McpToolFilter.KnownToolNames`, which is checked against by both the `tools/list` filter and the `tools/call` gate (and the per-slot guard inside `batch_query`). `BuildInstructions` is also gate-aware: scoped deployments never recommend a disabled tool in the `initialize` instructions, so the guidance stays in sync with the advertised surface. Top-level `tools/call` on a disabled known tool returns `-32601 Tool not enabled: <name>`; `batch_query` envelopes succeed but each disabled-tool slot carries a `code: -32601` field alongside the `error` string so clients can branch on the code without parsing prose. Truly unknown names still fall through to the existing `-32602 Unknown tool` path so operator-disabled tools remain distinguishable from typos. Tool names compare case-insensitively, unknown env-var entries are filtered against the known set (an allowlist of only-unknown names intentionally exposes nothing rather than silently disabling the gate), and the default — no env vars set — keeps every tool enabled so existing deployments are unaffected.
- **Backward-compatible symbol schema** — Opening an older DB with a newer binary auto-adds missing symbol columns when possible, including hotspot-family metadata such as `container_qualified_name` and `family_key`. If a read path cannot migrate the DB in place, symbol queries fall back to the legacy column set instead of crashing.
- **Bounded hotspot aggregation** — `DbWriter` maintains `hotspot_reference_counts` as compact per-file logical-reference totals. Limited hotspot readers use its rank index to select a fixed bounded candidate frontier before the non-SQL, SQL exact, SQL leaf, ambiguity, and target-family joins; they do not rematerialize the complete `symbol_references` graph on every query. Logical-site identity excludes raw aliases, mutations refresh cross-file context dependents and demote reference-identity trust transactionally, and reader/writer aggregate SQL is cancellation-interruptible. Writable legacy databases create and backfill the table transactionally, while immutable legacy readers retain the raw-reference compatibility path.
- **Manual arg parsing** — `System.CommandLine` was removed to reduce dependencies. Simple switch-based parsing.
- **SHA256 checksums** — Computed from raw file bytes and stored per file. Used as a fallback for change detection when timestamps differ (e.g. after `git checkout`).
- **UTF-8 with fallback** — Invalid UTF-8 bytes are replaced with U+FFFD rather than failing the entire file.
- **Portable trusted Git selection** — Git subprocesses select only validated known installation paths or the process-only `CDIDX_GIT_EXECUTABLE` absolute-path override; they never resolve an arbitrary `git` from `PATH`. Explicit overrides fail closed unless the target is a regular non-symlink/non-reparse/device file named `git` (`git.exe` on Windows). POSIX candidates and canonical ancestors must be owned by the effective user or root and have no group/other write bits, except for root-owned sticky ancestors such as `/tmp` and multi-user Nix stores; Windows candidates and ancestors must have trusted owner/write ACLs and the executable must contain a valid PE image. Every accepted candidate must successfully return a `git version` identity from a bounded `git --version` probe. CLI and MCP `status.git_executable` report the sanitized source, acceptance, stable rejection reason, owner-only-write result, Unix mode, owner category, owner/ancestor trust, and executable-probe result, while accepted environment overrides also appear in `trust_overrides[]`.
- **Worktree-aware git exclude** — `.cdidx/` is auto-added to `.git/info/exclude`. In a worktree, `.git` is a file (not a directory), so the worktree root has no `.git/info/exclude`. `GitHelper.ResolveGitCommonDir()` chases references to find the shared `.git/`:

  ```
  # Normal repo — .git is a directory
  /projects/my-app/                   ← project root
  ├── 📂 .git/                        ← directory
  │   └── 📂 info/
  │       └── exclude                 ← write here
  └── 📂 .cdidx/
      └── codeindex.db

  # Worktree — .git is a file
  /projects/my-app/                   ← main repo
  └── 📂 .git/                        ← shared git dir
      ├── 📂 info/
      │   └── exclude                 ← write here
      └── 📂 worktrees/
          └── 📂 feature-branch/
              └── commondir           ← contains "../.."

  /projects/my-app-feature/           ← worktree root
  ├── .git                            ← FILE: "gitdir: /projects/my-app/.git/worktrees/feature-branch"
  └── 📂 .cdidx/
      └── codeindex.db
  ```

  Resolution: canonicalize and validate every existing component of the `.git` directory/file boundary → read a single-link regular `.git` file → parse `gitdir:` → validate every component of its regular directory target → read a single-link regular `commondir` file → resolve `../..` relative to `feature-branch/` dir (`feature-branch/` → `..` → `worktrees/` → `..` → `.git/`) → validate the common directory → atomically replace `info/exclude`. Untrusted symlink/reparse-point redirection, device, multi-link file, wrong-entry-kind, or unreadable metadata entries fail closed before any metadata write; immutable root-owned POSIX system links such as `/var` are resolved and revalidated.

- **Cross-compiled linux-arm64 without runtime smoke test** — The `release.yml` workflow cross-compiles `linux-arm64` on an x64 runner (`dotnet publish -r linux-arm64 --self-contained`). Tests are skipped because the runner cannot execute ARM binaries natively. Ideally, a QEMU-based smoke test (`cdidx --version`) would run before publishing, but GitHub Actions free-tier runners do not include QEMU or ARM runners. Adding a QEMU setup step is possible but increases CI complexity and wall-clock time for every release. .NET's cross-compilation is an officially supported and widely used feature, so the risk of a broken artifact is low in practice. If ARM-specific failures are reported in the future, adding `docker run --platform linux/arm64` with QEMU should be the first mitigation step.
- **CLI / MCP only — no public library API (#1557)** — The `cdidx` assembly is shipped as `OutputType=Exe` with `PackAsTool=true` and is published as a .NET global tool, not as a referenceable library. The supported, versioned surfaces are the `cdidx` CLI (including its `--json` output) and the `cdidx mcp` JSON-RPC server. `public` types on the assembly (for example `CodeIndex.Database.DbReader` and DTOs in `CodeIndex.Models` / `CodeIndex.Database`) exist to satisfy CLI / MCP composition and the `CodeIndex.Tests` `InternalsVisibleTo` boundary — they are implementation details that may change, move, or become `internal` without a deprecation cycle. Embedders are expected to depend on the CLI / MCP / JSON surfaces, not on the assembly. See [INTEGRATION_POLICY.md — API Surface and Library Use](INTEGRATION_POLICY.md#api-surface-and-library-use). If a real library API is ever justified, it will be carved out as a separate package with its own interface and versioning contract rather than being implied by whatever happens to be `public` on this assembly.
- **Extractor plugins (#1937)** — `CodeIndex.Indexer.Extensibility.ISymbolExtractor` and `IReferenceExtractor` are the only supported assembly-extension surface. `cdidx` discovers trusted plugin DLLs in the user-owned `~/.cdidx/plugins/` directory by default. Workspace `.cdidx/plugins/` DLL discovery is fail-closed unless the process sets `CDIDX_TRUST_WORKSPACE_PLUGINS=1` (also accepts `true`, `yes`, or `on`), because loading a workspace DLL executes checkout-provided code. A plugin assembly must declare `[assembly: CdidxPlugin(minApiVersion: 1, maxApiVersion: 1)]` and expose a public parameterless type implementing one or both interfaces. Set `FileExtensions` when the plugin owns new file extensions so `FileIndexer` can route those files to the plugin language. Plugins execute in a bounded worker rather than the parent, but this is process isolation rather than a security sandbox; install only trusted local DLLs. This narrow contract lets teams add DSL-specific symbols/references without forking CodeIndex, but it is not a general library/SDK embedding API.

  Plugin DLL discovery is bounded to `ExtractorPluginRegistry.MaxPluginAssemblyCandidatesPerDirectory` candidates per directory and `ExtractorPluginRegistry.MaxPluginAssemblyCandidatesTotal` candidates per process. Each candidate must also be no larger than `ExtractorPluginRegistry.MaxPluginAssemblyBytes` bytes. Discovery truncation and oversize skips are reported through `status --json` / MCP `status` `extractors.diagnostics`.

  Registrations are resolved from immutable workspace snapshots keyed by the
  normalized workspace identity and language. Precedence is deterministic and
  exposed by `extractors.registration_precedence`, highest first: built-in,
  user plugin, user pattern, workspace plugin, workspace pattern. Workspace
  plugin assembly contexts and diagnostics are owned by their snapshot;
  replacing one snapshot starts unloading only that workspace's old contexts
  and cannot rewrite another workspace's active registrations. Reference purge,
  status/language reporting, and database graph predicates resolve supported
  languages from the active workspace snapshot. Long-running hosts retain at
  most 32 workspace snapshots with LRU eviction. Replacement, eviction, and MCP
  shutdown terminally retire their snapshots; an in-flight plugin load that
  reaches a retired state rejects its late commit and unloads its local context.

<a id="reference-kind-filtering-matrix"></a>

## Reference-kind filtering matrix

Different graph entry points walk different `reference_kind` subsets by design. The split mirrors **call graph vs. dependency graph**: default `callers` and `callees` are restricted to executable call, construction, and subscription semantics; `hotspots` and `impact` retain their broader dependency-oriented traversal, including closure and generic-invocation edges. `deps` and `impact`'s heuristic file-level fallback model the compile-time dependency graph and include metadata edges so that `[JsonConverter(typeof(User))]` and `@Inject(User.class)` still surface as real dependencies of `User`. Both directions of `deps` share the same SQL function (`DbReader.GetFileDependencies`), so forward and reverse walks always emit the same kind set.

| Entry point | Direction | Reference kinds walked | Backing function |
| --- | --- | --- | --- |
| `references` (CLI / MCP) | symbol-centric | all `reference_kind` rows; narrowed by `--kind` when provided | `DbReader.GetReferences` |
| `callers` / `callees` (default) | source ↔ container | `('call', 'instantiate', 'subscribe', 'unsubscribe', 'razor_event_binding')` (= `CallableReferenceKindsSql`); public rows canonicalize event variants to `subscribe` | `DbReader.GetCallers` / `DbReader.GetCallees` |
| `impact` callers mode | transitive forward (BFS) | `('augmentation', 'call', 'instantiate', 'generic_type_argument', 'subscribe', 'unsubscribe', 'razor_event_binding', 'friend', 'consumes_hook', 'capture', 'project_reference')` via `GetCallersExact`; solution project paths also match their project container names | `DbReader.GetTransitiveCallers` |
| `impact` file-hint fallback | reverse (definition file → dependent files) | all kinds; metadata-only rows gated by `IsMetadataTargetUnambiguous` + structured-type evidence | `DbReader.GetFileDependencyHintsToResolvedType` |
| `deps` (default = forward) | source file → target file | all kinds; metadata rows require class-like + metadata-eligible targets (`has_metadata_target_kind`) and a unique resolution (`target_ambiguity`); MSBuild imports/project references resolve paths relative to the declaring project instead of matching shared package names | `DbReader.GetFileDependencies` |
| `deps --reverse` | target file → source file | same as forward `deps` (same SQL) | `DbReader.GetFileDependencies` |

`deps --symbol`, `--symbol-family`, and `--suppress-noise` are pushed into the logical-reference and target-candidate SQL scopes before candidate ranking and `--limit`; cycle and cross-workspace reads apply the same filters before their candidate limits. Consequently, `reference_count`, ranking, and the `symbol_filter` before/after counters describe the SQL-filtered scope rather than the whole pre-filter workspace. Long SQLite dependency reads also register command cancellation with the query token.

Practical consequence: `impact <ClassName>` on a class-like symbol returns the heuristic file-dependency-hint fallback (with metadata edges) when no member-level callers exist, whereas default `callers <ClassName>` returns only executable edges. Both are correct under their own contracts; counts will not match. To reconcile, run `references <ClassName> --kind attribute` (or `annotation`), or pass an explicitly supported non-default kind to `callers` / `callees`, to surface edges that the default call graph intentionally drops.

`impact --json` and MCP `impact_analysis` expose zero-result diagnostics as structured routing fields. `zero_result_reason` remains the compact terminal reason; `impact_failure_chain` lists failed preconditions or traversal states in order, using values such as `definition_not_found`, `callable_filter_fails`, `multiple_definitions`, `multiple_definition_files`, `graph_unavailable`, `depth_requested_zero`, and `no_callers`. `suggestion_type` classifies the prose `suggestion` as `resolution`, `traversal`, or `precondition`. CLI `impact --strict` exits with `FeatureUnavailable` when the chain contains a resolution or precondition failure, but still treats a genuine `no_callers` traversal result as success.

`definition --json` and MCP `definition` results may include `disambiguator` for C# definitions when existing symbol metadata can distinguish otherwise identical names. Current values include `overload(...)` for method signatures, `partial-class` / `partial-struct` / `partial-interface`, and `extension-method-on(<receiver>)`. Languages without overload or receiver metadata omit the field.

## Cloud Claude Code bootstrap (no .NET SDK)

> **Maintainers / authorized operators only** — see [MAINTAINERS.md](MAINTAINERS.md). End users can skip this section.

This section explains — in detail — the mechanism by which a cloud AI coding
session (for example Claude Code or OpenAI Codex) that follows
[CLOUD_BOOTSTRAP_PROMPT.md](CLOUD_BOOTSTRAP_PROMPT.md) ends up with a working
`cdidx` binary plus a working SQLite runtime, even though the container has no
.NET SDK installed. Understanding each layer matters because every regression
in the install path is invisible to anyone who can just run `dotnet build` —
the cloud session is the canary for the published release experience.

The bootstrap prompt now documents three cloud-specific installer knobs that
matter for maintainers. `CDIDX_GITHUB_BASE_URL` and
`CDIDX_GITHUB_API_BASE_URL` let restricted-egress sessions swap the release
download host and latest-release API host independently. The built-in
`--self-test-local-mirror` path is intentionally isolated from the real
`~/.local/bin` install unless a non-empty `CDIDX_INSTALL_DIR` is provided. When
a non-empty `CDIDX_INSTALL_DIR` *is* provided, the self-test now refuses to run
against risky targets — well-known system paths (`/usr/local/bin`, `/usr/bin`,
`/opt/homebrew/bin`, `/opt/local/bin`), `$HOME/.local/bin`, and any directory
that already contains a `cdidx` executable — because the mock payload only
responds to `--version` and would silently cripple a real install. Unset
`CDIDX_INSTALL_DIR` to fall back to the isolated temp dir, or pass
`--self-test-allow-overwrite` on the CLI when you genuinely need to inspect
the mock layout in place. The escape hatch is intentionally CLI-only — a
`SELF_TEST_ALLOW_OVERWRITE=1` value inherited from the caller's environment
is ignored, so a stale env var in the user's shell or CI cannot silently
reintroduce the bypass. The self-test also still requires `python3` plus
permission to bind a loopback listener on
`127.0.0.1`; some sandboxes forbid that outright, in which case the self-test
must run in a less-restricted shell or against a pre-hosted mirror.

Codex cloud sessions have one extra repository-local constraint: the tracked
`.codex/hooks.json` Bash guard blocks generic network downloads and generic
global `cdidx` use. The guard therefore has a deliberately narrow bootstrap
exception for official installer and repo-local installer bootstrap only. It
allows the exact
`curl -fsSL https://raw.githubusercontent.com/Widthdom/CodeIndex/.../install.sh
| bash` shape and direct repo-local `bash ./install.sh ...` invocations with
the installer-supported flags (`--doctor`, `--self-test-local-mirror`,
`--self-test-allow-overwrite`, and `--reinstall-real`). It also allows the
exact absolute-path resolver-print command and fixed JSON-RPC `initialize` pipe
with the fully expanded installed path printed in `CLOUD_BOOTSTRAP_PROMPT.md`.
It still rejects
arbitrary download-and-execute commands, unknown installer flags, shell-control
wrappers around `install.sh`, bare `cdidx`, `~/.local/bin/cdidx`, and
`$HOME/.local/bin/cdidx`, plus other path-qualified global `cdidx` binaries and
`$CDIDX` / `${CDIDX}` variable calls. After installation, Codex operators should
resolve `$HOME/.local/bin/cdidx` to its fully expanded absolute path and paste
that literal path into every no-SDK code-search command, matching the tripwire
guidance in `CLOUD_BOOTSTRAP_PROMPT.md`. This exception only unblocks the
repository guard; it cannot bypass upstream proxy or egress policy denies such
as `CONNECT tunnel failed, response 403`.

For pre-release validation beyond the mock self-test, `install.sh
--reinstall-real <version>` downloads and installs the requested release tag
into an isolated `/tmp/cdidx-reinstall-real.XXXXXX` dir, runs `cdidx --version`
and verifies the reported version matches the requested tag, then builds a
tiny scratch Python project in `/tmp/cdidx-reinstall-scratch.XXXXXX` and runs
`cdidx . --db <scratch>/.cdidx/codeindex.db` followed by
`cdidx search greet --db <...>` against it and confirms the match payload
surfaces the scratch symbol. Human-readable output is used on purpose so this
validation covers the default user path. Current release binaries are trimmed
with source-generated CLI JSON DTOs, so `--json` is expected to work; the
`JsonOutputFailure` path is only a fallback for old or custom binaries that
miss a serializer registration. This exercises the real indexing path (symbol
extraction, native SQLite load, FTS5) on the freshly-downloaded binary —
`--self-test-local-mirror` only stubs `--version` and would miss those
regressions. `CDIDX_INSTALL_DIR` is
intentionally ignored by `--reinstall-real` so a broken build can never
clobber a working real install, and both temp dirs are cleaned up on normal
exit and on failure via `trap`.

For pre-install network diagnostics, `install.sh --doctor [vX.Y.Z]` prints
the active proxy environment (routing each value through the
`redact_proxy_userinfo` helper so URL userinfo such as
`http://alice:hunter2@proxy:8080` is surfaced as
`http://<redacted>@proxy:8080`, keeping the host/port visible for
reachability diagnosis without leaking credentials to shared logs / issues /
support transcripts) and probes the three upstream URLs the installer would
hit for the requested version (or the version recorded in `version.json`
when no explicit version is given): the latest-release API endpoint, the
release tarball asset, and the `sha256sums.txt` checksums asset. Each probe
uses `curl -sSI` so a multi-MB release tarball is not actually downloaded,
and on `CONNECT tunnel failed, response 403` (curl exit 56) the doctor
reuses the existing `is_proxy_tunnel_403` advisory so users get the
canonical "the deny is happening in an upstream proxy/egress policy before
TLS, route substitution alone will not fix it, ask for at least one
artifact host path to be allow-listed, or point
`CDIDX_GITHUB_BASE_URL` / `CDIDX_GITHUB_API_BASE_URL` at a reachable
internal mirror" next step. The doctor installs nothing, never writes
outside `/tmp`, never short-circuits on the first failure (so a single
network-policy deny never hides others), and exits 0 only when every probe
returns 2xx/3xx.

For post-install troubleshooting on "silent" hosts that swallow terminal
stderr, distributed/non-development executions also mirror stderr plus minimal
lifecycle breadcrumbs to a per-user daily log. The log path follows
`CDIDX_GLOBAL_TOOL_LOG_DIR`, then `XDG_STATE_HOME/cdidx/logs/`,
`XDG_CACHE_HOME/cdidx/logs/`, `XDG_RUNTIME_DIR/cdidx/logs/`, then the
platform default: `%LOCALAPPDATA%\cdidx\logs\` on Windows,
`~/Library/Logs/cdidx/` on macOS, or `~/.local/state/cdidx/logs/` on Linux.
If every platform candidate is unavailable, the final temp fallback is a
hashed per-user `cdidx-u.../logs` directory under the OS temp root. Each
candidate is probed with a create/write/delete round trip before the logger
commits to it, so read-only state/cache/runtime mounts fall through to the
next candidate instead of losing the first log write. The file name is
`stderr-YYYYMMDD.log`, timestamps inside the file are ISO-8601 UTC
(`yyyy-MM-ddTHH:mm:ss.fffZ`) using invariant culture, and the logger keeps
only the newest 30 daily files. `CDIDX_LOG_FORMAT` / `--log-format` switch
between text and JSONL, `CDIDX_LOG_RETAIN` / `--log-retain-count` set retained
file count, and `CDIDX_LOG_MAX_SIZE_MB` / `--log-max-size-mb` or
`CDIDX_GLOBAL_TOOL_LOG_MAX_BYTES` set the size-rotation cap. The default size
cap is 50 MiB and accepted values are capped at 1024 MiB / 1 GiB.
Repository-local development runs from
`src/CodeIndex/bin/...` and `tests/.../bin/...` are excluded by default so
ordinary build/test cycles do not accumulate persistent logs. Set
`CDIDX_DISABLE_PERSISTENT_LOG=1` to opt out entirely; the toggle accepts `1`,
`true`, `yes`, or `on` case-insensitively. Use
`CDIDX_GLOBAL_TOOL_LOG_DIR` to redirect the log directory during testing or
packaging.
Set `CDIDX_FORCE_GLOBAL_TOOL_LOG=1` to force lifecycle logging for local
package smoke tests or launcher diagnostics even when the executable path looks
like a development build; `CDIDX_DISABLE_PERSISTENT_LOG` still wins when both
are set.
Unhandled exceptions keep stderr concise but write the full exception chain and
stack trace to the lifecycle log for post-mortem diagnostics. Per-file index
failures also record the active extraction phase and a bounded safe detail;
isolated symbol-worker failures include the exception category and a redacted
origin frame, exceptions retain their original extraction stack when crossing
the parallel full-scan boundary, and oversized stack frames retain their
redacted source-line suffix instead of truncating it after a long method signature. Logged command
arguments are minimally redacted by default: secret-looking `--flag=value`
pairs, values following secret-looking flags, URI passwords, and long token-like
hex/base64 strings are replaced with `<redacted>`. `CDIDX_LOG_REDACT=none`
preserves raw arguments for controlled local debugging, while
`CDIDX_LOG_REDACT=full` also replaces path-like arguments with a stable hash.

### The moving parts

Four artifacts have to end up in three correct places for `cdidx` to work,
plus one supply-chain attestation that is published next to the binaries but
not installed:

| Artifact | Origin | Final location | Required by |
| --- | --- | --- | --- |
| `cdidx` (trimmed self-contained single-file binary) | `dotnet publish -r <rid> --self-contained -p:PublishSingleFile=true -p:PublishTrimmed=true` in `release.yml` | `$HOME/.local/bin/cdidx` | User's `PATH` |
| `libe_sqlite3.so` (Linux) / `libe_sqlite3.dylib` (macOS) | Native asset from the `Microsoft.Data.Sqlite` (SQLitePCLRaw) NuGet, copied into the publish output | `$HOME/.local/bin/` (next to the binary) | `SqliteConnection` static ctor → P/Invoke |
| `version.json` | Repo root; `CodeIndex.csproj` copies it to the publish output as a `Content` item | `$HOME/.local/bin/` (next to the binary) | `ConsoleUi.LoadVersion()` via `AppContext.BaseDirectory` |
| `sha256sums.txt` | `release.yml` computes it after packaging (covers tarballs/zips and the SBOM) | Downloaded to a temp dir during install, not kept | `install.sh` integrity check; SBOM consumers |
| `cdidx.sbom.cdx.json` | `release.yml` runs `dotnet CycloneDX` once on the `linux-x64` lane (content is RID-independent) and uploads it as a `CodeIndex-sbom` artifact; `create-release` copies it into `release-files/` so `sha256sums.txt` covers it | Published as a GitHub release asset; not installed on user machines | Compliance reviews (SOC2, FedRAMP-style); supply-chain scanners (Snyk, Trivy, Grype) |

The first three are packaged into `CodeIndex-<rid>.tar.gz` by
`release.yml`. A clean install has to reproduce that layout on the user's
machine. Miss any of the runtime files and one of the symptoms in the
diagnostic table below appears.

```mermaid
flowchart LR
    subgraph Repo["Repo (source of truth)"]
        V[version.json]
        C[CodeIndex.csproj]
    end
    subgraph CI["GitHub Actions — release.yml on v* tag"]
        P["dotnet publish<br/>--self-contained<br/>PublishSingleFile=true<br/>PublishTrimmed=true"]
        T["tar czf<br/>CodeIndex-&lt;rid&gt;.tar.gz"]
        H["sha256sums.txt"]
    end
    subgraph Tarball["Release tarball payload"]
        B[cdidx]
        L["libe_sqlite3.so<br/>(or .dylib on macOS)"]
        J[version.json]
    end
    subgraph User["User machine after install.sh"]
        UB["~/.local/bin/cdidx"]
        UL["~/.local/bin/libe_sqlite3.so"]
        UJ["~/.local/bin/version.json"]
    end
    V -->|read at build time| C
    V -->|copied via Content item| P
    C --> P
    P --> B
    P --> L
    P --> J
    B --> T
    L --> T
    J --> T
    T -->|install.sh: download + verify| H
    T -->|install.sh: extract + copy| UB
    T --> UL
    T --> UJ
```

### Phase 1 — The one-liner download

Command (from the prompt):

```bash
curl -fsSL https://raw.githubusercontent.com/Widthdom/CodeIndex/main/install.sh | bash
```

What `install.sh` does, in order (see `install.sh`):

1. **Preflight, then detect platform.** The installer checks one declared
   dependency list (`curl`, archive/temp/checksum tools, and the `find` / `sed`
   / `sort` manifest traversal tools) before release work. `uname -s` /
   `uname -m` are then normalized to the
   `<os>-<arch>` RID and validates it against the release workflow's
   published asset list (`linux-x64`, `linux-arm64`, `osx-arm64`,
   `win-x64`, `win-arm64`; see [Platform Support](docs/platform-support.md))
   before any release-asset download. Alpine / musl is rejected up front
   with an actionable error because the self-contained binary links against
   glibc. Unsupported RIDs such as `osx-x64` fail with the detected RID,
   supported list, NuGet global-tool and source-build alternatives, and the
   issue link for requesting official platform support.
2. **Inspect existing metadata without executing the binary.** If
   `INSTALL_DIR/version.json` exists, the installer reads its version before
   any network work. It never runs an unvalidated existing `cdidx` merely to
   decide whether that binary may be reused.
3. **Resolve version.** With an explicit argument, the installer accepts
   either the `v`-prefixed or bare form (`v1.8.0` or `1.8.0`). With no
   argument, it calls the GitHub API
   (`/repos/Widthdom/CodeIndex/releases/latest`), prefers `jq` when
   available for `tag_name` parsing, and falls back to the existing
   `grep` + `sed` extraction for portability. The installer then compares
   that latest release tag to any healthy existing install and skips the
   download only when the installed version matches the latest tag, the
   persisted release checksum receipt reauthenticates against the pinned
   release workflow/tag or pinned GPG signer, that receipt authenticates the
   installed `MANIFEST.sha256`, and every installed critical/legal artifact
   rehashes to it. The receipt, manifest, binary, `version.json`, native SQLite
   asset, and notices must be regular files; the binary must remain executable.
   The differing artifact is reported before the replacement is downloaded and
   staged. Promotion is transactional with rollback but uses per-file moves;
   concurrent `cdidx` invocations must be avoided during that short maintenance
   window. Broken `v0.0.0` installs or installs missing required adjacent
   assets are therefore reinstall targets instead of idempotent successes. HTTP
   failures are classified explicitly (`403` rate limit vs `404` vs
   `5xx` vs real curl network errors) instead of collapsing everything
   into a generic “check your network connection” message.
4. **Reinstall or switch when an explicit version is requested.** A
   no-argument rerun still targets the latest release, but an explicit
   target version always proceeds into reinstall/switch logic.
   Same-version explicit requests force a reinstall, while broken
   `v0.0.0` installs or same-version installs missing required assets
   are also treated as replacements, which is the desired behaviour.
5. **Download.** Fetches `CodeIndex-<rid>.tar.gz` and `sha256sums.txt`
   into a `mktemp -d` directory trap-cleaned on exit.
6. **Verify provenance, then checksum.** The default strict policy requires
   either a GitHub attestation for `sha256sums.txt` or a valid GPG signature
   whose signer matches `CDIDX_RELEASE_GPG_FINGERPRINT`. Only after that
   independent proof succeeds does the installer trust the manifest, compute
   SHA256 via `sha256sum` / `shasum` / `openssl`, and compare the archive.
   `CDIDX_VERIFY_POLICY=compat` is an explicit audited opt-in that warns before
   continuing without provenance. Any checksum mismatch still aborts before
   files are placed into `INSTALL_DIR`.
7. **Extract into a dedicated subdirectory.** `tar xzf … -C
   ${tmpdir}/extract` so the unpacked payload does not mix with the
   downloaded archive or checksum file.
8. **Validate the full extracted payload before copying anything.**
   Requires `cdidx`, `version.json`, and the platform-specific native
   SQLite library (`libe_sqlite3.so` on Linux, `libe_sqlite3.dylib` on
   macOS) to all be present in the extracted tarball. Missing files
   abort before touching `INSTALL_DIR`, so a healthy install is not
   replaced by a partial payload.
9. **Persist the authenticated receipt and manifest, then stage the asset set.**
   After validation succeeds, the installer copies `cdidx`, the verified
   `MANIFEST.sha256`, the independently authenticated release checksum receipt
   (and detached signature when available), plus the
   required adjacent runtime assets into a staging directory under
   `INSTALL_DIR`, marks the receipt and manifest read-only and the staged
   binary executable. It then renames existing files into a backup
   directory and promotes the staged assets
   into place with runtime assets first and the binary last. If any
   promotion step fails, the installer rolls back from the backup so a
   healthy install is not left half-updated. This prevents a
   “successful” install that would later crash with `v0.0.0` or
   `DllNotFoundException`.
10. **PATH guidance.** If `INSTALL_DIR` is not on `PATH`, emit the
   shell-specific snippet (`bashrc` / `zshrc` / `fish_add_path`).

After a successful current-release run, `ls -a $HOME/.local/bin/` shows `cdidx`,
`libe_sqlite3.so` (on Linux), `version.json`, `MANIFEST.sha256`, and the hidden
release checksum receipt side-by-side. Any other current-release layout is a bug; legacy releases that
predate payload manifests can still install but are not reusable without a
fresh download.

```mermaid
sequenceDiagram
    autonumber
    participant U as User shell
    participant S as install.sh
    participant API as api.github.com
    participant GH as github.com/releases
    participant TMP as mktemp -d
    participant FS as ~/.local/bin
    U->>S: curl | bash
    S->>S: detect_platform (uname)
    Note over S: reject musl / osx-x64 early
    S->>FS: read existing version.json (do not execute cdidx)
    alt no explicit version
        S->>API: GET /releases/latest
        API-->>S: tag_name (e.g. v1.8.0 — actual value per GitHub Releases)
        alt manifest-rehashed existing install matches latest
            S-->>U: exit 0 after latest-version comparison
        else upgrade or repair needed
            S->>FS: switch/reinstall to resolved latest version
        end
    else explicit version requested
        S->>S: normalize explicit version
        S->>FS: same version still proceeds into reinstall
    end
    S->>TMP: mkdir, trap cleanup
    S->>GH: GET CodeIndex-{rid}.tar.gz
    S->>GH: GET sha256sums.txt
    S->>S: sha256sum / shasum / openssl verify
    S->>TMP: tar xzf -C extract/
    S->>S: validate cdidx + required assets in extract/
    S->>FS: copy required files + MANIFEST.sha256 into .cdidx-stage.*
    S->>FS: chmod +x staged cdidx
    S->>FS: mv existing files into .cdidx-backup.*
    alt backup move fails
        S->>FS: restore already-backed-up files
        S-->>U: abort before replacing current install
    else backup complete
        S->>FS: mv staged runtime assets into place
        S->>FS: mv staged cdidx last
        alt promotion move fails
            S->>FS: remove newly promoted files
            S->>FS: restore backed-up files
            S-->>U: rollback and abort
        else success
            S-->>U: "Installed cdidx to ~/.local/bin/cdidx"
        end
    end
```

### Phase 2 — First invocation: `cdidx --version`

`Program.cs:12` calls `ConsoleUi.LoadVersion()` as the very first line of
`Main`. That method (`src/CodeIndex/Cli/ConsoleUi.cs:268-285`) does:

1. `AppContext.BaseDirectory` — for a single-file self-contained
   executable on Linux this resolves to the directory containing the
   extracted `cdidx` binary (.NET's single-file host extracts to a temp
   location but exposes the *apphost* directory — i.e. `~/.local/bin/` —
   through `AppContext.BaseDirectory`).
2. `Path.Combine(exeDir, "version.json")`. If present, parse JSON and
   return the `version` string.
3. Fallback: try `AppDomain.CurrentDomain.BaseDirectory`.
4. Final fallback: return the literal string `"0.0.0"`.

If the installer forgot to place `version.json` next to the binary,
`--version` prints `cdidx v0.0.0` — which is not just cosmetic. The same
string is used in the MCP `serverInfo.version` and in
`status --json`'s `version` field, so AI clients see a nonsense version
too. This is the most visible way a broken install path surfaces.

```mermaid
flowchart TD
    A[Program.Main] --> B["ConsoleUi.LoadVersion()"]
    B --> C{"exeDir/version.json<br/>exists?"}
    C -->|yes| D[Parse JSON → read 'version']
    C -->|no| E{"CurrentDomain.BaseDirectory/<br/>version.json exists?"}
    E -->|yes| D
    E -->|no| F["return '0.0.0' (fallback)"]
    D --> G["return version string"]
    G --> H["cdidx --version prints 'cdidx vX.Y.Z'<br/>MCP serverInfo.version = X.Y.Z<br/>status --json .version = X.Y.Z"]
    F --> I["cdidx --version prints 'cdidx v0.0.0'<br/>→ broken install-path signal"]
```

### Phase 3 — First SQLite-touching command: `cdidx .` (index)

This exercises the entire stack end-to-end.

1. **Binary boots.** The self-contained host resolves the managed
   entrypoint (`Program.Main`).
2. **CLI routing.** `Program.cs` dispatches to
   `IndexCommandRunner.Run(args, jsonOptions)`.
3. **DB path resolution.** `DbPathResolver` computes
   `<projectPath>/.cdidx/codeindex.db` unless `--db` overrides it, and
   creates the `.cdidx/` directory. The same helper also resolves
   query-time workspace roots for `status` / `map` / `inspect`:
   implicit queries without `--db` trust the default `.cdidx/codeindex.db`
   sibling path for the current workspace, while explicit `--db` values
   fall back to `codeindex_meta.indexed_project_root` when present and
   otherwise leave `project_root` / `git_head` / `git_is_dirty` /
   `indexed_head_commit` / `worktree_head_changed` unset on legacy DBs that
   have no stored root metadata, even when the explicit path itself
   looks like `.../.cdidx/codeindex.db`. `WorkspaceMetadataEnricher` compares
   the runtime HEAD against the latest successful index stamp in
   `indexed_head_sha` when available, falling back to the older full-scan-only
   `indexed_head_commit` only for legacy DBs, and surfaces
   `worktree_head_changed=true` so `status` can WARN that the worktree branch /
   HEAD switched since the index was built (issues #1512 and #3367). In
   addition, after a successful index (full scan AND partial update),
   `IndexCommandRunner` stamps `codeindex_meta.indexed_head_sha`,
   `indexed_head_branch`, and `indexed_head_timestamp` (best-effort; a
   failing `git` invocation never fails the index), and `status` surfaces
   them as `indexed_head_sha` / `indexed_head_branch` / `indexed_head_timestamp`
   plus a derived `commits_ahead_of_indexed_head` count computed at query
   time via `git merge-base --is-ancestor` + `git rev-list --count`.
   `commits_ahead_of_indexed_head` is `null` when the indexed SHA is
   unknown or no longer reachable from `HEAD` (force-push / divergent
   history) so consumers do not misread a divergent worktree as fresh.
   Unlike `indexed_head_commit` (#1508 / #1512, full-scan only), the #1509
   triple updates on every successful full, `--files`, `--commits`, or
   `--changed-between` run so cross-session drift is always detectable
   regardless of update mode. Failed or rolled-back runs keep the prior triple.
   Query-envelope `metadata.indexed_at_head_sha` and MCP/status
   `indexed_head_sha` read this same latest-successful stamp, with a
   full-scan-only `indexed_head_commit` fallback for legacy databases.

4. **Open SQLite.** `IndexCommandRunner` constructs
   `new DbContext(dbPath)`, which calls `new SqliteConnection(...)`.
   **This is when the native library is resolved.** `SqliteConnection`'s
   static ctor calls `SQLitePCL.Batteries_V2.Init()`, which invokes
   `sqlite3_libversion_number()` on `SQLite3Provider_e_sqlite3`, which
   P/Invokes into `e_sqlite3`. The .NET dynamic loader on Linux searches
   in this order (see the error message if it fails):
   - `${apphost_dir}/libe_sqlite3.so`
   - `${apphost_dir}/e_sqlite3.so` (and the non-`lib`-prefixed variants)
   - Then the OS's normal `dlopen` search path (`/lib`, `/usr/lib`, etc.)
   Because the self-contained publish bundles `libe_sqlite3.so` into the
   publish output, the release tarball ships it, and the fixed
   `install.sh` copies it alongside the binary, the very first probe
   succeeds. If it is missing, `DllNotFoundException: Unable to load
   shared library 'e_sqlite3'` is thrown at `SqliteConnection`
   construction and **the process terminates before any user code runs**.
5. **Schema init.** `DbContext.ctor` runs `PRAGMA journal_mode=WAL`,
   `PRAGMA busy_timeout=5000`, and the `CREATE TABLE IF NOT EXISTS` /
   `CREATE VIRTUAL TABLE IF NOT EXISTS fts_chunks USING fts5 (…)` /
   trigger DDL. A successful run proves that the native library is not
   just loadable but is a working SQLite build with FTS5 compiled in
   (SQLitePCLRaw's bundled build always has FTS5).
6. **Scan + write.** `FileIndexer` walks the project tree, reads files,
   detects languages, splits into chunks, extracts symbols and
   references, and `DbWriter` batches UPSERTs (500 per transaction).
   Progress is rendered via `ConsoleUi.SetProgressTheme()`.
7. **FTS optimize.** After the write is committed,
   `INSERT INTO fts_chunks(fts_chunks) VALUES('optimize')` runs.
8. **Print summary.** `Files / Chunks / Symbols / Refs / Elapsed`.

Seeing `Done.` means every prior step — native load, SQLite init, WAL
setup, FTS5 availability, trigger sync, batch write path — succeeded.

```mermaid
sequenceDiagram
    autonumber
    participant OS as Linux dynamic loader
    participant Host as .NET apphost (cdidx)
    participant Main as Program.Main
    participant CU as ConsoleUi.LoadVersion
    participant IR as IndexCommandRunner
    participant Ctx as DbContext
    participant Conn as SqliteConnection
    participant PCL as SQLitePCL.Batteries_V2
    participant SO as libe_sqlite3.so
    OS->>Host: execve(cdidx)
    Host->>Main: managed entry
    Main->>CU: LoadVersion()
    CU-->>Main: "1.8.0"
    Main->>IR: Run(args)
    IR->>Ctx: new DbContext(dbPath)
    Ctx->>Conn: new SqliteConnection(connStr)
    Conn->>PCL: static ctor → Init()
    PCL->>SO: P/Invoke sqlite3_libversion_number()
    alt libe_sqlite3.so next to binary
        SO-->>PCL: OK
        PCL-->>Conn: provider registered
        Ctx->>Ctx: PRAGMA journal_mode=WAL
        Ctx->>Ctx: PRAGMA busy_timeout=5000
        Ctx->>Ctx: CREATE TABLE IF NOT EXISTS ...
        Ctx->>Ctx: CREATE VIRTUAL TABLE fts_chunks USING fts5(...)
        Ctx->>Ctx: CREATE TRIGGER (sync chunks ↔ fts_chunks)
        IR->>IR: FileIndexer scan + DbWriter batch UPSERT
        IR-->>Main: "Done."
    else libe_sqlite3.so missing
        SO--xPCL: dlopen failed
        PCL--xConn: DllNotFoundException
        Conn--xMain: crash before user code runs
    end
```

### Phase 4 — SQLite read path: `cdidx status`, `cdidx search`

`cdidx status` runs `DbReader.GetStatus(...)`, which issues a small set
of `SELECT COUNT(*)` / `SELECT … GROUP BY` queries against `files`,
`chunks`, `symbols`, `symbol_references`. It proves the read path
(including the opportunistic read-only schema migration in
`TryMigrateForRead`) works.

`cdidx search "<query>" --path install.sh --snippet-lines 4` goes
through `DbSearchReader`. It:

1. Token-quotes the user query to make it FTS-safe (unless `--fts`).
2. Runs `SELECT … FROM fts_chunks JOIN chunks …` with path filters.
3. `SearchSnippetFormatter.Format` rebuilds compact match-centred
   snippets with highlights.

A successful snippet proves the FTS5 virtual table, the content-sync
triggers, and snippet assembly via `SearchSnippetFormatter` all line up.

### Phase 5 — The MCP path: `cdidx mcp`

Piping `{"jsonrpc":"2.0","id":1,"method":"initialize","params":{}}` into
`cdidx mcp` exercises a different code path:

- `McpServer` owns stdin/stdout and parses JSON-RPC 2.0 frames.
- Response construction is **hand-rolled** via
  `System.Text.Json.Nodes.JsonObject` / `JsonArray`, not
  `JsonSerializer.Serialize<T>(...)`. That is why the MCP path keeps
  working even when the trimmed binary has reflection-based
  serialization disabled.
- The `initialize` response returns `protocolVersion`, `capabilities`,
  `serverInfo.name`, `serverInfo.version` (read via
  `ConsoleUi.LoadVersion()` — the same `version.json` source), and the
  long `instructions` string that guides AI clients on tool selection.
  Its capability object contains only server-provided `tools`, `resources`,
  `prompts`, and `logging`; client-provided `roots` and `sampling` are never
  advertised as server capabilities. The client then sends
  `notifications/initialized` to complete the handshake. When the negotiated
  protocol supports roots, the client advertised `roots`, and the transport can
  carry server-to-client requests, cdidx follows that notification with a
  `roots/list` request; it sends no roots request when the capability is absent.
  That handshake-triggered refresh is coalesced to one task per session.
  Transport teardown drains it and retains the final stdio writer/disposal
  barrier even when the bounded drain expires, so repeated initialized
  notifications cannot accumulate detached client requests or race transport
  resource disposal.
  cdidx does not synthesize a
  server-origin `notifications/initialized` copy (#4433). HTTP sessions can receive opt-in keep-alive notifications
  on `/events` when `CDIDX_MCP_KEEP_ALIVE_INTERVAL_S` is configured.
  On HTTP transport, out-of-band notifications are delivered only to connected
  `/events` SSE streams; POST-only clients receive the initialize response but
  no separate notification frame.
- Every request frame must carry an exact `"jsonrpc":"2.0"` member. Except for
  `initialize`, responded methods are rejected until initialization succeeds
  (#4468). Exactly one `initialize` may be accepted per session: concurrent or
  later attempts receive structured `-32600` / `duplicate_initialize` errors,
  and responded methods are also rejected while the session is initializing,
  shutting down, or closed (#4848). EOF, invalid UTF-8, and oversized stdio input share a bounded
  teardown policy: accepted requests receive a grace period, then cancellation,
  then a post-cancel deadline (#4543). Malformed-input protocol-error writes and
  asynchronous shutdown-cancellation callbacks are included in those same
  deadlines, so a blocked writer, write gate, or callback cannot hold teardown
  indefinitely. `notifications/shutdown` cancels reads and request actions without
  cancelling the initiating transport completion (`204 No Content` on HTTP), and
  callbacks that start after the first drain snapshot still join the post-cancel
  deadline. Every concurrent-loop exit reaches the bounded drain. Stdio input is
  closed promptly while output disposal waits for accepted tasks that can still
  reach the response writer. The final diagnostic records each unfinished
  category. External transport or process cancellation can interrupt either
  cleanup window.
- Independent stdio requests and HTTP POSTs execute concurrently up to the
  configured MCP request limit (#4536). The read loop continues accepting
  cancellation and client-response frames while execution slots are full. The
  accepted-frame backlog is separately bounded at the execution limit plus 64;
  overflow requests receive retry-safe `-32003` / `server_busy`. Request ids are
  registered before protocol/gate waits, execution timeout starts only after a
  slot is acquired, and a timed-out cancellation-insensitive action retains its
  slot until it truly drains. Initialize and other session mutations retain
  receive order through protocol barriers, mutable request state lives in
  `AsyncLocal` or request-scoped snapshots, and shared writer tools remain
  serialized. JSON-RPC batch items consume the same global execution slots
  independently (#4545). Base `IMcpTransport` loops do not reserve an outer frame
  slot, so a single request at `maxConcurrency: 1` acquires `_concurrencyGate`
  exactly once; single requests and batch items consume slots only at dispatch.
- The advertised capability surface includes only server-provided `tools`,
  `resources`, `prompts`, and `logging`; client-provided `roots` and `sampling`
  are deliberately omitted. `resources/templates/list` advertises
  `cdidx://file-path/{path}` for direct, percent-encoded resolution of a
  known exact repository-relative path; successful reads return the canonical
  `cdidx://file/<path>` identity. `resources/list` pages indexed files as
  `cdidx://file/<path>` URIs and accepts bounded `path`, normalized `lang`,
  and `includeGenerated` filters. Generated files are excluded by default
  and require `includeGenerated: true` for either discovery or direct reads.
  Its opaque keyset cursors bind both the indexed-file generation and the
  canonical filters; index or filter changes return an explicit
  restart-required error. Its optional `maxBytes` parameter (4,096–1,000,000; default
  1,000,000) bounds the full JSON-RPC envelope, and bounded omission and
  continuation diagnostics are returned under `_meta.response_controls`.
  The `initialize` instructions advertise these template and list controls, and
  every `resources/list` result publishes the accepted extension parameters and
  their bounds under `_meta.discovery_contract`, so AI clients do not need to
  infer non-standard protocol extensions.
  `resources/read` accepts optional inclusive `startLine` / `endLine`
  ranges and a `maxBytes` UTF-8 text budget (4-byte minimum, 64 KiB by
  default, 128 KiB maximum). Every page is also capped at 1,000 logical lines. Successful
  reads preserve the standard `contents` item and add `result._meta` with the
  effective range, returned byte count, truncation reason, and an opaque
  `nextCursor`. Continue with that cursor (and optionally a new `maxBytes`),
  without resending line boundaries. Cursors are bound to the indexed file
  version and fail as stale after the resource changes. The database reader
  applies range and byte limits through incremental SQLite BLOB reads before
  constructing the managed response string, including for long single lines.
  The server derives the effective text budget from the tighter of the MCP
  response limit and the active transport response limit, reserving space for
  the JSON-RPC envelope and worst-case JSON escaping. Multiple `resources/read`
  calls in one JSON-RPC batch also share its aggregate frame ceiling, and each
  item is budgeted against the remaining frame space. A non-pageable item that
  cannot fit its allocation is replaced by a structured
  `batch_response_budget_too_small` error with the original request ID. File metadata, cursor
  validation, and chunk BLOB reads run inside one deferred SQLite read snapshot,
  so a concurrent reindex cannot mix resource versions. A truly empty indexed
  file returns an empty success; non-empty resources with unavailable content,
  incomplete chunk coverage, or unsafe chunk topology fail with structured
  `index_missing`, `index_stale`, or `index_corrupted` errors instead of returning
  partial or empty success. Read-only or immutable legacy databases without the
  dedicated range partial indexes use the existing `idx_chunks_file` index for
  metadata-only predecessor and candidate queries under a SQLite VM-step budget;
  budget exhaustion returns structured `resource_bounded_read_index_unavailable`
  instead of allowing an unbounded scan. Stable reasons include `resource_content_unavailable`,
  `resource_bounded_read_index_unavailable`, `resource_chunk_coverage_incomplete`, `chunk_limit_exceeded`,
  `chunk_candidate_scan_limit_exceeded`, `resource_file_metadata_inconsistent`,
  `resource_chunk_topology_invalid`, and `scan_limit_exceeded`.
  `prompts/list` exposes the built-in `summarize_file`, `find_unused`, and
  `impact_of_changing` prompts; `prompts/get` returns a user-message template
  that directs clients toward the matching cdidx tools. `logging` advertises
  MCP `notifications/message`; `logging/setLevel` accepts `debug`, `info`,
  `notice`, `warning`, `error`, `critical`, `alert`, and `emergency`.
- `protocolVersion` is **negotiated**, not hardcoded (#1554). The server
  maintains `McpServer.SupportedProtocolVersions` (newest first:
  `2025-06-18`, `2025-03-26`, `2024-11-05`), reads the client's requested
  `protocolVersion` from `initialize` params, and either echoes the
  supported version back (handshake success), falls back to the newest
  supported version when the client omits or sends a non-string value,
  or rejects with a structured JSON-RPC `-32602` whose `error.data`
  carries `requestedVersion` and `supportedVersions`. This keeps future
  MCP spec bumps visible as actionable handshake failures instead of
  silently desynced wire formats. Bump the array deliberately and keep
  `ProtocolVersion` aligned with its first entry. The lifecycle transport
  regression test sends the Codex `2025-06-18` handshake through
  `notifications/initialized` and `tools/list`, rather than testing version
  echoing alone. The session advances through explicit pre-initialize,
  initializing, initialized, shutting-down, and closed phases (#4848). Client
  identity, caller, roots, and capabilities are parsed into a detached
  initialize draft and committed only after negotiation and complete
  success-response serialization finish (#4540); a rejected handshake,
  `CDIDX_MCP_RESPONSE_MAX_BYTES` fallback, or serializer failure rolls the
  initializing claim back so one corrected retry can proceed without inheriting
  failed metadata. Frame cleanup seals its deferred initialize drafts, so a
  timeout-delayed worker that arrives after response serialization releases its
  claim instead of leaving the session stuck in the initializing phase. After
  the successful commit, every duplicate initialize is rejected and cannot
  replace the established session. The commit publishes
  initialization lifecycle, caller, client info, capabilities, and roots
  through one immutable snapshot, so a concurrent or draining request observes
  one complete generation. A `roots/list` refresh publishes only if no
  roots-change notification invalidated the snapshot while the client response
  was in flight.
  This guarantee covers the server-side JSON-RPC serialization
  boundary; HTTP delivery has a separate fail-closed boundary described below.
- **Authentication middleware** (#1559). `McpServer` runs every parsed
  JSON-RPC request through an `IMcpAuthenticator` *after* the method is
  extracted but *before* dispatch. The default `LocalStdioAuthenticator`
  is permissive (matches the historical stdio behaviour and tags every
  caller as `stdio` / `local`). Setting `CDIDX_MCP_AUTH_TOKEN` swaps in
  `TokenMcpAuthenticator` for stdio, which requires every responded request
  to carry a matching `params.auth.token` and compares it in constant time
  via `CryptographicOperations.FixedTimeEquals`. Unset or empty configured
  tokens keep the stdio gate disabled, while configured tokens must be 1-4096
  characters and cannot contain whitespace or control characters (#3505).
  HTTP bearer tokens additionally reject commas at startup because commas
  are reserved for rejecting ambiguous `Authorization` headers (#3756).
  HTTP does not also use this body-token gate: `ProgramRunner` resolves a
  bearer secret for the HTTP transport from `CDIDX_MCP_HTTP_TOKEN`, falling
  back to `CDIDX_MCP_AUTH_TOKEN` when the HTTP-specific variable is unset,
  and then relies on the `Authorization: Bearer ...` transport check (#3156).
  For the JSON-RPC
  body-token gate, failures uniformly return JSON-RPC `-32001 "Unauthorized"`
  (per #1530 sanitization — the
  wire never distinguishes missing-from-wrong), and `BuildAuthFailureLog`
  emits the detailed reason to stderr. The handshake-control
  `notifications/initialized` may short-circuit without
  authentication; any roots request it triggers is constrained by the capability
  snapshot committed from the authenticated initialize request.
  State-changing notifications (`$/cancelRequest`, `notifications/cancelled`,
  `notifications/roots/list_changed`, `notifications/shutdown`, and
  `notifications/exit`) pass through the gate before mutating cancellation,
  roots, or lifecycle state. Authentication failures remain response-free and
  emit only the bounded stderr diagnostic (#4537). The middleware is the seam
  for future transports — a
  networked listener supplies a different `IMcpAuthenticator` while the
  `McpCallerIdentity` shape (`Source` + `Subject`) stays stable for the
  audit log (#1562).

Because MCP uses a distinct serialization strategy, it is the most
robust smoke test for "is the binary runnable at all?" — it stresses
the .NET host, `Program.Main`, CLI routing, and
`ConsoleUi.LoadVersion()`, but not SQLite. (MCP tool *calls* like
`search` do hit SQLite; `initialize` alone does not.)

#### Pluggable transports (`IMcpTransport`) — issue #1558

`McpServer.RunAsync` is split in two: a public stdio entrypoint that
constructs `StdioMcpTransport` and the legacy stdin/stdout pair, and an
internal `RunAsync(IMcpTransport, CancellationToken)` overload that owns
the JSON-RPC loop and is transport-agnostic. The `IMcpTransport` contract
is:

- `Task<string?> ReadFrameAsync(CancellationToken)` returns one
  request-frame string, or `null` to signal end-of-stream (closed stdin,
  cancelled HTTP listener, etc.). On stdio EOF, the MCP loop applies a bounded
  grace/cancel/post-cancel drain to accepted requests before exiting. Terminal
  malformed-input writes and asynchronous cancellation callbacks participate in
  the same final deadline (#4543).
- `Task WriteFrameAsync(string?, CancellationToken)` writes one response
  frame. `null` means "this was a notification" — stdio drops it; HTTP
  closes the in-flight request with `204 No Content`.
- The base contract is strictly one read followed by one write. Transports
  reject re-entrancy explicitly unless they implement
  `IConcurrentMcpTransport`, whose `McpTransportFrame` captures the response
  writer for exactly one input frame. This lets multiple requests complete out
  of order without attaching an HTTP response to a different POST (#4536). The
  base loop does not acquire an outer concurrency permit; dispatch owns the one
  global execution permit. Shutdown completions from base transports also join
  the bounded terminal-write drain.
- `IAsyncDisposable` lets each transport release its kernel-side
  resources (file handles, listener prefixes) without coupling to
  `McpServer`.

`StdioMcpTransport` preserves the pre-#1558 stdio framing behavior while
enforcing strict UTF-8 on input (BOM auto-detection is disabled so UTF-16/UTF-32
frames cannot switch encodings, output is BOM-less UTF-8, 64 KiB buffer,
`AutoFlush = true`). Malformed UTF-8 bytes
raise a transport decode failure that the MCP loop maps to JSON-RPC `-32700`
with an invalid-UTF-8 hint instead of silently replacing bytes with U+FFFD.
During teardown, input closes immediately to unblock reads, while output disposal
is deferred behind the aggregate of accepted tasks that can still write.
JSON-RPC frames are also bounded before dispatch: at most 1,000,000 UTF-16
characters, at most 1,048,576 UTF-8 bytes, and JSON nesting depth 32. Oversized,
malformed, or too-deep frames return `-32700` with `id: null`; MCP `status`
  surfaces the active limits under `mcp.limits`. JSON-RPC batch arrays are
  supported up to 100 items. Independent items execute concurrently under the
  same global request limit, while initialize/session mutations and repeated
  request IDs act as input-order fences. Before cancellation controls run, the
  server durably preregisters every unique ordinary batch request ID. A queued
  target therefore remains cancellable independently of the short 64-entry /
  5-second scheduler-race tombstone cache, and a pre-dispatch return releases its
  registration for safe ID reuse. Each item keeps its own cancellation/error
  context, and response items are emitted in input order regardless of completion
  order. Notification-only batches produce no response; empty or nested batches
  return `-32600`. HTTP bearer authorization and session validation run before
  out-of-band cancellation handling. Cross-frame controls are extracted once before queue admission, while
  a control targeting an ID in the same batch remains in the raw batch for the
  server's durable preregistration pass (#4545).

`HttpMcpTransport` (also #1558) wraps `System.Net.HttpListener`:

- The transport deliberately implements one logical MCP client session per
  server process. The first successful `initialize` may arrive without a
  `Mcp-Session-Id`; its response issues a fresh identifier in that standard
  header. Every subsequent POST and `GET /events` must present the exact same
  value. Missing or incorrect identifiers are rejected at the transport
  boundary before they can reach `McpServer` caller, roots, or capability
  state, so another client cannot replace the established session. The
  identifier is process-scoped and changes after restart. Clients must keep it
  private as an opaque session selector; it is not a substitute for
  authentication and remains independent of the bearer-token gate. Missing
  identifiers return `400` / `session_required`, invalid or ambiguous values
  return `404` / `session_not_found`, and a competing headerless initialize
  while the first is pending returns `409` /
  `session_initialization_in_progress` in `X-Cdidx-Mcp-Rejection`.
- One JSON-RPC frame per HTTP POST; the matching response is the HTTP
  response body (`200 OK` / `application/json; charset=utf-8`) or
  `204 No Content` for notifications. `GET /events` opens an independent
  `text/event-stream` subscription for future server→client frames; the
  established session may hold multiple subscriptions and receives each
  server notification on all of them. The server emits no unsolicited frames
  unless keep-alive notifications are opted in with
  `CDIDX_MCP_KEEP_ALIVE_INTERVAL_S`. Long-lived event streams use an independent
  admission semaphore and do not consume POST handler capacity. POST accepts
  exactly one `application/json`
  Content-Type with an omitted or UTF-8 charset and decodes with strict UTF-8;
  unsupported media types/charsets return `415`, and malformed UTF-8 returns
  `400` before queueing. Non-POST verbs on `/` return `405 Method Not Allowed`.
  After session validation, empty / whitespace bodies are treated like a
  closed stdio line and return `204 No Content` *without* killing the loop, so
  a misbehaving client cannot pin the server
  on a junk frame. Request bodies are capped by
  `CDIDX_MCP_HTTP_MAX_REQUEST_BYTES` (default: 1,000,000 bytes, maximum:
  16,777,216 bytes) and oversized bodies return `413 Payload Too Large`
  before they are fully buffered. The
  pending request queue is bounded by `CDIDX_MCP_HTTP_MAX_QUEUE_DEPTH`
  (default: 64, maximum: 1,024); full queues return `429 Too Many Requests`
  with `Retry-After: 1` instead of retaining unbounded work. After bearer
  authorization and session validation, cross-frame cancellation notifications inside mixed batches are
  handled out of band before this queue check; same-batch target controls stay in
  the raw batch, and only the remaining raw items consume queue capacity. Accepted context
  POST and other short-lived handler tasks are bounded by
  `CDIDX_MCP_HTTP_MAX_CONCURRENT_HANDLERS` (default: 64, maximum: 1,024), while
  an independent admission gate bounds concurrent `/events` streams with
  `CDIDX_MCP_HTTP_MAX_EVENT_STREAMS` (default: 16, maximum: 1,024);
  saturated limits return `429 Too Many Requests` with `Retry-After: 1`.
  Only absent limit variables use defaults. Every present non-numeric, zero,
  negative, or over-maximum value fails before listener startup and identifies
  the exact variable and accepted range. `/healthz` reports both effective
  admission capacities and `http_separate_event_stream_handlers: true`.
- Request bodies share a process-wide weighted byte reservation from read admission through
  queueing, MCP execution, HTTP response completion, and any detached cancellation drain. The effective
  `CDIDX_MCP_HTTP_MAX_IN_FLIGHT_REQUEST_BYTES` budget defaults to 64 MiB, is capped at 1 GiB,
  and must be at least the per-request body limit. Known lengths reserve atomically before the
  first read; chunked bodies reserve before each bounded read. Exhaustion returns HTTP 429 with
  `request_body_budget_limit`. The handler owner, queue, pending frame, and writer transfer one
  idempotent reservation. A canceled isolated action that ignores its token transfers both its
  `McpServer` concurrency lease and this byte reservation to a completion continuation, so repeated
  disconnects cannot accumulate work outside the configured bounds. Shutdown serializes with the
  single queue reader before draining queued and pending requests. Health reports the limit, current local/process values, peak,
  process scope, and rejection count.
- POST lifetime starts before body validation and is bounded by both a per-read idle deadline and
  a total deadline. `CDIDX_MCP_HTTP_BODY_IDLE_TIMEOUT_MS` defaults to 30,000 ms and is capped at
  600,000 ms; `CDIDX_MCP_HTTP_REQUEST_TIMEOUT_MS` defaults to 120,000 ms, is capped at 3,600,000
  ms, and must be at least the idle deadline. The total deadline spans body reading, queueing, MCP
  dispatch, tool/SQLite work, and response completion. A linked-list queue permits O(1) removal of
  cancelled queued requests, and the single-winner lifetime state makes reservation, response,
  timer, and semaphore cleanup idempotent across timeout, disconnect, shutdown, and normal write.
  `IRequestLifetimeMcpTransport` links the selected HTTP request token into `McpServer`'s current
  request token, which propagates cancellation through tool dispatch and `DbReader.Cancellation`.
  Response-bearing JSON-RPC frames in an established session start a bounded chunked-response
  probe after queue admission; each flushed ASCII space is valid leading JSON whitespace and
  exposes a post-body disconnect. Once probe or SSE output starts, its stream-owned bounded write
  lifetime settles or aborts under the serialization gate even if the originating POST is canceled;
  a probe write timeout is terminal and cancels the matching request. The headerless initial `initialize` skips the probe so its
  successful response can add `Mcp-Session-Id` before headers are committed, while notifications
  also skip the probe and retain `204 No Content`. Request logs use
  `timeout:http_request_body_idle`, `timeout:http_request_lifetime`,
  `timeout:http_disconnect_probe_write`, and `client_disconnected`;
  health reports both effective deadlines plus timeout, disconnect, and queued-cancellation counts.
- SSE stream lifetime is represented by the active stream registry and a
  bounded active-stream counter only. Idle streams receive minimal SSE comment
  heartbeats so disconnected clients are detected and stream slots are
  released; completed stream tasks are not retained after the registry entry
  is removed.
- `ResolveListenSpec("host:port")` resolves the prefix up-front so the
  CLI can log the bound port to stderr (`Listening on http://...`).
  Port `0` is resolved by probing a temporary `TcpListener`; the
  TOCTOU window between probe and `HttpListener.Start()` is accepted
  because the transport is documented as local-only / single-tenant.
  The wildcard hosts `+` / `*` are rejected at parse time.
- Browser boundary: an absent `Origin` is accepted for native clients. A
  present Origin must be a single exact match for the listener's scheme, host,
  and port; malformed, `null`, ambiguous, or cross-origin values return `403`
  before auth. CORS preflights are rejected without emitting
  `Access-Control-Allow-*` headers (#4549).
- Shared-secret auth is secure by default: every HTTP listener requires
  `Authorization: Bearer <token>` and compares the token in constant time.
  `CDIDX_MCP_HTTP_TOKEN` is preferred; when it is unset,
  HTTP falls back to `CDIDX_MCP_AUTH_TOKEN` as the bearer secret; when both
  are set, `CDIDX_MCP_HTTP_TOKEN` wins. HTTP clients never need to also send
  `params.auth.token`. An unset/empty token refuses startup even on loopback,
  unless the operator passes the explicit `--allow-unauthenticated-http`
  unsafe opt-in; that flag is rejected for non-loopback listeners. Configured tokens must be 1-4096 characters and
  cannot contain whitespace, control characters, or commas (#3505, #3756). Supplied HTTP
  bearer values are compared exactly after the `Bearer ` prefix: they are not
  trimmed, and invalid-shape or oversized values are rejected before hashing.
  Bearer authentication is evaluated separately from the `Mcp-Session-Id`
  contract; after initialization, deployments with auth enabled require both.
- Optional request-loop logging: `ProgramRunner` connects `HttpMcpTransport`
  to `GlobalToolLog`, so persistent logging records one `mcp_http_request`
  line per HTTP request when the lifecycle log is enabled. The record includes
  method, path, status, duration, auth outcome, remote peer, correlation id,
  and, when available, an opaque JSON-RPC request-id token plus its type and
  decoded-value length. Caller-controlled method, path, and remote peer values
  are capped at 256 characters with a `...<truncated>` marker. The record never
  includes request/response bodies or a raw JSON-RPC id.
- Cancellation hooks the `CancellationToken` into
  `_listener.Stop()` so `GetContextAsync()` unblocks on shutdown;
  `HttpListenerException` / `ObjectDisposedException` are treated as
  end-of-stream so the MCP loop exits the same way as a closed stdin.

Wire selection happens in `ProgramRunner.RunMcp`:
`--transport stdio|http`, `--http-listen <host:port>`, and the loopback-only
`--allow-unauthenticated-http` opt-in are stripped
from the args before downstream parsing, HTTP bearer-token resolution uses
`CDIDX_MCP_HTTP_TOKEN` first and `CDIDX_MCP_AUTH_TOKEN` as a fallback, and
the dispatch lands in either the legacy stdio path or `RunMcpHttp`. The
pluggable seam keeps the JSON-RPC
ordering invariant identical across both transports, so the existing
McpServer test surface (which exercises `ProcessLineAsync`) continues
to cover the per-method behavior, while `HttpMcpTransportTests` cover
the wire-level contract for the new transport.

#### JSON-RPC request-id telemetry boundary — issue #4551

`McpRequestIdTelemetry` is the single conversion boundary between a
client-supplied JSON-RPC id and observability data. It derives a fixed-length
`rid:v1:...` token with HMAC-SHA256 and a random process-local salt. The raw id
stays only on the JSON-RPC wire and in protocol internals that need it for
response echo, routing, or cancellation; it must never be copied into a log,
metric, trace, activity, or audit event.

Token cardinality is process-bounded as well as token length. The provider
keeps individual tokens for at most 4,096 distinct ids per process. After that
budget is exhausted, every previously unseen id maps to one process-salted,
fixed-length overflow token while already registered ids retain their tokens.
Consequently, `request_id` has at most 4,097 distinct values in one process.

The associated metadata is content-free: `request_id_type` is `string`,
`number`, or `null`, while `request_id_length` is the decoded string-value
UTF-16 code-unit count, the numeric JSON-text character count, or `0` for `null`.
The stderr correlation prefix and invocation JSON, Activity tags
(`rpc.request_id`, `rpc.request_id_type`, `rpc.request_id_length`), MCP metrics,
audit JSONL, HTTP request logs, and timeout diagnostics/status all use the same
token/type/length tuple. CLI metrics omit these fields because they have no
JSON-RPC request. A token is deterministic only within one process; restarting
the server process changes the salt and intentionally breaks cross-process
correlation. Tests for any new telemetry surface must include a credential-like
id and prove that the raw value is absent.

#### Structured error envelope and server codes — issue #1581

Every MCP error response — both JSON-RPC `error` objects and MCP
tool-result errors (`isError: true`) — carries a canonical
`data` envelope so clients can branch on a stable machine-readable
category instead of parsing the human `message` string.
For required string tool arguments, the human message still says
`Missing required parameter: <name>` only when the argument is absent;
when the argument is present but empty or whitespace-only, the message
is `Parameter "<name>" cannot be empty or whitespace-only` (#2145).

- `data.category` — stable wire identifier (see table below).
- `data.suggestion` — operator-actionable next step in English.
- `data.retry_safe` — `true` when the same request can be retried
  without changing state on the server (e.g. cancellation, rate
  limit, stale schema), `false` otherwise (e.g. corrupted DB,
  unknown tool).

The constants live in `src/CodeIndex/Mcp/McpErrorEnvelope.cs` and the
helper `McpErrorEnvelope.BuildData(category, suggestion, retrySafe,
extraData)` is the single construction site. Callers may pass
`extraData` to merge category-specific fields (e.g. `tool` /
`retry_after_ms` for rate-limited responses); the canonical keys are
written first and `extraData` cannot shadow them.

JSON-RPC 2.0 reserves `-32700` and `-32600..-32603` for the spec
itself and `-32000..-32099` for server implementations. cdidx assigns
codes within the server range. The standard codes (`parse error`,
`invalid request`, `method not found`, `invalid params`, `internal
error`) still apply to the JSON-RPC envelope; the codes below cover
cdidx-specific categories.

| Server code | Category | `retry_safe` | Trigger |
| --- | --- | --- | --- |
| `-32000` | `rate_limited` | `true` | The caller-wide pre-validation bucket or a secondary known-tool bucket denied the call (#1560 / #4547). Legacy fields `error_category`, `tool`, `caller`, `retry_after_ms` are kept alongside the canonical envelope for backward compatibility. `retry_after_ms` is the earliest point when all required token-refill and bucket-cap capacity constraints can admit the retry. |
| `-32001` | `permission_denied` | `false` | Token auth failure (`TokenMcpAuthenticator`, #1559). The wire stays generic; stderr carries the detailed reason. |
| `-32003` | `server_busy` | `true` | The bounded concurrent-frame admission backlog is full (#4536). Retry after the advertised `data.retry_after_ms`. |
| `-32010` | `index_missing` | `true` | DB path does not exist or could not be opened for the requested tool call. The same request becomes safe to retry once the operator runs `cdidx index <projectPath>`. |
| `-32011` | `index_stale` | `true` | SQLite reported `no such table` / `no such column`; the DB was written by an older cdidx and needs `cdidx index <projectPath> --rebuild`. |
| `-32012` | `index_corrupted` | `false` | SQLite reported `database disk image is malformed` / `file is not a database` / `file is encrypted`. The DB cannot be read; the operator must delete it and reindex. |
| `-32015` | `request_cancelled` | `true` | The MCP request was cancelled (client disconnect, shutdown). Reissue if the work is still needed. |

The following categories ride the standard JSON-RPC codes:

| Standard code | Category | `retry_safe` | Trigger |
| --- | --- | --- | --- |
| `-32700` | `parse_error` | `false` | Frame was not valid JSON. |
| `-32700` | `message_too_large` | `false` | Frame is above the per-frame byte cap and is rejected before parsing. The frame reader uses the parse-error code because the frame is unreadable in the same sense as malformed JSON. |
| `-32600` | `invalid_request` | `false` | Frame parsed but is not a JSON object, is missing required JSON-RPC fields, or carries an invalid `id`. When no valid request id can be recovered—including top-level scalar or `null` frames, malformed objects, and invalid batch elements—the response includes `id:null`. String and numeric request ids are capped before echo/audit serialization; oversized ids also include `data.max_request_id_chars` / `data.max_request_id_bytes`. |
| `-32601` | `method_not_found` | `false` | Unknown JSON-RPC method (not one of `initialize` / `tools/list` / `tools/call` / `ping` / supported `notifications/*`). |
| `-32601` | `tool_disabled` | `false` | Known MCP tool is disabled by `CDIDX_MCP_TOOLS_ALLOW` / `CDIDX_MCP_TOOLS_DENY` (#1561). The `-32601` wire code is preserved so the pre-#1581 client contract still holds; only the envelope is additive. `data.tool` carries the disabled tool name. |
| `-32602` | `tool_unknown` | `false` | `tools/call` received an MCP tool name the server does not implement (typo or version mismatch). `data.tool` carries the unknown name. |
| `-32602` | `missing_parameter` | `false` | `tools/call` request omitted the required `params.name` string. |
| `-32602` | `invalid_argument` | `false` | Argument shape rejected by the tool (also covers the protocol-version handshake mismatch from #1554, which exposes `data.requestedVersion` / `data.supportedVersions`). |
| `-32602` | `regex_timeout` | `true` | A user-supplied regex exceeded the bounded match timeout while executing, for example `find_in_file` regex scans. `data.error_code` carries the CLI-aligned stable code. |
| `-32603` | `internal_error` | `false` | Unhandled exception path (fallback bucket). The wire message stays generic per #1530 sanitization; stderr carries the exception type. |

The classifier `McpErrorEnvelope.ClassifyException(ex)` maps unhandled
exceptions to `index_stale` / `index_corrupted` / `request_cancelled`
/ `internal_error` based on the exception type and selected
`SqliteException.Message` substrings — the raw message is never leaked
on the wire (#1530). Both the JSON-RPC catch-all in `ProcessFrame`
and the `tools/call` catch-all use the same classifier, so a
`SqliteException` raised mid-tool-call surfaces the same `index_stale`
envelope whether it lands as `error.data` or
`result.structuredContent`.

Two construction sites in `McpServer.cs` consume the envelope:

- `CreateErrorResponse(... category, suggestion, retrySafe,
  extraData?)` builds the JSON-RPC `error` object and attaches the
  envelope at `error.data`.
- `CreateToolErrorResponse(id, message, category, suggestion,
  retrySafe, extraData?)` builds the MCP tool-result error shape
  (`isError: true`) and attaches the envelope at
  `result.structuredContent`. The two-argument overload defaults to
  `invalid_argument` / `retry_safe=false` so legacy call sites that
  did not yet specify a category keep compiling without dropping the
  envelope.

When adding a new error path, pick the most specific category from
the tables above (or extend the list and update this section plus
`McpErrorEnvelope.cs` in lockstep). Tests under
`tests/CodeIndex.Tests/McpServerTests.cs` (`ErrorResponse_*`,
`ToolResult_*`, `ClassifyException_*`, `BuildData_*`) cover the
envelope contract — extend them whenever a new category is added so
the wire contract is regression-tested.

### Release `--json` policy and trimmed fallback

`release.yml` publishes the self-contained binaries with
`-p:PublishTrimmed=true`. CLI `--json` payloads such as `status --json`
and `index --json` are covered by `CliJsonSerializerContext` source
generation, so they do not depend on reflection-based `System.Text.Json`.
The release verify step installs the tarball and runs `status --json` so
this does not silently regress.

The project disables the Roslyn compile-time trim analyzer when
`PublishTrimmed=true` because the .NET 8 analyzer can fail with AD0001 before
RID-specific publish starts. This does not disable trimming: the ILLink
publish-time pass still runs and still emits trim analysis warnings.

`JsonOutputFailure` remains for manually modified or experimental builds.
In .NET 8, trimming sets
`JsonSerializerIsReflectionEnabledByDefault=false` implicitly. If a custom
build reaches a reflection serializer path without a source-generated
`JsonTypeInfo<T>`, CodeIndex fails fast with the dedicated stderr message
and exit code `4` (`FeatureUnavailable`) instead of misclassifying the
serializer failure as a database error. Any new CLI JSON DTO must be added
to `CliJsonSerializerContext` before release artifacts can keep trimming
enabled.

```mermaid
flowchart TD
    B["official release binary<br/>built with PublishTrimmed=true<br/>source-generated CLI JSON available"]
    B --> R{User command}
    R -->|cdidx status / index with --json| A["CLI runners call<br/>JsonSerializer.Serialize&lt;T&gt;(value)"]
    R -->|cdidx search / status without --json| H["Human-readable writer<br/>(no JSON)"]
    R -->|cdidx mcp| M["McpServer builds<br/>JsonObject / JsonArray graphs<br/>by hand, then Write()"]
    A --> AX["works"]
    H --> HX["works"]
    M --> MX["works"]
    T["custom build<br/>missing source-generated CLI DTO coverage"] --> TX["JsonOutputFailure fail-fast<br/>exit code 4 (FeatureUnavailable)"]
```

### Diagnostic table: symptom → cause → fix

| Symptom | Root cause | Fix |
| --- | --- | --- |
| `cdidx --version` prints `cdidx v0.0.0` | `version.json` not next to the binary in `$HOME/.local/bin/` | Re-run the fixed `install.sh`; verify `ls $HOME/.local/bin/version.json` exists |
| `DllNotFoundException: Unable to load shared library 'e_sqlite3'` on any command | `libe_sqlite3.so` (or `.dylib`) not next to the binary | Re-run the fixed `install.sh`; verify `ls $HOME/.local/bin/libe_sqlite3.*` exists |
| `install.sh` error: `musl-based Linux (e.g. Alpine) is not supported` | Container uses musl libc | Switch to a glibc-based image (debian/ubuntu) or install via `dotnet tool install -g cdidx` in an environment with the SDK |
| `install.sh` error: `macOS x86_64 (Intel) binaries are not published` | Intel Mac hitting `osx-x64` RID | Use `dotnet tool install -g cdidx` in a .NET SDK environment, or build from source with `dotnet publish src/CodeIndex/CodeIndex.csproj -c Release -r osx-x64 --self-contained true` |
| `install.sh` error: `Checksum mismatch!` | Tarball tampered with or transport corrupted | Retry; if persistent, check the `sha256sums.txt` and the tarball on the release page |
| `Error: --json is not available on this trimmed build.` | Manual/custom build reached a reflection-based `JsonSerializer` path that is not covered by source generation | Use the official `install.sh` release or NuGet/global-tool build, omit `--json`, use MCP, or add the missing DTO to `CliJsonSerializerContext` before publishing |
| `cdidx status` shows `Files: 0` on a repo that clearly has files | Index DB never built, or pointing at the wrong `--db` | Run `cdidx <projectPath>` first; verify `.cdidx/codeindex.db` exists |
| Every command shows `index fresh` but results are obviously stale | You indexed a different working copy | Re-run `cdidx . --commits HEAD` or `cdidx . --files <paths>` |
| The host swallowed stderr and the user only knows "cdidx did not work" | The shell or launcher captured/discarded terminal stderr | Run `cdidx status --log-path` and inspect the daily persistent stderr log (`stderr-YYYYMMDD.log`) in that resolved per-user directory; disable with `CDIDX_DISABLE_PERSISTENT_LOG=1` only when you intentionally do not want breadcrumbs |

### Why this matters

The cloud session is the only environment in the development loop that
cannot fall back to `dotnet build`. A broken install path would be
invisible to anyone with an SDK — they would just rebuild. The
bootstrap prompt, the smoke tests, and this section exist so that any
regression in the user-facing install flow is caught by the next person
who opens a cloud session, not by a real user after release.

## Regex timeout and redaction fallback policy

Regex timeout behavior is centralized in `RegexTimeoutPolicy` (`src/CodeIndex/Diagnostics/RegexTimeoutPolicy.cs`). Keep category strings, user-facing timeout messages, and redaction fallbacks there before adding a new timeout path.

Contract guarantees:

- **Indexing and configured extractor patterns.** Indexing diagnostics use `regex_timeout`; configured pattern diagnostics use `pattern_regex_timeout`. Indexing skips the affected file or pattern so the run can finish and reports bounded diagnostics instead of leaking the pathological pattern input.
- **Query/find and MCP find.** CLI human/JSON errors and MCP error envelopes use `regex_timeout` with the same timeout duration text. The recovery hint is surface-specific only where CLI flags and MCP tool arguments differ.
- **Redaction surfaces fail closed.** `DiagnosticRedactor`, `GlobalToolLog`, and MCP audit argument values replace the affected value with the configured redaction placeholder. Sensitive-name decisions use `SensitiveNameClassifier`, which normalizes separators and case before checking shared credential fragments so diagnostic and audit redaction cannot drift. `DiagnosticSanitizer` omits the whole message with `[message omitted after sanitization timeout]`. `SuggestionStore` records `redaction_timeout` and persists `[REDACTED:redaction_timeout]`. GitHub API response bodies are replaced with `[response body omitted after redaction timeout]`.
- **Bounded extraction helpers.** `BoundedRegex` keeps extraction best-effort by returning empty matches/`false` or the original input depending on the operation, and records captured timeout diagnostics when a capture scope is active. `EnumerateMatches` advances with `Match.NextMatch` only when the consumer requests another result, so bounded extractor loops can stop without materializing the rest of a dense match collection.

## Metrics emission

`MetricsSink` (`src/CodeIndex/Cli/MetricsSink.cs`) is the opt-in JSONL metrics sink for CLI commands and MCP tool calls. `ProgramRunner.Run` owns the session so one bounded lifetime covers command dispatch, MCP requests, and the final command-level metric. `MetricsSink.Record` must never perform destination IO on the caller: it serializes within the published event budget and uses a non-blocking write to a bounded multi-writer queue. A single background reader drains records in bounded batches and performs the append and flush operations.

Contract guarantees that callers and operators can rely on:

- **Non-blocking overload.** A full or completed queue drops the new event and increments explicit drop counters. Producers do not wait for queue space, so a blocked sink cannot extend producer or MCP response latency beyond bounded serialization and enqueue work. The outer CLI invocation may still spend up to the bounded session-disposal deadline draining metrics before `ProgramRunner.Run` returns.
- **Batch failure semantics.** A write or rotation failure accounts the affected batch as dropped and never replays it. An append may have partially succeeded before throwing, so replaying the same bytes could duplicate records or corrupt the JSONL stream. Later batches are attempted after capped exponential backoff; the next successful batch clears the current degraded state and records recovery.
- **Bounded diagnostics.** The first runtime sink failure in a session produces one bounded, sanitized warning without the configured path. Repeated failures update counters and the bounded `last_failure` category rather than flooding stderr. Full MCP status always exposes `mcp_session.metrics`, with `enabled:false` when unconfigured. An enabled sink publishes `enabled`, `path`, `max_bytes`, `bytes_written`, `disposed`, `degraded`, `queue_capacity`, `queue_depth`, `queued_event_count`, `written_event_count`, `dropped_event_count`, `queue_full_drop_count`, `serialization_failure_count`, `write_failure_count`, `rotation_failure_count`, `batch_flush_count`, `consecutive_failure_count`, and `recovery_count`; `next_retry_at`, `last_recovery_at`, and `last_failure` are optional. MCP ping always mirrors this object as `metrics`. Metrics are optional telemetry and must not make MCP ping or HTTP health/liveness degraded.
- **Bounded shutdown.** Disposing the session completes the producer side and waits only for a bounded drain deadline. Records not confirmed written by that deadline increment `dropped_event_count`; shutdown never waits indefinitely on the destination. This bounded drain is also what preserves the final CLI/MCP command metric during normal process exit.
- **Schema stability.** Batching and recovery do not alter the published one-object-per-line schema. Existing field names cannot be renamed or repurposed without a breaking output-contract change.

## MCP audit log emission

`AuditLogSink` (`src/CodeIndex/Mcp/AuditLogSink.cs`) is the opt-in per-MCP-server JSONL audit (#1562). It is owned by `ProgramRunner.RunMcp` and threaded into `McpServer` through an internal constructor overload; no other dispatch site participates. Each `tools/call` produces exactly one record — including malformed requests (`tool="(missing)"`) and unknown tools (`error_code=-32602`) — so a misbehaving client cannot hide its activity by varying the request shape.

Contract guarantees that downstream consumers can rely on:

- **Field stability.** `timestamp`, `tool`, `arg_keys`, `arg_lengths`, `elapsed_ms`, `error_code` are emitted on every record. `caller`, `caller_version`, `request_id`, `request_id_type`, `request_id_length`, `request_id_truncated`, `arg_key_lengths`, `arg_keys_truncated`, `arg_key_truncation_reasons`, `arg_values`, `arg_values_redacted`, `arg_values_truncated`, `arg_values_truncation_reasons`, `arg_values_serialized_bytes`, `arg_values_max_bytes`, `result_count`, `checked_root_identity`, `error` are emitted only when non-null or true; renaming or repurposing any published field is a breaking change, the same policy as the CLI `--metrics` schema.
- **Request-id privacy.** `request_id` is the process-salted fixed-length token described above, never the JSON-RPC wire value. A present token is accompanied by `request_id_type` and the decoded-value `request_id_length`; the legacy truncation guard is normally absent because the token is already bounded.
- **Error code semantics.** `0` = success, `1` = MCP tool error (`isError: true`), negative = the verbatim JSON-RPC error code (e.g. `-32602` for invalid params, `-32603` for internal error). The companion `error` string is one of `jsonrpc_error`, `tool_error`, `missing_tool_name`, or the sanitized exception type name (`McpServer.BuildSanitizedToolErrorMessage` keeps `ex.Message` out of the wire and out of the audit, #1530).
- **Result count.** `ExtractResultCount` prefers `structuredContent.count` over `structuredContent.results.length`; tool errors and JSON-RPC errors omit the field. Tools that return no count-shaped payload (e.g. `ping`) leave `result_count` absent rather than emitting `0`.
- **MCP index root identity.** `index` resolves the requested root canonically, captures its platform filesystem identity, and retains a no-follow directory handle for the run. Directory enumeration is handle-relative on Linux, macOS, and Windows; the authorized filesystem seam compares each directory/file's pre-open identity, opened-handle identity, and post-open canonical containment before consuming content. Language-map and pattern sidecars are confined to the authorized project tree, opened through that seam, and cached in an authorization-scoped snapshot that excludes wider user configuration and executable workspace plugins. A root, ancestor, link, or entry identity change raises a bounded `permission_denied` tool error with `authorization_failure_reason`; successful/dry-run structured output and every post-authorization audit record carry the same fixed-length opaque `checked_root_identity`, even when the response was built by an error path. A containing repository root is used for ignore rules only when it remains authorized; otherwise discovery is confined to the requested project root.
- **Argument privacy.** `arg_keys` and `arg_lengths` are always recorded so query *shape* is recoverable, but argument-key count and displayed key length are capped and marked with `arg_keys_truncated`. `arg_values` is gated behind `--audit-log-include-values` because cdidx queries can carry literal source snippets or secret-shaped strings. The echo is a sanitized, budgeted clone: secret-like keys classified by the shared diagnostic/audit taxonomy and known token patterns are replaced with `[REDACTED]`, and depth, object-property, array-item, total-node, string-length, serialized-byte, and event-byte limits can mark `arg_values_truncated` before values are written.
- **Caller identity.** The published initialize snapshot captures the bounded client name/version from every successful `initialize.clientInfo` and replaces them on successful reconnection within the same session, so a long-running MCP loop with multiple accepted `initialize` handshakes attributes records to the *currently connected* client rather than the first one. Failed protocol negotiation never overwrites audit attribution or other session state (#4540).
- **Rotation.** Writes go through an open-append-close cycle so external `tail -F` consumers follow rotations and so the file is closed during the rename. When `_bytesWritten >= MaxBytes`, `RotateLocked` drops `<path>.(RotationKeep-1)` (currently `<path>.2`), cascades surviving slots up by one, and moves `<path>` to `<path>.1`. `RotationKeep = 3`, so `<path>.3` is never created — exercised by `AuditLogSinkTests.Record_KeepsAtMostThreeFiles_DropsOldestOnRotationOverflow`.
- **Queue and shutdown accounting.** `queued_record_count` increments only after a successful channel write, and `written_record_count` increments only after a successful file append. `shutdown_abandoned_record_count` is the monotonic snapshot of pending records when the bounded shutdown deadline expires, and `shutdown_flush_timed_out` stays true after that deadline failure. Abandoned is not a drop category: the background writer may complete after `Shutdown` returns, so the snapshot is never decremented and cannot be used in a `queued = written + dropped + abandoned` invariant.
- **Status and degradation.** Full MCP status publishes the enabled sink as `mcp_session.audit_log`; MCP ping/HTTP health mirrors the same live object as `audit_log`. Alongside `enabled`, `path`, `include_values`, `max_bytes`, `bytes_written`, `disposed`, `queue_capacity`, and `queue_depth`, both surfaces expose `queued_record_count`, `written_record_count`, `dropped_record_count`, `queue_full_drop_count`, `serialization_failure_count`, `write_failure_count`, `rotation_failure_count`, `rotation_cleanup_failure_count`, and `rotation_degraded`, plus optional `last_drop_reason` and `last_rotation_failure`. A positive dropped count or degraded rotation degrades the top-level MCP ping/health status. Shutdown-only abandonment and timeout fields remain in `AuditLogShutdownResult.Diagnostics` and the bounded stderr diagnostic because the MCP server has already stopped when shutdown completes.
- **Best effort and strict shutdown.** Serialization, queue-full, IO, and rotation failures never crash the underlying tool call. The default shutdown remains best-effort, but a missed flush deadline emits exactly one bounded, count-only stderr warning without the audit path. `--audit-log-strict` requires `--audit-log`; if shutdown is incomplete, `ProgramRunner.RunMcp` changes an otherwise-successful exit to `CommandExitCodes.RuntimeError` (`10`) and preserves every existing nonzero exit. The constructor still fails fast on impossible paths so the operator sees a startup misconfiguration before any tool dispatch happens.

The flag parser (`ProgramRunner.TryConsumeAuditLogFlags`) is run before `QueryCommandRunner.ParseArgs` and consumes only the audit-specific tokens — `--db` and anything after `--` is left intact so existing escape semantics survive. `--audit-log-include-values` and `--audit-log-strict` require `--audit-log <path>` because neither value echo nor strict durability has meaning without a configured destination.

## Report artifact contract

`ReportBundleWriter` stages a complete gzip/tar sibling file and publishes it through `AtomicFileWriter`. Publication uses a no-overwrite move unless `ReportCommandOptions.Overwrite` came from an explicit `--overwrite`. Explicit replacement uses an atomic filesystem backup and retains it through the parent-directory durability flush; a publication failure restores that backup before surfacing the error. `support-manifest.json.bundle.members` is the authoritative archive member list. `db_inspected` and `db_diagnostics_included` describe read-only diagnostic collection, while `db_member_included` describes archive membership and is currently always `false`. The legacy `db_included` field remains an additive-compatibility alias for `db_inspected`.

`LastFailureEventStore` schema 3 adds opaque `workspace_id`, `database_id`, and `run_id` provenance to the existing binary version and UTC timestamp. Failure capture derives the effective database from the same parsed index or query options used by the command, including positional ordering and the `--` literal sentinel. Report resolves its default database through the normal query precedence (`CDIDX_DATA_DIR`, active workspace, XDG, and ancestor workspace), so collection and correlation use the same database identity. A report includes the saved event only when workspace, database, and binary version match and the event is no more than 24 hours old, allowing at most five minutes of future clock skew. Other records are excluded from the archive; the manifest publishes only a bounded `last_failure.disposition` / `reason` plus validated opaque provenance fields. Raw workspace and database paths never enter those fields.

## Coding conventions

- Comments are bilingual (English / Japanese), e.g. `// Enable WAL mode / WALモードを有効化`
- Documentation (README, CHANGELOG) is structured: English first, then Japanese.
- No unnecessary production packages. Test-only packages are allowed when they clearly improve the harness and stay scoped to `tests/CodeIndex.Tests/`; they do not relax the production dependency rule.

## Sensitive Buffer Policy

Pooled byte buffers that may hold credentials, request payloads, local file
bytes, or other user-controlled content must be cleared before the buffer can
be observed again. Prefer a helper whose name states the policy instead of a
bare `ArrayPool<byte>.Shared.Return(...)` at sensitive boundaries.

- **Token material** uses full-buffer clearing when returning rented arrays.
  `McpAuthenticationLimits.HashToken` hashes only the UTF-8 bytes written for
  the token, zeroes that used range immediately, and returns the rented array
  with `clearArray: true` so unused bytes from a previous rent are also erased.
- **LSP request payloads** clear only the used payload range before returning a
  rented buffer. The lease knows the declared content length, so clearing the
  used range is the stable contract; bytes beyond that range are not part of
  the payload and should not force a full rented-array clear on every request.
- **Bounded HTTP copy buffers** are treated as possibly sensitive because they
  can carry installer, archive, or response bytes before those bytes are
  written to private storage. They clear the whole rented copy buffer before
  returning it.
- **ASCII protocol headers and generated JSON/report bytes** are not treated as
  sensitive merely because they use pooled or accumulated storage. They still
  need explicit maximum-byte budgets, but they do not require clearing unless
  the call site starts carrying credentials or source payloads.

When adding `ArrayPool<byte>` or in-memory accumulation (`MemoryStream`,
captured `Utf8JsonWriter` output, report/archive buffers), first classify the
data as sensitive bytes, bounded non-sensitive payload, generated JSON,
archive/report bytes, or diagnostic snippet. Sensitive paths should route
through `SensitiveBufferPolicy.ReturnSensitiveTokenBuffer`,
`ReturnSensitivePayloadBuffer`, `ReturnSensitiveCopyBuffer`, or
`ClearUsedSensitiveBytes`; these helper names are intended to be positive
evidence during security audits. Bounded generated JSON capture should use
`SensitiveBufferPolicy.GetBoundedGeneratedJsonInitialCapacity`. Sensitive paths
need a test that proves the cleared range; bounded accumulation paths need a
test or constant that proves the maximum byte budget.

## Custom Language Extraction

Downstream users can add lightweight language support without rebuilding
`cdidx`:

- extension aliases are read from `~/.config/cdidx/langmap.yaml` and the first
  workspace ancestor `.cdidx-langmap.yaml`; workspace entries override user
  entries. A trusted suffix override is evaluated before built-in exact-filename,
  filename-prefix, and extension rules. If the closest workspace map cannot be
  probed or read, ancestor workspace lookup stops for that subtree instead of
  reusing a parent map; `languages --json` and the MCP `languages` tool expose
  the sanitized failure in `language_map_diagnostics` and publish the effective
  order in `detection_policy.precedence`;
- regex-backed symbol patterns are read from `.cdidx/patterns/*.yaml` and
  `~/.config/cdidx/patterns/*.yaml`; sidecars must be regular files under
  non-symlink pattern directories, discovery accepts at most 128 candidates per
  pattern directory, each file is capped at 64 KiB / 128 rules, each immutable
  workspace snapshot loads at most 128 configured rules total, and regex matches use a 100 ms
  timeout. Each sidecar is parsed, compiled, and checked against
  `SymbolKindCatalog` before its path, rules, or budget are committed. Rejected
  content is fingerprinted to suppress duplicate diagnostics, while content or
  metadata changes and transient read recovery are retried without restarting.
    Workspace discovery requires an explicit trust root and stops after checking
    that root; it never probes ancestors above it. Nested sidecars inside that
    boundary are loaded for the current file in the bounded extraction worker.
    Path identity follows the
  active filesystem's case-sensitivity, so case-distinct sidecars remain
  distinct on case-sensitive volumes. `status --json` reports accepted files in
  `extractors.pattern_configs[]` with sanitized path, workspace/user provenance,
  normalized language, and rule count. Reindexing atomically replaces the
  workspace snapshot so the old rule budget and timeout state become
  collectible without changing other workspaces. A timed-out rule is suppressed
  by a bounded one-minute cooldown in its owning workspace snapshot and emits a
  workspace-scoped diagnostic;
- `cdidx test-extractor --language <lang> --file <path> --json` runs symbol
  extraction without building an index, and `--expect-symbols <json>` compares
  the extracted JSON to a fixture. The source and expectation files are capped
  at 4 MiB each.

Query-side `--lang` resolution uses this same workspace-aware extension and
extractor registry rather than a separate built-in list. Registered language
IDs, aliases, and extension-like spellings resolve to the registry's canonical
ID. Unknown values fail with `E010_USAGE_ERROR` and bounded edit-distance
suggestions; `--allow-unknown-lang` is the explicit escape hatch for an
unregistered plugin ID and preserves its trimmed spelling through the database
filter.

Minimal examples:

```yaml
# .cdidx-langmap.yaml
entries:
  - extension: ".kts.in"
    language: "kotlin"
```

```yaml
# .cdidx/patterns/toydsl.yaml
language: "toydsl"
extensions:
  - extension: ".toy"
patterns:
  - kind: "class"
    regex: "^entity (?<name>\\w+)"
```

Each configured regex should expose a named `name` capture. If it does not,
`cdidx` uses the full match text as the symbol name. Invalid, symlinked,
oversized, or over-budget sidecar files are skipped with a stderr diagnostic so
a broken local experiment does not prevent indexing. A rejected sidecar never
consumes the rule budget or becomes registered, and repairing it makes the next
discovery attempt eligible to load it.

## Debugging SQLite reader errors

`Database/DbDebug.cs` captures the last SQL command, parameter list, and per-row state for `ExecuteTrackedReader` / `TrackedRead` calls so that a `SqliteException` raised mid-loop can be dumped to stderr with enough context to reproduce the failure. The dump is gated to keep indexed source bytes from leaking through unrelated channels:

- **Off by default.** Setting `CDIDX_DEBUG=` (unset) makes `DbDebug.DumpToStderr` a no-op.
- **`CDIDX_DEBUG=1` / `true` / `yes` / `on`** turns on **redacted** mode: SQL text and parameter names appear verbatim, but string payloads use length plus a process-salted SHA-256 prefix instead of raw content. Values whose parameter or column name contains `path` are reduced to a segment-count shape such as `<path segments=4>` instead of a hash, avoiding stable cross-run path fingerprints. Use this in CI, shared logs, and bug reports. `0` / `false` / `no` / `off` explicitly keep debug off; unrecognized non-empty values warn once and fall back to off.
- **Raw text dumps require two opt-ins.** `CDIDX_DEBUG=unsafe` alone now downgrades to redacted mode and emits a one-shot stderr warning (`CDIDX_DEBUG=unsafe was ignored: pass --debug-unsafe on the command line ...`). The unsafe path only activates when the same process *also* receives the `--debug-unsafe` CLI flag, which `ProgramRunner.TryConsumeDebugUnsafeFlag` strips from `args` before normal parsing (the flag respects the `--` query-escape sentinel). This prevents stale shell-profile / CI environment variables from silently dumping indexed source bytes to stderr.
- Tests reset the per-process gate via `DbDebug.ResetForTesting()`; production code uses `DbDebug.EnableUnsafeForProcess()` exclusively from the CLI flag handler.

For symmetry, the MCP server no longer echoes raw `Exception.Message` content into JSON-RPC responses. `McpServer.BuildSanitizedToolErrorMessage` and `BuildSanitizedLoopErrorMessage` return only the tool name (when known) and the exception type so AI clients can branch on retry strategy without ingesting indexed text. The full message and stack trace are still written to stderr via `BuildToolErrorLog` / `BuildUnhandledLoopErrorLog` for local diagnostics.

---

<a id="開発者ガイド"></a>
# 開発者ガイド

## ビルド・テスト

基本コマンド:

| コマンド | 用途 |
|---|---|
| `dotnet build` | 既定のローカル構成で solution をビルド。 |
| `dotnet test` | 既定のテストセットを実行。 |
| `dotnet format CodeIndex.sln --verify-no-changes` | PR 前に repository formatting を検証。 |
| `dotnet test tests/CodeIndex.Tests/CodeIndex.Tests.csproj --settings tests/CodeIndex.Tests/CodeIndex.Tests.runsettings --blame-crash --blame-hang --blame-hang-timeout 5m` | main test project を repository runsettings と VSTest crash/hang blame collection 付きで実行。 |
| `dotnet run --project src/CodeIndex -- <command> [options]` | source から CLI を実行して確認。 |

リポジトリ直下のタスクラッパー:

| ラッパー | 用途 |
|---|---|
| `make build` | repository wrapper 経由でビルド。 |
| `make test` | repository wrapper 経由でテスト実行。 |
| `make lint` | formatting / lint 検証を実行。 |
| `make coverage` | coverage workflow を実行。 |
| `make mcp-smoke` | MCP smoke workflow を実行。 |

net9 CI lane に合わせる場合は `FRAMEWORK=net9.0 make test` を使います。`make` がない
環境では、同じタスクを `./dev.sh build`、`./dev.sh test` などで実行します。

開発上の契約:

| 項目 | 契約 |
|---|---|
| formatting と warning | CI は `.editorconfig` による repository formatting を強制し、`Directory.Build.props` により compiler warning を error として扱います。ローカル変更は PR 前に formatting check を通してください。既存の trim 解析警告は、通常の警告エラー化を止めずに修正を進められるよう `WarningsNotAsErrors` に明示列挙されています。ILLink は trimmed publish の smoke test を失敗させずに trim warning を報告し続けます。 |
| CLI help | `cdidx --help` は短い概要、`cdidx --help-all` は全コマンド・flag・例の一覧、`cdidx --help-flags` は共有 flag table のみ、`cdidx <command> --help` は 1 コマンドの usage line を出します。新しいコマンドは主要な user workflow である場合だけ簡易概要に載せ、full help とコマンド固有の usage table には必ず載せてください。 |
| `cdidx validate` | replacement character、BOM、NUL byte、混在改行、UTF-16 BOM、非 UTF-8 らしい内容など、indexed content の問題を user-facing に検査する integrity scan です。validation issue の種別や filter を追加する場合は、CLI usage、README entry、help summary を同期してください。 |
| `cdidx doctor` | support request 向けにコピーしやすい environment summary です。既定では redacted に保ち、secret 風の `CDIDX_*` 値は出力しないでください。新しい diagnostic field は issue triage に使える程度に安定したものだけにします。full environment inventory の filter（`--env-domain`、`--env-category`、`--env-sensitivity`）は大文字小文字を区別しない完全一致で AND 合成し、filtered JSON summary は global catalog ではなく返却 inventory を表します。`--max-json-bytes` は `--json --env-inventory=full` とだけ組み合わせ、serialize した UTF-8 文書と改行を数え、上限を超える成功文書の代わりに structured usage error を返します。`github` block は `proxy_default_credentials` を `enabled` / `disabled` として出力し、bounded な `max_request_timeout_s` も出します。proxy credential material や raw secret value は出力しないでください。`license --json` は version 付きの `license`、`commercial_use`、`trademark`、controlling `documents` contract を返します。 |
| 例外診断 | user-facing な CLI / JSON / MCP / file issue / local diagnostic output では raw `ex.Message` を直接 echo しないでください。例外の prose は `CommandErrorWriter.FormatSanitizedExceptionMessage`、`DiagnosticSanitizer.ForMessage`、または既存の bounded な `DiagnosticRedactor` helper を通し、回復に message が不要な場合は安定した error code/category を使ってください。意図的に残す broad catch は `risky-code/broad-exception-catch` taxonomy に沿い、bounded diagnostic、private な best-effort suppression、または documented fallback に正規化してください。 |
| shell completion | 生成された shell completion script には、生成元の `cdidx` version comment が含まれます。command や flag の schema を変えた場合は completion test を更新し、upgrade 後に installed completion を再生成する README guidance も保ってください。 |
| target framework | 製品版 CLI と NuGet tool packaging は `net8.0` を対象にしています。test project は `net8.0;net9.0` の multi-target で、CI は Linux、Windows、macOS の各 lane で両方の framework に対して test suite を実行します。CI 相当の full matrix を検証する場合は、両方の target framework を restore / 実行できる .NET SDK を使ってください。 |
| SDK selection | `global.json` は repository SDK を `9.0.301` に固定し、`rollForward` を無効化します。CI は `8.0.413` と `9.0.301` を明示的に install します。`8.0.413` は `net8.0` runtime lane を提供し、`9.0.301` は restore、build、test、publish、changelog 検証で選択される SDK です。SDK を更新する場合は、`global.json`、すべての `actions/setup-dotnet` version list、Docker build image、この guide を同じ変更で更新してください。 |
| GitHub Actions policy | workflow は hosted runner を version 付き label（`ubuntu-24.04`、`windows-2022`、`macos-14`）に固定し、top-level の `contents` permission は既定で read-only に保ちます。`continue-on-error` は failure path の diagnostic artifact upload に限定し、すべての upload artifact に明示的な retention を付け、artifact download は pattern と path で境界を絞ります。cache key は workflow + runner OS + `packages.lock.json` / `global.json` に scope し、広い restore-key fallback は使いません。`CiWorkflowTests.GitHubActionsWorkflows_FollowRunnerArtifactCacheAndContinueOnErrorPolicy` がこの checklist を強制します。 |
| test diagnostics | CI は `tests/CodeIndex.Tests/CodeIndex.Tests.runsettings` と VSTest の crash/hang blame collection、上限付きの 1 回だけの retry を使い、再現性のある失敗と retry で通る flake を区別します。Build and Test workflow は `ubuntu-24.04` / `net8.0` を補完的な coverage shard に分割します。Windows と macOS も同じ補完的な net8 分割を coverage overhead なしで使い、Ubuntu net9 compatibility lane は full suite を実行します。test suite の構成、共有 helper、state-isolation rule、timeout diagnostics、test-writing convention については [TESTING_GUIDE.md#テストガイド](TESTING_GUIDE.md#テストガイド) を参照してください。 |
| mutation testing | weekly の `Mutation testing` workflow は `stryker-config.json` を使い、`src/CodeIndex/Database/DbWriter.cs` に対して Stryker.NET を実行します。runtime budget を意図的に広げる場合を除き、transaction、savepoint、rollback、batch-write behavior に scope を集中させてください。workflow は pinned `dotnet-stryker` 4.14.0 tool と NuGet package を cache し、cache miss のときだけ tool を update します。mutation score gate は high 75、low 70、break 65 で、rollback や savepoint coverage を弱める変更は通常の PR test path の外で失敗します。 |

## CI / アーティファクト配布

| workflow | コマンドまたは設定 | 注意点 |
|---|---|---|
| read-only database query | `cdidx status --db /artifacts/codeindex.db --read-only --json`; `cdidx search AuthService --db /artifacts/codeindex.db --immutable` | query コマンドは `--read-only`（alias: `--immutable`）を受け付け、既存の CodeIndex database を SQLite の immutable read-only URI mode で開けます。CI artifact、mounted cache、`codeindex.db-wal` / `codeindex.db-shm` sidecar を作成・更新できない sandbox で使います。 |
| 変更系コマンド | `index`、`backfill-fold`、`optimize`、`vacuum` | 書き込み可能な storage を必要とし、read-only database open を拒否します。 |
| 再利用可能な index artifact | `cdidx export codeindex.cdidx.zip`; `cdidx import codeindex.cdidx.zip --db <path>`; `cdidx import codeindex.cdidx.zip --dry-run --json` | CI job では index 後に export して archive を upload します。export は `--overwrite` を明示しない限り既存 destination を拒否し、owner-only temporary file から atomic に publish して POSIX mode `0600` を検証します。export 成功時の JSON は従来 field を維持し、最終 archive の byte 数と SHA-256、完全で immutable な manifest を追加します。利用側は query コマンドの前に import でき、`--dry-run` / `--check` で destination DB を置き換えず archive を検証できます。別 checkout 由来の archive を import 先 project root として扱いたい場合は `--prune-paths` を使います。`.../.cdidx/codeindex.db` を import 先にした場合は sibling の project directory を使い、それ以外の DB path では process current directory に fallback します。archive は `manifest.json` と `codeindex.db` だけを含みます。import は ZIP entry 名を `ZipArchiveSafetyPolicy` で検証し、absolute path、parent-directory segment、backslash、NUL、non-canonical name、duplicate entry、extra entry を extraction 前に拒否します。manifest は row count、readiness bit、writer / indexed-head metadata、schema contract stamp、利用可能な unknown-extension summary などの bounded summary/readiness metadata を持ちます。import は manifest format、manifest `user_version`、`database_sha256`、存在する summary count、embedded SQLite file が CodeIndex database であることを検証してから destination DB を置き換えます。archive の `codeindex.db` entry は compressed / uncompressed metadata と extraction stream の双方で 8 GiB を上限に拒否されます。 |
| maintenance checkpoint と managed rollback | `cdidx db checkpoint <name> [--dry-run]`; `cdidx db checkpoints --list|--delete <name>|--prune --keep <n> [--dry-run]`; `cdidx db restore <name> [--dry-run] [--no-backup]`; `cdidx db restore-backups --list|--prune --keep <n>|--restore <id> [--dry-run] [--no-backup]` | 危険な maintenance の前に `codeindex.db` と既存 WAL/SHM sidecar の checkpoint を作成できます。import と2種類の restore は、既存 DB を置き換える前に consistent かつ検証済みの managed SQLite rollback snapshot を既定で作成し、`--no-backup` を明示した場合だけ省略します。managed directory は `<db>.restore-backup-<id>/` で、bounded manifest と standalone database payload 1個を含みます。manifest は SHA-256、byte 数、対応する `user_version`、provenance、任意の source identifier を記録しますが、local absolute source path は記録しません。`restore-backups --list` は従来 directory metadata との互換性を維持しつつ ID と provenance を表示し、既存 prune retention もそのまま利用できます。`restore-backups --restore <id>` は directory 境界、manifest、payload hash、schema、staging と rollback を合わせた free space を再検証してから、失敗時の transient rollback を伴う atomic replacement を実行します。`--dry-run` は変更せず、すべての検証と作成予定 backup を報告します。checkpoint の delete / prune と restore-backup の prune は明示的な変更 action を必要とし、checkpoint prune の bounded scan が truncated の場合は削除をすべて skip します。checkpoint は `<db>.checkpoints/<name>/` に置かれ、`backfill-fold` は `--no-checkpoint` がなければ row mutation 前に automatic checkpoint を作ります。 |
| binary compatibility | [COMPATIBILITY.md](COMPATIBILITY.md) | `cdidx` binary の upgrade / downgrade をまたぐ database compatibility を記載します。readiness bit、`codeindex_meta` contract stamp、rebuild requirement を変える場合は、この policy も更新してください。 |
| Fold backfill の preview / recovery | `backfill-fold --dry-run`; MCP `backfill_fold` の `dry_run: true` または `force: true` | dry-run は DB を変更せず FoldReady stamp も書かずに、rewrite 対象の folded-key row をプレビューします。MCP も同じ preview を受け付け、stored version / fingerprint が current に見える場合でも suspicious な fold metadata や row state を復旧するため `force: true` を受け付けます。non-dry-run rewrite は中断後に resume でき、完了済み row update は durable に残り、最終 FoldReady metadata は verification 成功後にだけ stamp されます。MCP response は `progress.rows_done`、`progress.rows_total`、`progress.fraction` を含みます。 |

## ファイルシステム権限

| artifact | POSIX permission / behavior |
|---|---|
| `.cdidx/` | mode `0700` で作成。 |
| `codeindex.db` と WAL/SHM sidecar | ファイルが存在する場合は mode `0600` を適用。既定は best-effort で、`CDIDX_DB_PERMISSION_POLICY=strict` により strict enforcement にできます。 |
| `suggestions-*.json` suggestion store | POSIX では owner-only の mode `0600` で atomic write します。 |
| portable export archive | indexed source text を含むため `Sensitive` profile で atomic write し、POSIX では owner-only の mode `0600` であることを検証します。既存 destination の置換には明示的な `--overwrite` が必要です。 |
| インデックス対象ワークスペースソースの読み取り | ソースファイル本文とチェックサムの読み取りは `FileShare.ReadWrite | FileShare.Delete`、長いパスの正規化、設定された最大ファイルバイト数、更新時刻の再確認を使い、ビルドツールが開いたままのファイルも無制限な肥大化を許さず検査できるようにします。 |
| atomic file write | `AtomicFileWriter` は sibling temp file に書き込み、要求された POSIX mode を置換前に適用し、file content を flush してから target へ rename し、Unix では parent directory を fsync します。local state、cache、suggestion、checkpoint、portable archive など private payload には `Sensitive` write profile を使い、user-requested report は内容が明示的に private でない限り既定の `Public` profile を使います。置換後に parent directory flush が失敗した場合、file は置換済みだが directory durability を確認できていないことが caller に分かるよう command は明示的に失敗します。Windows では、この helper の directory fsync 保証は supported Unix platform に限定されるため skip します。 |
| index lock、watch sub-run spool、staged hook script、lock metadata sidecar、active workspace の `active.json` | 内容が露出する前に owner-only file (`0600`) として作成または書き込み、該当するものは stale / corrupt diagnostic が local path を広く漏らしたり unbounded allocation を強制したりしないよう小さな bounded buffer で読みます。 |
| database checkpoint root、snapshot directory、manifest file、copy された DB/WAL/SHM snapshot、restore staging/backup directory | POSIX では owner-only に固定。 |
| `status --json` | `data_dir_mode`、`db_file_mode`、有効な `database_permission_policy`、Unix mode 操作失敗時の support-safe な `database_permission_diagnostics` を報告。 |

`CDIDX_DB_PERMISSION_POLICY` は `best_effort`（既定）または `strict` を受け付けます。
best-effort mode では、database / WAL / SHM の mode 変更または database mode 読み取りで
`IOException`、`UnauthorizedAccessException`、`NotSupportedException` が発生しても、
利用可能な SQLite database を使用不能にしません。cdidx は安定した
`database_permission_hardening_failed` warning を出し、database path を公開せずに
operation、logical target、安定 reason、message、remediation を記録します。strict mode
では、同じ error code と remediation hint を持つ structured `CodeIndexException` で失敗します。
Windows と明示的な SQLite file URI では、この POSIX 限定 enforcement を skip します。

### 破壊的ファイルシステム操作の監査

本番コードの `File.Delete`、`Directory.Delete`、`File.Move` 呼び出しは、CodeIndex が所有する状態、または caller が承認した出力に限って許可します。これらの領域を変更する場合は、ローカル binary で次の監査を再実行してください:

```bash
dotnet ./src/CodeIndex/bin/Debug/net8.0/cdidx.dll search File.Delete --path src/ --exclude-tests --exact-substring --count-by file --limit 80
dotnet ./src/CodeIndex/bin/Debug/net8.0/cdidx.dll search Directory.Delete --path src/ --exclude-tests --exact-substring --count-by file --limit 80
dotnet ./src/CodeIndex/bin/Debug/net8.0/cdidx.dll search File.Move --path src/ --exclude-tests --exact-substring --count-by file --limit 80
```

| surface | ownership / boundary policy | cleanup / rollback policy |
|---|---|---|
| `AtomicFileWriter` の file delete / move helper | caller が destination を承認または検証した後の出力 path に使います。temp file は target の sibling として衝突しにくい名前で生成するため、置換は同じ filesystem boundary に残り、別 temp root の policy には依存しません。 | 書き込みは temp file を flush し、target に rename し、Unix では parent directory を flush します。move 前の temp cleanup は best-effort です。置換後の parent-directory flush failure は、target がすでに変わっているため command failure として明示します。 |
| `cdidx import` / `cdidx export` の temp database と sidecar | import temp DB は destination DB directory 内の hidden sibling、または dry-run 用の owner-only `codeindex-import-*` temp directory に置きます。export snapshot は owner-only `codeindex-export-*` temp directory に置きます。destination DB replacement は export output が source DB / sidecar と重なる path を拒否し、backup sidecar 経由で rollback します。 | temp DB、WAL、SHM、空の temp directory の cleanup failure は warning として出し、import/export の primary error を隠しません。replacement failure では destination state を確認できる residual diagnostics を報告します。 |
| `cdidx db` checkpoint restore staging と restore-backup prune | checkpoint、restore staging、restore backup は解決済み DB path から派生します。recursive cleanup は DB parent を safe root とし、`codeindex.db.restore-*` 形式の expected prefix を渡して `FileSystemBoundary.TryValidateDirectoryCleanupTarget` で検証します。checkpoint payload file は regular file である必要があり、symlink、reparse point、device は拒否します。 | restore は置換前に backup を作り、失敗時は rollback を試みます。temp directory cleanup と restore-backup prune の失敗は bounded diagnostic または warning になり、検証済み root の外側は削除しません。 |
| upgrade installer script と temp directory | upgrade download は `Path.GetTempPath()` 配下の owner-only `cdidx-install-*` directory を使います。recursive directory cleanup は削除前に temp root、required prefix、symlink / reparse / device 状態を検証します。install-directory write probe は、root、symlink / reparse point、unsafe POSIX mode を拒否する install-directory validation の後だけ実行します。 | installer script と temp-directory cleanup の失敗は warning です。install operation の結果は、二次的な cleanup failure とは分けて報告します。 |
| `.cdidx` write probe と case-sensitivity probe | write probe は、解決済み install directory、`.cdidx` directory、または `.cdidx/probes` directory 配下に fresh file として生成します。probe directory は owner-only で作成され、workspace data directory 配下にあります。 | probe file は確認後に削除します。case-sensitivity probe directory の cleanup は、作成済みの空 probe directory を削除する前に workspace data / probe root、期待する directory name、symlink / reparse / device 状態を再検証します。拒否した cleanup は bounded diagnostic を記録し、`cdidx` process が動いていないときに stale `.cdidx/probes` entry を削除するよう案内します。 |
| legacy scan checkpoint | full scan は `.cdidx/scan-checkpoint.json` を作成も参照もせず、最初の immutable scan-input barrier 後に success / partial のどちらでも旧fileをparseせず削除します。 | delete failure は human output では warning、JSON output では `CliJsonMessage` entry です。indexing は古いHEAD-only stateに依存せず継続します。 |
| Git hook staging | hook installation は操作を create、managed replacement、custom-hook chain、exact no-op のいずれかに分類します。`hooks install --dry-run` は directory 作成や staging より前にその plan と managed script を返し、実際に変更する場合は repository hook directory 内に private staged hook script を書き込み、必要に応じて backup path 付きの `File.Replace` で hook file を置き換えます。 | 同一かつ実行可能な UTF-8/no-BOM の managed hook に対する再実行は書き換えず `already_installed` を返します。それ以外の encoding や実行不可の managed hook は Git が実行できるよう置き換えます。staged script が配置されなかった場合の cleanup は best-effort で、hook warning として記録します。managed hook の削除失敗は requested mutation の失敗なので command error です。 |
| index / MCP lock metadata sidecar | lock file と `.info` sidecar は、解決済み DB または MCP index lock path の隣に置き、owner-only permission で作成します。 | lock dispose は metadata sidecar だけを削除します。cleanup failure は `GlobalToolLog` と任意の test sink に記録します。stale lock file は recursive cleanup ではなく OS の lock release に依存して復旧します。 |
| search audit recipe | `SearchAuditRecipes` には `Directory.Delete` や `File.Move` のような literal recipe string が含まれます。これは search metadata であり filesystem mutation ではありません。 | cleanup policy は適用されません。 |

best-effort cleanup の catch は、所有済み temp / probe / lock / metadata artifact の二次 cleanup に限定してください。operator が残骸に対処できる場合は bounded warning または diagnostic を出し、primary operation result を隠してはいけません。例外は atomic replacement 後の durability confirmation です。target がすでに置換済みで Unix parent directory を flush できない場合は、filesystem state は変わったが durability を確認できていないことを caller に伝えるため、command は明示的に失敗します。

## リリース配布チェックリスト

release 準備時は、[DISTRIBUTION.md](DISTRIBUTION.md) に記載された supported distribution
channel をすべて確認してください。

| channel / area | 確認内容 |
|---|---|
| `install.sh` | latest install、explicit-version install、`--doctor`、local mirror self-test。 |
| NuGet global tool | clean な .NET 8 tool environment での install/update。 |
| NuGet trusted publishing | GitHub Actions variable `NUGET_TRUSTED_PUBLISHING_USER` が trusted publishing policy を作成した NuGet.org ユーザー名に設定されていること。この値は package owner と異なり得る。 |
| release asset | advertised RID ごとに published release asset があること。 |
| release workflow の権限分離 | `.github/workflows/release.yml` は `prepare-release-files` と `verify-release-install` を `contents: read` に保ち、短命の `release-payload` artifact だけを `create-release` に渡し、一時 GPG material を公開処理の続行前に削除し、Windows 署名 secret を署名 step のみにスコープする。 |
| GHCR container image | 公開済みの `linux/amd64` / `linux/arm64` image で `cdidx --version` が動作し、runtime `git` を含まず、provenance / SBOM attestation を公開していること。 |
| package metadata | license、repository URL、tag、runtime prerequisite が正しいこと。 |
| documentation link | README、USER_GUIDE、package metadata からの link が意図した docs を指すこと。 |

### NuGet lock ファイル

| 契約領域 | 要件 |
|---|---|
| lock ファイル参加 | `Directory.Build.props` が `RestorePackagesWithLockFile=true` を設定しているため、本 solution 配下の各 project は `.csproj` と並んで `packages.lock.json` を出力します。lock ファイルは直接依存と**推移依存**の双方について解決済み version と `contentHash` を固定し、`Microsoft.Data.Sqlite` 配下に native を含めて出荷する `SQLitePCLRaw.bundle_e_sqlite3` まで対象に含めます。これにより machine、CI lane、release artifact の再現性が保たれ、推移依存の暗黙 bump や downgrade attack が build を壊す差分として顕在化します。 |
| package source 境界 | repository root の `nuget.config` は machine-wide な package source を消去し、`https://api.nuget.org/v3/index.json` だけを許可し、すべての package ID をその source に map し、署名付き package を必須にします。trusted signer は NuGet.org repository-signing certificate と、現在 lock されている package graph に必要な author-signing certificate に限定し、未署名 package、未設定 feed 由来の package、未知の author による署名 package を拒否します。NuGet.org または承認済み package author が signing certificate を rotate した場合は、restore 検証と同じ変更で `nuget.config` を更新してください。 |
| CI and Docker locked restore | CI（`.github/workflows/dotnet.yml`, `release.yml`, `codeql.yml`）は各 lane が必要とする最小の dependency graph に `--locked-mode` を適用します。primary build / CodeQL lane は solution、compatibility build lane は matrix framework の test project、native release lane は `net8.0` の test project、cross-compile release lane は production project を restore します。test-project restore は `ProjectReference` 経由で production dependency graph も含むため、commit 済み lock ファイルと選択した解決結果に差分があると artifact に混入する前に build が失敗します。Docker build は `src/CodeIndex/CodeIndex.csproj` を `--locked-mode` 付きで restore し、`--no-restore` 付きで publish します。container restore / publish flag を変える前に、Docker 用 musl RID の entry が project lock file に commit されている状態を保ってください。build / release / mutation workflow の NuGet package cache は workflow scope の lockfile 由来完全一致 key だけで復元し、OS 単位の broad cache prefix には fallback しません。ローカル開発の通常 restore は従来どおりで、lock file enforcement は CI と Docker build で強制されます。 |
| deterministic package metadata | `CodeIndex` package project は deterministic build に opt in し、Source Link 用の repository metadata を公開します。GitHub Actions では `ContinuousIntegrationBuild=true` も設定し、untracked source input を埋め込むため、PDB と `.snupkg` artifact は local machine path なしで repository に対応付けられます。build metadata は可能な場合 wall-clock build date ではなく Git commit date を使い、同じ commit の繰り返し build が timestamp で drift しないようにします。`Microsoft.SourceLink.GitHub` は build-only dependency（`PrivateAssets=All`）であり、runtime dependency ではありません。 |
| vulnerability check | 通常の build/test workflow は locked restore 後に `dotnet list src/CodeIndex/CodeIndex.csproj package --vulnerable --include-transitive` を実行し、direct または transitive runtime package に High / Critical の NuGet advisory があると失敗します。native SQLite graph に advisory が出て、`Microsoft.Data.Sqlite` がまだ脆弱な最小 version を許容している場合は、修正済み bundle version の `SQLitePCLRaw.bundle_e_sqlite3` を直接参照に追加し、commit 済み lock file を更新して CI、Docker、release restore が同じ修正済み native payload を解決するようにしてください。Dependabot は `.github/dependabot.yml` で NuGet と GitHub Actions の weekly update PR を作るよう設定されているため、security fix と通常の dependency/action bump は release surprise になる前に提案されます。 |
| release publish/pack restore | release の `dotnet publish`（RID ごと）と `dotnet pack`（NuGet packaging）には意図的に `RestoreLockedMode=true` を設定していません。これらは runtime-specific な restore を走らせ、直前の locked-restore boundary には存在しなかった lock entry（`net8.0/<rid>` 等の runtime section や trimming 用の `Microsoft.NET.ILLink.Tasks`）を正当に追加します。それでも `Directory.Build.props` の `RestorePackagesWithLockFile=true` により、その実行 machine 上の全 restore は lock file 経由で解決されるため version は固定されたままです。release / container build は repository で固定した .NET 9 SDK で実行しますが、CLI が `net8.0` を対象にしている間、直接参照する `Microsoft.NET.ILLink.Tasks` は .NET 8 系に留めます。Dependabot は同 package の major update を無視するため、9.x / 10.x ILLink task による runtime/tool mismatch を再導入しません。`Microsoft.Data.Sqlite` および `SQLitePCLRaw.*` graph に対する supply-chain 保証は、先行する lane 対応の locked restore で担保されます。native release validation は production graph を `ProjectReference` で含む `net8.0` test project を restore し、cross-compile release validation は production project を直接 restore します。 |

`dotnet pack` 後、release workflow は NuGet artifact の検証、hash、publish の前に
package normalization を実行します:

```bash
dotnet run --project tools/CodeIndex.PackageNormalize -- nupkg/*.nupkg nupkg/*.snupkg
```

release diagnostics では、書き換えずに検査し、candidate package 全体の bounded
summary を取得できます:

```bash
dotnet run --project tools/CodeIndex.PackageNormalize -- --dry-run --summary nupkg/*.nupkg nupkg/*.snupkg
dotnet run --project tools/CodeIndex.PackageNormalize -- --dry-run --json --continue-on-error nupkg/*.nupkg nupkg/*.snupkg
```

`install.sh` は `install_modules/` 配下の focused fragment から生成されます。
生成された bundle には、それらの fragment を canonical source とする
`@generated` provenance marker が付与されます。コピーされた実装が既定の definition / graph
query で二重計上されないよう、生成出力ではこの marker を維持してください。bundle 側の
コピー自体を監査する場合だけ `--include-generated` を使用します。
installer、doctor、self-test、reinstall、uninstall、dispatch logic を変更した場合は、
テスト前に checked-in の単一ファイル installer を再生成してください:

```bash
bash tools/build-install-sh.sh
```

| normalizer rule | 詳細 |
|---|---|
| 再現可能な OPC metadata (#2756) | NuGet の OPC package writer は `package/services/metadata/core-properties/*.psmdcp` part 名を pack ごとにランダム生成します。normalizer はその part を `package/services/metadata/core-properties/core-properties.psmdcp` に書き換え、対応する content-type / relationship 参照も更新し、ZIP entry timestamp を固定します。これが `.nupkg` / `.snupkg` archive の package 再現性境界です。 |
| 書き換えの耐久性 (#3961) | package normalization は package の隣に衝突しにくい `.cdidx-normalize-*.tmp` を作って書き込み、完成した temp file を flush してから package を置き換え、Unix では parent directory も flush するため、置き換え後の耐久性失敗を明示的に報告します。cancellation は ZIP entry 間と stream chunk 間で確認し、作成済み temp file は best-effort で削除します。 |
| 作業量の上限 (#2892) | 書き換え前に、normalizer は 4096 を超える ZIP entry、128 MiB を超える uncompressed entry、512 MiB を超える合計 uncompressed content、または 16 MiB を超える XML 参照テキストを持つ package を拒否し、細工された package が無制限の normalize 作業を強制できないようにします。 |
| unsafe ZIP name (#2894, #4174) | ZIP entry 名の検証は `ZipArchiveSafetyPolicy` で共有します。destination archive を作る前に、normalizer は absolute path、Windows drive root、backslash separator、NUL character、空の path segment、parent-directory segment、空に正規化される名前、path 正規化後に衝突する destination 名を拒否します。`cdidx import` も同じ policy を使い、正確に `manifest.json` / `codeindex.db` の entry だけを受け付け、duplicate entry と extra entry を payload 読み込み前に拒否します。 |
| unsafe ZIP attributes (#3552) | entry のコピー前に、normalizer は POSIX symlink / device / special-file type と unsafe DOS 属性を拒否し、source の permission bit を保持せず deterministic に scrub した external attributes で normalized entry を書き込みます。 |
| failure diagnostics (#3458) | CLI は 1 回の実行で受け付ける package path を最大 1024 件に制限し、raw な path-heavy exception text ではなく bounded な package path / ZIP entry diagnostics を報告し、cleanup 削除失敗を JSON 出力の package ごとの `warnings` として出します。 |
| temp-file replacement policy (#3996) | rewrite 前に、normalizer は古い legacy `.normalize-tmp` sidecar を排他的に open できた場合だけ削除し、lock 中または access 不能な場合は実行中の別 normalizer との競合を避けるため abort します。replacement archive は引き続き同じ directory の衝突しにくい `.cdidx-normalize-*.tmp` sidecar を使い、完成した temp file を flush して package に移動し、Unix では parent directory も flush します。 |

依存を意図的に更新する（あるいは直接 `PackageReference` を追加する）場合は、ローカルで lock ファイルを再生成し、同じ変更でコミットしてください:

```bash
dotnet restore CodeIndex.sln --force-evaluate
git status --short -- '**/packages.lock.json'
```

| 状況 | 必要な対応 |
|---|---|
| CI で `NU1004 The packages lock file is inconsistent with the project dependencies` が出る | ローカルで `dotnet restore --force-evaluate` をやり直し、lock file の差分を確認した上で commit してください。CI を通すために lock file を削除してはいけません。それは本契約が塞いだ supply-chain の穴を再び開けます。 |
| 直接 `PackageReference` を持たない project（例: `tools/CodeIndex.Changelog/`） | lock file は意図的に空の `net8.0: {}` dependency map を含みます。これは正常な状態で、当該 project も locked-mode 契約に参加している証拠です。 |

## アーキテクチャ

| 領域 | 主なファイル | 役割 |
|---|---|---|
| CLI 入口 | `Program.cs` | 薄いコマンド入口とルーティング。 |
| CLI ランナー | `Cli/IndexCommandRunner.cs`, `Cli/QueryCommandRunner.cs` | index と search / definition / reference / caller / callee / symbol / file / map / inspect / outline / status 系コマンド、引数解析。 |
| CLI サポート | `Cli/CliCommandCatalog.cs`, `Cli/CliFlagSchema.cs`, `Cli/ConsoleCompletionRenderer.cs`, `Cli/ConsoleUi.cs`, `Cli/CommandExitCodes.cs`, `Cli/SearchSnippetFormatter.cs`, `Cli/LineWidthFormatter.cs` | command / subcommand / flag の共有 metadata、生成される command help と completion、ユーザー向け出力、終了コード、一致中心スニペット、行幅クランプ。 |
| ワークスペース解決 | `Cli/DbPathResolver.cs`, `Cli/GitHelper.cs`, `Cli/IndexFreshnessChecker.cs`, `Cli/WorkspaceMetadataEnricher.cs`, `Cli/GlobalToolLog.cs` | DB パス解決、git-aware な更新入力、DB/作業ツリー鮮度確認、workspace metadata、install log。 |
| SQLite ストレージ | `Database/DbContext.cs`, `Database/DbConnectionFactory.cs`, `Database/DbPragmaPolicy.cs`, `Database/SqliteConnectionPolicy.cs`, `Database/SqliteCommandPolicy.cs`, `Database/SqliteIdentifier.cs`, `Database/DbWriter.cs`, `Database/DbReader.cs` | WAL 付き SQLite schema、共有 connection string / read-only URI / command timeout policy、retry と pragma policy、型付き SQLite parameter / scalar helper、quoted identifier SQL helper、batch write、古い file の cleanup、FTS5 search、symbol/reference lookup、excerpt、outline、inspect bundle、status、dependency query。 |
| リポジトリマップ | `Database/RepoMapBuilder.cs` | `map` 用の repo overview: file stats、entrypoint 候補、hotspot、module grouping。 |
| ファイル走査 | `Indexer/Scanning/FileIndexer.cs`, `Indexer/Scanning/FileIndexer.LanguageDetection.cs`, `Indexer/Scanning/ChunkSplitter.cs` | full/update 共通の path filtering、ignore 処理、language detection、file record、80 行 chunk と 10 行 overlap。曖昧な `.h` 判定は、上限付き C/C++ 字句マスカーを再利用し、評価対象外の byte でも字句・プリプロセッサ状態をストリーミング追跡しながら、48 KiB の UTF-8 byte budget 内でヘッダー全体または先頭・中央・末尾 range を評価します。判定元・信頼度 metadata は loaded record に保持し、`index --dry-run --json` の `language_detections` で公開します。 |
| シンボル抽出 | `Indexer/Symbols/SymbolExtractor.cs`, `Indexer/Symbols/SymbolExtractor.Lisp.cs`, `Indexer/Symbols/CSharpSymbolNameNormalizer.cs` | 対応言語の hybrid symbol extraction と C# persisted name canonicalization。 |
| 参照抽出の制御 | `Indexer/References/ReferenceExtractor.cs` | graph 対応言語の regex / state-machine reference extraction を dispatch。 |
| 参照抽出サポート | `Indexer/References/Support/*.cs` | masking、type-position scan、trailing lambda、JVM method reference、SQL name resolution の共有処理。 |
| 言語別抽出器 | `Indexer/References/Languages/*ReferenceExtractor.cs` | C#、Java、Python、SQL、Rust、Swift、Terraform などの言語別 reference extraction。 |
| MCP サーバー | `Mcp/McpServer.cs` | AI coding tool 向け JSON-RPC 2.0 server。batch query も含む。トランスポートは `IMcpTransport` で差し替え可能。 |
| MCP トランスポート | `Mcp/IMcpTransport.cs`, `Mcp/StdioMcpTransport.cs`, `Mcp/HttpMcpTransport.cs` | MCP サーバー向けの stdio（既定）と任意の HTTP `POST /` トランスポート（#1558）。 |
| DTO | `Models/FileRecord.cs`, `Models/ChunkRecord.cs`, `Models/SymbolRecord.cs`, `Models/ReferenceRecord.cs` | indexing、storage、query、MCP layers で共有する record。 |
| テスト | `tests/CodeIndex.Tests/*Tests.cs`, `TestProjectHelper.cs`, `TestConsoleLock.cs` | chunking、extraction、DB read/write、CLI、MCP、git helper、共有 test harness の focused unit / integration coverage。 |

command と nested subcommand の名前、および nested verb が optional で親 command の
flag も維持するかは `CliCommandCatalog`、primary flag、alias、
placeholder、description、command applicability、canonical value domain と value alias、
safety / scope 分類は `CliFlagSchema` が管理します。command usage の placeholder、
command / shared flag help、output-format validation、search の origin / result-kind
validation、全 shell completion renderer はこの共有定義を参照します。専用 parser /
nested parser は subcommand metadata が揃うまで usage 固有の正確な構文を維持します。
CLI option や受理値を追加・変更するときは、まず schema を更新し、help、validation、
completion に並行した value list を追加しないでください。これにより、不完全な option
一覧を出さずに runtime validation、help、completion、生成される next-step flag の同期を
維持します。
`CliCommandCatalog.CommandSubcommands` に掲載するすべての verb は、制約、副作用、例を
記載した hidden な verb 固有 `ConsoleUi` usage entry に解決させ、aggregate usage line
には受理する公開 flag を漏れなく列挙してください。

大きな command / extractor file については
[docs/large-file-decomposition-plan.md](docs/large-file-decomposition-plan.md)
に追跡可能な分割計画があります。`QueryCommandRunner`、`SymbolExtractor`、
`LanguageReferenceExtractionSupport`、`McpToolHandlers`、`FileIndexer` の
ownership boundary を分けるときは、挙動変更を review しやすく test しやすい単位に
保つため、この計画を使ってください。

### ワークスペース

`cdidx.workspace.json` と `.cdidx-workspace.json` は YAML dependency を増やさずに monorepo
member を宣言します。workspace manifest は 64 KiB、JSON nesting 16 level、1024 members、
member path 4096 characters、`default_db_name` 255 characters に制限されます。
schema は additive で、`members` は manifest directory からの相対 path かつ正規化後も
manifest directory 配下に残る member path、
`index_strategy` は `per_member` または `single`、`default_db_name` は
`codeindex.db` を上書きする plain file name、`shared_ignores` は共有 ignore policy 用の予約 field です。
invalid な `members` entries は件数を制限した diagnostics で拒否され、有効な entry は DB path を
作る前に workspace path casing policy で正規化・重複排除されます。
`cdidx workspace list` と `cdidx workspace status` は member DB path を報告します。
`workspace status` はさらに、member ごとの database 存在有無、probe status / reason、
schema compatibility、workspace との厳密な freshness、timestamp、index completeness、
graph readiness を報告します。1 回の実行で probe する既存の異なる member database は最大 64 個で、
`single` strategy で database が共有される場合は probe 結果を再利用し、それ以降の member は
`not_checked` として top-level の truncation summary に反映します。
JSON mode では、manifest schema または safety validation の失敗は top-level crash handler へ
落とさず、構造化された `workspace_manifest_invalid` error として返します。

`cdidx workspace use <name-or-relative-path>` は既存の manifest member または `default` を
active workspace として per-user config directory に保存し、存在しない member は拒否します。
directory name は member を一意に識別できる場合の省略形として引き続き使えますが、同名 directory
が複数ある場合は曖昧として拒否します。manifest-relative path は正規化後の member を厳密に選択し、
slash / backslash のどちらも受け付け、active workspace state には canonical な forward-slash
relative path を保存します。manifest member の選択では `manifest_member: true` も保存するため、
`default` または `env` という member も予約された非 manifest state と区別できます。
active workspace name の上限は manifest member path と同じ 4096 文字です。
`cdidx workspace clear`（alias は `workspace deactivate`）は `default` を current directory に
再設定せず、保存済みの選択を削除します。`CDIDX_ACTIVE_WORKSPACE` が設定されている場合は
persisted state より環境変数が優先されるため、先に環境変数を解除するよう報告します。
query DB 解決の優先順位は既存どおり、明示 `--db`、明示 `--data-dir` / `CDIDX_DATA_DIR`、
active workspace state、ancestor/CWD discovery の順です。

### 可観測性

CodeIndex は opt-in の `ActivitySource` 名 `CodeIndex` を公開します。MCP JSON-RPC frame は
`mcp.request` server span、tracked database helper 経由の SQLite command は `db.query`
span を作ります。MCP caller は `params._meta.traceparent` で W3C trace context を渡せます。
exporter dependency は bundled されないため、host process が OpenTelemetry/Diagnostics
listener を入れた場合だけ span が emit されます。

slow SQLite command diagnostic は `CDIDX_SLOW_QUERY_MS=<milliseconds>` で stderr に出せます。
query コマンドも JSON profile block 用の `--profile` と command-scoped profiling 用の
`--slow-query-ms <milliseconds>` を受け付けます。

### リソース境界契約

| 経路 | 契約 |
|---|---|
| worker protocol JSON | isolated worker の stdin frame は `BoundedLineReader` で読みます。symbol-worker client は request を直接 UTF-8 に serialize して改行区切りの byte を process stream へ書き、worker は response を stdout stream へ直接 serialize し、client は bounded response frame を UTF-8 byte のまま読み取って直接 deserialize します。これにより source file ごとに両方向で発生していた追加 UTF-16 JSON string と encoding pass を避けます。既定の frame 上限は文字数・UTF-8 byte 数ともに 32 MiB です。大きな `--max-file-bytes` によって JSON escape 分の余裕が必要な場合、protocol frame 上限は `WorkerProtocolLineLimits.MaxExtendedLineUtf8Bytes`（384 MiB）まで拡張できますが、`int.MaxValue` までは拡張しません。`WorkerProtocolJsonValidator` は `JsonDocument.Parse` の前に合意済みの文字数 / UTF-8 byte 上限を超える payload を拒否し、`DefaultMaxJsonDepth`（32）で parse し、object property 1,000,000 件超と frame 上限を超える string を拒否します。 |
| user regex find | `find --regex` は lookaround / backreference 互換性のため classic .NET regex engine を維持し、`RegexOptions.CultureInvariant` を付け、`--exact` でない場合は `IgnoreCase` も付け、各 match に `BoundedRegex.DefaultMatchTimeout` を使います。timeout は CLI JSON で `E014_REGEX_MATCH_TIMEOUT` / `regex_timeout` として返り、人間向け出力にも同じ recovery hint が出ます。`find --all` は index 全体を走査する前に candidate file と line scan の上限も適用します。 |
| shared regex construction | production の regex 構築は `BoundedRegex`、`RegexRegistry`、または `RegexTimeoutPolicy` に集約します。extractor pattern と bounded static regex API には `BoundedRegex`、timeout 例外を維持する必要がある raw BCL regex factory（`find --regex`、ignore glob regex、generated-code path pattern）には `RegexRegistry`、diagnostic / redaction surface には `RegexTimeoutPolicy` を使います。`RegexRegistry` は ignore glob timeout（100 ms）、generated-code pattern timeout（50 ms）、および `BoundedRegex.DefaultMatchTimeout` を使う find-regex factory の名前付き policy を所有します。search-audit recipe は `BoundedRegex` alias と `RegexRegistry.cs` だけを集約済みの positive evidence と見なすため、新しい production raw constructor は明示的な factory または generated-regex entry とテストを伴う必要があります。 |
| filesystem traversal helper | `FileSystemTraversalPolicy` は top-directory-only enumeration を明示し（`IgnoreInaccessible=false`、暗黙の再帰なし）、任意指定の `CancellationToken` / entry budget option を公開します。想定内の traversal failure は中央で分類し、command diagnostic が permission、I/O、invalid-path、unsupported-path、path-too-long、budget-exceeded の taxonomy を共有します。 |
| `MaxValue` sentinel | `int.MaxValue` は、次の操作が SQL limit、allocation、traversal、payload sizing、timeout conversion の前に clamp する場合だけ内部 sentinel として使えます。ユーザー影響値は multiplication、buffer sizing、protocol framing、query expansion の前に、名前付きの実用上限へ落としてください。 |

### インデックスパイプライン

```
ディレクトリ走査 / 共有パスフィルタ（組み込みスキップ + `.gitignore` / `.cdidxignore` + reparse/Windows Hidden/System 属性 pruning）→ 言語検出 → ファイル読み込み（UTF-8）
  → ファイルレコードUPSERT
  → チャンク分割（80行、10行重複）
  → 正規表現でシンボル抽出
  → 正規表現で軽量参照を抽出
  → チャンク＋シンボル＋参照をバッチ挿入（1トランザクション500件）
  → FTS5インデックス反映
```

single writer は、複数行の chunk、symbol、reference、reference-line command を
row-count shape ごとに bounded な `PreparedCommandCache` で再利用します。SQL text と
型付き parameter schema は cache miss 時だけ構築し、各実行では ordinal 順に parameter
value を再設定します。大規模 index で同じ SQLite command を file ごとに再構築しないよう、
新しい bulk-write 経路もこの bounded cache に載せてください。

リポジトリ全体の incremental scan は、C# contract prepass と parallel extraction の前に
stat-reuse 候補を 1 回の SQLite statement で読みます。各候補は引き続き最新の filesystem
size と UTC 更新時刻と照合し、language extractor version、extraction cap、古い issue metadata、
generated-code suppression も snapshot eligibility contract に含めます。CLI と MCP のどちらでも、
この snapshot を file ごとの database probe に戻さないでください。旧 DB の欠損または不正な
stat 値を持つ row は除外して通常の checksum reuse / 再 index で修復し、CLI/MCP の cancellation は
後続の extraction pipeline だけでなく snapshot query も中断できる状態を保ってください。

`FileIssue` rows には nullable な `origin` / `severity` metadata が入ることがある。
`replacement_char` では `origin: source_literal` が正規にエンコードされた U+FFFD
literal、`origin: decode_replacement` が不正 byte に対して decoder が挿入した U+FFFD
を意味する。source literal は `severity: info`、エンコーディング破損の可能性は
`severity: warning` として返す。

`--files` / `--commits` の部分更新も、フルスキャンと同じパスフィルタを再利用する。各ディレクトリでは `FileIndexer` が `.gitignore` を `.cdidxignore` より先に読み、この順序でルールを追加し、後続の `!` パターンを再包含として扱う。commit 単位更新に `.gitignore` または `.cdidxignore` の変更が含まれる場合、`IndexCommandRunner` は newly ignored file を安全に purge するため自動でフルスキャンへフォールバックする。malformed な ignore 行は走査エラーとして報告し、その行だけをスキップして index 全体は継続する。symlink は既定で `--follow-symlinks none` とし、`internal` は workspace root 内へ解決される file / directory target、`all` は解決可能なすべての target を追跡する。discovery、dry-run、C# workspace preflight、content loading は同じ解決済み file target identity を使うため、許可された静的な外部 file target は索引し、preflight 後に retarget された link は source drift として拒否する。dangling symlink は個別に集計して warning とし、index dry-run も実行時と同じく `warnings_total` / `warnings` で報告して成功終了する。directory target の解決時に発生した permission failure も scan warning として報告する。Windows では Hidden または System 属性が付いたファイルとディレクトリを言語検出前に拒否する。プロジェクト所有のソースを索引したい場合、ignore ルールでは再包含できないため先にそれらの属性を外す。

### メタデータ不変条件

`DbWriter.SetMeta` は、caller の writer transaction が active な場合はその transaction に参加します。
active な writer transaction がない場合は、metadata UPSERT を SQLite savepoint で包み、
standalone stamp にも commit boundary を持たせ、raw SQL transaction からの呼び出しで nested
`BEGIN` を試みないようにします。dependent metadata と row rewrite が一体で成功・失敗すべき
場合は、同じ `DbWriter.BeginTransaction()` scope に入れてください。dependent row を書く前に
readiness や schema trust metadata を stamp してはいけません。

carried fold metadata が current の場合、CLI と MCP は `MarkFoldReadyWithResult` を唯一の
validation-and-stamp 経路として使います。`BEGIN IMMEDIATE` の下で NULL folded column と current
folded value を 1 回だけ検証し、正確な失敗 category を返して、caller 側の full-table pre-scan なしで
readiness を stamp します。carried metadata が stale の場合は、legacy-backfill の degradation reason
を優先するためだけに、より軽い NULL-only check を caller が実行できます。

### インデクサー拡張

out-of-tree の post-extraction hook は、`~/.config/cdidx/hooks/` または
`CDIDX_HOOKS_DIR` が指す directory に置いた `.dll` 内で
`CodeIndex.Indexer.Hooks.IPostExtractionHook` を実装できます。hook discovery は
directory の全祖先を検証し、安全でない owner / group・world writable mode と
symlink / reparse point の祖先を拒否し、
`CDIDX_HOOK_DISCOVERY_MAX_DLLS` 件（既定 128）までの DLL 候補を調べ、symlink / reparse point の候補を拒否し、各候補は
`CDIDX_HOOK_DISCOVERY_MAX_BYTES` bytes（既定 67108864）以下である必要があります。その bounded
candidate set を path order で load します。discovery は concrete hook type が public parameterless
constructor を持つことを検証します。assembly load、module initialization、`GetTypes`、constructor validation は
deadline・memory・output 上限付き discovery worker 内だけで実行し、bounded manifest を返します。
worker は response を flush した後も parent input pipe 上で待機し、parent は manifest を受理する前に descendant を含む live な discovery process tree 全体を停止します。
hook constructor と callback は isolated callback worker 内で実行されます。
built-in symbol extraction 後と built-in reference extraction 後、row 永続化前に hook が呼び出されます。
hook は `FileContext` と mutable な `IList<SymbolRecord>` / `IList<ReferenceRecord>` を受け取り、
extracted record の annotation、synthetic symbol 追加、domain-specific reference 追加ができます。

plugin marker / API compatibility は PE metadata だけで検証し、実行可能な assembly を load する前に完了します。marker の受理には正確な attribute constructor signature、完全な value blob、単一 marker が必要です。metadata が参照する同一 directory の managed dependency も同じ filesystem boundary を通し、件数・総 bytes 上限の内側で再帰的に staging します。plugin assembly load、type inspection、construction、symbol / reference extraction はその後、5 秒の wall-clock deadline、256 MiB working-set limit、bounded line protocol を持つ専用 worker 内で実行します。parent は manifest-backed proxy を登録し、plugin executable content を load しません。失敗 fingerprint は source bytes が変わるまでだけ cache するため、project 初期化または status が明示的かつ直列化された refresh を開始した時に partial copy の修復を再試行できます。hot path の language / extractor lookup は現在の registration を再利用し、変更のない DLL を再列挙・再 staging しません。置換に成功すると registration を atomic に更新し、同じ path の以前の worker と staging を dispose して count を bounded に保ちます。hook discovery と callback も parent load context を保持しません。`status --json` は両方の isolation lifecycle と各 hook の stable な `id` を報告します。

plugin / hook discovery は、既定 directory と override directory の双方で単一の executable-content filesystem boundary を使います。owner、mode、全祖先、containment、regular-file leaf を検証した後、受理した DLL bytes を hash し、process-private な read-only staging directory へ copy します。Windows では同等の検証として owner と DACL を調べ、信頼されていない principal に write 権限がある path を拒否し、staging は継承を保護した current-user-only ACL で作成します。assembly load は staging path だけを使うため、validation から load までの rename-swap window を閉じます。staging は read-only dependency を含め、所有する registry / hook runner の dispose 時に削除されます。

`CDIDX_HOOKS_DIR` は trust boundary の override です。hook assembly は isolated worker 内で extension code を実行するため、信頼できる user が管理する local directory だけを指定してください。`status --json` と MCP `status` は override が accepted / rejected になった場合に sanitization 済みの `hook_diagnostics[]` を返し、存在しない directory、安全でない Unix owner / mode、symlink / reparse point の祖先を拒否します。hook diagnostics には bounded な `category` machine code が含まれるため、caller は human-readable message を parse せずに override、discovery、assembly load、constructor、callback、timeout failure を区別できます。

assembly load、construction、callback exception は sanitization 済み category 付きの diagnostic として捕捉され、indexing は継続します。
各 loaded hook は isolated worker process 内で動き、callback は scratch copy 上で実行され、
`CDIDX_HOOK_CALLBACK_BUDGET_MS`（既定 5000 ms）の wall-clock budget が適用されます。
最初の callback budget には worker startup と callback execution が含まれます。budget を超えた callback は
worker process tree を kill され、mutation は捨てられ、index warning を出し、その index run 中は
assembly-qualified hook ID だけが disabled になります。stable ID は staged assembly fingerprint、
normalized source-path identity、full type name を hash するため、異なる DLL の同じ `Type.FullName` は衝突しません。
`status --json` と MCP `status` は `hooks` に `id`、`name`、`assembly_path`、`type_name`、
`callback_budget_ms` を公開し、hook 固有 diagnostic は同じ値を `hook_id` として返します。

### ignore ファイルの解析

`.gitignore` と `.cdidxignore` の pattern 行は Git の空白規則に合わせて解析する。行頭の space / tab は pattern の literal 文字として扱い、`#` は unescaped な先頭文字のときだけ comment を開始する。未エスケープの末尾 space / tab は削除するため、ファイル名 pattern の一部として末尾空白を含めたい場合は `\` で escape する。

bracket expression は Git 互換の glob 挙動に合わせる。`!` または `^` が `[` の直後にある場合、`[!a]` と `[^a]` はどちらも negated character class として扱う。class の途中にある caret は literal（`[a^b]`）で、先頭 caret を literal にしたい場合は escape する（`[\^a]`）。

### CLI の回復可能エラー形式

人間向け出力の回復可能な command error は次の canonical line shape を使います。non-null line
だけを出しますが、すべての error に recovery hint を含めます。

```text
Error: <message>
Hint: <actionable recovery path>
Usage: <command shape>
```

error code がある場合、先頭行は `Error [<code>]: <message>` です。新しい CLI parse、
validation、filesystem preflight error は `CommandErrorWriter` を使い、`ProgramRunner`、
`IndexCommandRunner`、query runner の形式を揃えてください。JSON error payload は
`CommandErrorJsonResult` を使い続けます。

JSON mode の回復可能な非データベース系失敗も、共通のバージョン付き
`CommandErrorJsonResult` envelope を使います。`api_version`、`status`、
`message`、`hint`、`error_code`、`category`、`command`、`exit_code`、
`usage` は必須です。command 固有 field を加える場合は、path、warning、preview を
sanitization し、上限を適用してから merge します。JSON の失敗は envelope を stdout に
出し、stderr を空に保ちます。human の失敗は対応する code 付き `Error`、`Hint`、
`Usage` を stderr に出し、stdout を空に保ちます。

| failure class | exit code | error code | category |
|---|---:|---|---|
| usage / 不正な引数 | 1 または 7 | `E010_USAGE_ERROR` | `usage` |
| outline path が見つからない | 2 | `E019_FILE_NOT_FOUND` | `not_found` |
| 不正な設定 | 1 | `E024_CONFIG_INVALID` | `configuration` |
| hook の platform / filesystem failure | 9 | `E025_HOOK_OPERATION_FAILED` | `platform` |
| Git repository 外での hooks 実行 | 2 | `E026_NOT_GIT_REPOSITORY` | `not_found` |
| その他の回復可能な command failure | command ごと | `E023_COMMAND_FAILED` | writer による安定した分類 |

### プロセス起動ポリシー

本番の subprocess 起動箇所はすべて `ProcessStartInfo.ArgumentList` を使い、`UseShellExecute` を無効のままにしてください。start-info の構築は `ProcessLaunchPolicy` またはそれを呼ぶ用途別 helper に置き、git、isolated worker、hook callback、installer dispatch、その他の subprocess が同じ argument、encoding、no-shell default を共有できるようにします。

environment 継承は opt-in です。subprocess environment には `SubprocessEnvironmentPolicy` の allowlist を使ってください。git は base / proxy / certificate / git knob だけを残し terminal prompt を無効化します。isolated worker は base / .NET runtime 値と test 用の `CDIDX_TEST_` 変数だけを残します。installer handoff は文書化済みの installer / proxy / certificate 変数だけを残します。trust boundary の文書化とテストなしに、広い `CDIDX_*`、token、credential、shell environment forwarding を追加してはいけません。

すべての subprocess wait には明示的な cancellation または timeout 経路が必要です。capture した stdout / stderr は user-visible diagnostic に出す前に bounded にし、diagnostic は既存の safe-formatting helper で sanitize してください。

### CLI 出力エンコーディングと端末制御

CLI JSON output は機械処理向けにきれいでなければなりません。redirected stdout は BOM なし UTF-8 で
書き、JSON-mode command は `--color=always` や `CLICOLOR_FORCE=1` が人間向け出力を色付けする
場合でも ANSI escape sequence を出してはいけません。JSON-safe styling suppression は
`ConsoleUi.ColorizeKind` など共有 formatter の近くに置き、将来の query output path も同じ
invariant を継承できるようにしてください。

stdout は command payload のための stream です。human mode では human-readable result、JSON mode では
`--format` で選ばれた文書化済みの JSON object / array / NDJSON stream / envelope / external format だけを
出します。warning、progress、slow-query message、lifecycle log、worker diagnostic、recoverable-error prose は
stderr または private global tool log に出します。`report` や export command のように artifact を作りつつ
JSON を報告する command は、stdout を JSON として parse 可能に保ち、artifact は human text を stdout に混ぜず
JSON summary で説明してください。MCP stdio も protocol level で同じ分離に従い、stdout は JSON-RPC frame のみ、
server diagnostic と telemetry は stderr に出します。

JSON serialization site は contract domain ごとに分けます。公開 CLI JSON は
`ProgramRunner.CreateDefaultJsonOptions()` と `CliJsonSerializerContext` を使います。field name は
snake_case、null は省略、audit 済みの公開 top-level event/result DTO は `api_version` を持ち、
DOM で組み立てる `JsonObject` payload は sanitized / bounded 済み field だけを追加します。
公開 top-level CLI JSON DTO を追加または audit するときは `api_version` を追加してください。MCP JSON-RPC は
protocol envelope に `McpServer` の camelCase option を使い、tool structured content は文書化済みの
machine-readable key を保ちます。object 形式のすべての tool `structuredContent` envelope は
`CreateToolResult` が追加する root-level `api_version` を持ちます。`JsonObject` / `JsonNode` を mutate する前に値を sanitize /
redact してください。LSP、quickfix、SARIF 出力は CLI snake_case contract ではなく外部 schema に
従います。GitHub/report helper と worker/private storage path は API client、永続化ローカル状態、
process-internal protocol のいずれかなので、それぞれの bounded serializer を使います。
`LocalJsonlJsonWriterOptions` の relaxed encoder は private append-only JSONL diagnostic 専用であり、
公開 CLI、MCP、LSP、HTTP、埋め込み可能な JSON に再利用してはいけません。

`cdidx export ctags --json` も同じ contract に従います。stdout は単一の JSON summary または
structured error だけを含み、tags file 自体は artifact として残します。summary には解決済みの
output / database path、tag / emitted / skipped counts、filters、metadata field names を含め、
editor integration が human output を parse せず filtered export を検証できるようにします。
`skip_reason_counts` は安定した reason key だけを持つ上限付き object です。skip された各候補は
必ず1つの理由にだけ計上され、値の合計は `skipped_count` と一致します。generated file は
query command と同じ contract に従い、既定で除外し、`--include-generated` で opt in します。
legacy database に `files.generated` column がない場合はその column を参照せず
`unavailable` と報告します。

interactive terminal control は stdout が redirected / captured されておらず、terminal
capability hint があり、environment が opt out していない場合にだけ許可します。`TERM=dumb`、
truthy `CI`、Unix terminal hint の欠落、`NO_COLOR`、`CLICOLOR=0` は、明示的な human-facing
override が文書化されていない限り ANSI/progress control を抑止する理由として扱います。

### C# / .NET 連携

`SolutionProjectResolver` は plain-text の `.sln` に含まれる `Project(...) = "...", "...csproj"` 行を non-regex parser で読み、C# / F# / VB の project file を解決する。active workspace root の外側へ正規化される project entry は、filesystem probe や path-filter 評価の前に無視する。solution parsing は 8 MiB を超える `.sln`、16,384 文字を超える行、4096 件を超える .NET project reference を明確な diagnostic とともに拒否する。root 直下の `.sln` 自動検出は sort 前に最大 128 candidates で打ち切り、その上限を超えた場合は明確な error を返すため、solution が多い workspace では `--solution <path>` を渡す。自動 solution discovery が workspace root を列挙できない場合は、生の filesystem exception を出さず、明示的な `--solution <path>` recovery hint を含む bounded traversal diagnostic を追加する。上限内で workspace root に `.sln` が 1 つだけある場合、`--project <name|path>` は自動でそれを使う。複数ある場合は caller が `--solution <path>` を渡せる。fallback project discovery は 4096 directories / 65,536 files で traversal を打ち切り、`--solution <path>` を示す明確な recovery hint を返す。fallback project discovery と project-file expansion は long-path-safe な per-directory 列挙を使い、読めない subtree を skip し、project filter を解決できない場合は bounded traversal diagnostics を含める。

path filter を受け付ける query コマンド（`search`, `definition`, `references`, `callers`, `callees`, `symbols`, `files`, `find`, `map`, `inspect`, `deps`, `impact`, `unused`, `hotspots`, `validate`）は、`--project` を対応する project directory glob に展開してから `DbReader` に渡す。これにより既存の SQL path predicate をそのまま利用できる。indexed project root を解決できず process current directory に fallback して project expansion する場合、CLI query context と MCP structured payload は `project_filter_root` と `project_filter_root_fallback_reason` を含める。`index --project` は選択された project directory 配下のファイルに展開し、既存の `--files` 更新経路を再利用する。ただし 1 project で 65,536 files、requested projects 全体で 131,072 unique files を超える展開は拒否し、明示的な `--files` を使う recovery hint を返す。

`cdidx batch` は、同じ DB に複数の query command を投げる editor integration や script 向けの CLI 側 query loop である。newline-delimited な stdin record は従来の JSON 文字列配列 form、または検証済みの `{"command": "...", "args": [...]}` object form を使用できる。object input は重複/未知 property、欠落または空白 command、array でない `args`、文字列でない値、array input と同じ引数数/長さ違反を拒否する。serial mode は 1 つの `DbContext` / `DbReader` を開く。`--parallel <n>` は `--json-summary` を必須とし、最大 16 workers に制限し、active worker ごとに分離した query-only context を開く。すべての form は `CliCommandCatalog` が正本となる副作用なし allowlist の command だけを dispatch する。この schema には `goto` や `audit` などの query / read-only discovery surface が含まれ、top-level command や dispatcher arm を追加しただけでは batch の安全境界を越えられない。

既定の入力 budget は 1,024 行のままで、`--max-input-lines <n>` により最大 65,536 まで設定できる。デコード後の各文字列引数は引き続き 8,192 文字に制限する。JSON-summary 出力 budget は既定で 10,485,760 文字であり、`--max-output-chars <n>` は 4,096 から 67,108,864 までを受け付ける。command がない即時 EOF は既定で exit 0 かつ無出力のまま維持される。非対話の呼び出し元が空入力を明示的に判定したい場合は、`--json-summary` が `commands_processed`、`line_errors`、`command_failures`、`exit_code` を含む最終 JSON オブジェクトを追加する。
既定では child query command の通常の stdout / stderr を直接 stream する。`--json-summary`
mode では、空白でない stdin 行ごとに final summary より前へ 1 つの machine-readable batch
envelope を出力しなければならない。parse 済み command は `record: "batch_result"` として
`line`、`command`、`arguments`、`exit_code` を含める。output text の推測ではなく requested
command / output format で projection を選び、成功した単一 document JSON
は型付き `result`、NDJSON は 1 row の場合も安定した型付き `results` array として埋め込む。
成功した text command は `stdout` のまま保持する一方、すべての失敗は安定した `error_code`、
`category`、安全な `message` / `hint`、`scope` を持つ共通の型付き `error` object を使う。
malformed line や入力上限超過 line は `record: "batch_error"` と同じ typed error serializer を
使う。失敗 record は既定で捕捉した child stdout / stderr を省略し、
`--include-raw-streams` を明示した場合だけ上限付きの `raw_streams` object に追加する。
この mode では child output を batch metadata と並べて直接出力してはならない。envelope、
arguments、escape 展開、terminal error、final summary を含む serialized stream 全体には
設定された `--max-output-chars` budget（既定 10,485,760、最大 67,108,864）を適用し、
使い切った item も
`error.scope: "batch"` と exit / error metadata を保持する。final `record: "batch_summary"` は
empty input、failure、budget accounting のために `commands_processed`、`line_errors`、
`command_failures`、`exit_code`、`output_chars`、`output_char_limit`、`input_line_limit`、
`parallelism` と input / output limit state を保持する。parallel worker は stdout / stderr を
command ごとの bounded writer へ route し、分離した read-only SQLite connection と thread-local
batch reader を使い、active worker window だけを buffer する。`ScopedConsoleOutput` は nested
JSON-envelope capture を現在の worker の routed stdout に保ち、他 worker の process-wide writer を
置き換えない。完了 record は入力順で共有 output writer へ commit する。通常の item failure は
他 item から隔離する。caller cancellation は、消費済み input item と final summary に
`batch_cancelled` を記録してから後続処理を停止する。parallel input wait は stdin が
block 中でも cancellation を検知し、database setup 中の cancellation でも型付き final
summary を出力する。

editor integration は標準的な location 形状を直接要求できる。`definition`、`references`、`search`、`find`、`validate` は `--format <text|json|lsp|qf|sarif>` を受け付け、`lsp` は LSP `Location` 配列、`qf` は Vim quickfix 行、`sarif` は SARIF 2.1.0 を出力する。`goto <symbol>` は曖昧でない単一定義を 1 つの LSP `Location` として返し、`goto --all <symbol>` は既定または環境変数由来の query limit を適用せず、一致する全 location を返す。明示的な `--limit` または `--top` を指定した場合は location 配列をその件数に制限する。

`cdidx lsp` server は full text document synchronization を advertise し、open document text は
上限付きの in-memory cache にだけ保持する。position-based provider は未保存 editor buffer から
request token を特定できるよう disk より先に live cache を読む必要がある。provider result は
保守的かつ index-backed のままとするが、indexed document の document symbol だけは上限付きの
live buffer を通常の language extractor と container pipeline で構造的に再抽出できる。このとき
path から再判定せず、indexed file の authoritative language を使う。live extraction は
document-symbol materialization 上限で停止し、その bounded extractor を利用できない場合は
indexed symbol に fallback する。numeric document-version tombstone は live text の eviction
後も上限付きで保持し、`didClose` で消去するため、evict 済みの新しい version を stale change が
置き換えることはない。それ以外の provider は database が安全に答えられない場合、
language-server analysis を作り上げず、空配列または null を返す。
`LspServer` は通常 dispatch、cancellation fast path、queue-overload response の全経路で、
1 つの lock 保護された lifecycle state machine を使う。phase は before-initialize、
initializing、running、shutdown、exited である。最初の `initialize` request だけが初期化へ
遷移できる。transport は各 frame の lifecycle action を受信順で予約する。output gate の下で、
serialize 済み initialize response と running への遷移は 1 つの公開境界を共有し、frame の
書き込み開始直前に state を変更する。shutdown は active dispatch の完了待ちより先に phase を
変更し、その後に所有する query context と reader を正確に 1 回だけ破棄する。shutdown 後の
request は `-32600` を返し、notification は無視し、`exit` notification だけが正常な lifecycle を
完了させる。
disk 上の position-line cache は、事前の `Length` check だけでなく streaming 中も 4 MiB の
input 上限を強制する必要がある。共有 file が同時に増大する場合も上限超過 byte を text decode に
渡してはならず、bounded な failure reason は `position_file_too_large` のままとする。

LSP reference は、indexed definition または call site の target を CLI の symbol analysis と
同じ identity-scoped candidate 経路で解決する。`includeDeclaration` が追加するのは、その選択済み
declaration だけである。document symbol の selection と workspace symbol の location は identifier
範囲を返す。保存済み column を lookup の起点とし、indexed source line を読める場合はその行の
identifier を確認して、古い不正確な column にも対応する。source text を利用できない場合は
保存済み column に fallback し、column も無い場合だけ character 0 を使う。type inlay hint は
indexed return type が declaration identifier の前に明示されていない場合だけ返すため、method の
明示 return type、local、field の明示型は表示しない。

document/workspace symbol provider は work-done 対応を advertise し、上限付きの string /
integer `partialResultToken` と `workDoneToken` を処理する。partial result は provider の
決定的な順序を維持し、1 notification あたり最大100 item・64 KiB JSON body の
`$/progress` で送る。document-symbol の partial result は意図的に flat な
`SymbolInformation` item を使い、token のない response は既存の階層化された
`DocumentSymbol` contract を維持する。この階層は全 symbol を materialize してから、indexed
container name・container kind・包含 range・同一行の selection column で親を解決するため、
positional record property のように同じ range を持つ member も決定的な表示順序に左右されず
宣言元 type の配下に留まり、行内で後にある同名 container が前の member を取り込まない。
live document symbol は indexed symbol と同じ extractor、normalization、hierarchy builder を
使うため、full-text change では range と container が一緒に更新される。numeric document version
は増加する必要があり、古い、または同じ version の change は最後に受理した live text を
置き換えられない。
stdio reader と単一 response worker は上限付き queue
で分離するため、database-backed request processing を並行化せずに `$/cancelRequest` で active
または queued symbol request を cancel できる。cancellation ID は JSON 型を保持し、cancel
された request は work-done progress を終了して `-32800` を返す。result / progress cap は
work-done の end message または上限付き `window/logMessage` warning で必ず通知する。progress
notification は symbol item の列挙中に独立した2 item channel から drain するため、notification
memory は bounded のままで、symbol materialization の完了前に work-done `begin` が client へ
届く。request queue が満杯の場合は超過 request を `-32000`（`Server busy`）で拒否しながら
document-sync notification と control notification は拒否せず順序どおりの処理を待たせる。
rejection response は上限付き output channel を使い、空きがある間は cancellation を読み取り、
output backpressure 時は response を破棄せず input を一時停止する。output worker の failure は
伝播前に pending input read を cancel する。

### 抽出器の性能契約

symbol / reference extractor は `cdidx index` 中に実行されるため、言語別 helper は
生成ファイル、非常に大きなメソッド、1 つの構文スコープ内に数千個の宣言や参照がある入力を
前提にする。候補ごとに同じ本文、行範囲、蓄積済み結果リストを再走査する helper 形状は避ける。
多数の候補に対して scope や delimiter 情報が必要な場合は、file / function / block 単位で
範囲情報を一度だけ事前計算し、候補ごとの lookup でその構造を再利用する。

同じ規則を extracted symbol の membership にも適用する。後続の per-line / per-match 判定が
class、property、import alias などの存在を繰り返し確認する場合は、dictionary / set を一度だけ
構築して再利用する。candidate loop の中で `symbols.Any(...)`、LINQ 列挙、signature parse を
隠す helper も反復走査である。まれな language / feature 用 lookup は lazy に構築し、
`Distinct` ベースの走査を置換するときは source order や first-match semantics を維持する。

container ownership にも同じ契約を適用する。reference が extracted declaration を name と
source range で繰り返し解決する場合は、candidate を name ごとに一度だけ索引化し、その name の
ordered range list だけを走査する。duplicate name の first-candidate behavior を維持し、index は
1 回の extraction call 内だけに保持する。
C# では symbol assignment と reference resolution の両方が、enclosing type より先に、
`test.method` や nested local function を含む最も狭い active callable range を選ぶ。
完全な body range を持たない named lambda はその enclosing callable に所属し、
reference container にはしない。
symbol の container assignment は、sort 済みの per-symbol walk で1つの path buffer を再利用する。
active stack を buffer へ列挙して outer-to-inner 順に反転し、深く nest した生成ファイルの member
ごとに stack array と新しい path list の両方を実体化してはならない。

hot な抽出ループでの重複検出には、出力 record の完全な identity を key にした `HashSet` などの
定数時間構造を使う。大きな生成ファイルで local variable、parameter、call site、type reference、
pattern match ごとに実行され得るループへ、`List.Any(...)`、`List.Contains(...)`、nested regex scan、
繰り返しの string join を追加してはならない。
reference deduplication は特に、value-type identity を持つ `ReferenceDedupeSet` を使う。
candidate name / container name は長くなり得て、duplicate rejection より前に key が作られるため、
連結 string key へ戻してはならない。

hot extractor の delimiter-only parsing では、consumer が item を一度に1つだけ必要とする場合や
validation を source string 上で完結できる場合、index / span walk を優先する。`string.Split` は
import、dependency、path segment、declaration item ごとに array と substring を作る。置換時は
元の empty-item、trim、quote、first-separator semantics を維持する。
single delimiter には allocation-free な共通 walker `DelimitedSpanEnumerable` を使う。
repository metadata、application manifest、VHDL declaration / package path、CUDA parameter
header は split array を作らずこの walker で処理する。
同様に、dense match loop 内の exclusion-range 判定で capturing LINQ predicate を使っては
ならない。Erlang、OCaml、Raku は remote / qualified / quoted-atom / type-reference の
抑制に `ContainsFunctionalSpan` / `OverlapsFunctionalSpan` を共有する。
hardware language も同じ規則に従う。Verilog / SystemVerilog / VHDL の shadow scope と、
CUDA / GLSL / HLSL / Metal / WGSL の binding / resource scope は direct indexed loop を
使い、identifier ごとの predicate closure を作らない。
repository metadata の character validation は predicate-based enumeration ではなく
`SpanCharacterSearch` を使い、application manifest の dependency ownership は
`assemblyIdentity` ごとの ancestor stack 再走査ではなく XML depth で追跡する。
state-machine の sentinel 判定も span 上で行う。Erlang specification / callable terminator
と Raku heredoc terminator は、padding を含む copy を実体化せず original line の view を trim する。
`SpanCharacterSearch.EndsWithAfterTrim` はこれらの sentinel に加え、CSS selector continuation
と C# / Java の body-less declaration termination が共有する suffix primitive である。
functional-language の exclusion span list は lazy にする。Erlang quoted / remote call、
OCAML type / qualified call、Raku qualified / method call は、対応する match がない source line
ごとに empty list を割り当ててはならない。
functional-language の regex loop は match を demand-driven に列挙し、bounded reference list
が満杯になった時点で停止する。Clojure、Elixir、Erlang、OCAML、Raku でこの契約を維持し、
dense な1行に対して未使用の match object を強制的に作らない。
JVM-family reference scanner も同じ規則に従う。Java、Kotlin、Scala、Gradle / Groovy の
multi-match loop は `BoundedRegex.EnumerateMatches` を使い、bounded reference list を
所有する loop は上限に達した時点で停止する。
Python reference scanner は decorator、annotation、runtime type check、typing factory、
dataclass / attrs integration、dynamic import を逐次走査する。`BoundedRegex.EnumerateMatches`
の start-offset overload により、decorator prefix を再走査せず argument scan も
demand-driven のままにする。offset なしの overload は regex instance の既定開始位置を
維持し、`RegexOptions.RightToLeft` では source の逆順に match する。
PHP、Ruby、R、Perl の dynamic-language scanner は multi-match の attribute / type、
DSL target、namespace / member / resource reference、arrow call を逐次走査する。
bounded reference list へ直接書く loop は dense line の残りを走査せず上限で停止する。
Prolog goal scan の per-line call list は lazy にする。call がある場合は同じ list 上で
directive metadata を更新してから保存し、call-free rule ごとの empty list や populated
list を射影した2つ目の list を割り当ててはならない。
secondary reference scanner も Fortran、Visual Basic、F#、Pascal、Objective-C、
Haskell、Elixir、Smalltalk、Lua、Dart、Razor、JSON、JavaScript、GitHub Actions、
C++ compound requirement の match を demand-driven に保つ。static pattern の列挙は
extraction timeout を明示的に受け取り、逐次化によって timeout 契約を変えない。
reference list を所有する regex loop は `ReferenceExtractor.EnumerateReferenceMatches`
を使い、bounded list が満杯なら次の match を要求しない。共有の行単位 pipeline も
  type、infrastructure、SQL、call、member、metadata、Razor、Python、R の各 phase 間で
  同じ上限を確認する。
symbol / dependency extractor も scientific / native、Pascal / Ada、SQL、Python、
Swift、GraphQL、markup / XAML、shell、Ruby、Perl、Elixir、CSS、HDL、C++、
manifest parsing をまたいで同じ逐次走査規則に従う。総数だけが必要な場合は
`BoundedRegex.CountMatches` を使い、従来の timeout 時 all-or-nothing 結果を保ったまま
`MatchCollection` を保持しない。先頭 match を分類に、残りを構文解析に使う scanner は、
input を実体化または再走査せず1つの enumerator を維持する。
XAML supplemental symbol phase は structured-data symbol budget を共有する。diagnostic
置換用の overflow marker を最大1件だけ保持したら、直ちに
`TrimStructuredDataSymbols` を通って戻る。全 XAML phase の完了後まで無制限の一時
symbol list を蓄積してから trim してはならない。
JSON symbol / reference の byte-offset mapping は `Utf8LineStarts` を共有する。先に改行数を
数えて最終 offset array を exact capacity で確保し、dense JSON file で `List<int>` を成長させて
copy 後の array と同時に保持してはならない。
systems-language scanner は C / C++ construction と template group、Rust call と
value / signature type、Swift property wrapper、Go concurrency と composite / signature
type、共有 scientific / native call group を逐次走査する。source-order emission を維持し、
所有する bounded list は上限で停止する。
SQL reference scanner は statement、source、target、generated-column、window-clause、
procedure-call、一時 object の match を逐次走査する。複数の SQL match を受け取る helper
は sequence を demand-driven のまま保ち、reference を出力する loop は bounded list の
上限到達時に消費を停止する。
infrastructure / markup scanner は CSS、XAML、HTML / GraphQL / Markdown、HDL、
MSBuild、Dockerfile、shell、PowerShell の match group を逐次走査する。state-only scan
も demand-driven のまま保ち、bounded-list の停止判定は scanner が reference list を
所有する箇所だけに適用する。
core reference scan は共有 call、C# attribute / type / pattern / local、JSX element、
JVM documentation link、Solidity reference を逐次走査する。複数 pass で意図的に再利用する
match set は materialize してよいが、single-pass emitter は demand-driven を維持し、
所有する bounded list の上限で停止する。
line-based symbol / reference extractor はすべて `SourceLineSplitter` を共有する。
newline boundary を一度数えて exact result array を確保し、downstream scanner が必要とする
line string だけを実体化する。`string.Split` による separator-index array を戻してはならない。

構造マスクによって source line が空白だけになった場合、reference がないことを確認するため
だけに trim 済み copy を実体化してはならない。documentation handling と、original line を
検査する build-automation / markup 経路は維持しつつ、通常の C#、Java、
JavaScript / TypeScript 経路では reference context を作る前に multiline payload を skip する。
prepared-line の whitespace 判定は core-loop iteration ごとに一度だけ行い、special-line
dispatch と通常の empty-line 経路で共有する。masked payload line は数千文字になり得る。

C / C++ header の曖昧性解決は bounded lexical sample 上で行う。sample は span と newline index
で走査し、line array に split してはならない。split は sample 内の全行を一時的に複製し、
曖昧な `.h` file が多い巨大 repository でスケールしにくい。

C# の value receiver 経路を参照例とする。local receiver の scope は containing function 用に
事前計算した block span から導出し、重複 receiver record は hash set で追跡する。この領域の
regression には、scope rule の focused correctness test と、ユーザーが multi-hour indexing stall を
見る前に失敗する大規模 fixture の runaway guard を追加する。

### 抽出器の並行実行契約

`SymbolExtractor` と `ReferenceExtractor` は、異なるファイルへの並行呼び出しや、同じファイル内容に対する繰り返し呼び出しでも安全でなければならない。共有される `Regex` インスタンスや static な lookup table は CLR が一度だけ初期化し、型初期化後は immutable として扱う。抽出ごとの状態は、ローカル変数、メソッド引数、呼び出し元が所有するコレクション、またはその抽出呼び出し用に生成した言語固有の state object に持たせる。

抽出器コードに mutable な static cache、共有 `StringBuilder` インスタンス、使い回しの `MatchCollection` enumerator、シングルトンの scanner state を追加してはならない。将来の抽出器が呼び出しをまたぐ memoization を必要とする場合は、明示的にスレッドセーフなコレクションを使い、並行呼び出し下でも決定的な出力になることを証明する focused な並列回帰テストを追加する。

### シンボル種別分類

`symbols.kind`、`symbols.container_kind`、`symbol_references.container_kind` は
以下の公開 symbol kind taxonomy に従います。新しい extractor が kind 値を追加する場合は、
書き込み前に `SymbolKindCatalog` へ登録し、schema check、writer validation、CLI
filter、downstream JSON consumer が同じ値を理解できるようにしてください。

| Kind | 現在の producer / 意味 | Graph behavior |
|---|---|---|
| `accessor` | owning property から別 symbol として抽出される accessor declaration | Search/filter symbol |
| `add` | Dockerfile `ADD` の destination path | dependency / file-flow search symbol |
| `anchor` | HTML entity の decode 後も ID の大文字小文字と句読点を正確に保持する Markdown 内の明示的な HTML anchor | path に限定した Markdown fragment reference の定義対象 |
| `annotation` | annotation declaration または annotation-like な言語構文 | Metadata/search symbol |
| `async_function` | JavaScript / TypeScript の async function declaration | Callable definition。reference row 経由で callers/callees に参加 |
| `async_generator` | JavaScript / TypeScript の async generator declaration | Callable definition。reference row 経由で callers/callees に参加 |
| `attribute` | Razor attribute と metadata-like declaration | Context/search symbol。単独では call edge ではない |
| `associatedtype` | Swift associated type declaration | Type-like definition target |
| `base_image` | Dockerfile `FROM` の image name | container image search symbol |
| `build_arg` | Dockerfile `ARG` 名 | variable/search symbol。Dockerfile variable reference に参加 |
| `class` | object-oriented language 全般の class declaration | Definition target and container |
| `class_hook` | Python dunder hook など、function から再分類された class hook method | Callable/search symbol |
| `code` | Markdown fenced code block または structured code block | Search/outline symbol |
| `constant` | 言語が区別する constant declaration | Search/filter symbol |
| `copy` | Dockerfile `COPY` の destination path | dependency / file-flow search symbol |
| `delegate` | C# / F# delegate declaration | Callable type definition and container-like target |
| `enum` | enum declaration | Definition target and container |
| `environment` | Dockerfile `ENV` variable name | variable/search symbol。Dockerfile variable reference に参加 |
| `event` | event declaration | Search/filter symbol |
| `expose` | Dockerfile `EXPOSE` port | container runtime search symbol |
| `field` | property と区別される field declaration。C# の const / static readonly field は通常 field と tuple-aware な型文法を共有する | Search/filter symbol |
| `file_module` | file-scoped module / package declaration | Namespace-like context symbol |
| `function` | 関数、method、constructor、delegate、task、およびより狭い kind がない callable binding | Primary callable definition。reference row 経由で callers/callees に参加 |
| `generator` | JavaScript / TypeScript generator declaration | Callable definition。reference row 経由で callers/callees に参加 |
| `heading` | Markdown heading、C# region、Python module docstring、JavaScript / TypeScript `@module` docblock などの language section marker | Outline symbol。Markdown heading は path に限定した fragment reference の定義対象 |
| `hook` | JavaScript / TypeScript React custom hook binding | Callable-like search/filter symbol |
| `implements` | Razor `@implements` directive | Context/search symbol |
| `import` | import、using directive、alias、package include | Search/filter symbol |
| `interface` | interface declaration | Definition target and container |
| `lambda` | named lambda / arrow binding | Callable definition。reference row 経由で callers/callees に参加 |
| `label` | Dockerfile `LABEL` key | metadata/search symbol |
| `layout` | Razor layout directive | Context/search symbol |
| `method` | function と method を明示的に区別する言語または hook | Callable definition。reference row 経由で callers/callees に参加 |
| `module` | module declaration | Definition target and container |
| `namespace` | namespace declaration | Definition target and container |
| `operator` | C# operator overload と conversion operator declaration | Callable definition。reference row 経由で callers/callees に参加 |
| `object` | nested extracted symbol が使う object-literal / object container context | Container context |
| `package` | package declaration | Namespace-like context symbol |
| `property` | property、property-like field、GraphQL input field | Definition target。単独では call edge として扱わない |
| `procedure` | Fortran などの procedure declaration | Callable definition |
| `program` | Fortran などの program block declaration | Definition target and container |
| `protocol` | protocol を interface と区別する言語の protocol declaration | Definition target and container |
| `protocol_impl` | Elixir `defimpl` protocol implementation declaration | Definition target and implementation block container |
| `reference` | HTML class、metadata key、GraphQL union variant などの secondary extracted symbolic reference | Search/filter symbol |
| `rule` | nested reference が使う CSS / SCSS rule container context | Container context |
| `route` | Razor route directive | Context/search symbol |
| `run` | Dockerfile `RUN` command body | container build-step search symbol |
| `service` | IDL / protobuf-like language の service declaration | Definition target and container |
| `shell` | Dockerfile `SHELL` executable | container runtime search symbol |
| `specialization` | C++ template specialization declaration | specialized type / function form の definition target |
| `stage` | Dockerfile named build stage | build-stage definition。Dockerfile stage reference に参加 |
| `stopsignal` | Dockerfile `STOPSIGNAL` value | container runtime search symbol |
| `struct` | struct declaration | Definition target and container |
| `submodule` | Fortran submodule declaration | Namespace/module-like definition target |
| `subroutine` | Fortran subroutine declaration | Callable definition |
| `test.method` | test-aware extraction が検出した test method | Callable definition。reference row 経由で callers/callees に参加 |
| `trait` | trait を interface と区別する言語の trait declaration | Definition target and container |
| `type` | より狭い class / interface / struct / enum kind が使えない type declaration | Definition target |
| `type_parameter` | Python の `TypeVar`、`ParamSpec`、`TypeVarTuple` declaration | 宣言された type parameter の type-like definition target |
| `typealias` | type alias declaration | alias name の definition target |
| `union` | union declaration | Definition target and container |
| `user` | Dockerfile `USER` value | container runtime search symbol |
| `block data` | Fortran block data declaration | Definition target |
| `variable` | variable binding | Search/filter symbol |
| `volume` | Dockerfile `VOLUME` path | container storage search symbol |
| `workdir` | Dockerfile `WORKDIR` path | container filesystem search symbol |

古い粗い taxonomy だけを理解する consumer 向けに、
`SymbolKindCatalog.CompatibilityKindFamilies` は `typealias` と
`type_parameter` の両方を広い `type` family へ mapping します。永続化される
`kind` は semantic な値を維持し、`--kind` filter も完全一致のままなので、
`--kind import` にローカル type declaration は含まれません。

`symbol_references.reference_kind` は別の reference taxonomy を使います。

| Reference kind | 意味 |
|---|---|
| `annotation` | annotation と attribute を区別する言語での annotation 使用 |
| `attribute` | metadata / attribute 使用 |
| `augmentation` | TypeScript declaration / interface merge edge |
| `call` | function、method、operator、macro、command の呼び出し |
| `capture` | impact analysis で使う callback / delegate capture 関係 |
| `column_reference` | statement-specific context 内の SQL column reference |
| `consumes_hook` | React hook consumption relationship |
| `const_assertion` | TypeScript `as const` assertion edge |
| `const_generic_reference` | Rust const generic argument reference |
| `copy_from` | Dockerfile `COPY --from=<stage>` stage dependency |
| `cte_body_reference` | SQL common table expression body reference |
| `decorator` | Python decorator 使用 |
| `extends` | inheritance または type-extension relationship |
| `from` | Dockerfile `FROM <stage>` dependency |
| `friend` | C++ friend declaration relationship |
| `generic_type_argument` | explicit invocation に付随する generic type argument |
| `implement` | interface implementation relationship |
| `implicit_implementation` | C# implicit interface implementation relationship |
| `import` | module system 経由の import / include / reference |
| `instantiate` | constructor または object creation |
| `join_condition_reference` | SQL join / merge condition column reference |
| `lifetime_reference` | Rust / C# 風 lifetime または lifetime-like type reference |
| `metadata` | metadata-only reference |
| `reference` | より狭い edge kind を持たない fixture / extractor 用の generic persisted reference row |
| `razor_event_binding` | Razor event binding relationship |
| `stage` | build-stage relationship |
| `subscribe` | event subscription relationship |
| `type_reference` | type annotation、generic constraint、その他 type-position reference |
| `unsubscribe` | event unsubscription relationship |
| `use` | より狭い reference kind がない generic usage relationship |

### status 鮮度の経過時間しきい値

`status --check` の DB/worktree checksum 比較は `IndexFreshnessChecker` に置き、ユーザー向け age hint のしきい値は `QueryCommandRunner` で解決する。優先順位は CLI の `--stale-after <duration>`、`CDIDX_STALE_AFTER`、`.cdidxrc.json` の `stale_after`、24 時間の既定値。duration suffix は `m` / `h` / `d` をサポートする。有効な CLI `--stale-after` は workspace check を暗黙に有効化する。check mode の JSON はトップレベルの `stale_after_seconds` / `index_age_seconds` に加え、`query_context.check_mode`（`explicit` または `implied_by_stale_after`）と `query_context.stale_after_seconds` を返すため、クライアントは text を解析せず activation path と適用しきい値を監査できる。通常の status JSON はこれらの check 専用 field を省略する。

`status --json` は trust field のいずれかが degraded の場合に structured readiness guidance を出す。トップレベルの `degraded_root_cause` は primary の安定した machine-readable code で、`readiness_degradations[]` は degraded な各 field と `root_cause`、人間向け `degraded_reason`、`recommended_action`、`alternative_action` を列挙する。`migration_in_progress` は active batch marker から設定し、一時的な writer/migration window と恒久的な degraded index をクライアントが区別できるようにする。`issues_table_available` は物理的な `file_issues` table の存在を意味し、validate rows を authoritative として扱う前の freshness/trust bit は `file_issues_data_current` を使う。

index generation の completeness は単一の persisted-readiness reader で計算し、
成功した full/update index response、直後の status / workspace status、MCP の
indexing/status response で再利用します。symbols-only run、`file_too_large`、
`symbol_count_exceeded`、`reference_count_exceeded`、extractor failure、
reference safety cap の永続化済み省略証拠がある場合は
`index_complete=false` となり、安定した `index_incomplete_reasons` を返します。
`reference_graph_complete` はさらに利用可能かつ current な graph generation を要求し、
graph 固有の安定した理由を返します。completeness metadata を持たない legacy database は、
永続化済み row が処理の省略を証明しない限り compatibility default を維持します。

reference extraction の固定 safety limit は lookup symbol 50,000件、lookup line
20,000行、1行あたりの name 512件、container candidate 20,000件で、CLI の
`languages --json` / `status --json` と対応する MCP response に公開します。cap diagnostic は file ごとの `file_issues`
として永続化し、current generation の `reference_extraction_cap_hits` に集約し、
`last_index_run.reference_extraction_cap_hits` に snapshot します。1件でも到達すると
`reference_graph_complete=false` / `graph_data_current=false` になり、callers、callees、
CLI/MCP の callers、callees、deps、impact response も上限付き summary と stable diagnostic kind を返すため、consumer
は incomplete な0件を authoritative な不在と誤認しません。

### ワークスペースのバージョン固定

startup 時、`cdidx` は current directory から上へ `.cdidx-version` を探します。最初の non-empty
line を workspace が要求する CLI version として扱います。pin file は 4096 byte 上限で読み、
先頭の blank line は最大 16 行まで skip し、読み取る各 line は最大 256 文字です。これらの
上限を超えた場合、pin は warning とともに無視されます。mismatch は既定では warning のみです。
`--strict-version` または `CDIDX_STRICT_VERSION=1` では exit code `64` (`EX_USAGE`) になります。
この check は advisory で、file は書き換えません。index contract や query behavior が
release 間で異なる場合に、team の binary version を揃えるために使います。

### リリース鮮度とアップグレード確認

`cdidx --check-updates` と `cdidx status --check-updates` は、`--version` の hint と同じ
24-hour cache と `CDIDX_DISABLE_UPDATE_CHECK=1` opt-out を使って GitHub latest-release
endpoint を確認します。`cdidx upgrade --check-only` はこの check を再利用します。
`cdidx upgrade` は signed release installer の薄い wrapper で、`sha256sums.txt` と
`install.sh` を private temp directory に download し、両方の exact file を
`github.com/Widthdom/CodeIndex/.github/workflows/release.yml` と
`refs/tags/<selected-version>` に固定した `gh attestation verify` で独立に検証してから
manifest checksum を信頼し installer を起動します。既定では verifier 欠如または
provenance 失敗時に実行を拒否し、`CDIDX_VERIFY_POLICY=compat` だけが監査対象の明示的
opt-in です。upgrade JSON は `verification_policy`、`manifest_provenance_verified`、
`installer_provenance_verified`、`installer_verification_status`、
`provenance_audit_code` で method と実測結果を分離し、check-only は `not_attempted`、
成功は `verified`、strict で中断した失敗は `verification_failed`、compat bypass は
`compat_bypass` と `compat_provenance_bypass` を返します。`--json` 指定時の無効な policy 値は
通常の構造化 usage-error JSON になります。検証後に current binary directory
が writable か確認し、`CDIDX_INSTALL_DIR` をその directory に向けて選択した release の
installer を実行します。

Upgrade installer と git subprocess は、起動前に継承環境を scrub します。
forward するのは、PATH / home / temp / proxy / certificate 挙動に必要な共有 subprocess
allowlist と、`CDIDX_INSTALL_DIR`、installer verification variables、選択された `GIT_*`
controls のような tool-specific knob だけです。`CDIDX_TEST_*` variables は public runtime
contract ではありません。repository tests が worker-only hook を検証できるよう、isolated worker
process にだけ forward します。

`cdidx upgrade --json` は automation 向けの stdout contract を持ちます。check-only と
no-update の結果は update-check fields (`current_version`, `latest_version`,
`update_available`, `from_cache`, `error`, `error_category`, `error_hint`) に release-selection fields
(`selected_version`, `selected_channel`, `selection_source`, `include_prerelease`)
を加えたものを使います。update を install する場合、installer stdout/stderr は
capture されるため stdout は 1 個の JSON document のままになり、update-check fields に
`install_attempted`、`install_exit_code`、`install_succeeded` が追加されます。Windows
handoff response には `handoff_command`、`handoff_url`、`handoff_asset`、
`handoff_asset_url` も含まれます。

### 劣化理由コード

readiness degradation reason code は `DegradationReasonCodes` に集約します。reader、CLI、
MCP payload から新しい code を emit する前に、human text、recommended action、
alternative action を同じ場所へ追加してください。

現在の stable code と trigger:

| Code | Trigger | Recovery |
|---|---|---|
| `missing_fold_backfill` | legacy row に folded-name value が無い | `cdidx backfill-fold` または full rebuild |
| `stale_fold_key_version` | folded row が古い fold-key version で stamp されている | `cdidx backfill-fold` または full rebuild |
| `stale_fold_key_fingerprint` | folded row が古い runtime fingerprint で stamp されている | `cdidx backfill-fold` または full rebuild |
| `fold_rows_not_restamped` | fold metadata は current だが、1 件以上の folded row が restamp されていない | `cdidx backfill-fold` または full rebuild |
| `fold_ready_bit_set_but_rows_incomplete` | fold-ready bit が立っているのに row-level verification で NULL folded-name value が見つかった | `cdidx backfill-fold` または full rebuild |
| `fold_ready=false` | aggregate fold readiness bit が degraded | `cdidx backfill-fold` または full rebuild |
| `sql_graph_contract_ready=false` | SQL graph row が現在の call-column / qualified-name contract と一致しない | `cdidx index <projectPath>` |
| `hotspot_family_ready=false` | 1 つ以上の hotspot-family language で current authoritative family stamp が不足している | `cdidx index <projectPath> --rebuild` |
| `hotspot_family_marker_fingerprint_incomplete` | hotspot-family marker fingerprint traversal が safety cap に到達し、family trust が authoritative に stamp されなかった | generated / ignored marker tree を減らすか code 側の cap を上げてから `cdidx index <projectPath> --rebuild` |
| `partial_family_key_population` | hotspot-family metadata は stamp 済みだが、一部の indexed symbol で `family_key` が NULL | `cdidx index <projectPath> --rebuild` |
| `graph_table_available=false` | `symbol_references` が無い、または graph-ready ではない | `cdidx index <projectPath>` |
| `symbols_only_graph_omitted` | 直前の symbols-only generation が reference-graph row を意図的に省略した | `--symbols-only` を付けずに `cdidx index <projectPath>` を実行 |
| `reference_graph_complete=false` | graph generation が unavailable/stale、symbols-only run で省略、または永続化済み file/extractor/cap 証拠により index generation が incomplete | 報告された安定理由に対処してから `cdidx index <projectPath>` |
| `index_complete=false` | symbols-only run、または永続化済みの file-size / symbol-count / reference-count / extractor-failure / safety-cap 証拠により indexing work の省略が判明 | `index_incomplete_reasons` に対処してから `cdidx index <projectPath>` |
| `issues_table_available=false` | `file_issues` が無い、または issue-ready ではない | `cdidx index <projectPath>` |
| `csharp_symbol_name_ready=false` | C# canonical symbol-name stamp が stale | `cdidx index <projectPath>` |
| `csharp_metadata_target_ready=false` | C# metadata-target stamp が stale | `cdidx index <projectPath>` |
| `csharp_metadata_target_missing_column` | `symbols.is_metadata_target` が無い | `cdidx index <projectPath> --rebuild` |
| `csharp_metadata_target_stamp_outdated` | C# metadata-target version stamp が無い、または stale | `cdidx index <projectPath>` |
| `index_newer_than_reader=true` | DB が、この reader が理解する上限より新しい persisted contract で書かれている | current `cdidx` binary を使うか、この version で rebuild |

`cdidx db schema [--json]` は schema inspection 用に `sqlite_master` entry と
`PRAGMA user_version` を出力し、自動化が bounded diagnostics を必要とする場合は
`--summary-only`、`--type`、`--name`、`--limit`、`--max-sql-chars`、
`--exclude-internal` を受け付けます。`cdidx db prune --dry-run|--apply [--json]`
は orphaned `symbol_references`、`reference_lines`、`symbols` row を数えるか削除し、
apply 時は `PRAGMA optimize` を実行します。

### SQLite WAL の耐久性ポリシー

| 項目 | policy |
|---|---|
| writable open pragma | `DbContext` は writable な index を WAL mode で開き、connection performance pragma を `DbPragmaPolicy` 経由で適用し、新規の空 DB では schema 作成前に `PRAGMA auto_vacuum=INCREMENTAL` を設定し、`PRAGMA application_id=0x43444958` (`CDIX`)、`PRAGMA synchronous=NORMAL`、`PRAGMA wal_autocheckpoint=1000` を固定します。 |
| open intent | すべての `DbContext` caller は `QueryOnly`、`WriteIndex`、`Migration`、`Repair` のいずれかを宣言します。`QueryOnly` は unpooled connection を使い、永続的な pragma、migration、metadata write、repair を skip します。WAL database は private temporary snapshot から読みます。checkpoint 済み database は main file を copy してその copy を immutable read-only URI で開き、non-empty WAL database は安定した main/WAL pair を copy して committed WAL content を維持します。copy 前後の database header / WAL generation fingerprint が一致しなければ bounded copy を retry し、最終的には stale data を返したり source `-wal` / `-shm` を変更したりせず open を拒否します。cross-database で attach した snapshot は、attach 元 connection を閉じた後に cleanup します。source/WAL file の消失は generation churn として retry できますが、永続的な copy I/O failure は temporary storage の remediation を含む `query_only_snapshot_copy_failed` を返します。長時間動作する MCP / LSP session は source generation が変わると detached snapshot を refresh します。`RepairIncompleteBatchReadiness` は `Repair` intent の場合に限り実行できます。 |
| application id | application id は file-type detection tool が cdidx database と generic SQLite database を区別するための印です。 |
| maintenance error contract | `vacuum`、`backfill-fold`、`optimize` / `index --optimize`、`db integrity` の失敗は `MaintenanceDatabaseErrorClassifier` version `1` と単一の JSON / human writer を通ります。SQLite primary code `5` / `6`、`8`、`11`、`26` から locked / busy、not-writable、corrupt、not-a-database を分類し、例外 message は判定に使いません。共有 response は stable error code / category、条件別 recovery hint、redaction 済み path metadata、任意の primary / extended SQLite code を返します。absolute path は既定で redaction し、`--show-paths` を明示的な diagnostic opt-in とします。 |
| durable WAL file set | WAL が有効な場合、永続化された SQLite index は `.db` file と sibling の `.db-wal` / `.db-shm` file の組です。backup、diagnostics bundle、手動 copy では sibling が存在する場合に 3 file すべてを含めるか、live connection から SQLite の `.backup` command/API を使う必要があります。`codeindex.db` だけを copy すると、committed page がまだ `codeindex.db-wal` に残っているため stale snapshot になる可能性があります。 |
| `synchronous=NORMAL` | WAL では `NORMAL` により 500 row 単位の indexing batch ごとの fsync 負荷を避けつつ、crash 後の database consistency を保ちます。 |
| checkpoint | `DbWriter` は outer transaction commit 後に `PRAGMA wal_checkpoint(PASSIVE)` を実行し、SQLite も設定済みの 1000 page threshold を超えると自動 checkpoint する場合があります。どちらの checkpoint path も opportunistic で、active reader は block されず、未 checkpoint の WAL は corruption ではなく期待される状態です。 |
| checkpoint result contract | 明示的な `PRAGMA wal_checkpoint(TRUNCATE)` path は reader を実行し、SQLite の `(busy, log, checkpointed)` を含む構造化結果を返します。`busy` が 0 以外、または remaining page が正の場合は、上限付き machine reason を伴う unsuccessful result です。`(0, -1, -1)` は SQLite の非 WAL database に対する成功 no-op です。instance checkpoint、read-only fallback 前の static preflight、query diagnostics、top-level status、nested connection-policy status は同じ結果と count を保持します。raw exception text や path を diagnostics に含めてはいけません。 |
| crash recovery | SQLite が transaction を commit した後、checkpoint 前に process が kill された場合、次の通常 open が WAL を roll forward するため手動 recovery は不要です。commit 前に process が終了した transaction は SQLite により rollback されます。 |
| migration transaction と foreign key の所有権 | `TryMigrateForRead` は transaction を開始する前に SQLite の autocommit state を確認します。active transaction は明示的に caller-owned として扱い、cdidx は commit / rollback せず、無関係な `BEGIN` failure も nested transaction と誤認せず伝播します。foreign key を無効にする必要がある rebuild migration は、所有する transaction の開始前に `PRAGMA foreign_keys=OFF` を設定して read-back し、nested rebuild helper 内では無効状態が実効的であることを assert します。成功・失敗どちらでも transaction dispose 後に caller の元の mode を復元し、再度 read-back して検証します。 |
| schema discovery cache | `DbReader` の schema discovery は正規化済み DB path を key にした process-level cache を使います。column / index 結果は immutable な `FrozenSet` snapshot として保存・返却されるため、caller が他の reader と共有する schema 判定を変更することはできません。path state は有効な `DbSchemaCache` owner により参照カウントされ、最後の owner `DbContext` が dispose されると削除され、owner が active な間は退避されません。lookup 前に `PRAGMA schema_version` を確認するため、cdidx や外部 `sqlite3` session による SQLite DDL は stale snapshot を invalidate します。cdidx 外での手動 schema edit は運用上 unsupported であり、その後は query output を信頼する前に `cdidx validate` を実行してください。 |
| batch trust marker | index write batch は mutation transaction を始める前に `codeindex_meta.batch_in_progress=true` を stamp し、対応する row と readiness metadata を commit する transaction 内で clear します。marker が書かれた後、clear される前に indexer が crash した場合、その後のすべての open は readiness metadata を変更せずに `Last batch did not complete; run cdidx index --rebuild to re-index from a known clean state.` と警告します。readiness を degrade するのは、明示的な `index --rebuild` repair path だけです。file ごとの error が graceful に処理された場合は rollback 後に marker を clear するため、orphaned marker は interrupted / crashed batch の trust metadata を clean と扱わないための signal です。 |
| read-only open / fallback | query-only command は最初の試行から SQLite `Mode=ReadOnly` で開き、WAL の可視性を保ちながら writable setup と opportunistic migration を実行しません。write-capable intent は journal/WAL setup に失敗した場合に read-only へ fallback することがあります。明示的な `immutable=1` URI は stale snapshot を許容する opt-in escape hatch です。sidecar を公開できない storage 上の WAL を観測する必要がある場合は、`.db` / `.db-wal` / `.db-shm` をまとめて readable location に copy するか、full WAL set を open できる環境で SQLite backup を使います。 |
| status pragma diagnostics | `status --json` は選択された read-only connection を `sqlite_connection_policy` (`active_mode=read_only`, `open_mode=read_only`) で、解決済みの接続値を `db_pragma_settings` (`journal_mode`, `synchronous`, `wal_autocheckpoint`, `busy_timeout_ms`, `page_count`, `freelist_count`, `page_size`, `auto_vacuum`) で公開します。また、prepared command cache counter を `prepared_command_cache` (`count`, `capacity`, `hit_count`, `miss_count`, `eviction_count`) で公開します。`maintenance_guidance` は raw 値を変えずに `wal_state`、`freelist_ratio`、`freelist_state`、`estimated_*_reclaimable`、`auto_vacuum_mode(_name)`、`recommended_command`、`post_maintenance_follow_up` を派生します。`status --check --json` は `repair_commands[]` に `name`、`args`、`reason`、`safety_notes` を返し、client が prose remediation を parse しなくてよいようにします。`last_failed_or_partial_index_run` は bounded な failed / partial index context (`status`、`mode`、timing、count、stable error code、reason、`progress_persisted`、bounded な `recovery_hint`) のみを公開し、raw exception text や file path を含めてはいけません。 |
| maintenance threshold | WAL guidance は `CDIDX_MAINTENANCE_WAL_WARN_BYTES` (既定 64 MiB) 以上で `checkpoint_recommended` になります。freelist guidance は `CDIDX_MAINTENANCE_FREELIST_WARN_RATIO` (既定 `0.20`) 以上で `vacuum_recommended` になります。不正・範囲外の環境変数値は既定値へ戻します。 |
| vacuum | `cdidx vacuum` は incremental-auto-vacuum DB では `PRAGMA incremental_vacuum` を実行し、legacy no-autovacuum DB では初回のみ `PRAGMA auto_vacuum=INCREMENTAL` と full `VACUUM` で変換します。`cdidx vacuum --dry-run --json` は vacuum pragma を実行せず、回収可能 page/byte の推定と同じ maintenance guidance を返します。実行系 `cdidx vacuum --json` は DB / WAL byte の before / after sample も返します。`wal_checkpoint_timing_note` は `wal_size_bytes_after` が connection cleanup 前の計測であり、checkpoint / truncation 後の `status --json` では WAL が小さく見える場合があることを示します。 |
| FTS optimize preview | `cdidx optimize --dry-run` は `QueryOnly` snapshot を開き、既存 lockfile を作成も取得もせずに probe し、source DB/WAL/SHM set に対する write PRAGMA、schema setup、FTS control insert、metadata write を一切実行しません。JSON は size/freelist/readiness 指標、write threshold に基づく推奨、実行系の repair mode による schema 初期化または migration の確認を含む planned operation を返します。object size は利用可能なら `dbstat` page byte を使い、利用できない場合は明示した logical-payload fallback を使います。実際の optimize は所要 millisecond を記録し、後続 preview が `estimated_duration_ms` として返せるようにします。 |
| size / process diagnostics | `status --json` は `db_size_bytes`、`wal_size_bytes`、上限付きの `symbol_kinds` / `symbols_by_language` kind map と、上限適用時の `symbol_kind_*` / `symbols_by_language_kind_*` overflow metadata、現在の `process` heap / GC / working-set metrics、成功した CLI / MCP index 実行由来の `last_index_run` metadata、最新の成功 index/update 時刻を示す `last_workspace_freshened_at` も公開します。`last_index_run.bytes_read_skipped_file_count` と `bytes_read_incomplete` は、読み取り不能な file が `bytes_read` 合計から除外されたかどうかを報告します。`last_index_run.diagnostics`、`diagnostic_count`、`diagnostics_truncated` は、index data 自体の書き込みが成功した後に best-effort index metadata write が失敗した場合の上限付き warning を保持します。`indexed_at` は引き続き indexed file row 由来なので、partial / no-op update は `indexed_at` を動かさずに workspace 鮮度だけを更新することがあります。 |
| memory tracing | `index --json --memory-trace` は CLI index 結果に `memory_timeline` block を追加し、peak working-set MB を `last_index_run` に保存します。dry-run 結果も live な `start`、`snapshot`、`scan`、`finalize` sample を返しますが、run metadata は保存しません。`index --dry-run --rebuild` は index を削除も rewrite もしないため destructive confirmation を bypass します。`CDIDX_MEM_WARN_MB=<mb>` は sampled working set がしきい値を超えたときに warning を出します。 |
| newer schema protection | writable open は、`PRAGMA user_version` に current binary の `CurrentSchemaVersion` mask 外の readiness bit が含まれる database も拒否します。read-only status/query path は degraded audit signal として `index_newer_than_reader=true` を表示できますが、write-capable path は古い cdidx が新しい binary で stamp された DB を黙って rewrite しないよう `E003_SCHEMA_TOO_NEW` で失敗しなければなりません。 |

### データディレクトリ解決

`--db <path>` が省略された場合、cdidx は data directory を解決し、その下に `codeindex.db` を置く。優先順位は次のとおりです。

1. `--data-dir <dir>`
2. `CDIDX_DATA_DIR`
3. `XDG_DATA_HOME` が設定されている場合の `XDG_DATA_HOME/cdidx/<workspace-hash>`
4. `<workspace>/.cdidx`

`--db <path>` は最も明示的な override であり、data-directory resolution を bypass する。`status --json` は effective directory を `data_dir`、選択元を `data_dir_source` (`flag`, `env`, `xdg`, `workspace`) として報告し、automation が index の配置先を audit できるようにする。

### SQLite パフォーマンス調整

すべての `DbContext` connection は `PRAGMA cache_size=-65536` (64 MiB)、`PRAGMA temp_store=MEMORY`、64-bit process では `PRAGMA mmap_size=268435456` (256 MiB) を設定する。これらは connection-scoped な query-performance knob であり、on-disk schema は変更せず、SQLite が適用できない場合だけ skip される。

operator は environment variable で既定値を上書きできる。

| Variable | Default | Meaning |
|---|---:|---|
| `CDIDX_SQLITE_CACHE_KB` | `65536` | KiB 単位の正の cache size。上限は `1048576`。cdidx は SQLite が KiB として解釈するよう負の `cache_size` 値として適用する。invalid / oversized value は既定値に戻る。 |
| `CDIDX_SQLITE_MMAP_BYTES` | `268435456` | 64-bit process で使う memory-map window の byte 数。`0` 以上、上限 `1073741824`。`0` で mmap を無効化する。invalid / oversized value は既定値に戻る。 |
| `CDIDX_SQLITE_BUSY_TIMEOUT_MS` | `5000` | SQLite busy timeout の millisecond 値。`0` 以上、上限 `3600000`。低速 disk や concurrent MCP/index workflow では大きい値を使える。invalid / oversized value は既定値に戻る。 |
| `CDIDX_PREPARED_COMMAND_CACHE_CAPACITY` | `64` | connection ごとの prepared SQLite command cache capacity。正の整数、上限 `512`。invalid / oversized value は既定値に戻る。 |

`cdidx index` が成功すると、writer は SQLite planner statistics を更新し、大規模 repository で `search`、`references`、`callers` などの join が default selectivity estimate に依存しないようにする。新規 index database は初回 population 後に full `ANALYZE` を一度実行し、それ以降の成功した index run では軽量な `PRAGMA optimize` を使う。この maintenance は best-effort であり、schema contract は変更しない。

### MCP リクエスト相関

各 JSON-RPC MCP request には、client-controlled な JSON-RPC `id` に加えて、server-generated な `correlation_id` を付与する。成功 response は `result._meta.correlation_id`、error response は `error.data.correlation_id` または tool-error の `result.structuredContent.correlation_id` に含める。serialized JSON-RPC id がある場合は同じ metadata に `request_id` として echo する。`batch_query` は parent value に `.1`、`.2` のような suffix を付けて slot ごとの child correlation ID を割り当てる。

MCP stderr diagnostic は request context に id がある場合、`[rid=<opaque-token> rid_type=<id-type> rid_length=<decode 後の値長> cid=<correlation-id>]` prefix を付ける。すべての `tools/call` は同じ opaque な `request_id` / `request_id_type` / `request_id_length` tuple、`event: "mcp.tool.invocation"`、tool name、elapsed milliseconds、status、可能な場合の result count、error metadata、argument key、argument length を含む structured JSON line も出す。request id の生値と argument value はこの telemetry に記録しない。

### MCP クエリページング

MCP `search` response には、その call が使った DB snapshot の index freshness timestamp からコピーした `result_stable_at` を含める。client が search result を page する場合は、call 間で `result_stable_at` を比較すること。値が変わっていれば、途中の index mutation により result set がずれた可能性があるため、pagination を最初からやり直すべきである。

non-empty な `search` response には `next_cursor` も含める。同じ query と filter でその値を `cursor` argument として渡すと、最後に返した `(score, chunk rowid)` anchor の後から継続する。cursor は opaque な response value であり、client が構築・編集してはいけない。

大量の discovery 結果を返す `symbols`、`files`、`validate` も opaque な `cursor` を受け付ける。count 以外の全 response は `returned_count`、`total_count`、`total_count_authoritative`、`remaining_count`、`cursor_offset`、`page_limit`、`has_more`、`result_stable_at`、`next_cursor` を報告し、最終 page または空 page は `has_more: false` と `next_cursor: null` を返す。`symbols` と `files` の total、および `file_issues_data_current` が true のときの `validate` の total は authoritative である。validation data が利用不能または current でない場合、`validate` は合成した 0 件を clean result として見せず、`issues_table_available` と `file_issues_data_current` とともに `total_count_authoritative: false` を報告する。各 page は generation、total、row を単一 SQLite snapshot で読み、決定的な順序により gap や duplicate なしで全 row を列挙できる。generation には永続的で単調増加する indexed-file write counter を含めるため、同じ timestamp 秒内に既存 file を更新した別 commit の indexing batch でも古い token は invalid になる。

`next_cursor` は、filter、`format`、`limit` を一切変えずに同じ tool へ渡す。token は stateless で、正規化済み query と index generation の両方に束縛される。不正 token は `cursor_malformed`、argument 変更は `cursor_query_mismatch`、範囲外 offset は `cursor_offset_out_of_range`、途中の index generation 変更は `index_stale` category の `cursor_stale` を返す。いずれも token を破棄し、`cursor` なしで最初からやり直す。`countOnly: true` と `format: "count"` は cursor を受け付けない。`status` tool は token 入力上限を `mcp.limits.max_query_cursor_characters` として公開する。

### MCP ヘルスプローブ

MCP JSON-RPC `ping` method は `status`、`uptime_s`、`last_request_at`、`db_open`、`last_db_check_at`、`transport_ready` を持つ structured health object を返す。HTTP MCP transport は同じ object を既存 listener の `GET /healthz` でも公開する。HTTP transport が bearer token で保護されている場合、`/healthz` も POST と `/events` と同じ `Authorization: Bearer <token>` requirement を使う。

`db_open` は configured SQLite DB に対する軽量な `SELECT 1` probe である。probe が失敗した場合、`status: "degraded"` を返し、raw filesystem / SQLite detail の代わりに sanitized な `db_error` exception type を含める。

HTTP health object には request-log drop、response cleanup failure、SSE event-stream drop（`http_event_stream_drop_count`、`http_event_stream_write_failure_drop_count`、`http_event_stream_last_drop_reason`）、および bearer auth denial class（`http_auth_denial_*`）の transport observability counter も含める。これらは内部診断であり、unsafe debug logging を明示的に有効化しない限り bearer auth failure は generic な 401 body を返し続ける。

### MCP keep-alive 通知

HTTP MCP `/events` stream は opt-in の server-initiated `notifications/keep_alive` JSON-RPC notification を出せる。`CDIDX_MCP_KEEP_ALIVE_INTERVAL_S` に `1` から `300` 秒の finite value を設定すると有効化される。未設定、non-finite、範囲外の値は既定の off behavior を維持し、warning を出す。stdio session は parent process が transport の liveness を所有するため、既定では keep-alive notification を出さない。

各 keep-alive notification は `params` に `server_time` と `uptime_s` を含める。notification は best-effort であり、切断済み SSE client は stream registry から削除され、keep-alive write failure が MCP server を終了させてはならない。

### MCP HTTP トランスポートの上限

`HttpMcpTransport` は通常の JSON response body を `CDIDX_MCP_HTTP_MAX_RESPONSE_BYTES`
で制限します。既定値は 1,000,000 bytes、最大値は 16,777,216 bytes です。上限を超える
JSON-RPC response は payload を stream せず、bounded な text diagnostic を持つ HTTP 500
として返します。request-loop diagnostics は、長さ不明の request body を
`request_body_length_unknown`、request body 上限超過を `request_body_limit_exceeded`
として記録します。server-side JSON-RPC serialization 後、`McpServer` は initialize state を
commit し、`HttpMcpTransport` は HTTP 配送前に session を公開します。そのため、すでに
serialization 済みの initialize response を `CDIDX_MCP_HTTP_MAX_RESPONSE_BYTES` が拒否する
場合、transport は新しい `Mcp-Session-Id` を付けた HTTP 500 を返し、別 client に server
state を継承させないよう committed session を fail-closed で保持します（#4539）。この
transport 上限は commit 前の `CDIDX_MCP_RESPONSE_MAX_BYTES` fallback（#4540）とは別です。
response write と SSE write の timeout は `timeout:http_response_write`、
`timeout:sse_write` のような stable diagnostics を使います。timeout 期限に達した場合は
HTTP response を能動的に abort するため、非協調的な output stream が request や SSE gate
を無期限に保持できません。JSON-RPC request timeout は `timeout_category: "mcp_request"` を
持つため、caller cancellation と区別できます。

HTTP request queue は `TryWrite` 前に slot を取得します。満杯時は HTTP handler を block
せず、HTTP 429、`Retry-After: 1`、`X-Cdidx-Mcp-Rejection: request_queue_limit`、
`http_request_queue_rejection_count` で拒否を記録します。request-log queue は best-effort
で、飽和時は `http_request_log_queue_full_drop_count` / `http_request_log_dropped_count`
を増やします。POST handler gate と長寿命 event-stream gate は独立しており、
`concurrent_handler_limit`、`event_stream_limit` を別々に報告します。HTTP health は両方の有効
capacity と `http_separate_event_stream_handlers` も返します。limit 環境変数は未設定の場合だけ
既定値を使い、設定済みの malformed 値または範囲外値は listener 起動前に失敗します。transport
queue / handler semaphore は bounded shutdown で全取得 slot の返却を確認できた場合だけ dispose
します。late handler が listener teardown 後にも完了し得るためです。frame-loop gate
は EOF drain がすべての request task を観測できた場合だけ dispose します。bounded drain は
late task を意図的に残すことがあり、その task が gate をまだ使う可能性があるためです。

## データベーススキーマ

### テーブル

```sql
-- ファイルメタデータ
files (
    id          INTEGER PRIMARY KEY AUTOINCREMENT,
    path        TEXT NOT NULL UNIQUE,       -- プロジェクトルートからの相対パス
    lang        TEXT,                       -- 検出された言語（例: "python"）
    size        INTEGER,                    -- ファイルサイズ（バイト）
    lines       INTEGER,                    -- 行数
    checksum    TEXT,                       -- ファイルバイトを CRLF/CR→LF に正規化した上で取った SHA256（BOM はそのまま）。これにより OS をまたいだ clone でも checksum が一致し、BOM の追加/削除は引き続き再索引のトリガーとして機能する
    modified    DATETIME,                   -- ファイル更新日時（UTC）
    indexed_at  DATETIME DEFAULT CURRENT_TIMESTAMP
)

-- 全文検索用コンテンツチャンク
chunks (
    id          INTEGER PRIMARY KEY AUTOINCREMENT,
    file_id     INTEGER NOT NULL REFERENCES files(id) ON DELETE CASCADE,
    chunk_index INTEGER NOT NULL,           -- 0始まりのチャンク位置
    start_line  INTEGER,                    -- 1始まりの開始行
    end_line    INTEGER,                    -- 1始まりの終了行（含む）
    content     TEXT,
    UNIQUE(file_id, chunk_index)
)

-- 抽出されたシンボル（関数、ラムダ、クラス、インポート、名前空間など）
symbols (
    id              INTEGER PRIMARY KEY AUTOINCREMENT,
    file_id         INTEGER NOT NULL REFERENCES files(id) ON DELETE CASCADE,
    kind            TEXT,                    -- "function"、"lambda"、"class"、"import"、"namespace" など
    sub_kind        TEXT,                    -- kotlin_value_class などの言語固有の細分類
    name            TEXT,
    line            INTEGER,                 -- 1始まりのアンカー行
    start_line      INTEGER,                 -- 定義開始行
    end_line        INTEGER,                 -- 定義終了行
    body_start_line INTEGER,                 -- 分かる場合の本体開始行
    body_end_line   INTEGER,                 -- 分かる場合の本体終了行
    signature       TEXT,                    -- trim済みの宣言/シグネチャ行
    container_kind  TEXT,
    container_name  TEXT,
    container_qualified_name TEXT,           -- 修飾付きの親コンテナ経路
    family_key      TEXT,                    -- 判定できる場合の正式なファイル横断グループキー
    visibility      TEXT,
    return_type     TEXT
)

-- 呼び出し箇所などのインデックス済み参照
symbol_references (
    id              INTEGER PRIMARY KEY AUTOINCREMENT,
    file_id         INTEGER NOT NULL REFERENCES files(id) ON DELETE CASCADE,
    symbol_name     TEXT,                    -- 参照先シンボル名
    reference_kind  TEXT,                    -- "call", "instantiate", "generic_type_argument", "subscribe", "razor_event_binding", "attribute", "annotation", "decorator", "type_reference", "implicit_implementation"
    line            INTEGER,                 -- 1始まりの行番号
    column_number   INTEGER,                 -- 1始まりの列番号
    context         TEXT,                    -- trim済みソース行
    container_kind  TEXT,
    container_name  TEXT,
    source_symbol_id INTEGER,                -- 解決済みの場合の親定義
    target_symbol_id INTEGER,                -- 単一に解決した参照先定義
    target_symbol_key TEXT,                  -- language/path/container/name による安定 family key
    target_qualifier TEXT,                   -- 抽出できた安定した型相当 receiver 修飾子
    resolution_state TEXT,                   -- resolved / resolved_group / ambiguous / unresolved
    resolution_candidate_count INTEGER NOT NULL DEFAULT 0
)

-- resolved group と明示的な曖昧性のために保持する最良 scope の候補
symbol_reference_candidates (
    reference_id INTEGER NOT NULL,
    symbol_id    INTEGER NOT NULL,
    scope_rank   INTEGER NOT NULL,
    PRIMARY KEY(reference_id, symbol_id)
)

-- chunks.contentをミラーするFTS5仮想テーブル
fts_chunks USING fts5(content, content='chunks', content_rowid='id')

-- クエリ意味論の readiness メタデータ
codeindex_meta (
    key         TEXT PRIMARY KEY NOT NULL,
    value       TEXT
)
```

### インデックス

```sql
idx_files_lang      ON files(lang)
idx_files_modified  ON files(modified)
-- idx_files_path は不要: path の UNIQUE 制約が暗黙的にインデックスを作成済み
idx_chunks_file     ON chunks(file_id)
idx_symbols_name    ON symbols(name)
idx_symbols_file    ON symbols(file_id)
idx_symbols_file_kind ON symbols(file_id, kind)
idx_files_lang_modified ON files(lang, modified)
idx_symbol_refs_name      ON symbol_references(symbol_name)
idx_symbol_refs_file      ON symbol_references(file_id)
idx_symbol_refs_container ON symbol_references(container_name)
idx_symbol_refs_source_symbol ON symbol_references(source_symbol_id)
idx_symbol_refs_target_symbol ON symbol_references(target_symbol_id)
idx_symbol_ref_candidates_symbol ON symbol_reference_candidates(symbol_id, reference_id)
```

### クエリプランナー期待値

`symbol_references.symbol_name` と小さな `reference_kind IN (...)` 集合で絞る
hot graph aggregation は、`idx_symbol_refs_name_kind` で indexable な状態を
保つ必要があります。回帰テストは `ANALYZE` 前後の `EXPLAIN QUERY PLAN` を使い、
`GROUP_CONCAT(DISTINCT r.reference_kind)` の要約が単一カラムの symbol-name
probe と行ごとの kind filtering に戻らず、この compound index を期待計画として
維持することを確認します (#1922)。

言語 + シンボル種別の定義検索では、`lang` を `symbols` に非正規化せず
`files` に保持する方針です。クエリビルダーはこの条件を
`s.file_id IN (SELECT id FROM files WHERE lang = @lang)` と表現し、
SQLite が `files(lang)` で候補ファイルを絞ってから
`idx_symbols_file_kind (file_id, kind)` を probe できるようにしています。
この種のクエリを `JOIN files f ... WHERE f.lang = @lang AND s.kind = @kind`
へ戻さないでください。大きいインデックスでは `idx_symbols_kind` から始まり、
要求された kind の全シンボルを走査してから言語を確認する計画に戻る可能性があります
(#1933)。

### FTS5同期トリガー

```sql
-- chunksテーブルとfts_chunksを自動的に同期するトリガー
fts_chunks_ai   AFTER INSERT ON chunks  -- FTSに挿入
fts_chunks_ad   AFTER DELETE ON chunks  -- FTSから削除
fts_chunks_au   AFTER UPDATE ON chunks  -- 旧エントリ削除＋新エントリ挿入
```

### エンティティ関連図

```
files 1──N chunks 1──1 fts_chunks（コンテンツミラー）
files 1──N symbols
files 1──N symbol_references
symbol_references 1──N symbol_reference_candidates N──1 symbols
```

identity-aware read は、`codeindex_meta` の `reference_identity_contract_version` が
現在値である場合だけ有効になります。legacy read migration で column と candidate table を
追加しただけでは ready とみなしません。通常、no-op、削除のみの index 実行でも reference
resolution を再構築し、同じ transaction で marker を設定します。C# の無修飾名 reference は、
対象となる symbol 集合で名前が一意の場合だけ global candidate を持ちます。それ以外は
`ambiguous` または `unresolved` のままとし、dependency query は同名 edge へ fallback しません。

C# の `type_reference` candidate は qualifier / namespace の順位付け前に絞り込みます。candidate は
型相当の symbol（`class`、`struct`、`record`、`interface`、`enum`、`delegate`）でなければならず、
正規化済み行 context から reference の generic arity を復元できる場合は declaration の arity と
一致する必要があります。arity の復元では有効な block comment trivia を読み飛ばし、永続化 context の
trim 後は元の source column を上限として扱うため、同じ行の後続同名 generic を誤って選びません。
型ではない同名 symbol、大文字小文字が ordinal 一致しない symbol、arity 不一致の generic declaration は解決候補に
含めず、適合 candidate が残らなければ reference は `unresolved` のままです。これは framework 型の
blacklist ではなく一般的な candidate compatibility rule です。`Name.Trim()` のように大文字で始まる
property receiver は graph 解決前に修飾済み member reference へ書き換え、property が partial class の
別ファイルまたは index 済み base class で宣言されている場合も inheritance chain 上で最も近い property を
ordinal 名一致で選び、型限定 filter によって実在する member dependency を失ったり曖昧化したりしないようにします。
Java の reference resolution は変更しません。
この規則は persisted reference-identity contract の version 対象であり、compatibility filter 導入前に
作成された index は、通常の index 更新で candidate を再構築するまで非 authoritative として扱います。

reference finalization は、candidate count、最小 symbol ID、distinct target-family count、安定 target
key を reference ごとに1回の correlated aggregate で計算します。この4つの resolution field は
row-value assignment のまま維持してください。scalar subquery を分けると、大規模 graph で
candidate index と symbol/file lookup が重複します。global に一意な language/name family は
connection-local な `temp.reference_unique_symbol_families` table へ1回だけ集約し、non-C#、C#、
C# attribute fallback で共有します。この temp table は refresh command を prepare する前に別の
prepared command で作成してください。SQLite は command batch の全statementをprepareする時点で
参照tableを解決します。

`inspect` / MCP `analyze_symbol` は返された各定義を別々の identity bundle として
扱います。candidate selector は永続化した symbol ID に加え、qualified/container name、
signature、language、kind、path、line を公開します。identity-scoped な
reference/caller/callee query は `symbol_reference_candidates` または
`source_symbol_id` を join します。複数 candidate の top-level graph 配列は
`primary_candidate` と明示して優先順位1位の bundle だけを反映し、複数定義を
未ラベルで集約しません。言語フィルタと定義のどちらからも graph language が得られない場合、
`analyze_symbol` は reference/caller/callee evidence が一貫して1言語だけを示すときに限って
言語を推論します。`graph_language_source`、`graph_language_confidence`、
`graph_language_candidates`、`graph_language_conflict` により、filter/definition による
authoritative な判定と一貫した推論を区別し、複数言語の evidence は未確定のままにします。
path/line resolution は `symbols.id` を select し、name resolution と同じ
candidate-bundle builder に入れてください。graph loader に display name だけを渡しては
なりません。上限付きの references、callers、callees section は、それぞれ安定順序の page と
authoritative な総数を計算します。`graph_sections` は `total`、`returned`、`offset`、
`truncated` を報告し、CLI `inspect` と MCP `analyze_symbol` は truncated な section ごとに
query、effective page size、index generation に束縛した cursor を付けます。
caller/callee の順序は grouped identity field のすべてを末尾の tie-breaker に使い、offset page が
同順位 row を飛ばさないようにします。candidate selector も cursor scope に含め、1つの overload
または partial-family representative の pagination が別 candidate の graph row を借用しないように
してください。path/line locator と graph path filter は分離してください。locator は persisted
symbol を選択しますが、その inbound references と callers は、明示された graph filter が許可する
任意の indexed file から取得できます。共有 cursor parser が inspect cursor を保持しても、
inspect 以外の各 command は実行前にその cursor family を拒否しなければなりません。

### 参照 taxonomy

`symbol_references.reference_kind` には extractor が出力した raw label を保存する。既定の call-graph 表示（`callers`、`callees`、inspect/analyze の caller / callee bundle、および JSON/MCP フィールド）は、公開 canonical 語彙 `call`、`instantiate`、`subscribe` を返す。primary `reference_kind`、`reference_kinds`、`reference_kind_counts` の key はすべて同じ語彙を使う。raw extractor 出力を調べる場合は、`callers` / `callees` の `--raw-kinds`、または `references --kind <raw-kind>` を使う。

`ReferenceRecord.SpanLength` と `symbol_references.span_length` は、解決後の symbol 名から導出せず、物理的に一致した token 幅を永続化する。これは `base`、`super`、`this` のような constructor-chain token で重要になる。`DbReader.GetCallees` は count を集約しながらその span を保持し、列が保存された row のうち最小の `(line, column_number)` を選び、その 1-based 座標を `first_line` / nullable な `first_column`、同じ row の nullable な幅を `first_length` として公開し、`reference_count` は独立した集約値のままにする。寄与する legacy row がすべて `column_number IS NULL` の場合は最小行と null 列を保持する。移行済み row では列があっても span 長が null の場合がある。CLI/MCP の location adapter はどちらの場合も精度を捏造せず劣化させる。

| Raw kind | Logical graph kind | 備考 |
|---|---|---|
| `call` | `call` | 直接実行される呼び出しエッジ。 |
| `instantiate` | `instantiate` | constructor / construction エッジ。 |
| `goroutine_spawn` | `goroutine_spawn` | Go の `go f()` による非同期 spawn edge。呼び出し先には通常の `call` edge も併せて出力する。 |
| `channel_send`, `channel_receive` | raw label | Go の channel send / receive 式を表す通信エッジ。既定の invocation graph からは除外する。 |
| `razor_event_binding` | `subscribe` | Razor の `@on...="Handler"` event binding から C# handler 名への edge。 |
| `subscribe`, `unsubscribe` | `subscribe` | 既定の call-graph query で可視化するイベント配線エッジ。 |
| `generic_type_argument`, `friend`, `capture`, `consumes_hook`, `project_reference` | raw label | 既定の `callers` / `callees` から除外する依存関係 / metadata edge。`references`、明示 kind filter、対応する dependency / impact surface では利用できる。 |
| `binding`, `resource_reference`, `import` | raw label | GPU / shader の binding 宣言、resource 利用、静的に確認できる include。invocation graph から除外し、`references --kind <kind>` と raw reference の export / query で利用できる。 |
| `system_variable` | raw label | T-SQL `@@ROWCOUNT` / `@@IDENTITY` や MySQL `@@session.sql_mode` / `@@global.max_connections` など、SQL 実行 context variable。intrinsic variable なので definition site は持たない。 |
| `attribute`, `annotation`, `type_reference`, `implicit_implementation` | raw label | 依存関係 / reference 専用の metadata、型位置エッジ、および C# async iterator の `GetAsyncEnumerator` / `MoveNextAsync` のようなコンパイラ合成の実装エッジ。既定の call-graph 行からは除外する。 |

TypeScript decorator は decorator 名を `annotation` 行として出力し、decorated declaration の型位置エッジを隠してはならない。たとえば `constructor(@Inject() svc: Service)` は `Inject` を `annotation`、`Service` を `type_reference` として記録し、`@Input() profile: UserProfile` も decorator と field type の両方を記録する。

C# の `overwrite:` のような named-argument label は構文であり、型位置ではないため `type_reference` 行を出力してはならない。declaration-type scanner は argument fragment の先頭にある単一 colon の label を、comma で終わる複数行 argument も含めて読み飛ばし、argument value 内の式参照、named `out` declaration の型、明示型 lambda / anonymous method の parameter、および型付き LINQ range variable を維持する。複数行 property subpattern でも property label 後の型を維持する。alias-qualified name（`Alias::Type`）、statement / `case` label、nullable type、ternary expression は別の colon 構文として扱う。

### GPU / shader の参照抽出

CUDA、GLSL、HLSL、Metal、WGSL は request ごとの stateless な参照 extractor を使う。
通常の helper / entry point 呼び出しと CUDA の `kernel<<<...>>>(...)` launch は `call`
edge を出力し、既定の call graph に参加する。静的に確認できる include、binding、
resource 利用、ユーザー定義型の利用はそれぞれ `import`、`binding`、
`resource_reference`、`type_reference` metadata 行になる。include 先ファイルの型宣言は
上限付き workspace symbol snapshot から供給できる。extractor は comment を
mask し、attribute / binding 構文を phantom call として出さず、追跡名数と1行あたりの
走査数に上限を設ける。macro expansion、binding validation、function pointer 解決、
意味的 data-flow 解析は意図的に行わない。built-in extractor registry への登録が
`languages --json` の `reference_extraction` と `graph_queries` に公開される readiness
contract である。

### Python シンボル分類

Python 抽出は、通常の関数と method を `function`、class declaration と dynamic
class factory を `class`、class attribute、`@property` descriptor、accessor
decorator、`Final` constant、walrus assignment の名前を `property` として扱う。
`__init_subclass__`、`__class_getitem__`、`__set_name__`、
`__class_subclasses__` のような lifecycle dunder hook は `class_hook` として記録する。
`SubKind` は Python property accessor を `getter` / `setter` / `deleter`、
walrus assignment を `walrus`、class hook を `dunder` として細分化する。

### Scala シンボル分類

Scala 抽出は `class` / `case class` 宣言を `class`、singleton の `object` / `case object` / sealed object 宣言を `object` として記録する。`implicit def` / `implicit val` / `implicit var` / `implicit class` 宣言は `implicit`、Scala 3 の `given` 宣言は `given` とし、それらの source / target / evidence type は `type_reference` 行として出力する。`for` comprehension の generator も generator source への call edge を出力する。同じファイルに top-level の `class X` と top-level の `object X` がある場合、`SubKind` は object 側に `companion_object`、class 側に `has_companion_object` を記録し、singleton をインスタンス化可能な class と扱わずに inspect / outline consumer が companion 関係を表示できるようにする (#1823、taxonomy tracking は #1772 に関連)。

### TypeScript 型グラフ抽出

TypeScript 抽出は、type-only 構文から `type_reference` edge を dependency metadata として出力し、実行される call-graph edge とは扱わない。type alias、mapped type、indexed access type、conditional type、template literal type の hole、`infer` 句では参照先 identifier を走査し、`keyof`、`in`、`as`、`extends`、`infer` のような TypeScript type operator は keyword として抑止する。たとえば ``type Getters<T> = { [K in keyof T as `get${Capitalize<K>}`]: () => T[K] }`` は `T`、`K`、`Capitalize` への参照を記録し、`type Unwrap<T> = T extends Promise<infer U> ? U : never` は `T`、`Promise`、`U` を記録する。

## なぜgrepではなくデータベースなのか？

小規模プロジェクトなら `grep` で十分です。しかしファイルが数万規模になると `grep` はボトルネックになります。特にAIエージェントが繰り返し検索を実行するケースで顕著です。cdidxは**すべてのファイルを一度だけ読み込んで検索用の構造を構築する**ことで、以降の検索で元のファイルを一切開かずに済むようにします。

`grep -r "keyword" .` は力任せの線形スキャンです。毎回すべてのファイルを開き、すべての行を読み、マッチを確認します。10回目の検索でも1回目と同じコストです。cdidxは重い処理を一度きりのインデックス作成ステップに集約し、以降の検索は事前構築されたデータベースへの軽い参照で済みます。

| 比較項目 | `grep -r` | cdidx（SQLite FTS5） |
|---|---|---|
| **検索アルゴリズム** | 毎回全ファイルを線形スキャン | 転置インデックスでのトークン参照 |
| **繰り返し検索** | 毎回同じフルコスト | 初回インデックス後はほぼ即時 |
| **初期コスト** | なし | 一度きりのインデックス作成（以降はインクリメンタル更新） |
| **保存内容** | なし — 毎回ファイルを読み込み | チャンク化されたソーステキスト＋トークンの転置インデックス |
| **構造化クエリ** | テキストマッチのみ | 言語、パス、シンボル種別、行範囲でフィルタ可能 |
| **シンボル認識** | なし — 生テキストのみ | 関数・クラス・インポート名と位置を認識 |
| **AIトークンコスト** | 生の行を返す — ノイズが多くトークン消費大 | ファイルパスと行番号付きの正確なチャンクを返す |

### 使い分け

| シナリオ | 推奨 |
|---|---|
| 小規模プロジェクトでの一回きりの検索 | `grep` |
| 大規模コードベースでの繰り返し検索 | **cdidx** |
| AIエージェントによる複数回のコード検索 | **cdidx** |
| 関数名で全使用箇所を検索 | **cdidx**（`symbols`テーブル） |
| バイナリファイルや非コードコンテンツの検索 | `grep` |

## なぜSQLiteなのか？

データベースが正しいアプローチだとして、なぜPostgreSQL、DuckDB、LiteDB、Tantivy等の専用検索エンジンではなくSQLiteなのか？

**端的に言えば、cdidxを「設定ゼロ・単一ファイル・本番依存1個」のCLIツールとして維持できるのはSQLiteだけだからです。**

### 検討した代替案

| 代替案 | 強み | cdidxに適さない理由 |
|---|---|---|
| **PostgreSQL / MySQL** | 並行性、スケーラビリティ、高度なFTS | サーバープロセスが必須。cdidxを使う前にDBのインストールと管理が必要になり、`dotnet tool install -g cdidx` で即使える体験が壊れる。 |
| **DuckDB** | 高速な分析（OLAP）クエリ、カラムナストレージ | 全文検索が未搭載。cdidxのワークロードはOLTP（挿入＋キーワード検索）であり分析ではない。.NETバインディングも `Microsoft.Data.Sqlite` ほど成熟していない。 |
| **LiteDB** | .NETネイティブの組み込みNoSQL、スキーマフリー | FTSなし。symbols → references → callers/callees のリレーショナル構造はSQLのJOINが自然であり、ドキュメントクエリでは扱いにくい。 |
| **Tantivy / Lucene** | 全文検索専用で高精度なランキング | 検索のみを扱う。リレーショナルデータ（シンボル、参照、ファイルメタデータ）には別のストレージが必要になり、2ストレージの同期問題が発生する。 |
| **ベクトルDB** (Qdrant, Chroma) | セマンティック/埋め込みベース検索 | 埋め込みモデルが必要（大きな依存やAPI呼び出しが増える）。キーワード検索や構造化クエリが弱い。将来SQLiteを補完する可能性はあるが、置き換えはできない。 |

### SQLiteが最適な理由

1. **設定不要** — サーバープロセス、接続文字列、ポート設定が一切不要。`cdidx index .` だけで動く。
2. **単一ファイルDB** — インデックス全体が `.cdidx/codeindex.db` に収まる。コピー、削除、移動が通常のファイル操作で完結。
3. **クロスプラットフォーム** — Windows、macOS、Linuxで同一の動作。プラットフォーム固有のセットアップ不要。
4. **本番 NuGet 依存は1個だけ** — `src/CodeIndex` の production/runtime 依存は `Microsoft.Data.Sqlite` だけである。`tests/CodeIndex.Tests/` には test-only package が存在しうるが、出荷物には含まれず、このルールも緩めない。これによりサプライチェーンリスクとバイナリサイズを最小化できる。
5. **FTS5が組み込み** — 全文検索は転置インデックス、フレーズクエリ、ランキングを備えたSQLiteネイティブ拡張。外部検索エンジン不要。
6. **リレーショナル＋FTSが1エンジンで完結** — シンボル、参照、チャンク、ファイルメタデータがFTSインデックスと同じDB内に共存。JOIN、トリガー、トランザクションでクロスシステム同期なしに整合性を維持。
7. **WALモード** — Write-Ahead Loggingによりインデックス中も並行読み取りが可能。MCPサーバーがバックグラウンドインデックス中にクエリを返すケースを支える。
8. **インクリメンタル更新に適した基盤** — SQLiteのトランザクション、`ON CONFLICT DO UPDATE`、タイムスタンプ比較でインクリメンタルインデックスを自然に実現。

### SQLiteでは足りなくなるケース

- **超大規模monorepo（100万ファイル超）:** SQLiteのsingle-writerモデルがボトルネックになりうる。プロジェクト単位のDB分割（cdidxは既にプロジェクト別DB）で緩和できるが、真の並列書き込みにはサーバーDBが必要。
- **セマンティック検索:** 埋め込みベースの類似検索にはベクトルインデックスが有利。`sqlite-vec` 拡張でSQLiteを離れずに対応する方法か、ハイブリッド構成（SQLite＋外部ベクトルストア）を検討できる。

現在のユースケース — 単一プロジェクトをキーワード検索とシンボルナビゲーション用にインデックスするローカルCLIツール — において、SQLiteはシンプルさ・パフォーマンス・機能のバランスが最適です。

## FTS5 全文検索

[FTS5](https://www.sqlite.org/fts5.html)（Full-Text Search 5）はSQLiteの拡張で、全文検索用の**転置インデックス**を提供します。各トークン（単語）からそれを含むドキュメントのリストへのマッピングを構築し、全行スキャンではなくO(1)のキーワード検索を実現します。

FTS5は**仮想テーブル**で動作します。通常のSQLiteテーブルと同じように見え、`SELECT`やJOINが可能ですが、テキスト検索に最適化された特殊な形式でデータを格納します。

検索結果の順序は、同じ入力と同じインデックスに対して決定的でなければなりません。ranking の `ORDER BY` 句は、ユーザーに見える関連度キーの後に安定して永続化されたキーを置いて終えるべきです。これにより FTS rank、file timestamp、path が同点になっても SQLite 実装依存の row order に落ちません。このため chunk search の `ORDER BY` は最後を `f.path, c.id ASC` で締めています（#1731）。

Chunk search の ranking は、裸の BM25 tie に落ちる前に symbol 構造を使います。exact / prefix の symbol-name boost は、query に一致する symbol 定義と重なる chunk を、同じ file 内の別 chunk から区別し、それでも足りない場合は file-level の symbol presence に fallback します。text relevance が同点の場合、重なる symbol name/signature に query hit がある chunk、高価値な symbol kind（class/interface/struct/enum、次に function/method/property）、浅い symbol scope が、comment-only match や深い nest の match より前に並びます。これにより、scope root や definition に近い結果を冗長な inner mention や非構造的 mention より前に保ちつつ、最後は決定的な fallback ordering を維持します。

### 転置インデックスとは？

転置インデックスは、各単語（トークン）からそれを含むドキュメント（行）のリストへのマッピングです。教科書の巻末索引のようなものです。

例えば、3つのチャンクに以下のコードが含まれるとします:

| チャンクID | 内容（簡略化） |
|---|---|
| 1 | `handleRequest(ctx)` |
| 2 | `sendResponse(ctx)` |
| 3 | `handleRequest(req); sendResponse(res)` |

FTS5が構築する転置インデックス:

| トークン | チャンクID |
|---|---|
| `handleRequest` | 1, 3 |
| `sendResponse` | 2, 3 |
| `ctx` | 1, 2 |
| `req` | 3 |
| `res` | 3 |

`handleRequest` で検索すると、FTS5はそのトークンのエントリを読み、チャンクID `{1, 3}` を即座に返します。スキャンは不要です。

### B-treeインデックスとの違い

B-tree（平衡木）はSQLiteのデフォルトのインデックス構造です。値をソート済みのツリー型階層に整理します:

```mermaid
flowchart TD
    root["go | python"]
    left["csharp"]
    middle["java, kotlin"]
    right["rust, typescript"]

    root --> left
    root --> middle
    root --> right
```

B-treeインデックスは完全一致（`WHERE lang = 'csharp'`）、範囲クエリ（`WHERE modified > '2025-01-01'`）、ソートに適しています。しかし「テキストカラムのどこかに `handleRequest` という単語を含む行はどれか？」には効率的に答えられません。FTS5が必要です。

| | B-treeインデックス | FTS5転置インデックス |
|---|---|---|
| **用途** | 単一カラムの完全一致・範囲・前方一致 | テキスト全体に対する自然言語キーワード検索 |
| **検索例** | `WHERE path = 'foo.py'` | `WHERE fts_chunks MATCH 'authenticate'` |
| **構造** | カラム値のソート済みツリー | トークン → ドキュメントIDのポスティングリスト |
| **ランキング** | なし（完全一致を返す） | BM25関連度スコアリング |
| **使用対象** | `path`, `lang`, `modified`, `file_id`, `name` | `chunks.content`（コードテキスト） |

この2種類のインデックスは相互補完的です。典型的なクエリではFTS5でマッチするチャンクを見つけ、B-treeインデックスで言語やファイルパスでフィルタします。

### `fts_chunks` 仮想テーブル

```sql
CREATE VIRTUAL TABLE IF NOT EXISTS fts_chunks USING fts5(
    content,
    content='chunks',
    content_rowid='id'
);
```

| パラメータ | 意味 |
|---|---|
| `USING fts5(...)` | FTS5エンジンでこの仮想テーブルを管理 |
| `content` | インデックス対象のカラム — `chunks.content`（コード本文）に対応 |
| `content='chunks'` | **外部コンテンツテーブル** — `fts_chunks`はテキストのコピーを保存せず`chunks`を参照 |
| `content_rowid='id'` | 各FTS5エントリの`rowid`が`chunks.id`に一致し、直接行参照が可能 |

### コンテンツ同期

`fts_chunks`は**コンテンツ外部参照型**のFTS5テーブル（`content='chunks'`）です。元のテキストを保存せず、`content_rowid`で`chunks.id`を参照します。これによりストレージの倍増を回避しています。cdidxはデータベーストリガー（`fts_chunks_ai`、`fts_chunks_ad`、`fts_chunks_au`）でFTSインデックスを自動的に同期します。

### クエリ構文

FTS5は高度なクエリ構文をサポートしています:

```sql
-- 単一語句
WHERE fts_chunks MATCH 'authenticate'

-- フレーズ（完全一致の語順）
WHERE fts_chunks MATCH '"handle request"'

-- ブール演算子
WHERE fts_chunks MATCH 'auth AND token'
WHERE fts_chunks MATCH 'auth OR login'
WHERE fts_chunks MATCH 'auth NOT oauth'

-- 前方一致検索
WHERE fts_chunks MATCH 'auth*'

-- カラムフィルタ（ここでは1カラムだが、複数カラムFTSで有用）
WHERE fts_chunks MATCH 'content:authenticate'
```

### 検索の仕組み

以下のクエリを実行すると:
```sql
SELECT f.path, c.start_line, c.content
FROM fts_chunks fc
JOIN chunks c ON c.id = fc.rowid
JOIN files f ON f.id = c.file_id
WHERE fts_chunks MATCH 'handleRequest'
LIMIT 20;
```

1. FTS5が転置インデックスで `handleRequest` を参照 → マッチするチャンクの`rowid`リストを直接取得
2. `chunks`テーブルにJOINして80行のコードブロックと行番号を取得
3. `files`テーブルにJOINしてファイルパスと言語を取得

ファイルを開くことも、ディレクトリを走査することもありません。検索全体がSQLite内部で完結します。

## チャンク分割戦略

ファイルは**80行のチャンクに10行の重複**で分割されます。重複により、チャンク境界をまたぐシンボル定義やコードブロックが少なくとも1つのチャンクに完全に含まれます。

```
1-80行    → チャンク0
71-150行  → チャンク1（チャンク0と10行重複）
141-220行 → チャンク2（チャンク1と10行重複）
...
```

ステップサイズは `80 - 10 = 70` 行です。N行のファイルは `ceil((N - 80) / 70) + 1` 個のチャンクを生成します（最小1個）。

## シンボル抽出

言語 surface ごとの抽出方針:

| surface | extraction strategy |
|---|---|
| 大半の言語 | **コンパイル済み正規表現パターン**を 1 行ずつ適用して function、class、場合によっては import を抽出します。正規表現ベースの経路では引き続き名前付き capture group で identifier を取得します。 |
| JavaScript / TypeScript の主要形 | 行単位の正規表現だけでは安定して扱えない class body の bare method、computed / modifier 付き method、scope-aware な synthetic class expression、JS/TS 専用の range 解決を補うため、軽量な lexer / state machine を追加しています。 |
| Swift | 引き続き正規表現経路です。`func` / `class` / `struct` / `protocol` / `enum` / `indirect enum`、`private(set)` / `fileprivate(set)` 付き stored property、backtick escaped name、enum case を拾い、小さな trailing-lambda 専用 path で `name { ... }` 形の慣用的な呼び出しを graph から落とさないようにしています。 |
| Kotlin | 正規表現経路のままです。 |
| Scala | `name { ... }` / `name { x => ... }` 形式を拾う専用の block-call path があり、`foreach {}`、`Try {}`、`synchronized {}` のような慣用的な呼び出しも graph から消えないようにしています。 |
| Common Lisp / Racket | 文字列、行 comment、`#| ... |#` block comment を mask してから definition と function range を抽出する軽量な S-expression scanner を使います。 |
| HTML | 汎用の正規表現 loop を使わず、専用の文字単位 state machine で tag opener、引用符付き/なし attribute value（複数行値を含む）、`<script>` / `<style>` / `<textarea>` / `<title>` body、`<!-- ... -->` comment を扱い、attribute 名に似た body 内文字列から phantom symbol が漏れないようにします。 |
| JSON / JSON Lines | JSON は `object`、`array`、`property` と、上限付きの primitive-array `value` symbol を index 付き path で出力します。`.jsonl` と `.ndjson` は空でない物理行を個別に parse し、`[0].result.path` のような安定した 0 始まり record path を付けます。各有効 record から repository-local path reference を出し、不正な隣接 record の内容は平坦化しません。 |
| TOML / repository metadata | TOML の table / key、EditorConfig の section / key、Git / Docker ignore rule、Git attribute の rule / attribute、`.rules` の block / key を上限付き structural symbol として出力します。reference は repository-local な path / glob に限定し、remote URL、絶対 filesystem path、親 directory traversal は抑止します。 |
| Windows application manifest | manifest element path、assembly identity、execution level、supported OS value を structural symbol として維持します。依存 assembly identity は `dependency` reference、local な `file` / `codeBase` / probing path は `project_reference` edge を出力します。 |
| XML / NuGet.config | 汎用 XML は上限付きの element / attribute path を出力します。NuGet.config ではさらに package source、source mapping、署名検証モード、trusted signer 名、証明書 fingerprint、`allowUntrustedRoot` の値を `nuget.*` subkind 付きの semantic `property` symbol にします。 |

JavaScript / TypeScript の export / reference 詳細:

| 項目 | 動作 |
|---|---|
| barrel re-export surface | commented、multiline、namespace、minified、TypeScript type-only-star、`with {}` / `assert {}` import-attribute form を cover し、対応する source-module `import` row も維持します。 |
| CommonJS export と typed arrow | direct CommonJS named export assignment と same-line / multiline parenthesized wrapper、multiline / constrained / async TypeScript generic-arrow RHS form を扱います。 |
| function/class discrimination | prefix-safe な function/class discrimination を保ちます。 |
| object-literal export | exported object-literal alias / shorthand property を index します。 |
| destructured named export | `export const { foo, renamed: localName } = source` のような destructured named export も、実際に出力される binding 名で index します。 |
| namespace / dynamic import alias | `export * as NS from "./module"`、`import * as NS from "./module"`、named import alias、`const NS = await import("./module")` 由来の namespace alias は、後続の `NS.Member()` のような qualified usage から module specifier への `reference` edge も出します。同名の local declaration が後から現れた場合は保守的にその行以降の module linking を止め、dynamic import alias は local brace scope 内に限定します。 |
| discriminant string guard | `shape.type === "circle"` のような JavaScript / TypeScript の比較は query 可能な `type_tag` reference を出力します。この行は narrowing metadata であり、runtime call graph には含めません。 |
| React hook | 名前ベースです。JavaScript / TypeScript の function symbol name が `use[A-Z]...` に一致する場合は `hook` として再分類し、`use[A-Z]...()` call は `consumes_hook` reference として出力します。 |
| module specifier resolution | `import` symbol として収集した module specifier は、最寄りの `tsconfig.json` / `jsconfig.json`、relative `extends` chain、`compilerOptions.baseUrl` / `paths` mapping を使って解決し、解決できなければ literal specifier のまま残します。alias は実 file が存在する場合だけ project-relative path へ書き換え、TypeScript / JavaScript extension と `index.*` 候補を試します。 |

SQL 固有の symbol extraction:

| SQL surface | 動作 |
|---|---|
| MySQL backtick identifier | case-preserving symbol name として扱います。 |
| unquoted identifier | 既存の case-insensitive lookup path を通ります。 |
| MySQL `DEFINER=user@host` | `definer` symbol を出力します。 |
| PostgreSQL `RETURNS TABLE(...)` / `OUT` parameter | synthetic result column 用に function-scoped `field` symbol を出力します。`RETURNS TABLE` の列リストは上限付き・quote-aware な括弧対応で走査するため、ネストした型修飾子や未対応の後続構文が file extractor 全体を中断しません。 |

言語別対応シンボル種別:

| 言語 | 主なシンボル種別 | import / reference surface | Graph |
|---|---|---|:---:|
| Python | function、class、`@property`、PEP 695 generic function | `from` / `import`, `type NAME = ...` | yes |
| JavaScript | function、arrow、method、class expression、class-field arrow、export surface property | ESM import/re-export、CommonJS named export、tagged-template call | yes |
| TypeScript | JavaScript 相当 + typed arrow、generic method、interface、enum、type alias | ESM/TS import/re-export、CommonJS named export、declaration type reference | yes |
| C# | method、constructor、operator、conversion operator、indexer、property、field、event、delegate、class、record、struct、interface、enum、enum member、`#region` | `using`、`using` alias、`extern alias`、XML-doc `cref`、type-position reference | yes |
| Java | method、compact constructor、enum constant、class、record、interface、annotation、enum、record component | import、sealed `permits`、`module-info.java` directive | yes |
| Kotlin | function、extension function、secondary constructor、class、object、interface、enum class、enum entry、property | import、type alias、trailing-lambda call | yes |
| Go | function、method、test/benchmark/fuzz/example/init role、type alias、struct、interface | import、type-position reference、goroutine spawn、channel send/receive | yes |
| Rust | function、macro、`const`、`static`、impl/type alias、struct、union、trait、enum | `use`、turbofish、structural type-position reference | yes |
| Swift | function、class、actor、struct、protocol、enum、stored property | import、trailing-closure call | yes |
| Ruby | `def`、Rails DSL、block call、class、module、attribute | `require` | yes |
| Perl | package、subroutine、constant | `use`、`require`、`parent` / `base`、arrow method call | yes |
| C / C++ | function、macro、struct、C++ class、enum、enum class。constructor、destructor、operator、後置戻り値 function を含む、括弧の対応を考慮した C++ callable declarator | `#include`、type-position reference | yes |
| PHP | function、constant、enum case、class、interface、trait、enum | `use`、`require`、`include` | yes |
| Scala | `def`、class、object、trait、enum | import、type alias、block call | yes |
| Elixir | `def`、`defp`、module、protocol | `import`、`alias`、`use`、`require` | yes |
| Common Lisp / Racket | package/module、function/macro、class、struct、variable | S 式の call head、`#'name` function reference、`make-instance` instantiation | yes |
| Clojure / Erlang / OCaml / Raku | namespace/module、function、record/type/class、protocol/role | 上限付きの import、alias、call、type / protocol / behaviour 関係 | yes |
| SQL | procedure/function/trigger、DDL object、schema、enum type、extension | source/target dependency、procedure call、temp-object tracking | yes |
| Verilog / SystemVerilog / VHDL | module/entity/architecture、package、interface、class、function/task/process、type、signal/parameter | 構文上確認できるインスタンス化、package/import/use 関係、architecture/entity link、同一ファイル内の上限付き signal/type reference | yes |
| Shell | function declaration | command-style call、source、alias | -- |
| Terraform | variable、output、locals、resource、data、module、dotted dependency | same-file `var.*`, `local.*`, `module.*`, `data.*`, `TYPE.NAME` reference | -- |
| Protobuf / GraphQL | protobuf RPC/message/service/enum、GraphQL operation/type/interface/enum | protobuf import | -- |
| Gradle / Makefile | Gradle task/def、Makefile target と `.PHONY` metadata | Gradle plugin declaration、Makefile target の前提条件と `.PHONY` target list | -- |
| Dockerfile | named stage、base-image/stage dependency | `FROM`, `COPY --from` | -- |
| Lua / R / Haskell / F# | 言語別の function / type / module / signature | import / require / open など対応済み surface | mixed |
| VB.NET | Sub/Function、Class/Module、Structure、Interface、Enum、Property、Event | Namespace、Imports、`AddressOf`、`Handles` | yes |
| Zig / PowerShell / CSS-SCSS / Batch / Assembly / HTML | 言語別 function、label、selector、stage、Web Component、property、import | 実装済みの言語別 reference | mixed |
| XML | 上限付き element / attribute path と NuGet.config security-policy value | 実装済みの XAML reference | mixed |

C# の parameter / argument-list modifier token（`out`、`ref`、`in`、`params`、`this`、`scoped`）は、modifier 位置として解析された場合だけ除外します。後続の concrete type / generic type の `type_reference` は維持し、`out var` では modifier も暗黙の `var` も型として出力しません。contextual keyword は modifier 位置以外では合法な identifier になり得るため、global keyword blacklist にしてはいけません。

Shell と PowerShell のファイルには、ファイル全体を覆う合成 `<script>` 関数シンボルも作成される。トップレベルの call reference はこのスコープを graph container として使い、宣言済み関数内の reference はその関数 container を維持する。

Rust / TypeScript / Swift / Go / F# / Scala の type alias は `import` シンボルとして index される。F# では record は `struct`、discriminated union は `enum`、constructor 形式の `type` は `class` として扱う。

C# の `Graph = yes` は callable reference、event subscription、type-position dependency、XML-doc `cref`、enum-member reference、generic constructor / method call、pattern head、verbatim identifier を含む。`unused` は C# enum member についてはまだ狭い制限があり、該当 scope を degraded として報告する。

JavaScript / TypeScript では、reference extraction が `` gql`...` ``、`` styled.button`...` ``、`` sql`...` ``、generic 付き `` html<User>`...` `` のような tagged template literal の call site も捕捉する。member access tag は末尾 segment（`styled.button` -> `button`）に帰属する。

HDL reference extraction は意図的に構文ベースとし、公開済みの reference lookup 上限に従います。comment と string を mask し、対応するローカル宣言がなくても hierarchy/package/architecture edge を出力しますが、signal、type、function reference は現在のファイルで既知かつ適用可能な lexical/design-unit scope の symbol に限定します。generate、preprocessor macro、parameterized hierarchy、signal data flow の elaboration は行いません。full scan 成功時に `hdl_graph_contract_version` を stamp し、stamp が未設定または古い場合は graph readiness を縮退させ、通常の `cdidx index .` で未変更の Verilog、SystemVerilog、VHDL ファイルも更新してから trust を復元します。

SQL は `CREATE SCHEMA` から `namespace` シンボルも出力するが、上の要約表には namespace 専用列はない。SQL graph extraction は `FROM`、`JOIN`、`INSERT INTO`、`UPDATE`、`TRUNCATE TABLE`、`DELETE FROM`、`DELETE ... USING`、`MERGE ... USING` のような source/target 形を `reference` edge として出力し、procedure call と table-valued function 使用は `call` 経路に残す。

登録済みのシンボル抽出器がない言語もテキスト検索には利用できます。現在の symbol / reference / graph capability flag は `languages --json` で確認してください。

正規表現ベースの抽出は意図的にシンプルです。AST精度よりも速度とポータビリティを優先しています。

### 行番号契約

`files.lines`、`symbols.line`、シンボル範囲フィールド、`symbol_references.line` は、改行正規化と行頭不可視文字の除去を行う前のデコード済みファイル内容に対する 1 始まりの物理ソース行番号です。CRLF、LF、単独 CR はそれぞれ 1 つの改行区切りとして数え、末尾の改行区切りは追加の空行を作りません。抽出器は LF 正規化済みコンテンツで動作してもよいですが、永続化する symbol anchor 行（line、start_line）は元ファイルの物理行範囲内でなければなりません。end と本体範囲は末尾の空範囲を表すため EOF の 1 行後を指す場合があります。インデックス作成は symbol range を書き込み前に検証し、正規化済み checksum が一致していても保存済み行数が現在のファイルと異なる場合は unchanged-file reuse を拒否します。

## インクリメンタルインデックス

デフォルトでは、cdidxは各ファイルの`modified`タイムスタンプ（UTC）をデータベースの値と比較します。変更がなければファイルは完全にスキップされます。

ファイルが再インデックスされる場合:
1. そのファイルの古いチャンクとシンボルを削除（FTSエントリはトリガーで自動クリーンアップ）
2. ファイルレコードをUPSERT（`INSERT ... ON CONFLICT DO UPDATE`、行IDを保持）
3. 新しいチャンクとシンボルを挿入（FTSエントリはトリガーで自動反映）

### 古いファイルのパージ

インデックス開始前に、cdidxはデータベースの全ファイルパスをクエリし、ファイルシステムと照合します。ディスク上に存在しなくなったファイル（ブランチ切り替えや削除後など）はチャンクやシンボルとともに削除されます。

| 状況 | 動作 |
|---|---|
| ブランチ間でファイル未変更 | スキップ（即時） |
| ファイル内容が変更 | 再インデックス |
| checkout後にファイル削除 | DBからパージ |
| checkout後にファイル追加 | 新規インデックス |

```mermaid
flowchart LR
    A[git checkout branch-B] --> B[cdidx .]
    B --> C{ファイルごとの判定}
    C -->|未変更| D[スキップ]
    C -->|変更あり| E[再インデックス]
    C -->|削除済み| F[DBからパージ]
    C -->|新規| G[新規インデックス]
    D & E & F & G --> H[DB = branch-Bと同期完了]
```

### 部分更新モード

`--commits`、`--changed-between`、`--files` で、プロジェクト全体をスキャンせずに特定ファイルのみ更新できます:

```bash
cdidx ./myproject --commits abc123 def456   # これらのコミットの変更ファイル
cdidx ./myproject --changed-between main feature
                                             # 2つのref間の変更ファイル
cdidx ./myproject --files src/app.cs        # 特定ファイルのみ
```

`--commits` は `git diff-tree --no-commit-id -r --name-only` で変更ファイルパスを解決します。
`--changed-between` は `git diff --name-status -M <old-ref> <new-ref>` を使い、rename の旧パスと新パスを両方含めるため、古い indexed path も purge できます。

watch batch は top-level の cancellation token を使ってこの部分更新 runner を再利用する。Ctrl-C、SIGTERM、埋め込み host token は初回 scan 後も有効なので、sub-run の完了を待たず、idle watch wait、実行中 extraction、FTS recovery / rebuild / optimization、SQLite planner maintenance を中断できる。watch は第2の console handler を登録せず、top-level の「最初の Ctrl-C は協調的 cancellation、2 回目は強制終了」という契約を維持する。cancel された bulk FTS completion は同期 trigger を復元し、transaction rollback で marker が戻らなかった場合は owner 非依存の recovery marker を残す。長寿命 MCP write context は request ごとの token を登録し、cancel 後の dispose-time planner maintenance を抑止する。sub-run JSON は `CommandOutputWriter` の async-local scope から watch capture writer へ送られ、watch loop は `Console.Out` を置き換えないため、他の command や埋め込み host は自身の stdout を維持できる。

source membership は `FileIndexer` で共有し、full scan、workspace freshness check、watch のすべてで `.cdidx` namespace を除外する。watch は source filter の適用前に ignore file と `.cdidx/patterns/**` / `.cdidx/plugins/**` を分類するため、これらの非 source 入力は debounce 付き reconciliation event として保持し、通常の `.cdidx` sidecar は除外したままにする。subdirectory watch は repository rule root までの各 ancestor directory に non-recursive かつ ignore-file-only の watcher を追加する。生成された `--files` sub-run は extractor 入力を認識し、process registry の generation を refresh してから unchanged-file reuse を無効にした full scan へ fallback し、保持対象の全 source row を新しい generation で抽出し直す。refresh は埋め込み host が明示的に登録した extractor を維持しつつ、file から発見した以前の workspace plugin / pattern を unload して現在の generation を読み込むため、編集や削除後に extension membership や persisted row が stale のまま残らない。

watcher は startup reconciliation scan より先に有効化する。`FileChangeBatcher.TryDrainImmediately` は通常の debounce interval を待たずに buffer 済み startup generation を閉じ、その path を `watching` event より前に適用する一方、snapshot 後に到着した event は通常の live update として queue に残す。すべての startup reconciliation sub-run が成功した場合だけ `watching` を出力し、失敗した generation は batch を捨てて ready を宣言せず non-zero exit を返す。この generation boundary により、初回 scan と subscribe の間の gap と、変更が連続する workspace で ready が無期限に遅れる問題の両方を防ぐ。

FTS5 を変更する差分更新は `codeindex_meta.fts_incremental_writes_since_merge` と `codeindex_meta.fts_incremental_writes_since_optimize` の両方を増やします。merge counter が 25 write に達すると、index runner は `INSERT INTO fts_chunks(fts_chunks, rank) VALUES('merge', -1000)` を実行します。1,000 page は最小 work target であり、SQLite が完全な segment 単位で処理するため実際の page 数は target を超える場合があります。merge では専用 counter のみをリセットし、optimize counter は `cdidx optimize --dry-run` の推奨判定用に累積を続けます。CLI の full scan と MCP refresh は dirty source byte が既知 workspace source byte の 5 分の 3 以上なら、trigger を停止した bulk rewrite、FTS rebuild、full optimize に切り替えます。fresh index と明示的 rebuild は常にこの経路を使います。dirty byte は今回書き換える各 file の current size と永続化済み size の大きい方に、rename の旧 path を含む削除予定 indexed row の永続化済み byte size を加算します。比較対象の total には読み取り可能と判明した current workspace byte、削除予定 byte、および縮小した書き換え file の永続化済み size が current size を上回る差分を含め、更新前 footprint と同じ基準で比較します。scan error、永続化 size の不正値、または byte 加算 overflow がある場合は estimate を incomplete として保守的に trigger 同期を維持します。stale file ID は FTS policy の選択前に mutation なしで plan し、選択した bulk guard の内側で削除します。plan の ID は昇順に保つため、C# static-interface workspace prepass は削除 set を複製せず二分探索で purge 予定 row を除外します。一方、reusable-stat snapshot は eligibility subquery より前に ID を index 付き一時 SQL filter へ読み込みます。これにより MCP の plan 後から scan までに同じ path が再出現しても、この run が purge する旧 row を reuse せず、現存 file を再indexします。purge 前の contract 存在 query は plan が非空の場合だけ実行し、削除によって obsolete になる implicit implementation reference を除くための C# 再抽出判定に使用します。そのような contract が存在する場合、MCP は最初の mutation で C# symbol-name contract も invalid にします。purge 後の scan error で implementer を未処理のまま残しても、次の clean run は stat reuse を無効化して implicit reference を修復し、その後にだけ contract を再 stamp します。batch delete transaction は処理中も cancellation を確認し、commit 前の cancellation では全削除を rollback します。bulk purge の commit 後に cancellation された場合は、guard の abandon 処理が残存 chunk から FTS を rebuild し、trigger を復元してから run を終了します。full scan と MCP refresh は、current target に使われない非 purge indexed row の数が current target 数を上回る場合だけ、current-target path set で reusable-row snapshot を filter します。それ以外は全 current path を第2の大きな set に複製せず、昇順 ID 除外を使います。scoped `--files` / `--commits` refresh は trigger 同期と incremental merge maintenance を維持します。`cdidx optimize --db <path>` と `cdidx index <projectPath> --optimize` は引き続き明示的 full optimize を実行し、両 counter をリセットして `fts_last_optimized_at` を記録します。大きな index では短時間 writer lock を保持する可能性があります。

C# の purge 前 contract preflight は、昇順の削除予定 ID を SQLite parameter 最大 500 件の uncached batch で調べ、managed 側の keyword-boundary 判定を通してから再抽出を強制する。以前の symbol-kind policy が contract member kind を落とし得る場合は interface 宣言を保守的 evidence とする。scoped update では false の source-evidence marker を authoritative とし、repository 全体の exact-member scan を hot path に戻さない。transition path の probe は正確な `files(path)` row から開始して `symbols(file_id, kind)` へ join し、最大 500 path の cancellation-aware かつ cache-neutral な batch で実行するため、通常の interface と LIKE だけ一致する decoy で再利用可能な C# row を無効化しない。永続 workspace の materialize も `files(lang)` から開始して member 候補 kind の `symbols(file_id, kind)` だけを probe し、managed 側で signature を厳密検証してから、保持 contract の container 名に一致する interface 宣言だけを bounded かつ cache-neutral な `symbols(name)` batch で取得する。negative または LIKE decoy だけの読込は第1段階で終了し、repository 内の通常 interface を materialize しない。tri-state の source-evidence marker は post-extraction hook、kind filter、row cap より前の built-in C# symbol を観測するため、hook で隠された contract も安全な implicit-reference refresh を強制できる。CLI full scan と MCP は、以前の index が明示的に complete、GraphReady 済みで、symbols-only omission、filter/version/root/hotspot の contract が互換、永続 C# path の language transition がなく、全 C# target が stat-reusable な場合だけ authoritative な true / false の source evidence を source 再読込なしで維持する。この厳密な known-evidence no-op は永続 C# symbol の load と workspace lookup 構築も省略し、legacy completeness/readiness metadata が欠ける場合は保守的に raw workspace prepass へ戻る。

full scan は HEAD だけに結び付いた directory checkpoint を作成も参照もしない。in-place 変更、untracked file、設定入力が run 間で安定していたことをその形式では証明できないためであり、最初のimmutable scan-input barrier後はsuccess / partialのどちらでも旧checkpoint fileをparseせず削除する。列挙対象の非 ignore directory は、membership 設定の probe と再帰 listing より前に更新時刻の基準を1回だけ記録する。既存の ignore、language-map、pattern、submodule 入力は内容・metadata・file identityにも結び付け、project外のmissing inputとnested repository markerは明示的な状態として保持する。consumer はこの単一scan-input snapshotを、schema initialization後の最初のdomain/index-state mutation直前とreadiness stamp直前の2回だけ検証し、旧per-directory after-traversal stat mapは保持しない。C# source fileのstat snapshotはworkspace materializeと最終readinessを独立に固定する。first barrierでinputが不安定なら、以前のrow・trust・evidence・purge状態・FTS recovery状態を変更せず終了する。mutation開始後のdriftはrunをpartial、source evidenceをunknownとしてclean retryへ委ねる。一方、workspaceがread-onlyな段階で検出したC# file変更はcomplete raw prepass 1回と全C# refreshへ昇格できる。以前のevidenceがpositiveまたはunknownの状態でdiscoveryがfatal errorになった場合もC# writeとstale cleanupを延期し、immutable prepass後に初めてsource contractを観測した場合はmarkerをunknownのままにして次のclean runで未変更implementerも修復する。

scoped update は caller target を unique checksum と同一 directory/stem で group 化して C# 限定候補を key ごとに1回だけ調べ、one-sided rename と C# のみに絞った `--changed-between` cleanup の正確な昇順 stale ID を workspace prepass 前に plan し、immutable plan の apply 直前にその ID を primary key で最大 500 件ずつ再読込する。予定した database path が filesystem に再出現していれば C# cleanup と write を延期し、run を partial、source evidence を unknown として clean retry で収束させる。例外は、exact retained targetではないliveな旧spellingのfilesystem file identityが、同じcase-fold bucketの保持対象caller targetと一致する場合だけである。binary/oversized C# skip record は nested transaction 内で cleanup/upsert 前に fresh stat を再検証し、drift 時は row 更新と batch marker の両方を rollback する。cleanup planningはindexedな `files(path COLLATE NOCASE)` lookupとmanaged foldingをboundedなalias候補filterだけに使う。live candidateがexistence guardを迂回するにはexact spellingが保持対象ではなく、同じcase-fold bucketにidentityが含まれることを必須とするため、case-sensitiveまたはmixed-policy directory tree上のdistinctなleaf-case・ancestor-case・Unicode-folded pathとcross-target hardlinkを保持する。Gitのcommit/range discoveryはNUL区切りname-statusを要求してnon-ASCII・tab・改行pathを保持し、exactな永続rename sourceは遅延materializeしたidentity bucketで照合するため、live fileのhashingと病的fold variantの二乗処理を避ける。以前の authoritative evidence が false の `--changed-between` run は従来の missing-file reconciliation を維持するが、その run で contract を新規発見した場合や C# evidence が incomplete になった場合は late reconciliation から C# row を除外する。以前の authoritative evidence が positive または unknown なら workspace 前の C# 限定 stale plan だけを使い、scoped delta を無関係な全言語 walk へ拡大しない。未変更の caller-selected path は stat 一致の indexed point lookup で永続 checksum を再利用し、追加の streaming checksum read は新規または stat-changed の rename 候補だけに行うため、処理量は full index ではなく delta に比例する。後続の live cleanup が未計画 C# row を検出した場合は authoritative result を暗黙に commit せず、workspace drift として source evidence を unknown に保つ。

expanded C# target は exact retained-path の再出現 guard より前に repository の `IndexPath` へ正規化します。contract expansion が追加した absolute path も永続 cleanup-plan path と同じ namespace で比較されるため、再出現した exact path を extraction 成功前に削除可能な case-fold alias と誤認しません。

file purge の transaction gate 取得は、delete batch 内だけでなく待機中も caller cancellation token を監視します。別 writer の後ろで待つ purge は file、chunk、FTS row を削除せず速やかに終了します。

bulk guard の setup と abandon cleanup はどちらも failure 後に recovery 可能な状態を維持します。process owner 付き marker の確立後に trigger suspend が失敗した場合（partial trigger drop を含む）、または後続の trigger 復元・FTS rebuild が例外を投げた場合、writer は元の例外を再送出する前に marker を owner 非依存の `true` へ best-effort で置き換えます。これにより同一 process の後続 request が同期 trigger をすべて復元し、検索可能な FTS state を rebuild して marker を消去できます。marker write の二次失敗で元の cleanup error を隠しません。

process owner 付き FTS state の primary marker は、旧 reader でも解釈できる正確な `pid:<pid>` 形式を維持します。別の owner-generation value は同じ PID を反復し、platform から取得できる process start-time generation、または process ごとの token fallback を記録します。永続 insert/update/delete cleanup trigger は旧 writer が primary marker だけを変更したときにこの関連付けを消去します。新 reader は両 value と trigger set の状態を単一 SQLite snapshot で読み、cleanup trigger 3本が揃い、generation 側 PID が primary と一致する場合だけ generation を信頼します。欠落・不正・不一致・確認不能な foreign generation は保守的な PID-only 判定へ戻します。bulk guard は trigger suspend 後の writer durable-commit generation も記録します。SQLite commit を post-commit bookkeeping と passive WAL checkpoint より先に公開するため、その後の例外時に caller の mutation flag が未更新でも `Complete` または abandon が FTS を rebuild します。transaction scope は post-commit action の完了または失敗確定まで finalizing state を維持し、並行 `Dispose` は diagnostic contention timeout を超えても待機して、finalizer が使用中の transaction resource や writer gate を解放しません。rollback も terminal state の公開前に完了済み SQLite transaction を detach し、gate の owner 交代後に旧 finalizer が後続 transaction の cached reference を消す race を防ぎます。

VB.NET のコンテナ系パターンは `RegexOptions.IgnoreCase` と `VisualBasicEnd` ベースの範囲追跡を使うため、`Partial` の大小文字差や複数ファイルにまたがる型ファミリーでも、安定した定義範囲と `hotspots` 集計用メタデータを維持できる。

## AI連携

AI エージェント向け検索ルールのテンプレートについては、ユーザーガイドの[AI連携](USER_GUIDE.md#ai連携)セクションを参照してください。

### 出力形式

bounded projection field は `ProjectionFieldRegistry` だけで定義します。
実行時検証、`--fields list` による発見、compact 既定値、alias 解決、command
help はすべてこのレジストリを参照します。field 名は大文字・小文字を区別し、未知の
値で JSON が要求されている場合は versioned `E010_USAGE_ERROR` command error を
返します。発見処理は query や database access より先に実行します。

| output mode | 契約 |
|---|---|
| human-readable default | query command（`search`、`definition`、`references`、`callers`、`callees`、`symbols`、`files`、`excerpt`、`map`、`inspect`、`outline`、`suggestions`）は既定で**人間向け出力**です。 |
| `--json` | JSON lines output（1 行 1 JSON object）に切り替えます。AI agent が容易に parse できるよう設計されています。 |
| `definition --json` の未検出 | 既定 format の definition lookup で一致する symbol がない場合、`--body` の有無にかかわらず、共通の versioned `E018_QUERY_NOT_FOUND` command-error object を出力して終了コード `2` を返します。空の stdout のまま成功することはありません。bounded-envelope control の使用時は object を location row として projection せず `metadata.error` に移し、`results` は空のままにします。この object は `--max-json-bytes` に対して事前検査され、収まらない上限では oversized stdout を出さず usage error を返します。`--count` は引き続き構造化された 0 件 object を返し、明示的な location format も既存の format 固有の empty-result output を維持します。 |
| raw discovery JSON shape | `symbols` と `files` は、array、NDJSON、envelope の各出力で同じ DTO 経路から result row を構築します。そのため `symbols --json=array` も NDJSON と同様に `exact_index_available` を保持します。結果件数や `--max-json-bytes` の有無にかかわらず選択した flat shape を維持し、0 件の NDJSON は空 stream、`--json=array` は常に array となり、byte cap 到達時は top-level type を変えずに末尾の完全な row を省略します。bounded projection は row を `results`、pagination fact を `metadata`、exact-query readiness を `metadata.response_context` に保持し、result row を response context として再利用しません。truncation / freshness metadata も結果と一緒に必要な場合は `--format compact` または `--json-envelope` を使用します。 |
| generated-code filtering metadata | DB-backed discovery の `query_context` は常に `include_generated`、`generated_code_policy`、`generated_file_filter_available` を返します。`files --count --json` と `issue-drafts` を含むすべての JSON `map` summary は、`generated_file_count_excluded` と `generated_file_count_excluded_authoritative` も返します。generated file を含める場合、除外数は `0` です。`files.generated` が無い legacy DB で filter が要求された場合、未実行の filter を実行済みと誤認させないよう、policy は `unavailable`、count は `null`、authoritative / available flag は `false` になります。明示的な `--include-generated` は `include` のままで、authoritative な除外数 `0` を返します。byte cap の有無にかかわらず、raw discovery array は query result row が 0 件でも SQLite trust diagnostics を維持します。 |
| map の scope、depth、freshness | `map --depth <n>` は path、language、test、generated-code、除外条件を適用してから、指定した prefix depth で module を集計します。scope を絞った map output からは workspace 全体向けの decomposition plan を除外します。workspace HEAD metadata は map と同じ SQLite snapshot から 1 query で読み、`head_freshness` に `scope=workspace`、現在の成功 index stamp なら `indexed_head_source=latest_index`（fallback の場合だけ `legacy_full_scan`）、互換用 stamp は別名の `legacy_full_scan_head` として明示します。`issue-drafts` は scope 内の全 file を閾値評価するため、`candidate_source=evaluated_scoped_candidates`、candidate 件数、group 合計、省略数、`truncation.issue_draft_candidates` は candidate 基準になります。`truncation.largest_files` は明示的な互換 alias としてのみ残します。 |
| `test-extractor` JSON | 機械可読な `test-extractor` success は versioned `{"api_version":"1","symbols":[...]}` envelope を使い、内側の symbol object は既存の property 名を維持します。`--json` failure は共通の versioned command-error 契約を使います。 |
| compact location envelope | CLI の `--format compact` location output は、`api_version`、返却 `count`、limit 到達を基準にした保守的な `truncated` / `truncation` metadata、適用済み `query_context`、軽量な `results` row を持つ versioned envelope です。 |
| grouped search の総数 | `search --format grouped` の `total_matches` / `matched_count`、`total_groups`、`total_files` は、表示 page ではなく上限適用前の query 全体から算出します。`grouped_match_count` は返却 group に渡した row 数、`emitted_match_count` は file ごとの上限適用後に残った row 数を表し、`omitted_match_count`、`truncated`、`has_more`、`continuation_action` が未完了出力を示します。 |
| 高ボリューム応答の bounded 契約 | `search`、`definition`、`find`、`status`、`hotspots`、`references`、`callers`、`callees`、`symbols`、`files`、`languages`、`impact`、`map` は、それぞれの schema が公開する共通 bounded-response control に対応します。新しく発行する opaque な `--cursor <response:v2:...>` は offset を command / query / filter と index generation に束縛し、移行用に legacy の `response:v1:<offset>:<fingerprint>` も受理します。選択条件または generation を変えて再利用すると restart-required の案内付きで失敗します。`search --format compact`、`symbols --format compact`、`files --format compact` は bounded 契約を自動選択し、`search --json=array --json-envelope` は opt-in の array envelope、`languages --json` は paging または `--max-json-bytes` 指定時に同じ契約を使います。既存 compact の root と location row は維持したまま共通 metadata を追加します。metadata は `returned_count`、取得可能な場合は authoritative な `total_count`、`omitted_count`、`remaining_count`、`cursor_offset`、`page_limit`、`has_more`、`next_cursor`、`result_stable_at`、`pagination_window_limit`、`pagination_window_exhausted` を返します。safety window は 10,000 row で、上限到達時は次の request が拒否する cursor を返さず `next_cursor` を抑止します。pageable command は `offset + limit` 件を serialize せず、cursor offset を database / scan layer へ渡します。`find --all` の partial scan cursor は次の path / line を保持し、再利用時は最後に scan した line の次から継続します。`hotspots` と `impact` は active な主要 nested collection を `results` としてページングし、`metadata.primary_collection` でその名前を示し、scalar / container evidence は `metadata.response_context` に保持します。`callers.path,callers.depth` のような dotted field で collection と row field を同時に選べます。`--max-json-bytes` は最後の改行を含み、完全な envelope が収まるまで末尾の完全な row を省略します。`definition` は既定で metadata-only のままで、明示的な `--body` は `body`、`body_content`、`all` で保持し、それ以外の projection では materialize 前に抑止します。`map --sections` は section-level projection として残り、dotted な bounded field は選択した array section を section 固有の総件数付きでページングし、scalar projection は不要な ranking array を構築しません。 |
| bounded 応答の edge case | `impact` は選択された nested collection だけに cursor offset を適用するため、definition page の重複や caller / fallback mode の変化を防ぎます。通常の `map --compact` は既存の section array と truncation payload を維持し、collection projection が `--summary-only` または除外する `--sections` filter で失われる組み合わせは拒否します。明示的な definition body field は compact default より優先します。profile / verbose record は `metadata.stream_control_records` へ移し、parser / capture failure の error envelope は active な hard byte cap に収まる場合だけ出力します。 |
| `--count --json` envelope | `search`、`definition`、`references`、`callers`、`callees`、`symbols`、`files`、`find`、`impact`、`unused` の count-only JSON は単一の自動化向け object です。常に `count`、適用済み `query_context`、freshness metadata（`indexed_file_count`、`indexed_at`、`freshness_available`）、trust flag の `degraded` / `authoritative_count` を含みます。matched-file total を持つ command は `files` と古い互換 alias の `file_count` も含みます。`file_count` は `files` と同じ値を持つ互換用 field として残り、少なくとも次の major release までは削除予定はありません。`unused --count --json` は `returned_bucket_counts`、`returned_contract_domain_counts`、`summary.by_bucket` / `summary.by_confidence` / `summary.by_contract_domain` も含みます。`authoritative_count=false` は readiness または graph/exact trust signal により count が authoritative ではないことを示し、freshness field は count に使った index snapshot を説明します。 |
| search row selection | row を返す plain search / recipe path は `ApplySearchOutputSelection` を共有し、`--first-per-file` と固定 seed の決定的な `--sample` を、有効な query ごとの limit / 残り total limit より先に適用します。sample 用 fetch envelope は少なくとも要求 sample 数を基準に sizing します。aggregate / compact の query DTO、plain compact root、run summary、issue-draft の source DTO、NDJSON terminal、bounded array envelope の stream terminal は `source_total`、`selected_total`、`returned`、`selector_omitted_count`、`limit_omitted_count` を公開し、`source_total_authoritative` / `source_total_lower_bound` で完全な population と bounded な観測を区別します。guard filter、origin / facet の後段 filter、candidate window の枯渇、recipe の file-reject 後段 filter は lower-bound authority にします。適用順の `selectors` entry は各段階の input / output / omission count と sample の size / mode / seed を保持し、nullable な `selection_reason` / `selection_omitted_count` は互換用 summary として維持します。bounded plain-search selection は一度だけ計算し、その selected page を compact / envelope serialize で再利用します。search の `query_context.row_selectors` は適用済み selector 設定を記録します。selection だけによる省略は matched / omitted の lower bound を更新しますが、`truncated`、`has_more`、`next_cursor` は設定しません。後続の limit が選択済み row を truncate する場合、limit truncation は表示しますが raw database cursor は selector state を保持できないため `next_cursor` を抑止し、同じ理由で selector と受け取った `--cursor` の併用も拒否します。compact / issue-draft の生成 replay command は selector を保持します。count、aggregation、named-query、recipe-list、results-only、metadata を持たない array、非対応 formatted、summary-only compact の shape は `--first-per-file` / `--sample` を拒否し、すべての recipe shape は grouped 専用の `--per-file-limit` を拒否します。 |
| search selection の edge case | issue-draft の root は query ごとの `selection_accounting` を独立して保持するため、draft が 0 件の場合や total limit を使い切った query も accounting を失いません。byte 上限付き compact / array envelope は `returned` を実際の出力 row 数へ更新し、論理的な `limit_omitted_count` を保持します。hard cap による省略は `metadata.byte_limit_omitted_count` で別に報告します。 |
| ad-hoc search SARIF | `search <query> --format sarif` は completion metadata を SARIF の各 run に格納します。run と単一の `queries[]` summary は `source_result_count`、`source_result_count_authoritative`、出力済み `result_count`、適用された `limit_per_query` / `result_limit`、保守的な `minimum_omitted_result_count`、`truncated` state を返します。source / emitted count は exact search の occurrence 展開を含む最終的な SARIF result / location 単位を使用します。guard 付き search は completion の再計数で失敗せず bounded candidate budget を維持し、source count を明示的に non-authoritative な lower bound として返して truncation state を保守的に保ちます。facet filter 付き exact search は表示用 candidate window ではなく exhaustive な source count を使います。ad-hoc search は継続 cursor を公開しないため、`cursoring_available` は `false`、`next_cursor` は null となり、shell quote 済みの `replay_command` が option のような query と有効な search control を保持します。completion vocabulary は意図的に recipe SARIF と共通化し、空 run も count が 0 の同じ field を保持します。 |
| ad-hoc issue-draft selection | `search --format issue-drafts` は filter 済みの ad-hoc 母集団全体を読み、`--first-per-file`、決定的な `--sample`、`min(--limit, --total-limit)` の順に適用します。guard 付き検索は有限の candidate inspection 契約を維持し、`source_total_count` を省略し、観測下限を `source_minimum_count`、非 authoritative 状態を `source_total_count_authoritative=false`、bounded fetch を `source_fetch_limit` で報告し、母集団が未完了であることを `truncated=true` で保持します。既存の `result_count`、`result_limit`、`omitted_count`、`truncated` field は返却 selection を正確に表し、additive な `source_total_count`、`returned_count`、`limit_per_query`、`total_limit`、`first_per_file`、`sample` field により適用済み契約を監査できます。replay command は正規化済み parse option から serialize し、POSIX-safe な単一引用符 escape を使い、raw / exact / prefix mode、path / language / facet / guard filter、selection control、evidence formatting、duplicate preflight、issue hint を維持します。 |
| Recipe SARIF | `search --recipe <name> --format sarif` は、上限付き recipe result ごとに result を1件出力します。rule ID は `recipe/query` を使い、標準の `fingerprints.cdidx/v1` は正規化済み source location から導出します。result properties は recipe/query identity、severity、confidence、query ごとの truncation を保持し、run properties は scope、適用済み result limit、集計 count、保守的な omitted-result metadata を保持します。SARIF の上限には `--limit` / `--total-limit` を使い、`--sample`、`--first-per-file`、`--per-file-limit` のような row selector は黙って無視せず拒否します。recipe severity は `critical` / `high` を `error`、`medium` を `warning`、`low` / `info` を `note` に対応付けます。 |
| Recipe classifier output | recipe classifier が hit を分類できる場合、recipe run JSON は個別の `CompactSearchResult` row に `audit_classifications` を追加することがあり、分類済み row がある query / count payload は `classifier_counts` を追加することがあります。これらは additive field です。raw search query を変えずに、DTO / result-wrapper の `.Result` property と Task / ValueTask の blocking wait などの triage domain を分離するために使います。 |
| NDJSON terminal record | `search`、`symbols`、`files` の既定 NDJSON は result row の後に最後の `terminal_record` を 1 件追加します。`search` は 0 件応答にも終端を出力しますが、raw `symbols` / `files` の 0 件 NDJSON は空のままです。recipe / audit search の row stream も同じ writer を使います。終端は返却件数と観測済み総件数、`total_count_authoritative` / `total_count_lower_bound`、selection または中断理由、適用上限、省略行数、復旧案内を報告します。`--max-json-bytes` は改行と終端レコードを含む stdout stream 全体を対象にし、追加 selector-accounting field が原因で終端が収まらない場合は、終端自体を不可能と判定する前にそれらの任意 field を省略します。それでも終端が収まらない cap は stdout 出力前に失敗します。上限付き出力は `--profile`、`--verbose`、`--json-envelope` を拒否します。byte cap による部分出力は、`--allow-partial` で終了コード `0` を明示許可しない限り `CommandExitCodes.PartialResult`（`11`）を返します。`--results-only` はこれらの NDJSON row stream から終端レコードを明示的に除外するための option であり、array / compact / summary / count 出力との組み合わせは拒否されます。 |
| `outline` / `unused` cursor の束縛 | `outline --json` は bounded な機械向け出力として `--kind <kind[,kind]>`、`--limit` / `--top`、opaque な `--cursor <next_cursor>`、`--outline-fields <csv>` を受け付けます。制御付き outline 応答は通常の envelope を維持し、`total_symbol_count`、`returned_symbol_count`、`cursor_offset`、`next_cursor`、`has_more`、`result_stable_at` を追加し、該当時は `kind_filter` と `selected_fields` も返します。`outline` と `unused` の cursor は offset を正規化済み path/scope、filter、ordering、index generation に束縛するため、条件変更後または index 更新後の再利用は restart-required の明示案内付きで失敗します。移行用に legacy の `outline:<offset>` / `unused:<offset>` 入力は受理しますが、新しく出力する cursor はすべて opaque かつ束縛済みです。 |
| `hotspots --json` grouping semantics | `hotspots` と MCP `symbol_hotspots` は `grouped_by`、`grouping_unit`、`count_kind`、`limit_applies_to`、`score_fields`、`ranking_fields` と、対応する `query_context` field を返します。`--limit` は返却される symbol、file、name/kind group、SQL statement に適用されます。`--count` は `--limit` を無視し、total group 数を返します。明示的な `statement` grouping は SQL 専用です（`--lang sql` / `lang: "sql"`）。 |
| `--json-envelope` 対象 command | `search`、`definition`、`references`、`callers`、`callees`、`symbols`、`files`、`find`、`excerpt`、`map`、`inspect`、`outline`、`status`、`validate`、`languages`、`impact`、`deps`、`unused`、`hotspots`。 |
| `--json-envelope` shape | per-line `--json` stream を単一の `{"metadata": {...}, "results": [...]}` document に包みます。stream 終端レコードは `results` から除外して `metadata.stream_terminal` に保持し、0 件時の prelude / control record は `metadata.stream_control_records` に保持するため、`result_count` は result row だけを数えます。`find --all --count` object は count result であると同時に終端 scan metadata でもあるため、`results` に残しつつ `metadata.stream_terminal` にも複製します。`metadata` は `api_version`、`command`、`cdidx_version`、`elapsed_ms`、`db_path`、`exit_code`、該当時は `query_normalized` と `indexed_at_head_sha` も持ちます。`indexed_at_head_sha` は full、`--files`、`--commits`、`--changed-between` refresh 後に status / MCP output が使う永続化済みの最新成功 `indexed_head_sha` に対応します。失敗または rollback された refresh では進まず、この key を持たない legacy DB では full-scan 限定 `indexed_head_commit` に fallback します。上記の bounded 高ボリューム command は最終 document を測定することで `--json-envelope --max-json-bytes` を許可し、それ以外の envelope / byte-cap 組み合わせは引き続き拒否します。 |
| indexed HEAD envelope snapshot | `indexed_head_sha` row が存在する場合は値が NULL でも authoritative とし、Git HEAD を解決できない current-format database では legacy baseline に fallback せず `indexed_at_head_sha` を省略します。通常および bounded envelope は解決済みの値を `ResponseSnapshot` に保持し、inner query 後に generation を再検証して serialization 中も同じ snapshot を再利用します。generation が変わった場合は不整合な row を返さず再実行案内を返すため、bounded metadata、projection 済み row、`result_stable_at`、cursor も同じ database generation に固定されます。 |
| envelope migration | `--json-envelope` は `--json` を imply するため、caller は両方を指定する必要がありません。既定 output は 1 release の間 legacy NDJSON / array form のままです。次の major release では envelope が既定になり、flat form は `--json-flat` による opt-in になります。 |
| `find --all` scan summary | `find` は repeatable な `--path <glob>` か明示的な `--all` のどちらかを要求し、`--all` と `--path` は併用できません。3 文字以上の安全な大文字小文字を区別しない ASCII literal は external-content trigram FTS index で file を選び、選択した全 file に既存の行 matcher を再適用します。regex、`--exact` normalization、短い literal、非 ASCII literal、trigram table のない旧 database、trigram 同期 trigger の欠落、FTS bulk-load による再構築中は上限付き line-scan fallback を使い、未対応の tokenization、normalization、古い index 状態による false negative を防ぎます。writable initialization は同期 trigger が 1 つでも欠けた既存 trigram table を再構築し、旧 writer の rebuild 後に残った artifact も修復します。JSON と human summary は `search_strategy` と任意の `search_fallback_reason` を返します。`candidate_files` は scope 内の総 file 数を維持し、`files_scanned` / `lines_scanned` は index 適用後の検証量を数えます。既定 JSON row は `scan_complete`、`authoritative_rows`、返却 / 走査件数、有効な cap、切り詰め / continuation field、復旧案内を持つ終端レコードで終了します。count JSON は単一 object に同じ終端 scan 状態を持ち、`authoritative_count` を使います。終端 metadata を表現できない JSON array、compact、CSV/TSV、LSP、quickfix、SARIF は `--all` との組み合わせを拒否し、競合する JSON / text flag は option 順序にかかわらず拒否するか NDJSON に正規化します。candidate-file または line-scan による切り詰めは、`--allow-partial` で `0` を opt-in しない限り partial-result 終了コード `11` を返します。通常の result limit による早期停止は成功のままですが、`scan_complete=false` と `result_limit_reached=true` を設定します。human stderr summary は strategy、有効な cap、scan / authority 状態、continuation action、復旧案内を含み、同じ partial exit semantics を使います。 |

#### JSON 出力 API バージョン契約

| 契約領域 | rule |
|---|---|
| `api_version` を持つ DTO | CLI/MCP の top-level JSON success / failure DTO はすべて `JsonOutputContract.ApiVersion` から stamp された `api_version` string field を返します。primary query DTO には `StatusResult`、`RepoMapResult`、`SymbolAnalysisResult`、`ImpactAnalysisResult`、`OutlineResult`、`FileExcerptResult`、`CompactSearchResult`、`SymbolResult`、`DefinitionResult`、`UnusedSymbolResult`、`ReferenceResult`、`CallerResult`、`CalleeResult`、`FileResult`、`FileFindResult` が含まれます。監査済み utility DTO は共通の `IVersionedJsonResult` 契約を実装し、command error、update / upgrade check、database restore / backup maintenance、diff result、hook、workspace / config、language / version inventory、test-extractor result、index run / watch result を含みます。 |
| envelope metadata | `--json-envelope` の `metadata` block にも同じ `api_version` value が出ます。 |
| binary version との分離 | `api_version` は JSON output contract の version であり、`version.json` や `cdidx --version` で公開している cdidx binary version とは別物です。 |
| bump 条件 | `JsonOutputContract.ApiVersion` を bump するのは**破壊的**な shape 変更（既存 field の rename / remove / type 変更）に限定します。追加（任意の新 field、新 readiness flag、新 enum value など）では据え置くため、旧 consumer も payload を引き続き parse できます。strict な downstream は major 値で pin し、変化時は graceful に degrade してください。Issue #1555。 |

`status --json` trust contract:

| field group | fields |
|---|---|
| readiness / graph trust | `fold_ready`, `fold_ready_reason`, `graph_table_available`, `graph_data_current`, `reference_extraction_limits`, `reference_graph_complete`, `reference_graph_incomplete_reasons`, `reference_extraction_cap_hits`, `index_complete`, `index_incomplete_reasons`, `issues_table_available`, `file_issues_data_current`, `migration_in_progress`, `sql_graph_contract_ready`, `sql_graph_contract_degraded_reason`, `hotspot_family_ready`, `hotspot_family_degraded_reason`, `language_readiness`, `csharp_symbol_name_ready`, `csharp_metadata_target_ready`, `csharp_metadata_target_degraded_reason`。 |
| workspace / HEAD freshness | `indexed_head_commit`, `worktree_head_changed`, `indexed_head_sha`, `indexed_head_branch`, `indexed_head_timestamp`, `commits_ahead_of_indexed_head`, `head_freshness`。 |
| version / forward compatibility | `index_writer_version`, `index_newer_than_reader`, `index_newer_than_reader_reason`。 |
| unknown-extension / runtime diagnostics | `unknown_extension_file_count`, `unknown_extension_files`, `unknown_extension_files_truncated`, `unknown_extension_file_path_limit`, `unknown_extension_extension_counts`, `unknown_extension_category_counts`, `unknown_extension_groups`, `extractors`, `hooks`, `hook_diagnostics`, `trust_overrides`, `path_case_sensitive`, `data_dir_mode`, `mac_profile`, `mac_profile_diagnostics`, `stale_after_seconds`, `index_age_seconds`, `query_context.check_mode`, `query_context.stale_after_seconds`, `last_index_run.reference_extraction_cap_hits`, `last_failed_or_partial_index_run`, `last_failed_or_partial_index_run.progress_persisted`, `last_failed_or_partial_index_run.recovery_hint`, `last_failed_or_partial_index_run.file_errors`。 |
| database maintenance | `db_size_bytes`, `wal_size_bytes`, `db_pragma_settings` (`journal_mode`, `synchronous`, `wal_autocheckpoint`, `busy_timeout_ms`, `page_count`, `freelist_count`, `page_size`, `auto_vacuum`), `prepared_command_cache` (`count`, `capacity`, `hit_count`, `miss_count`, `eviction_count`), `maintenance_guidance`。 |
| remediation fields | `degraded_root_cause`, `degraded_reason`, `recommended_action`, `alternative_action`, `readiness_degradations`, `repair_commands`。 |
| MCP-only session diagnostics | `mcp_session`、`mcp_session.metrics`、`mcp_session.audit_log`、`mcp.rate_limit.bucket_limit`、`mcp.rate_limit.bucket_limit_rejection_count`。`mcp_session` は persisted DB state ではなく session-scoped diagnostics で、`log_level`、上限付きの `roots`、任意の `client_info`、上限付きの任意の `client_capabilities`、常設の `metrics` object、audit 出力が有効な場合の `audit_log` を含みます。advertised root が切り詰められた場合は `roots_truncated`、`root_count`、`root_limit`、`root_uri_length_limit` が切り詰め内容を示します。client capabilities が切り詰められた場合は `client_capabilities_truncated`、`client_capabilities_truncation_reason`、`client_capabilities_serialized_bytes`、`client_capabilities_byte_limit`、`client_capabilities_depth_limit` が保持された診断 subset を示します。未設定時の `mcp_session.metrics` は `{"enabled":false}` です。有効な metrics sink は `enabled`、`path`、`max_bytes`、`bytes_written`、`disposed`、`degraded`、`queue_capacity`、`queue_depth`、`queued_event_count`、`written_event_count`、`dropped_event_count`、`queue_full_drop_count`、`serialization_failure_count`、`write_failure_count`、`rotation_failure_count`、`batch_flush_count`、`consecutive_failure_count`、`recovery_count` に加え、任意の `next_retry_at`、`last_recovery_at`、`last_failure` を追加します。MCP ping は常に metrics object を `metrics` として返し、metrics の degradation は意図的に top-level liveness result へ反映しません。audit status field と health semantics は [MCP 監査ログの出力](#mcp-監査ログの出力) に定義します。`mcp.rate_limit.bucket_limit` は normalized な `(partition, caller)` bucket 全体に対する process-local 上限で、direct call はすべて caller-wide の固定 coarse partition、canonical な既知 tool は追加の secondary per-tool partition、unknown な `batch_query` slot は caller ごとの 1 つの固定 invalid-slot partition を使います。`mcp.rate_limit.bucket_limit_rejection_count` は新規 bucket 作成がその上限を超えるため拒否された呼び出し数です。 |
| documentation sync | この一覧は `README.md` と `AGENT_GUIDE.md` と同期してください。必須 field がそれらの docs から欠けると `DocumentationStatusContractTests` が失敗します。 |

`head_freshness` は machine consumer 向けの compact summary です。
`state=fresh` は complete な index に対する `status --check` の workspace 比較成功が必要で、
`state=fresh_but_incomplete` は workspace freshness と failed-file coverage を分離し、
`state=head_current` は runtime HEAD と `indexed_head_source` が選んだ
`indexed_head` の一致だけを示します。
`indexed_head_source` は `indexed_head` が最新 index stamp (`indexed_head_sha`) 由来か、
legacy full-scan 限定 stamp (`indexed_head_commit`) 由来かを示します。

full scan または scoped update の per-file failure は、成功した file transaction を commit し、persisted edge を
query できるよう graph-presence bit を復元します。一方で issue、SQL graph、hotspot、C#、
fold の currentness stamp は復元しません。代わりに `index_completeness=incomplete`、上限付きの
構造化 file error、recovery metadata を永続化し、次の complete run がそれらを clear します。
full-scan JSON は extracted / persisted count を分離し、primary total には commit 済み DB count を使い、
`--allow-partial` で終了コード `0` を明示しない限り `11` を返します。

runtime diagnostic subcontract:

| surface | contract |
|---|---|
| `hotspot_family_degraded_reason` stable code | `hotspot_family_support_not_indexed`, `hotspot_family_metadata_stale`, `hotspot_family_disabled_at_index_time`, `partial_family_key_population`, `hotspot_family_marker_fingerprint_incomplete`。 |
| `partial_family_key_population` | 一部の indexed symbol に family key がまだ無く、rebuild / restamp が必要です。 |
| `hotspot_family_marker_fingerprint_incomplete` | 前回 index run で marker fingerprint traversal が safety cap に当たったことを示します。rebuild 前に generated / vendor marker tree を narrow または ignore してください。 |
| `extractors` | runtime extractor plugin と pattern-config の health を報告します。loaded plugin assembly / pattern count、symbol/reference extractor count、skipped file count、incompatible / malformed file 用の bounded diagnostic list を含みます。diagnostic の path と message は出力前に sanitization されます。 |
| `hooks[]` / `hook_diagnostics[]` | worker が discovery した hook manifest に stable な assembly-qualified `id`、`callback_budget_ms`、worker-only load-context lifecycle を含めます。`hook_diagnostics[]` は bounded discovery の sanitization 済み diagnostic を返し、concrete hook が判明している場合は `hook_id` も含みます。index run は scratch copy 上で timeout した callback mutation を破棄します。 |
| `trust_overrides[]` | 受理された拡張信頼境界の環境変数 override を報告します。各 entry は `kind`、`environment_variable`、sanitization 済みの `value`、任意の sanitization 済み `path`、`message` を含みます。現在は `CDIDX_TRUST_WORKSPACE_PLUGINS` による workspace plugin discovery と `CDIDX_HOOKS_DIR` による hook directory override の受理を対象にします。 |

`references` は以前から人間向け出力の各行先頭に `reference_kind` を表示しており、`callers` も grouped caller 行に対して同じタグを出す。1 つの grouped container で kind が混在する場合（例: 同じ event メンバに対する `call` と `subscribe`）は、単一 preferred label へ潰さずに `call+subscribe` のように distinct kind を `+` で連結して表示する。reference-kind 列の幅はバッチ内で最も長いラベルに合わせて動的に広がるため、mixed 行が隣接列を押し出さない。`callers` / `callees` の JSON 出力では、後方互換のため scalar な `reference_kind`（preferred 順 `instantiate` > `subscribe` > `MIN(call)` の要約 kind）を残しつつ、ソート済みの `reference_kinds` 配列と `has_mixed_reference_kinds` bool も追加した。これにより consumer は単一 summary label に騙されずに mixed container を検出できる。端末上でも `call` / `instantiate` / `subscribe` / mixed を `--json` なしで見分けられ、AI クライアントも `--exact` を改めて投げ直さずに mixed-kind の問いに答えられる。

`ReferenceResult` は `is_self_reference` と `is_mutual_recursion` を含み、`CallerResult` は `has_self_reference` と `has_mutual_recursion` を含む。これらのフィールドは、正当な再帰呼び出しを既定の graph 結果から削除せずに、自己再帰エッジと直接の2シンボル循環を識別する。非再帰 view が必要な reader API は自己参照除外を opt-in で使える。

MCPツール呼び出しは `structuredContent` に構造化JSON、`content` に短い要約を返すため、クライアントは型付きデータを直接利用できます。

exact-match flag の互換性は [USER_GUIDE.md](USER_GUIDE.md#フラグ互換性と移行) に記載しています。MCP schema はこの表と同期してください。`search.exact` は `exactSubstring` の legacy alias、name-based tools の `exact` は `exactName` の legacy alias です。新しい exact-match alias を追加する場合は、compatibility table、CLI help、MCP description、changelog fragment を同じ変更で更新してください。

`search`、`definition`、`references`、`callers`、`callees`、`symbols`、`files` は `--path`、繰り返し指定できる `--exclude-path`、`--exclude-tests` による絞り込みを共有します。読み取り層は tests や docs より source を優先し、`search` はシンボル名やパスがクエリと正確に一致する候補をさらに上位に出して、AIクライアントが実装ファイルへ早く到達できるようにします。

literal-safe な `search` query は reader 層で FTS5 sanitization 前に 1000 文字、128 whitespace term へ制限します。CLI、MCP、直接 reader caller の failure mode を揃えるため、この guard は `DbReader` に置きます。raw `--fts` query は別途 raw FTS complexity limit を使います。

`search --json` と MCP の `search` は、フルチャンクを `chunk_start_line`、`chunk_end_line`、`snippet_start_line`、`snippet_end_line`、`snippet`、`match_lines`、`highlights`、`context_before`、`context_after`、`truncated_line_count`、`dropped_match_line_count`、`truncation_context` を持つ軽量スニペットへ投影します。compact CLI row と MCP search result は有効な出力オプションも `snippet_lines` / `snippetLines`、`max_line_width` / `maxLineWidth`、`exact`、`raw_fts` / `rawFts`、`literal_highlights_available` / `literalHighlightsAvailable`、任意の `literal_highlight_warning` / `literalHighlightWarning` として返します。`--snippet-lines` で抜粋長を先に制限でき（デフォルト: 8、最大: 20）、`--max-line-width`（CLI）/ `maxLineWidth`（MCP）は `find` / `references` / `excerpt` / `inspect` と同じ共有 `LineWidthFormatter.ClampLine` 契約（デフォルト: 512、最大: 4096、`0` で切り詰め解除）で各スニペット行を最初のマッチトークン周辺にクランプするため、minified / transpiled / 生成された 1 行ファイル内の 1 ヒットで数百 KB を返さなくなります。クランプされた行はスニペットに `...(+N)...` マーカーが入り、`truncation_context.char_counts`、`truncation_context.total_chars`、`highlights[].truncated`、`highlights[].original_line_length`、`highlights[].truncated_char_counts` で AI クライアントがクランプの有無と省略文字数を検出できます。`highlights[].terms` は互換性のため distinct な term list のまま残し、`highlights[].term_occurrences` は一致ごとの `term`、1-based の `line` / `column`、`length` に加えて、行クランプ後に返却 snippet text 内へ残っている部分を示す `visible`、`visible_column`、`visible_length` を記録します。exact substring search では `highlights[].literal_terms` と `highlights[].literal_term_occurrences`（MCP では camelCase）も追加され、広めの診断 token list を残したまま、要求された literal phrase だけを render できます。raw FTS row は FTS 構文を単一の literal phrase へ対応付けられないため、`literal_highlight_warning` / `literalHighlightWarning` に `literal_highlights_unavailable_raw_fts` を設定します。exact ではない記号の多い code phrase 検索では、FTS tokenization が記号を失いやすい場合に exact substring semantics で再検索できるよう、CLI JSON compact result に `exact_substring_hint`、MCP `search` に `recovery_hint` を追加します。`focus_mode`、`focus_line`、`focus_column`、`focus_reason` は snippet に選ばれた match window を説明し、`dropped_match_line_count` と任意の `next_match` は選択された snippet window 外に落ちた一致行を示します。

既定の `quality` snippet focus は、先頭が文字または underscore で、残りが文字、数字、underscore だけで構成される単一 query を identifier 形状として扱います。一つの result に一致する code と、それより前の comment / string が混在する場合は、最初の `code` origin occurrence の行と列を snippet の優先位置にします。同じ長い行に literal と実行 code の occurrence がある場合も code 側を選び、自動 occurrence focus は text が行だけを指定する preferred-focus probe の上限を超える有効な chunk でも chunk 全体を走査します。空白区切りの phrase query と、明示的な `leftmost` / `proximity` focus mode は従来の選択を維持します。明示的な origin filter がある場合は、最初に残る facet の行と列へ再 focus し、focus metadata、visibility、行 clamping を filter 後の result と一致させます。選択位置は既存の focus / origin metadata で監査でき、`dropped_match_line_count` は最終的に返す window から計算し、`next_match` はその window の先を引き続き示します。

マッチ行がインデックス済みシンボル範囲内にある場合、`search --json` と MCP の `search` は任意フィールドの `enclosing_symbol_name`、`enclosing_symbol_kind`、`enclosing_symbol_start_line`、`enclosing_symbol_end_line`、`enclosing_container_name` も返します。

`find --json` は繰り返し一致でも line-delimited のまま維持し、各 row に bounded な match span / truncation metadata を追加します。`length` は 1-based の `column` から始まる一致長、`original_line_length` は行幅クランプ前のソース行長、`snippet_truncation_context.line_count` / `char_counts` / `total_chars` / 任意の `reason` は snippet クランプを表します。`--max-line-width` によって snippet 行が省略された場合、`reason` は `line_width` になります。

`excerpt --json` は 1-based の source 開始/終了位置、token `type`、`modifiers` を持つ軽量 range list の `semantic_tokens` を返すため、IDE や LLM クライアントは生の `content` 文字列を再パースせずに抜粋範囲を描画・後処理できます。C# の excerpt と LSP `textDocument/semanticTokens/full` は、keyword/modifier、namespace と type、method と property、parameter、variable と field、declaration modifier を判定する同じ source classifier を共有します。excerpt の分類は indexed source の context を利用し、出力 token budget を可視 source 行へ絞った後に適用します。bounded source scan が可視範囲まで到達できない場合は可視 content の分類へ fallback するため、狭い範囲では利用可能な context を維持し、file 後半の excerpt が手前の token に出力 budget を消費されて空になることも防ぎます。excerpt の range mapping と LSP の delta encoding は座標変換だけを担当し、semantic kind を選びません。`semantic_token_coordinate_space` は `source` です。`--max-line-width` で返却内容がクランプされた場合、`content_line_spans` は返却 content 行と可視 content column span を、対応する source 行と source column span に対応付けます。clamp marker は未対応領域として扱い、semantic token には含めません。excerpt row は `requested_start_line`、`requested_end_line`、`effective_start_line`、`effective_end_line`、`content_truncation_reasons`、任意の `content_recovery` も返すため、`--max-line-width` による `line_width_cap` を検出して省略部分を再取得できます。body を持つ JSON row は対応する `body_requested_*`、`body_effective_*`、`body_content_truncation_reasons` も返します。body reason には snippet/body 行数上限の `body_line_cap` と definition body byte 上限の `body_byte_cap` があります。

`content_recovery` と `body_content_recovery` では `argv` が一次的な機械可読契約です。共有用の CLI JSON と MCP response は既定で、機械固有の apphost、assembly、source、database の絶対パスを構造化 path sanitizer で伏せてから `command` を生成し、render 済み shell 文字列を regex で置換しません。SQLite file URI の query segment は個別に処理するため、`mode=ro` などの安全な control は維持しつつ、path 値や機密値を持つ query は sanitization されます。database option は既知の source 引数位置より後だけで探索するため、`--db` のように option と紛らわしい source 名でも DB path redaction を迂回できません。既定の metadata は `paths_redacted: true`、`command_display_only: true` を返し、いずれかの引数を置換した場合は `requires_local_path_substitution: true` も返します。先頭が `-` の root-level path には対応済みの `--` end-of-options marker を維持し、`command_shell`（`posix-sh` または `powershell`）で escape 契約を示します。CLI の `definition`、`references`、`callers`、`callees`、`excerpt`、`inspect`、`impact` は、既定を明示する `--redact-paths` と、ローカル用途だけの opt-in である `--show-paths` を受け付けます。`--show-paths` は解決済みの apphost、または `dotnet` と実行中 assembly、source、database の各引数を出力し、`paths_redacted: false` と `command_display_only: false` を設定し、宣言した shell 向けに安全に quote した command を生成します。MCP は常にサポート共有向けの安全な既定を使い、同等の camelCase metadata を返します。

`status --config` も同じ policy に従います。`db_path`、`data_dir`、`global_tool_log_dir` は既定で伏せられ、SQLite file URI query 内の path 値と機密値も対象です。top-level の `redaction.paths_redacted` が path mode を記録し、secret はどちらの mode でも伏せられます。解決済み path 値を出すローカル opt-in は `--show-paths` だけです。`--redact-paths` は既定を明示し、どちらの path 表示 flag も `--config` のない通常の `status` では拒否されます。

`inspect` と MCP の `analyze_symbol` は、主定義、同一ファイル内の近傍シンボル、参照、caller、callee、ファイルメタデータ、さらにワークスペース鮮度/git メタデータと graph 対応メタデータを1レスポンスにまとめます。bundle 内の graph 節が実際に SQL ベースの read に依存する場合だけ、`sql_graph_contract_ready` / `sql_graph_contract_degraded_reason`（MCP では既存の camelCase alias も）も返します。mixed-language bundle で C# / JS などの graph row しか返っていない場合は SQL trust signal を出さないため、無関係なクエリが stale SQL state に引きずられません。複数の連続クエリを避けたい AI ワークフロー向けです。call graph 系の節は言語差分を考慮しており、未対応言語では `graphSupported` / `graphSupportReason` によって「未対応」と「ヒットなし」を区別できます。その場合は `search` を優先して使う前提です。

直接の MCP graph ツール（`references`、`callers`、`callees`）も、言語フィルタが指定されている場合は `graphLanguage`、`graphSupported`、`graphSupportReason` を返し、未対応言語クエリが対応言語の 0 件ヒットと同じ見た目にならないようにします。CLI/MCP の graph / dependency 系 payload は、SQL ベースの row が実際に結果へ寄与したときだけ `sql_graph_contract_ready` / `sql_graph_contract_degraded_reason` を反映し、同じスコープに stale SQL file があるだけの非SQLクエリは劣化扱いにしません。

```json
{"path":"src/auth.py","lang":"python","chunk_start_line":1,"chunk_end_line":80,"snippet_start_line":1,"snippet_end_line":6,"snippet":"def authenticate(user):\n    token = issue_token(user)\n    return token","match_lines":[2],"highlights":[{"line":2,"text":"    token = issue_token(user)","terms":["token"]}],"context_before":1,"context_after":3,"score":-1.5}
```

## リリース手順

バージョン文字列の真実は、リポジトリ直下の `version.json` 1か所だけにある。
公式 GitHub Release と NuGet publish job は canonical repository である
`Widthdom/CodeIndex` に制限されています。派生配布で公式 release workflow、
NuGet package ID、cdidx / CodeIndex branding を再利用するには、別途の書面
契約が必要です。

### バージョンの流れ

1. **ビルド時。** `src/CodeIndex/CodeIndex.csproj` が `version.json` を読み取り、`<Version>` を設定する。NuGet パッケージと self-contained バイナリは自動で正しいバージョンになる。
2. **実行時。** 同じ project file が `version.json` を publish 済みバイナリの隣へコピーする。`ConsoleUi.LoadVersion()` が `AppContext.BaseDirectory` から読み取るため、`cdidx --version`、MCP `serverInfo.version`、`status --json` の `version` が一致する。
3. **インストール時。** `install.sh` と生成される Homebrew formula が `cdidx` の隣へ `version.json` とネイティブ SQLite ライブラリを配置する。`version.json` が欠けると `cdidx --version` は `v0.0.0` にフォールバックし、ネイティブ SQLite ライブラリが欠けると SQLite に触るコマンドは index 開始前に失敗する。

C# 側にバージョン定数は無い。`version.json` の外でバージョン文字列が出てくるのは、`CHANGELOG.md` のリリース見出しと compare リンクだけである。

### メンテナ向けチェックリスト

以下の `1.9.0` はあくまで例です。実際にリリースするバージョン番号へ読み替えてください。

0. **バージョンを上げる前に、未マージブランチと open PR を必ず全件トリアージする。**
   `git fetch --all --prune` を実行し、`git branch -a --no-merged main` で未マージブランチ、`gh pr list --state open --limit 1000` で open PR を列挙する。ブランチ名では事前フィルタしないこと。各項目について、リリース前にマージするか、リリース PR 説明で見送り理由を明記するかを必ず決める。
1. 最新の release branch で changelog tool を実行する:
   `dotnet run --project tools/CodeIndex.Changelog -- prepare --version 1.9.0 --date YYYY-MM-DD`。
   この tool は `changelog.d/unreleased/` を検証し、英日 bilingual fragment を `CHANGELOG.md` に集約し、既存の direct `[Unreleased]` 内容を保持し、英日両方の `[Unreleased]` を空に戻し、`version.json` を更新し、`.gitkeep` を残して消費済み fragment を削除し、compare-link footer も更新する。
2. 生成された `CHANGELOG.md`、`version.json`、`changelog.d/unreleased/` の diff を確認する。release heading や compare link は、tool の不具合を直す場合を除き手で編集しない。fragment または tool 入力を直して `prepare` を再実行する。
3. `.codex/workflows/release-changelog.md` の release validation（Release 構成の restore、build、test、pack）を実行する。
4. 生成された release prep をコミットする。例: `Prepare release v1.9.0`。
5. release PR が merge された後、merge commit に `v1.9.0` タグを付けて push する。`.github/workflows/release.yml` は `v*` タグで起動し、各プラットフォームの tarball と NuGet パッケージをビルドし、`Widthdom/homebrew-tap` の `codeindex` formula を `HOMEBREW_TAP_TOKEN` で更新する。
6. リリース公開後、クリーンなマシンでワンライナーインストーラと Homebrew install path を実行し、`cdidx --version` が公開バージョンを返すことを確認してから告知する。

クリーンインストールで `cdidx v0.0.0` が返る場合は、リリースの不具合として扱うこと。tarball に `version.json` が入っていないか、`install.sh` がそれをバイナリの隣へコピーしていない。クリーンインストールのスモーク経路は `CLOUD_BOOTSTRAP_PROMPT.md` を参照。

### ライセンス変更後の legacy package 対応

`main` の README 変更は、過去の source archive、tag、branch、NuGet package
version に同梱されたライセンスを遡及的には変更しません。現行ライセンスポリシー
より前の古い NuGet version については、nuget.org の package management UI で
次を行います:

1. 各 legacy version を deprecate し、現行ライセンスおよび商標ポリシーより前の
   version であることを明確に伝える。
2. 新規発見を減らすべき legacy version は unlist する。
3. 置き換え先の最新 listed version について、`.nupkg` に `LICENSE`、
   `COMMERCIAL_LICENSE.md`、`TRADEMARKS.md` が含まれることを確認する。

Unlist しても exact version restore は不可能になりません。これは発見性を下げる
措置であり、遡及的なライセンス変更や削除ではありません。

## AIフィードバックの実装

`suggest_improvement` MCPツールにより、AIエージェントがギャップやエラーを報告できる。

### ソースファイル

| ファイル | 役割 |
|---------|------|
| [`src/CodeIndex/Models/SuggestionRecord.cs`](src/CodeIndex/Models/SuggestionRecord.cs) | 提案データモデル（DTO） |
| [`src/CodeIndex/Cli/SuggestionStore.cs`](src/CodeIndex/Cli/SuggestionStore.cs) | SHA256重複排除付きのローカルJSON蓄積 |
| [`src/CodeIndex/Cli/SourceCodeDetector.cs`](src/CodeIndex/Cli/SourceCodeDetector.cs) | ヒューリスティックによるソースコード漏洩防止 |
| [`src/CodeIndex/Cli/GitHubIssueReporter.cs`](src/CodeIndex/Cli/GitHubIssueReporter.cs) | GitHub Issues APIクライアント（ベストエフォート） |
| [`src/CodeIndex/Mcp/McpToolHandlers.cs`](src/CodeIndex/Mcp/McpToolHandlers.cs) | `ExecuteSuggestImprovement` ハンドラ |
| [`src/CodeIndex/Mcp/McpToolDefinitions.cs`](src/CodeIndex/Mcp/McpToolDefinitions.cs) | ツールスキーマ定義 |
| [`src/CodeIndex/Cli/SuggestionsCommandRunner.cs`](src/CodeIndex/Cli/SuggestionsCommandRunner.cs) | ローカル提案の一覧、監査付き lifecycle 遷移、上限付き原子的 export、issue draft 生成、open issue duplicate preflight |

### 送信されるデータ（GitHubトークン設定時）

- カテゴリ（14個の固定値のいずれか: `symbol_extraction`, `reference_extraction`, `search_ranking`, `language_support`, `output_format`, `crash_report`, `unexpected_error`, `security`, `performance`, `bug`, `cleanup`, `documentation`, `feature_request`, `other`）
- 言語名（例: `typescript`）
- 説明テキスト（自然言語、SourceCodeDetectorにより検証済み）
- コンテキストテキスト（自然言語、SourceCodeDetectorにより検証済み）
- 呼び出し元が渡した任意の repository-relative evidence path。これは path 文字列だけで、payload 用にファイル内容を読むことはありません。
- cdidx バージョン文字列
- attribution メタデータ: `created_by_agent`、`session_id`、`client_version`、`mcp_client_name`、`mcp_client_version`、および任意の `tool_invocation_context`
- 不変の提案 ID と、送信する編集可能内容の SHA256 revision

### 提案 ID と revision

`SuggestionRecord.Id` は、CLI の解決・変更・export、MCP 応答、GitHub 再試行の冪等性で使用する不透明で不変の公開 identity です。新規 ID は提案内容と独立して生成されます。`RevisionHash` は全ての編集可能 field の SHA256 digest で、その内容が変わるたびに更新されます。update と delete は draft を変更する前に、caller が読み取った revision と store lock 内の現在の revision を比較します。完全一致の重複検出には別の正規化 content hash を使います。`hash` しか持たない legacy record は、その値を `Id` として保持し、`RevisionHash` を計算し、stable ID の alias として `hash` を引き続き永続化することで in-memory migration されます。そのため、移行前または編集前に控えた ID は引き続き有効で、現在の正規化内容で重複排除しつつ content-derived ID を再利用しません。

suggestion sidecar は `DataDirectorySecurity.ResolveSensitiveSidecarDirectoryForDatabase` を使います。private directory 内の database は JSON / archive / lock file を隣接配置したままです。POSIX では、database の直接の親が group- または other-writable の場合、`ResolveSensitiveTempFallbackDirectory` 配下の path-derived scope を代わりに使い、`CreateSensitiveDirectory` が `0700`、sensitive file writer が `0600` を強制します。CLI の filesystem failure は suggestion-store 境界で捕捉し、`E021_SUGGESTION_STORE_UNAVAILABLE` と `FileSystemBoundary.ClassifyProbeFailure` による `category` を返します。MCP 経路は unhandled exception を漏らさず、structured content に `permission_denied` と `error_code`、`filesystem_category` を載せます。

提案テキストを永続化する前に、型付き redaction が名前付き credential、AWS access key、bearer 値、GitHub、Stripe、GitLab、Slack、OpenAI の prefix を含む既知の構造化 credential 形式、および不透明な混合文字 token を置換します。high-entropy fallback は識別子を考慮し、複数の単語境界を持つ構造化された PascalCase / snake_case のコード・テスト識別子（埋め込み数値 component を含む）、先頭 underscore 付き識別子、slash / hyphen 形式の recipe ID は再現性の根拠として保持します。一方、単一ブロックまたは大文字・小文字が交互に並ぶ不透明 token と既知の token 形式は、引き続き `[REDACTED:high_entropy_token]` に置換します。

### 重複排除とローカル保持

`SuggestionStore` はまず正規化した SHA256 content hash を確認し、その後、同じ category / language の直近提案と正規化 token の Jaccard 類似度で比較する。fuzzy しきい値の既定は `0.85` で、`cdidx mcp --suggestion-dedup-threshold`、`CDIDX_SUGGESTION_DEDUP_THRESHOLD`、または `.cdidxrc.json` の `suggestion_dedup_threshold` で `0` から `1` の値へ上書きできる。fuzzy match は GitHub 送信前に重複として返され、監査用に一致先の不変 ID と score を stderr に記録する。

ローカル提案の保持は `CDIDX_SUGGESTION_MAX_AGE_DAYS` と `CDIDX_SUGGESTION_MAX_COUNT` で制限され、`.cdidxrc.json` では `suggestion_max_age_days` と `suggestion_max_count` として設定できる。組み込み既定値は 365 日と 5000 件で、受け付ける値の上限は 3650 日と 100000 件。0 以下、数値以外、overflow、または上限を超える環境変数値は既定値へ戻り、上限を超える config-file 値は config validation 時に拒否される。

### ローカルライフサイクルフィールド

ローカルの提案レコードは、送信済みかどうかの二値フラグではなく `status` ライフサイクルフィールドを使う。新規レコードは `draft` で始まり、GitHub への送信が成功すると `submitted_pending_triage` へ移行し、判明している範囲で `upstream_url`、`upstream_issue_number`、`last_synced_at` を記録する。GitHub 送信を試みるたびに `last_submit_attempt` を stamp し、`submit_attempt_count` を増やし、失敗時は `last_submit_error` を記録します。成功時は最後の error を clear します。GitHub の rate-limit 応答では `next_retry_at` も記録し、未送信の重複提案はその時刻を過ぎるまで再送しない。`submitted_to_github` / `github_issue_url` を含む古いレコードは、読み取り時に新しいライフサイクルフィールドへ正規化される。

`SuggestionStore.TryTransitionStatus` は `suggestions update <id> --status <state>` が使う原子的な手動遷移境界です。`submitted_pending_triage` は自動設定専用です。`open_in_upstream` と `resolved_in_upstream` には既存の upstream 根拠が必要で、`draft` には upstream 根拠がないことが必要です。`wont_fix`、`duplicate`、`superseded` はメンテナーによるローカルの判断です。ローカルの判断は重複提案の自動再送を抑止しますが、`AlreadySubmitted` や upstream 送信済み response flag は設定しません。同じ状態への遷移、および送信 reservation が active な間の遷移は fail closed になります。store は file lock 内で expected revision を再確認し、最新の `previous_status`、UTC の `status_changed_at`、上限・redaction 付きの `status_changed_by`、任意の上限・redaction 付き `status_change_reason` を stamp し、`resolved_in_upstream` では `resolved_at` を更新して、`revision_hash` を再計算します。監査値全体を redaction してから surrogate-safe な最終上限を適用するため、上限境界をまたぐ credential も redaction を回避できません。1件の監査 event の意味を曖昧にしないため、content 編集と lifecycle 遷移は別々の CLI 操作です。

`suggestions export --format markdown|issue-drafts --output <path>` は上限付き payload をメモリ上で描画し、書き込み前に 16 MiB 超過を拒否し、選択中の database または suggestion-store path も拒否します。既存ファイルでは正規化した path 表記に加えて filesystem identity も比較するため、symlink 付き親 directory、mount alias、hard link で source 保護を迂回できません。既存の出力先は `--overwrite` を明示しない限り拒否します。公開処理は兄弟一時ファイルを使い、内容を flush してから同一 filesystem 上で no-overwrite move または原子的置換を行い、失敗時は一時ファイルを片付けます。writer は BOM なし UTF-8 を出力し、不足している親 directory を作成し、JSON 形式の suggestion export は stdout のままです。test は store の遷移・revision 契約、CLI validation と filtering、source target alias 拒否、no-overwrite の race safety、置換、一時ファイル cleanup を網羅します。

### GitHub 再試行の冪等性

`SuggestionStore.TryAddAndSubmit` は、ローカル read/write と送信予約を suggestion-store file lock の下で行いますが、GitHub callback は lock を解放してから呼び出します。予約時に `last_submit_attempt`、`submit_attempt_count`、production callback の設定可能な最大 timeout を覆う 6 分の `next_retry_at` guard を stamp します。この guard が active attempt を示す間、update / delete は `submission_in_flight` を返し、callback の結果は remote call 完了後に短時間だけ lock を取り直して永続化されます。finalization でも予約済み revision と現在の revision を比較し、期限切れ reservation 後に変更された revision を古い callback 結果で送信済みにしません。

upstream Issue を作成する前に、`GitHubIssueReporter` は同じ不変の提案 ID を持つ Issue が既に存在するか確認する。まず GitHub Search で Issue 本文内の ID を検索し、その後 fallback として、cdidx が付与する既存 repository label（通常提案は `enhancement`、crash/error report は `bug`）付きの Issue を一覧し、各本文内の ID を照合する。この fallback により GitHub Search の indexing 遅延を回避できるため、作成レスポンスが失われた直後の再試行でも、作成済み Issue を検出して重複 POST を防げる。lookup 失敗時は fail closed として扱う。GitHub Search、label 付き Issue 一覧、またはレスポンス解析が不確定な場合、reporter は sanitization 済みの `last_submit_error` を記録し、重複の可能性がある Issue は作成しない。

共有 GitHub HTTP クライアントは既定で 10 秒 timeout と platform default proxy（.NET の既定 proxy 処理を通じた `HTTPS_PROXY`、`HTTP_PROXY`、`ALL_PROXY`、`NO_PROXY`）を使う。`CDIDX_GITHUB_SUBMIT_TIMEOUT_SECONDS` で最大 300 秒まで設定でき、0 以下、数値以外、または上限を超える値は 10 秒の既定値へ戻る。GitHub API request には共有 request helper を通じて `User-Agent: cdidx`、`Accept: application/vnd.github+json`、`X-GitHub-Api-Version: 2022-11-28` を設定する。bearer token は空でない token がある場合だけ request ごとに設定し、suggestion submission では `CDIDX_GITHUB_TOKEN` のみを使う。作成失敗の診断には proxy 環境変数の確認ヒントを含める。`429` 応答と `x-ratelimit-remaining: 0` 付きの `403` 応答は rate limit として扱い、`Retry-After`、`x-ratelimit-reset`、1 分の fallback retry window の順で再試行時刻を決める。解析した retry date は現在時刻から 1 時間後までに制限し、無効または範囲外の reset header は fallback 前に無視する。

Outbound HTTP/GitHub egress は意図的に狭くしている:

| 目的 | 経路と上限 |
|---|---|
| update check | `UpdateChecker` は GitHub release metadata を 2 秒の request scope、`ResponseHeadersRead`、64 KiB の response 上限、JSON 深度 16、private cache write、sanitization 済み failure category で読む。 |
| release installer/download verification | `ProgramRunner` の release download helper は GitHub API media type を付けない release-download header、256 KiB の checksum 上限、1 MiB の installer script 上限、private script-file write、bounded な release asset 診断を使う。 |
| GitHub issue creation | `GitHubIssueReporter` は duplicate lookup 成功後に scrub 済みの構造化 suggestion field だけを POST する。success JSON は 256 KiB / 深度 16 で制限し、API error body は 4 KiB で制限して sensitive JSON field を redact する。 |
| duplicate preflight | `IssueDuplicatePreflight` は最大 1000 件の open issue と 1000 件の repository label だけを読み、GitHub page body は 8 MiB / 深度 32 に制限し、pull request を除外し、issue/title/body/label scalar を切り詰め、recoverable diagnostic を sanitize する。 |
| suggestions/reporting exports | `SuggestionStore`、`SuggestionsCommandRunner`、search issue-draft output、report/query output は、呼び出し元が live GitHub duplicate preflight を明示するか `suggest_improvement` に `CDIDX_GITHUB_TOKEN` がある場合を除き local-only である。export される description/context/tool invocation text は `[truncated]` marker 付きで制限される。 |
| MCP exposure and diagnostics | MCP `suggest_improvement` は受理した suggestion をまず local に保存する。doctor 診断は GitHub proxy/default-credential 状態と maximum request timeout を token 値なしで出し、submission failure は raw response body ではなく sanitize 済み category/detail として保存する。 |

### ペイロードに設計上含まれないもの

- ユーザーのプロジェクトからのソースファイル内容
- インデックス済み SQLite データベースからのあらゆるデータ
- `.cdidx/codeindex.db` からのあらゆるデータ
- OS やシステム環境の情報

### ヒューリスティックなソースコードガード（セキュリティ境界ではない）

description、context、および任意の tool invocation context フィールドは、保存およびオプションの GitHub 送信前に `SourceCodeDetector` を通過する。このヒューリスティックは一般的なコードコピペパターン（複数行ブロック、バッククォートまたはチルダのフェンスドコード、import の連打、関数定義）を拒否するが、ギャップの説明として有用な短いインラインコード例は意図的に許容する。拒否時は、拒否対象フィールド名、安定した主理由の `reason_code`、およびマッチしたヒューリスティックの `reason_code_counts` 診断を持つ上限付きの `source_code_rejection` object を返し、拒否された本文は反映しない。これは**セキュリティ境界でもデータ漏えい防止境界でもない** — 意図的に回避しようとするエージェントは回避でき、エンコードまたは難読化されたコード風テキストは偽陰性になり得る。このガードはコードの誤混入を防ぐベストエフォートのフィルタであり、コード的テキストが一切送信されないことの保証ではない。

### SourceCodeDetector の設計

`SourceCodeDetector` は6つの独立したヒューリスティックを使って、コピペされたソースコードに見えるテキストを拒否する。各ヒューリスティックは明確な名前の private メソッドとして実装され、何を検出し、なぜそれがソースコードの兆候なのかを詳細なコメントで説明している。また、`statement-ending`、`indented-code-lines`、`block-structure`、`repeated-imports`、`function-definition`、`fenced-code-block` のような安定した理由コードに対応する。検出時はすべてのヒューリスティックを評価するため、最初にマッチした理由を主 `reason_code` として維持しながら、マッチした理由コードの件数を診断として返せる。可読性を重視して設計されており、オープンソースのコードをレビューする誰もが検出ロジックと偽陰性のトレードオフを理解できる。

短いインラインコード例（例: `` `const foo = () => {}` ``）は意図的に許容し、複数行のコードブロックのみを拒否する。偽陰性（一部のコードの見逃し）は許容する。偽陽性（有効な説明の拒否）は許容しない。

## 終了コード

USER_GUIDEの[終了コード](USER_GUIDE.md#終了コード)セクションを参照してください。

## エラーコード taxonomy

プロセス終了コード（`0` 成功、`1` 引数、`2` 未検出、`3` DB、`4` 機能未提供、`5` stale、`6` 一時的 DB、`7` 不正な引数値、`8` シグナルキャンセル、`9` install / upgrade installer 失敗、`99` 想定外例外）は粗い分類です。スクリプト・オンコール runbook・AI エージェントが「どのバケットか」だけでなく「どの失敗か」で分岐したい場合は、CLI / MCP のすべてのエラー経路で発行される安定した `Exxx_NAME` taxonomy を読んでください。利用者向けの一覧は [エラーコード](USER_GUIDE.md#エラーコード) にあります。本節は開発者向けの契約をまとめます。

**どこに出るか**

- 人間向け stderr は角括弧で前置: `Error [E001_DB_NOT_FOUND]: database not found at <path>`。コード以降の文言はリリース間で変わり得ますが、コードは変わりません。
- `--json` エンベロープ（`CommandErrorJsonResult`）には任意フィールド `error_code` を追加します。`JsonIgnoreCondition.WhenWritingNull` で null 時は省略されるため、既存 JSON 利用者にスキーマ破壊なし — runner がコードを付けた瞬間にだけ field が現れます。
- taxonomy の本体は `src/CodeIndex/Cli/CommandErrorCodes.cs`。すべての発行点は `DbCommandRunner` / `IndexCommandRunner` の `WriteCommandError(... , string? errorCode = null)` または `QueryCommandRunner.WithDb` 内の同等な stderr ライターを通します。

**安定性契約**

- コードは append-only。一度 tagged release で公開したコードは、実装が移動しても renaming / renumbering / 別の失敗形態への流用をしません。
- コードを廃止する場合は新規 emission を止めるだけにし、古いログをデコードできるよう `CommandErrorCodes.cs` の定数自体は残します。
- 新しいコードを追加するときは、次の空き `Exxx` slot を割り当て、定数の XML doc-comment にトリガー条件を書き、関連するすべての `WriteCommandError` 呼び出しを通し、`USER_GUIDE.md` の英語表と日本語表の両方に同じ行を追加し、`tests/CodeIndex.Tests/CommandErrorCodesTests.cs` に regression test を追加（角括弧 stderr の prefix 用と JSON `error_code` field 用の 2 アサーション）してください。
- `E003_SCHEMA_TOO_NEW` は意図的に発行点なしで予約しています。今は同等な forward-compatibility 条件を `status --json index_newer_than_reader` でソフトに表示しています。将来 open 時の未知 schema stamp を hard fail させるバイナリは `E003` を発行する必要があります。

## 設計判断

- **integration boundary では language capability pattern の型を維持** — CLI/MCP の `languages` 行は suffix のみの `extensions`、literal な `exact_filenames`、`<suffix>` 表記の `filename_prefix_patterns` を公開します。`legacy_patterns` は deprecation 中に従来の combined list を保持し、`pattern_provenance` は built-in、plugin/pattern、language-map override の所有元を示します。round-trip test は広告した全 typed pattern を `FileIndexer.DetectLanguage` に戻して検証します（#4617）。
- **曖昧な source extension は曖昧なまま明示** — `.m` と `.pl` を既定で Objective-C / Perl に割り当てません。`FileIndexer` は authoritative な認識済み shebang、64 KiB 上限 prefix 内の相互排他的で強い Objective-C/MATLAB または Perl/Prolog marker、各 ancestor directory 最大 256 entry の保守的な project marker の順に確認します。競合または弱い証拠は `ambiguous_m` / `ambiguous_pl` として index し、未確定の `.m` は位置を保つ共通コメントマスクの後で上限付きの MATLAB / Objective-C symbol・reference 経路を実行します。一方、Prolog と `ambiguous_pl` は保守的な reference / graph 対応を広告し、曖昧な `.pl` bucket は content-based classification を変えずに symbol / reference rule の和集合を使います（#4612、#4738、#4746）。
- **動的言語の reference-graph readiness は extractor contract に従う** — index 済みの Crystal、Groovy、Tcl、Prolog、`ambiguous_pl` row で symbol-extractor version stamp が欠落または古い場合、status は `dynamic_reference_graph_contract_stale` を報告し、通常の index refresh が対象 row を更新するまで `reference_graph_complete` / `graph_data_current` を false に保ちます（#4746）。
- **hotspot marker fingerprint は上限付きtree traversalを1回共有** — full/update CLIとMCP indexingは、directory treeを言語ごとに歩かず、C#、VB、F#、MSBuildのmarker fingerprintをまとめて計算します。各directoryでは固有marker globごとにplatform filesystemのmatching挙動を保って1回ずつ列挙し、child directoryも1回だけ列挙する一方、marker集合、budget、truncation sentinel、warning順は言語別に分離します。single-language APIも同じengineへ委譲し、ignore rule、nested repository/submodule境界、MCP authorized read failureを維持します。
- **lock file の依存グラフは package 間の関係をモデル化** — `packages.lock.json`、`package-lock.json`、`npm-shrinkwrap.json` は package 宣言を symbol として保持しますが、`dependency` reference は明示された親 package → 子 package の項目だけに出力します。NuGet lock の symbol / reference は現在の file、target/RID、親 package、正確な JSON property span を保持し、candidate 解決を file 内に限定します。file 単位の `deps` は package 名による file 間推論を抑止し、通常の index update は以前の dependency-lock 抽出 contract と reference-identity contract を無効化します。そのため、`callers` は無関係な lock file を接続したり、反復宣言を最初の一致行へ畳み込んだりせず、要求元 package を特定できます（#4409、#4845）。
- **依存サイクル監査では解析と表示を分離** — CLI の `deps --cycles` と MCP `deps` の `cycles=true` は、独立した `--graph-budget` / `graphBudget` まで path 順で決定的な edge 集合を解析してから、強連結成分を安定順位付けします。`--limit` / `limit` はその SCC 順位集合をページ分割するだけで、不透明 cursor は生成時の filter、graph budget、indexed graph に結び付けます。machine-readable 応答は `analysis_complete`、graph edge 件数/予算、安定 ranking mode、authoritative な総 cycle 件数かどうか、continuation metadata を公開し、graph budget 枯渇時は完全な cycle 監査を装わず明示的な未完了解析として報告します（#4731）。
- **ORMなし** — `Microsoft.Data.Sqlite`でパラメータ化クエリを直接使用。依存関係を最小限に、制御を明確に。
- **バッチコミット** — 書き込み性能のため1トランザクション500レコード。fsyncオーバーヘッドを削減。
- **部分的なバッチ失敗** — `DbWriter` は通常の chunk / symbol batch では高速な multi-row `INSERT` 経路を保ちます。SQLite が batch を拒否した場合、その batch を rollback し、各 row を per-row `SAVEPOINT` の下で再試行し、有効な row だけを commit し、失敗 row だけを skip して `BatchRowsSkipped` を増やし、row identifier と SQLite error を含む warning を出します。これにより、抽出された 1 行の破損で大きな indexing batch 全体が捨てられることを防ぎます（#1754）。
- **WALモード + busy_timeout** — Write-Ahead Loggingで読み書き同時アクセスとクラッシュ安全性を確保。5秒のbusy_timeoutで即座のSQLITE_BUSYエラーを回避。
- **複数 SELECT をまたぐ reader の snapshot 隔離** — 1 回の呼び出しで複数 SQL を発行する read エントリポイント（`DbReader.GetStatus`、`DbReader.AnalyzeSymbol`（CLI `inspect` / MCP `analyze_symbol`）、`RepoMapBuilder.Build`（CLI `map` / MCP `repo_map`））は、本体を 1 つの `BEGIN DEFERRED` transaction で囲み、すべての sub-query が同じ WAL snapshot を参照するようにする。これが無いと、2 つの `COUNT(*)` の間に writer が commit した結果として並行 reader が `files=836, refs=0` のような不整合状態を観測しうる（issue #180 で露見）。`DEFERRED` は最初の SELECT で `SHARED` lock を取るだけで writer を阻害せず、末尾で明示 Commit して `SHARED` lock を早期解放する。独自に `SqliteDataReader` を開く sub-query は内側ブロックに閉じ込めて `Commit()` より前に handle を解放すること — `SqliteTransaction.Commit()` は同じ connection 上で開いている reader があると失敗する。新しい多段 read エントリポイントは同じパターンに従うこと。単一 SQL のクエリは SQLite の auto-commit が文単位の snapshot を与えるため不要。
- **デフォルトはリテラル安全検索** — 検索は既定でトークンごとに引用してFTS構文エラーを避ける一方、`search "\"new Regex\""` のようなダブルクォート範囲は独立トークン一致に広げず、単一の FTS5 phrase token として扱う。生のFTS5構文は `--fts` またはMCPの `rawQuery` で明示 opt-in。prefix 拡張も opt-in：トークン末尾に `*` を付ける（`search auth*`）とそのトークンだけが FTS5 prefix phrase に昇格し、`--prefix` フラグ（MCP の `prefix`）はクエリの全トークンを昇格させる。opt-in がなければ `search 計算` は indexed token `計算` のみにマッチし `計算する` には広がらない（issue #1519）— unicode61 は連続 CJK コードポイントを 1 トークンとして扱うため、広く拾いたい場合は `--prefix` か末尾 `*` を明示する。
- **`find` の正確な正規表現 span** — `find --regex` の JSON / MCP 結果は正規表現エンジンの一致長をそのまま保持し、`^` や `$` のような挿入位置アンカーは `length: 0` を返す。可視範囲が必要な表示向け形式では、machine-readable な一致長を変えずに1文字の span として表示する場合がある（#4473）。
- **Git 風 ignore ルール対応** — `FileIndexer` は non-repo ディレクトリ向けに常時有効な `SkipDirs` / `SkipFiles`（および macOS の `._*` AppleDouble resource fork を indexability/言語プローブに到達させないための接頭辞除外 — #1583）を維持しつつ、走査時にはユーザーの `.gitignore` と任意の `.cdidxignore` をディレクトリごとに積み上げて適用する。Git 管理下のワークスペースでは大小文字の扱いを OS 名ではなく `core.ignorecase` から解決し、repo 配下の subdirectory を project root にした場合でもその設定を引き継ぐ。さらに repo-root やその途中階層にある ancestor `.gitignore` を preload してから走査し、`--commits` でも Git が返す repo-root 基準の changed path を project root 基準へ正規化してから update filter に通す。Git でないツリーではベストエフォートの filesystem probe にフォールバックする。`**` も Git の path-form globstar の場合だけ特別扱いし、それ以外は通常の single-segment wildcard として扱う。ignore ファイルが読めない場合は、そのディレクトリ範囲を fail-closed で扱い、full scan では subtree を飛ばし、scoped refresh では不完全なルールのまま index を更新しない。後勝ちの negation により、秘密情報、生成コード、fixture、ビルド成果物を index から外しつつ、Git でないツリーに対する cdidx 既定の挙動も崩さない。
- **パス考慮の絞り込みとランキング** — `search`、`definition`、`references`、`callers`、`callees`、`symbols`、`files` はパス include/exclude フィルタと `--exclude-tests` を共有する。読み取りクエリは tests や docs より source を優先し、全文検索はシンボル名やパスの exact match を追加ブーストして、実装ファイルを先に返しやすくする。
- **AI向けの軽量検索スニペット** — `search --json` と MCP の `search` は、チャンク全文ではなく snippet range、match line、highlight、context count、`truncated_line_count`、`truncation_context` を持つ一致中心スニペットを返す。`truncation_context.char_counts` と `truncation_context.total_chars` はクランプされた各スニペット行の省略文字数を公開し、truncated な highlight も `truncated_char_counts` を持つ。`--snippet-lines` でペイロード量と文脈量のバランスを取れ、`--max-line-width`（CLI）/ `maxLineWidth`（MCP）は `find` / `references` / `excerpt` / `inspect` と同じ共有 `LineWidthFormatter.ClampLine` 契約で各スニペット行を最初のマッチトークン周辺にクランプするため、minified / transpiled / 生成された 1 行ファイル内の 1 ヒットで数百 KB を返さなくなる。クランプされた行はスニペットに `...(+N)...` マーカーが入り、`highlights[].truncated` と `highlights[].original_line_length` で AI クライアントがクランプを検出できる。
- **初動向けの repo map** — `map` は、インデックス済みデータから言語、モジュール、主要ファイル、ホットスポット、推定エントリポイントを集約し、AIクライアントが精密検索前に見るべき場所を決めやすくする。シンボル抽出が `Main` 系シンボルを出さない場合でも、既知のトップレベル実行ファイルへフォールバックして入口候補を補う。
- **信用判断のための鮮度メタデータ** — `status` はワークスペース全体の鮮度と git 状態を返す。`map` は `indexed_at` / `latest_modified` を絞り込み結果の鮮度として維持しつつ、`workspace_indexed_at` / `workspace_latest_modified` でワークスペース全体の鮮度も返す。`inspect` も同じワークスペース鮮度と git フィールドを返すため、シンボル中心の AI フローで `status` を別途呼ばずに済む。さらに `status` は `sql_graph_contract_ready` / `sql_graph_contract_degraded_reason`、`hotspot_family_ready` / `hotspot_family_degraded_reason` に加えて、forward-compatibility 監査 (`index_writer_version`、`index_newer_than_reader`、`index_newer_than_reader_reason`、詳細は「リーダー側の forward-compatibility 監査」を参照)、および fold-only remediation 用の `fold_ready_reason`、`degraded_reason`、`recommended_action`、`alternative_action` も返すため、AI クライアントは SQL graph/dependency/impact、duplicate-name hotspot family、Unicode `--exact` のどれが authoritative か、また DB が現在の binary より新しい `cdidx` で書かれていないかを最初に判断できる。現行の全体 scan 後は `unknown_extension_file_count` も返すため、未知拡張子で index 対象外になった件数を `status` から確認できる。これらの fold-only remediation field は、明示的な read-only `file:///...?...` DB URI から導出された場合でも、失敗する read-only URI をそのままコマンドへ埋め込まず、writable な filesystem path に正規化して返す。さらに `impact` / MCP `impact_analysis` に加えて、`inspect` / MCP `analyze_symbol`、`references` / `callers` / `callees`、`deps` / `unused` / `hotspots` 系も、SQL ベースの graph/dependency read が実際に結果へ関与したときだけ `sql_graph_contract_ready` / `sql_graph_contract_degraded_reason` を反映するため、stale な SQL 行が authoritative なヒットや 0 件応答に見えてしまうのを防ぎつつ、mixed-language index 内の純粋な非SQL結果を誤って degraded 扱いしない。`files` はファイルごとの checksum・modified・indexed timestamp を返す。古いDBに対する file 列の移行は可能なら自動で行い、その場移行できない場合でも読み取り経路がクラッシュしないようにする。CLI と MCP の 0 件 JSON レスポンスは `indexed_file_count`、`indexed_at`、`freshness_available` を含む。`freshness_available=true` で `indexed_at:null` なら空インデックス、`freshness_available=false` なら legacy/read-only DB で鮮度 timestamp を取得できず、理由は `freshness_degraded_reason` に入る。**HEAD 起点の stale 検知**: `cdidx index` の full scan が成功するたびに、現時点の `git HEAD` を `codeindex_meta` に stamp し、後続実行で workspace HEAD と比較できるようにする。`--rebuild` 指定なしに両者が異なる場合、CLI は `cdidx index <projectPath> --rebuild` を勧める `head_changed` 警告を表示し、`index --json` に `head_changed` / `prior_indexed_head_commit` / `current_head_commit` / `head_change_notice` を出力する。`status --check` も同じ比較を `workspace_check.head_changed` として公開し、差分時には `indexed_head_commit` / `workspace_head_commit` も併記するため、鮮度 gate ですでに `status --check` を通している AI クライアントは `git switch <branch>` 後の既定の incremental scan を別クエリなしで拒否できる。`--commits` / `--files` の部分更新は意図的に記録 HEAD を維持し、次の full scan が worktree を再インデックスするまで stale 通知が継続する。非 Git workspace と HEAD を記録していない legacy DB は比較自体をスキップし、false-positive な警告を出さない。
`unknown_extension_files` は `unknown_extension_file_path_limit` 件と decoded-character budget の両方で上限付けされた未知拡張子 path sample で、`unknown_extension_files_truncated` は件数上限または decoded-character budget により未出力の path が残ったことを示します。`unknown_extension_file_path_limit` は item 上限であり、常にその件数まで返す保証ではありません。

`extractors` は extractor plugin と pattern config の runtime health で、読み込み済み plugin assembly / pattern 件数、symbol/reference extractor 件数、parent load context が 0 であることと isolated-worker lifecycle、skip されたファイル数、上限付き diagnostics list を返します。

`hooks[]` は `callback_budget_ms` を含み、`CDIDX_HOOK_CALLBACK_BUDGET_MS`（既定値: 5000 ms）で強制される post-extraction callback 予算を反映します。hook は結果反映前の scratch copy 上で実行されるため、timeout した callback の変更は破棄されます。

文書化された `status --json` trust contract は `fold_ready`、`fold_ready_reason`、`graph_table_available`、`issues_table_available`、`file_issues_data_current`、`migration_in_progress`、`sql_graph_contract_ready`、`sql_graph_contract_degraded_reason`、`hotspot_family_ready`、`hotspot_family_degraded_reason`、`csharp_symbol_name_ready`、`csharp_metadata_target_ready`、`csharp_metadata_target_degraded_reason`、`indexed_head_commit`、`worktree_head_changed`、`indexed_head_sha`、`indexed_head_branch`、`indexed_head_timestamp`、`commits_ahead_of_indexed_head`、`index_writer_version`、`index_newer_than_reader`、`index_newer_than_reader_reason`、`unknown_extension_file_count`、`unknown_extension_files`、`unknown_extension_files_truncated`、`unknown_extension_file_path_limit`、`extractors`、`path_case_sensitive`、`stale_after_seconds`、`index_age_seconds`、remediation field の `degraded_root_cause`、`degraded_reason`、`recommended_action`、`alternative_action`、`readiness_degradations`、および MCP 専用の `mcp_session` を対象にします。MCP `mcp_session` は永続化された DB 状態ではなく、セッション単位の診断情報で、`log_level`、`roots`、任意の `client_info`、任意の `client_capabilities` を含みます。この一覧は `README.md` と `AGENT_GUIDE.md` に同期してください。いずれかの必須 field がこれらの docs から漏れると `DocumentationStatusContractTests` が失敗します。
- **再解析不要の folded-key アップグレード** — `backfill-fold` と MCP `backfill_fold` は、既存 DB 行から `name_folded` / `*_folded` を直接再計算し、必要な folded 値に NULL が残っていないことを検証してから `FoldReadyFlag` を stamp する。これにより、pre-#86 DB から AI クライアントやユーザーが低コストで Unicode `--exact` へ上がれる。さらに `fold_key_version` が未記録または不一致なら全 folded 行を再生成するため、将来の `NameFold.Version` 変更後に古い key を silent に再 stamp してしまうことも防ぐ。
- **まとめて取るシンボル分析** — `inspect` と MCP の `analyze_symbol` は、定義、近傍シンボル、参照、caller、callee、ファイルメタデータ、ワークスペース信頼メタデータ、graph 対応メタデータを1回で返し、AIクライアントが一般的なシンボル調査を少ない往復で終えやすくする。
- **言語考慮の参照抽出** — `references`、`callers`、`callees` は、正規表現ベースの call/reference 抽出が意味を持つ言語だけに対してインデックス化された参照テーブルで支える。未対応言語では、低信頼な疑似グラフ結果を返す代わりにテキスト検索へ戻る前提で設計する。**nested generic 呼び出し**: `new Dictionary<string, List<int>>()` のような C#/Java のコンストラクタ呼び出しと、`Helper.DoWork<List<int>>()` のような C# generic method call は、平坦な regex fast-path で `>>` を釣り合わせられなくても depth-aware fallback scanner で拾い直し、外側 target を参照テーブルへ残す。**コンストラクタ連鎖呼び出し**: C# の `: this(...)` / `: base(...)` イニシャライザと、Java のコンストラクタ本体冒頭文 `this(...)` / `super(...)` は、汎用 call regex とは別に検出し、呼び先が実際のコンストラクタとなるように書き換える（`this` は外側の class/record、`base` / `super` は外側クラスのシグネチャから解析した基底型）。C# のクロス行イニシャライザは外側クラスではなく、そのコンストラクタに紐付ける。基底型の解析は generic 引数、record のプライマリコンストラクタ引数、`where` 制約、`global::` やドット付きの namespace 修飾を剥がす。Java の `super.method()` は通常のメソッド呼び出しのまま扱う。**型位置の依存エッジ**: C#/Java の継承リスト、宣言型、generic 制約、`throws`、`is` / `as` / `instanceof`、および実際の C# XML doc `///` `cref` は `type_reference` 行として索引し、既定の `callers` / `callees` が見せる動的 call graph を汚さずに、`references` / `impact` から compile-time rename 依存を辿れるようにする。**SQL qualified-name alignment**: SQL の graph/dependency reader は、各 reference 行の source-line context、記録済み call 列位置、enclosing container から SQL 参照名を復元して定義と照合するため、qualified な `references` / `callers` / `impact` query は exact / non-exact を問わず sibling schema へ widen しない。source 側が genuinely unqualified な場合にだけ bare leaf fallback を許可するので、qualified call を含む `deps` / `unused` / `hotspots` も schema 単位で整合し、`EXEC dbo.fn_Target; EXEC sales.fn_Target;` のような同一行 multi-call も二重計上しない。列位置が記録されている row は、その列に qualified token が見つからなければ whole-line の別 qualified token へ昇格させないため、行末コメント・文字列リテラル・後続の別 call が先頭の unqualified edge を横取りすることもない。qualified な `callees` query でも caller query 自体が unqualified なとき以外は leaf fallback を無効化したため、`callees sales.Caller` が `dbo.Caller` へ広がらない。SQL extractor は qualified-name の `.` 前後空白も許容し、definition 系 reader は quoted qualified SQL name (`[dbo].[fn_X]` → `dbo.fn_X`) を正規化してから照合する。さらに exact SQL 定義照合は segment 数を保持し、SQL の exact graph leaf fallback は Unicode folded exact path を維持する。SQL CTE 本体内の source 行は raw `cte_body_reference` kind を使うため、`references --kind cte_body_reference` で anchor/recursive member 内部を outer query の table reference と区別できる。そのため、quoted single identifier の衝突や Unicode exact lookup の ASCII-only `NOCASE` 退行も防ぐ。
  exact な SQL の graph/dependency reader は解決済み segment 数も保持するため、`"sales.fn_Target"` のようなドット入り quoted single identifier が、本物の qualified name `sales.fn_Target` と exact `references` / `callers` / `impact` や集計系の `deps` / `unused` / `hotspots` で衝突しない。
- **言語考慮の参照抽出** — `references`、`callers`、`callees` は、正規表現ベースの call/reference 抽出が意味を持つ言語だけに対してインデックス化された参照テーブルで支える。未対応言語では、低信頼な疑似グラフ結果を返す代わりにテキスト検索へ戻る前提で設計する。**nested generic 呼び出し**: `new Dictionary<string, List<int>>()` のような C#/Java のコンストラクタ呼び出しと、`Helper.DoWork<List<int>>()` のような C# generic method call は、平坦な regex fast-path で `>>` を釣り合わせられなくても depth-aware fallback scanner で拾い直し、外側 target を参照テーブルへ残す。**JS/TS の no-paren constructor**: JavaScript / TypeScript の zero-argument constructor call で `()` を合法的に省略できる `new Foo;`、`new Date;`、`new Demo.Provider;`、`new Box<number>;` も、専用の言語別経路で `instantiate` edge として出す。行末 `new Foo` に対する次行 `.bar()` / `[0]` continuation は suppress し、phantom な単独 instantiation にしない。**コンストラクタ連鎖呼び出し**: C# の `: this(...)` / `: base(...)` イニシャライザと、Java のコンストラクタ本体冒頭文 `this(...)` / `super(...)` は、汎用 call regex とは別に検出し、呼び先が実際のコンストラクタとなるように書き換える（`this` は外側の class/record、`base` / `super` は外側クラスのシグネチャから解析した基底型）。C# のクロス行イニシャライザは外側クラスではなく、そのコンストラクタに紐付ける。基底型の解析は generic 引数、record のプライマリコンストラクタ引数、`where` 制約、`global::` やドット付きの namespace 修飾を剥がす。Java の `super.method()` は通常のメソッド呼び出しのまま扱う。**型位置の依存エッジ**: C#/Java の継承リスト、宣言型、generic 制約、`throws`、`is` / `as` / `instanceof`、および C# XML doc の `cref` は `type_reference` 行として索引し、既定の `callers` / `callees` が見せる動的 call graph を汚さずに、`references` / `impact` から compile-time rename 依存を辿れるようにする。C# XML doc の `cref` 抽出は、実際に後続宣言へ結び付く XML-doc comment である `///` 行と delimited `/** ... */` block の両方を対象にしつつ、通常の `//` / `////` コメントや通常の block comment は phantom 依存として扱わない。また、同じ物理行でも closing `*/` より後ろに続く code / string の内容、doc comment と後続宣言の間へ割り込むトップレベル実行文、brace-free field/property initializer continuation、brace-free expression lambda、nested executable continuation、複数行 raw/verbatim string のうち行頭がたまたま `/**` で始まる内容は doc-comment slice の外として扱う。regex 自体は narrowed した doc-comment slice に対して走らせるが、`symbol_references.column` は元の物理ソース行位置に固定したまま保持する。C# の read path では、`using static` による constant-pattern suppress が `is` / `case` の前後の trivia を考慮してトークン単位で判定され、anchor が前行にある場合は anchor-aware な複数行コンテキストをインデックス済み行から再構成するため、`value is/*comment*/Red`、`value is\n    Red or Blue`、`value is\n    // comment\n    Red`、`case\n    // comment\n    Point:`、長い `case` / `or` 連鎖、`case\tRed:` のような形でも phantom `type_reference` を漏らさない。qualified constant/member pattern は exact-name read path でも qualifier 起点で suppress するため、`case Color.Red or Color.Blue:` に対して無関係な `class Red {}` が suppress を打ち消さない。extractor 側の pending type-pattern carry も trivia-only 区切り行、standalone な continuation-line `not`、複数行 `case` head / logical continuation をまたいで維持されるため、comment-only 行や `not` だけの継続行で後続の本物の type head を落とさない。`case > 0:` や `case not > 0:` のような非型 `case` ラベルではその pending carry を armed にしないため、次行の call/identifier token が `type_reference` に混入しない。同名型の rescue も `file` 可視性を尊重し、file-local な型は同じ物理ファイル内の参照だけを救済する。基底クラスから見える protected/public/internal nested type は、基底型参照を active な型 alias / namespace alias 経由まで正規化し、さらに alias 展開後に constructed generic な基底型を再 canonicalize したうえで derived class の pattern head を救済する一方、implemented interface は inherited nested-type rescue に参加しない。さらに same-file `using Namespace;`、project-wide `global using Namespace;`、型 alias も同じ rescue 集合に入る。一方で extractor は file-local な情報だけでは同一 namespace の別ファイルにある実型を判定できないため、`value is Red` のような曖昧な unqualified `using static` head は DB に残し、pure constant-only case の抑止は workspace-aware な read path 側で行う。**SQL qualified-name alignment**: SQL の graph/dependency reader は、各 reference 行の source-line context、記録済み call 列位置、enclosing container から SQL 参照名を復元して定義と照合するため、qualified な `references` / `callers` / `impact` query は exact / non-exact を問わず sibling schema へ widen しない。source 側が genuinely unqualified な場合にだけ bare leaf fallback を許可するので、qualified call を含む `deps` / `unused` / `hotspots` も schema 単位で整合し、`EXEC dbo.fn_Target; EXEC sales.fn_Target;` のような同一行 multi-call も二重計上しない。列位置が記録されている row は、その列に qualified token が見つからなければ whole-line の別 qualified token へ昇格させないため、行末コメント・文字列リテラル・後続の別 call が先頭の unqualified edge を横取りすることもない。qualified な `callees` query でも caller query 自体が unqualified なとき以外は leaf fallback を無効化したため、`callees sales.Caller` が `dbo.Caller` へ広がらない。SQL extractor は qualified-name の `.` 前後空白も許容し、definition 系 reader は quoted qualified SQL name (`[dbo].[fn_X]` → `dbo.fn_X`) を正規化してから照合する。さらに exact SQL 定義照合は segment 数を保持し、SQL の exact graph leaf fallback は Unicode folded exact path を維持する。SQL CTE 本体内の source 行は raw `cte_body_reference` kind を使うため、`references --kind cte_body_reference` で anchor/recursive member 内部を outer query の table reference と区別できる。そのため、quoted single identifier の衝突や Unicode exact lookup の ASCII-only `NOCASE` 退行も防ぐ。exact な SQL の graph/dependency reader は解決済み segment 数も保持するため、`"sales.fn_Target"` のようなドット入り quoted single identifier が、本物の qualified name `sales.fn_Target` と exact `references` / `callers` / `impact` や集計系の `deps` / `unused` / `hotspots` で衝突しない。
- **推移的 impact analysis** — `impact` と MCP `impact_analysis` は、シンボルの推移的 caller chain を BFS で計算する。caller matching は substring expansion と大小文字差の脆さを避けるため `lower() = lower()` の大小文字非依存 exact match を使い、symbol 名は exact-case を優先して definition から事前解決し、read path は graph-supported language に限定して削除済み言語の stale edge を防ぐ。heuristic fallback が使う definition set も `--lang` / `--path` / `--exclude-path` / `--exclude-tests` と graph-supported language を尊重し、class-like definition だけを fallback 候補にするため、同名 namespace/import sibling は単一の class / struct / interface target を妨げず、純粋な non-callable `namespace` / `import` query は `non_callable_symbol_kind` guidance を返す。heuristic file-level hints は成功応答だが non-authoritative status を `impact_mode`、`heuristic`、`hint_count`、`truncated` で示し、caller rows は `result_kind: "graph"`、heuristic `file_impacts` rows は `result_kind: "file_heuristic"` を持つため、クライアントは list 位置や depth 値から推測せずに authoritative hop-depth graph 結果と境界 fallback hint を区別できる。`truncated` が `true` のときは JSON / MCP payload に `truncated_reason` も出し、`user_limit` は caller 指定の `--limit` 到達、`safety_cap` は内部の per-symbol BFS fetch-iteration cap 到達を意味する。`impact` / MCP `impact_analysis` は `termination_reason`（`completed`、`max_depth_reached`、`cycle_detected`、`row_limit_truncated`、`safety_cap`、`cancelled`）、`cycle_detected`、`cycles` も出すため、caller cycle と通常完了や limit/depth termination を区別できる（#1883）。`safety_cap` は `user_limit` より優先し、heuristic file-level hints path は caller の `--limit` だけで切り詰められるため `user_limit` のみを使う。`truncated` が `false` のとき `truncated_reason` は省略される。（#1533）`count` / `file_count` は返却された可視集合、`confirmed_count` / `confirmed_file_count` は heuristic-success payload の symbol-level caller totals を保持し、`impact --json --count` も full payload と同じ `*_count` field 名を使う。一般名の衝突を減らすため、type fallback では候補 member 名への参照に加え、同一ファイル内に source/target pair を anchor する証拠が必要になる。証拠は解決済み target 名への `call` / `instantiate` reference（この経路は call graph 自体が関係を pin するため metadata-attribute bypass より先に走り、緩い ambiguity guard に依存しない）、または signature / return type など indexed symbol metadata からの structured type evidence に限り、comment/string の raw text match は使わない。call/instantiate anchor は解決済み名を exact に照合し、suffix-strip alias は使わない。callable reference はすでに authoritative identifier を持つため、C# の `[Foo]` → `FooAttribute` alias をここへ適用すると無関係な `Foo()` method call が `impact FooAttribute` を偽 anchor できてしまうためである（#1881）。metadata bypass は attribute use site が正当に target 名を省略するため C# `Attribute` suffix alias を維持する。signature evidence path は Unicode-aware で、hint `reference_count` は実際に一致した reference row 数を表し、symbol list は deduplicate される。fallback ambiguity は同じファイル内であっても複数の class-like definition がある場合だけ扱い、`PurgeUnsupportedReferences` は CLI full scan、CLI update mode、MCP index のすべての indexing path で走る。
- **impact cycle の identity** — 現行の reference-identity graph では、`impact` は解決済み source/target symbol ID を BFS の全 hop へ引き継ぎ、実際に走査した正規 ID 間の有向辺だけから cycle を判定する。表示名の重複や fold 一致だけではゼロホップ cycle にせず、永続化された辺の source/target ID が同一になる直接再帰は実在する singleton cycle として残す。未解決または曖昧な名前一致 edge と、一意でない `resolved_group` の overload 候補は保守的な traversal 出力には残すが正規 cycle graph には入れず、同じ caller から複数の同名 target identity への参照は callee ID を捏造せず 1 caller row に集約する。構造化 caller row は `caller_symbol_id` / 一意に解決できた `callee_symbol_id`、`--with-paths` の node は一意な identity の場合だけ `symbol_id`、cycle row は互換用の表示名 `members` に加えて `member_identities` を公開する。現行 identity 契約を持たない legacy graph は名前キーの互換経路を維持する（#4847）。
- **構造化MCPレスポンス** — MCPツール呼び出しは `structuredContent` に型付きJSONを返し、`content` は互換性のため簡潔に保つ。
- **MCP の pre-validation rate limiting と bucket eviction** — direct な `tools/call` はすべて tool 名、enablement、argument の詳細検証前に caller-wide の固定 coarse bucket を 1 つ消費する。canonical な既知 tool 名は secondary `(tool, caller)` bucket も維持し、missing、malformed、empty、oversized、case-variant、unknown な名前は名前由来 bucket を作成しない。unknown な `batch_query` inner-slot 名は caller ごとの 1 つの固定 invalid-slot partition を共有する。`CDIDX_MCP_RATE_LIMIT_BUCKET_IDLE_SECONDS` は既定 900 秒。process-local 上限到達時は期限切れ bucket を直ちに prune する。layered acquisition は両 partition を 1 lock 内で評価し、secondary 拒否前に coarse token を消費した場合は、`retry_after_ms` が必要なすべての token refill と capacity 境界を含む。これにより malformed call の既知名ローテーションによる burst 増幅と未信頼名による cardinality 増加を防ぎ、正規の bucket 作成は通知された時刻に回復でき、過去の caller ID もプロセス寿命いっぱい保持しない（#2824 / #4547）。
- **MCP envelope レスポンス上限** — `CDIDX_MCP_RESPONSE_MAX_BYTES` は既定 10 MiB、最大 64 MiB。invalid 値は既定値へ戻し、最大超過値は stderr 警告付きでクランプするため、operator が誤って JSON-RPC response guard を実質無効化できない。
- **MCP `batch_query` レスポンス上限** — `batch_query` は集約した slot 結果の UTF-8 JSON サイズを見積もり、`CDIDX_MCP_BATCH_RESPONSE_MAX_BYTES`（既定: 1 MiB / 1,048,576 bytes、最大: 10 MiB）を超える場合は追加を止める。切り詰めたレスポンスには `truncated: true`、`truncated_queries`、byte limit メタデータを含めるため、クライアントは prose を parsing せず batch 分割や slot limit 縮小を判断できる (#1416)。invalid 値は既定値へ戻し、最大超過値は stderr 警告付きでクランプし、有効値は MCP `status` の `mcp.limits.batch_response_bytes` で確認できる。
- **HTTP MCP response / stream 上限** — `HttpMcpTransport` は通常の JSON response body を `CDIDX_MCP_HTTP_MAX_RESPONSE_BYTES`（既定 1,000,000 bytes、最大 16,777,216 bytes）で制限する。response / SSE write timeout は stable diagnostic を使い、期限超過時は HTTP response を abort する。
- **HTTP MCP aggregate request-body budget** — `CDIDX_MCP_HTTP_MAX_IN_FLIGHT_REQUEST_BYTES` は read、queued frame、MCP 実行、response 完了にまたがる process-wide の request-body reservation を制限する。既定値は 64 MiB、最大値は 1 GiB で、request 単位上限以上でなければならない。飽和時は分類済み HTTP 429 で拒否し、queue depth と request 単位 size の積が memory 上限になることを防ぐ (#4548)。
- **MCP の有界 queue / concurrency gate** — HTTP request queue は `TryWrite` 前に slot を取り、満杯時は handler を block せず HTTP 429、`Retry-After: 1`、`X-Cdidx-Mcp-Rejection: request_queue_limit` で拒否する。POST handler と長寿命 event stream は独立した admission semaphore を使い、HTTP health は両方の有効 capacity と `http_separate_event_stream_handlers` を返す。limit 環境変数は未設定の場合だけ既定値を使い、設定済みの malformed 値または範囲外値は listener 起動前に失敗する。transport 所有 gate は bounded shutdown で全取得 slot の返却を確認できた場合だけ dispose する。
- **MCP pagination offset 上限** — `references`、`callers`、`callees` は SQL query 実行前に `offset` を 10,000 へクランプする。`tools/list` は各 offset schema に最大値を広告し、MCP `status` も `mcp.limits.max_pagination_offset` に同じ値を返す。
- **MCP compact discovery と status projection** — `tools/list` は完全 response を既定値および明示的な `format: "full"` 契約として維持する。opt-in の `format: "compact"` は長い説明、schema、example、catalog metadata を上限付き要約と完全定義の on-demand 取得方法へ置き換える。`names` は有効化済み tool の exact name だけを filter し、128 文字の名前を最大 24 件まで受け付ける。opaque な continuation cursor は compact / name-filtered control を保持し、name-filtered な full response は返却 list を限定 scope と明示しつつ、有効な全 tool から capability metadata を構築する。compact schema は意図的に非 authoritative であり、その旨を `_meta` に明示する。MCP `status.fields` は `format` と任意 diagnostic attachment の構築後に exact な top-level field を project し、`api_version` はすべての structured result に残す。projection 入力は最大 32 件、各 128 文字、合計 2,048 文字で、未知名と nested path は `invalid_argument` とする。新しい discovery / status mode を追加する場合は、引数なし response を byte-for-byte で維持し、size と互換性の回帰テストを追加すること。
- **MCP ファイル resource discovery** — `resources/templates/list` は `cdidx://file-path/{path}` を公開し、正確なリポジトリ相対 path が既知の client は全 inventory をページングせず `resources/read` URI を構築できる。simple URI-template expansion は separator と `?` / `#` などの予約 filename 文字を percent encode する。template 専用 resolver は値を一度だけ decode し、absolute path、traversal、backslash、空 segment、query、fragment を拒否して canonical な `cdidx://file/<path>` identity を返す。canonical resource URI は encoded separator を引き続き拒否する。`resources/list` は `path` に 1 文字列または各 1024 文字・wildcard operator 128 個以内かつ最大 100 件の文字列を受け付け、file query と同じ anchored directory / glob semantics を使うほか、正規化した完全一致の `lang` と `includeGenerated`（既定 `false`）も受け付ける。cursor は generation と canonical filter の両方に結び付き、ページ間の filter 変更は `-32602` / `resources_list_filters_changed` と `restart_required: true` を返す。generated file は discovery と direct read のどちらでも `includeGenerated: true` が必要である。
- **MCP resource list カーソルの安定性** — `resources/list` は、最後に消費した file id と永続化されたインデックス済みファイル世代を結び付ける固定長の不透明 keyset cursor を返す。reader は同じ SQLite snapshot 内でその id を既存の source/test/docs bucket と path の並び順へ解決する。ファイルの追加・削除・更新で世代が変わると、後続ページは `restart_required: true` 付きの `-32011` / `index_stale` を返し、混在 snapshot を続行せず `params.cursor` を省略して再開する必要がある。書き込み可能な legacy DB は cursor 発行前に通常の read migration で世代 row と trigger を導入する。世代追跡を証明できない変更可能な read-only legacy DB は `migration_required: true` 付きの `resources_list_generation_unavailable` を返すが、canonical かつ曖昧性のない `immutable=1` legacy URI（任意で `mode=ro` を併記）はページ間で変化しないため connection-local な世代 0 を安全に利用できる。encoded、case variant、空白付き、重複、競合、または余分な query parameter はこの immutable 保証として信頼しない。旧 decimal の 0 は先頭ページ用の移行入力として残すが、0 以外の decimal offset は発行時世代を検証できないため同じ再開必須 error を返し、decimal cursor は出力に使わない。
- **MCP resource list の response budget** — `resources/list.params.maxBytes` は 4,096〜1,000,000 bytes を受け付け、既定値は HTTP response body の既定上限と同じ 1,000,000。有効 budget はこの要求値、server-wide MCP envelope 上限、および該当時の active HTTP transport response-body 上限の最小値とし、HTTP の設定上限が低い場合も HTTP 500 で拒否せず有効なページへ整形する。server は JSON-RPC envelope 全体を計測し、candidate 上限を 200 件に保ったまま、次の resource で有効 byte budget を超える直前に停止する。HTTP JSON-RPC batch では active transport budget を bracket と comma を含む response 配列全体へ適用し、応答対象 item へ公平に分配する。notification は response slot を消費しない。各 `resources/list` item は現在の割当を守り、有界なページさえ収まらない場合も canonical な budget error に request ID を保持する。state-changing item とその他の non-resource outcome を retry-safe として付け替えることはなく、実行後の aggregate overflow は completion state が unknown で自動再試行不可であることを報告する。`_meta.response_controls` は要求／有効 budget、消費／返却件数、`omitted_resource_count`、有界な理由別件数（`resource_uri_too_long` / `resource_exceeds_max_bytes`）、`byte_budget_reached`、`continuation_reason`（`byte_budget`、`item_limit`、`completed`）を返す。継続 cursor は最後に消費した DB row を anchor とし、収まらなかった有効 resource は次ページ用に未消費のまま残すが、空ページにも収まらない resource は消費して件数へ計上し、pagination の livelock を防ぐ。
- **MCP 配列引数の上限** — `path` / `project` / `excludePaths` / mixed `names` などの string-array filter は、不正要素を暗黙に落とさず拒否する。配列は 100 件、各要素は 4096 文字を上限とし、`batch_query` では `request_index` と `ok: false` 付きの slot 失敗として報告する。
- **MCP schema のロックダウン** — すべての tool `inputSchema` は `additionalProperties: false` を含み、`tools/call` も同じ契約として未知の引数名を黙って既定値にせず `-32602` / `invalid_argument` で拒否する。
- **MCP stability marker と命名** — すべての tool は `x-stability`（`stable`、`experimental`、`deprecated`）を公開する。MCP の構造化 payload key は CLI JSON 契約に合わせて snake_case を使う。新規 field に camelCase alias を追加しないこと。
- **MCP の言語サポート句** — 公開されるすべての MCP ツール説明は、`McpServer.CreateToolDefinition` で生成される `Language support:` 句で終わる。Graph 系ツールは `ReferenceExtractor.GetSupportedLanguages()`、symbol 系ツールは `SymbolExtractor.GetSupportedLanguages()`、file/content 系ツールは `cdidx languages` と同じ検出言語カタログを参照するため、`tools/list` は手書き説明ではなく実行時レジストリと同期する。
- **MCPツールアノテーション** — 全ツールが MCP 仕様に沿った `annotations`（`readOnlyHint`、`destructiveHint`、`idempotentHint`、`openWorldHint`）を返し、AIクライアントが安全な読み取り専用クエリを自動承認できるようにする。
- **MCPサーバー instructions** — `initialize` レスポンスにツール選択ガイダンスの `instructions` 文字列を含め、AIクライアントが初回接続時に適切なツールを選べるようにする。
- **デプロイ単位での MCP ツール有効化** — `cdidx mcp` が 2 つの環境変数を尊重し、コード変更なしに公開ツールを絞れるようにする (#1561)。`CDIDX_MCP_TOOLS_ALLOW=<csv>` は厳格な allowlist で、指定された場合はそのツールだけが `tools/list` に現れ `tools/call` で dispatch される。`CDIDX_MCP_TOOLS_DENY=<csv>` は既定の全有効集合から個別ツールを除外する。両方指定された場合は allow を優先。既知ツール名の真実の源は `McpToolFilter.KnownToolNames` に集約し、`tools/list` 側の filter、`tools/call` 側のゲート、`batch_query` の slot ガードのいずれもここを参照する。`BuildInstructions` もゲート対応で、scoped デプロイの `initialize` instructions では無効化されたツールを推奨しなくなり、案内と公開面が一致する。トップレベル `tools/call` で無効化された既知ツールを呼ぶと `-32601 Tool not enabled: <name>` を返し、`batch_query` 自体はエンベロープとして成功するが各無効化スロットに `code: -32601` が `error` 文字列と並んで載るため、クライアントは prose を parsing せず code で分岐できる。サーバーに無い名前は既存の `-32602 Unknown tool` に流すことでオペレータ無効化と typo を区別できる。ツール名比較は大小文字無視、env var 内の未知名は既知集合で filter（typo で未知名のみの allowlist は意図的に何も公開しないため、ゲートが silent に外れない）。env var を一切設定しない既定挙動は全ツール有効なので、既存デプロイへの影響はない。
- **トリガー付きコンテンツ外部参照FTS5** — `chunks`テーブルを参照しコピーを保存しないことでストレージ倍増を回避。データベーストリガーでFTSインデックスを自動同期。
- **extractor regex の backtracking policy** — built-in symbol/reference extractor は repository-controlled な file content に対して unbounded regex match を使わない。backtracking regex は `BoundedRegex.DefaultMatchTimeout` を使い、`RegexOptions.NonBacktracking` は non-backtracking engine と互換な pattern で使ってよい。lookaround-heavy な extractor や balancing-group を使う extractor など、意図的に backtracking-only のまま残す pattern は、共有 timeout audit の対象になる場合だけ許容する。将来の extractor が `System.Text.RegularExpressions.Regex` を直接使う必要がある場合は、明示 timeout を渡し、`BoundedRegex` や `NonBacktracking` が適さない理由を文書化すること。
- **ハイブリッドなシンボル抽出** — ASTパーサーも重量級の言語固有依存も追加しない方針。大半の言語はコンパイル済み正規表現で処理し、JavaScript / TypeScript だけは class body の method 抽出、private-scope filtering、synthetic class expression の binding 判定、JS/TS 固有の range 解決など、正規表現だけでは壊れやすい箇所を軽量 lexer / state machine で補う。引き続き精度より速度とポータビリティを優先しつつ、言語パターンや JS/TS state machine から推論できる範囲で定義範囲、本体範囲、シグネチャ、親シンボル、修飾付きコンテナ経路、正式なグループキー、可視性、戻り値型も保存する。Visual Basic では `Namespace ... End Namespace` も実コンテナとして扱い、implicit visibility の宣言や `Shared` / `Overrides` / `Partial` など visibility 以外の先行修飾子も受理するようにしたため、他のクラス系言語と同じようにトップレベル構造とメンバーを取りこぼしにくくなった。Visual Basic のコンテナパターンは `VisualBasicEnd` ベースの範囲追跡を大文字小文字非依存で扱うため、partial 型ファミリーでも安定した本体範囲と `hotspots` 集計用メタデータを維持できる。**パターン外部化**: 言語パターンは現在 `SymbolExtractor.cs` 内にコンパイル済み `Regex` として定義。抽出パイプラインが自己完結し、コンパイル時検証が効くが、言語追加にはコード変更と再ビルドが必要。将来的にはJSON/TOMLファイルに外部化し（起動時読み込み）、コミュニティ貢献の敷居を下げ、開発時のホットリロードも可能にできる。トレードオフはコンパイル時安全性の喪失と起動コストの微増。外部化時のスキーマ: 言語名、種別（function/class/import/namespace）、正規表現文字列、本体スタイル（brace/indent/ruby-end/none）、可視性・戻り値型のキャプチャグループ名。
- **C# の nested interpolation state** — C# lexical masker は、別の interpolation hole 内で interpolated regular / verbatim / raw string が始まると immutable な親 frame を保持する。nested string を閉じると外側の mode、delimiter、dollar count、brace depth を完全に復元し、expression-bodied property 内の call を declaration pattern から除外する。C# extractor contract の更新により、既存 index の対象ファイルも再抽出される。
- **C# static lambda の宣言ゲート** — 宣言 scanner は、確認済みの `static`、`static async`、`async static` lambda header 内にある候補名を式コンテキストとして扱います。複数行 property-header の結合によって呼び出し引数が前置された場合も同様です。本物の static member、local function、代入済み lambda symbol は引き続き抽出対象です。C# extractor contract v6 により、通常の index 更新で古い C# symbol が再抽出されます（#4830、#4453 の回帰）。
- **`hotspots` の正式な family trust** — `hotspots` が重名グループをコードベース全体の件数へ昇格させるのは、永続化済み `symbols.container_qualified_name` / `symbols.family_key` が現行の言語別 `hotspot_family_version_*` 契約で生成されたときだけ。readiness stamp と marker fingerprint は `codeindex_meta` に置き、旧形式・混在・部分更新直後の DB は古いファイル横断グループ識別子を黙って再利用せず、明示的に縮退する。
- **C# metadata-target の正式な trust** — `deps` / `impact` の metadata attribute edge（`[Foo]` 使用と定義側 `FooAttribute` クラスの紐付け）は、永続化済み `is_metadata_target` が現行の `metadata_target_version_csharp` 契約で stamp されている DB ではシグネチャ形状ヒューリスティックではなく authoritative resolver の判定結果を使う。resolver は C# クラスの base list を同 DB 内の class 行で fixed-point 展開して解決し、未解決の外部基底のみ BCL 規約（`Attribute` サフィックス）へフォールバックする。readiness は `codeindex_meta` に置き、reader は (1) ready → `is_metadata_target = 1`、(2) 列はあるが stamp 未完（legacy 行）→ `signature LIKE '%: %'`、(3) 列すらない → 命名のみ、の 3 way 分岐で縮退する。これにより、`class FooAttribute : BaseService` のような非 attribute 同名 impostor が真の `FooAttribute : Attribute` と同居したときにエッジを黙ってドロップする挙動を修正した（#435）。
- **人間向けがデフォルト** — 全コマンドのデフォルト出力は人間向け。`--json`でAI/機械向け出力。
- **手動引数解析** — `System.CommandLine`は依存削減のため削除。シンプルなswitch文での解析。
- **後方互換なシンボルスキーマ** — 新しいバイナリで古いDBを開いたときは、可能なら不足するシンボル列を自動追加する。対象には `container_qualified_name` や `family_key` のような `hotspots` 用グループメタデータも含む。読み取り経路でその場移行ができない場合も、シンボル検索は旧カラム構成へフォールバックしてクラッシュを避ける。
- **bounded な hotspot 集約** — `DbWriter` は `hotspot_reference_counts` を file 単位の compact な logical-reference totals として維持する。limit 付き hotspot reader は rank index で固定上限の candidate frontier を先に選び、その後だけ non-SQL、SQL exact、SQL leaf、曖昧性、target-family の join を実行するため、complete `symbol_references` graph を毎回再 materialize しない。logical-site identity から raw alias を除外し、mutation 時は cross-file context 依存を再集計して reference-identity trust を transaction 内で降格し、reader/writer の aggregate SQL は cancellation で中断できる。writable な legacy database では table の作成と backfill を transaction 内で行い、immutable な legacy reader は raw-reference compatibility path を維持する。
- **SHA256チェックサム** — ファイルのraw bytesから算出しファイルごとに保存。タイムスタンプが異なる場合の変更検出フォールバックとして使用（例: `git checkout`後）。
- **UTF-8フォールバック** — 不正なUTF-8バイトはファイル全体を失敗させずU+FFFDに置換。
- **portable な信頼済み Git 選択** — Git subprocess は検証済みの既知 installation path または process-only の絶対パス override `CDIDX_GIT_EXECUTABLE` だけを選び、`PATH` 上の任意の `git` は解決しない。明示 override は `git`（Windows は `git.exe`）という名前の regular かつ symlink / reparse point / device ではない file でなければ fail-closed になる。POSIX candidate と canonical ancestor は effective user または root の所有で group / other write bit がないことを要求するが、`/tmp` や multi-user Nix store のような root 所有の sticky ancestor は受理する。Windows candidate と ancestor は信頼済み owner / write ACL を持ち、executable は有効な PE image でなければならない。受理する candidate は上限付きの `git --version` probe で `git version` と自己識別できなければならない。CLI / MCP の `status.git_executable` は sanitization 済み source、acceptance、stable な rejection reason、owner-only-write 判定、Unix mode、owner category、owner / ancestor trust、executable probe の結果を報告し、受理済み environment override は `trust_overrides[]` にも含まれる。
- **worktree対応のgit exclude** — `.cdidx/`を`.git/info/exclude`に自動追加する。worktreeでは`.git`がディレクトリではなくファイルのため、worktreeルートには`.git/info/exclude`が存在しない。`GitHelper.ResolveGitCommonDir()`で参照を辿り共通`.git/`を見つける:

  ```
  # 通常リポジトリ — .gitがディレクトリ
  /projects/my-app/                   ← プロジェクトルート
  ├── 📂 .git/                        ← ディレクトリ
  │   └── 📂 info/
  │       └── exclude                 ← ここに書き込む
  └── 📂 .cdidx/
      └── codeindex.db

  # worktree — .gitがファイル
  /projects/my-app/                   ← 元リポジトリ
  └── 📂 .git/                        ← 共有gitディレクトリ
      ├── 📂 info/
      │   └── exclude                 ← ここに書き込む
      └── 📂 worktrees/
          └── 📂 feature-branch/
              └── commondir           ← "../.."が入っている（2階層上 = .git/）

  /projects/my-app-feature/           ← worktreeルート
  ├── .git                            ← ファイル: "gitdir: /projects/my-app/.git/worktrees/feature-branch"
  └── 📂 .cdidx/
      └── codeindex.db
  ```

  解決手順: `.git` directory / file boundary の既存 component をすべて canonicalize・検証 → single-link の regular な `.git` file を読む → `gitdir:`を解析 → regular な directory target の全 component を検証 → single-link の regular な `commondir` file を読む → `../..`を`feature-branch/`ディレクトリ起点で解決（`feature-branch/` → `..` → `worktrees/` → `..` → `.git/`）→ common directory を検証 → `info/exclude`を atomic replacement で更新する。信頼されない symlink / reparse point redirect、device、multi-link file、entry kind 不一致、読取り不能な metadata entry は metadata write 前に fail-closed になるが、`/var` のような immutable かつ root 所有の POSIX system link は解決後に再検証する。

- **クロスコンパイルの linux-arm64 にランタイムスモークテストがない** — `release.yml` は x64 ランナー上で `linux-arm64` をクロスコンパイルする（`dotnet publish -r linux-arm64 --self-contained`）。ランナーが ARM バイナリをネイティブ実行できないためテストはスキップされる。理想的には QEMU ベースのスモークテスト（`cdidx --version`）をリリース前に実行すべきだが、GitHub Actions の無料枠ランナーには QEMU も ARM ランナーも含まれない。QEMU セットアップステップの追加は可能だが、リリースごとに CI の複雑さと実行時間が増す。.NET のクロスコンパイルは公式サポート機能で広く使われているため、実際に壊れたアーティファクトが出るリスクは低い。将来 ARM 固有の不具合が報告された場合、`docker run --platform linux/arm64` と QEMU の組み合わせが最初の対策となる。
- **CLI / MCP のみ — 公開ライブラリ API は提供しない (#1557)** — `cdidx` アセンブリは `OutputType=Exe` かつ `PackAsTool=true` で配布される .NET グローバルツールであり、library / SDK としての参照を意図していない。バージョニング契約の対象となるのは `cdidx` CLI（`--json` 出力を含む）と `cdidx mcp` の JSON-RPC サーバーだけである。アセンブリ上の `public` 型（例: `CodeIndex.Database.DbReader`、`CodeIndex.Models` / `CodeIndex.Database` 内の DTO）は CLI / MCP の構成と `CodeIndex.Tests` の `InternalsVisibleTo` 境界を満たすために露出しているにすぎず、deprecation cycle なしに変更・移動・`internal` 化されうる実装詳細である。embedder は、アセンブリではなく CLI / MCP / JSON 出力に依存することを想定している。詳細は [INTEGRATION_POLICY.md — API Surface and Library Use](INTEGRATION_POLICY.md#api-surface-and-library-use) を参照。将来、本物のライブラリ API が必要になった場合は、現アセンブリで偶然 `public` だった型に依存させるのではなく、独立したパッケージとして独自のインターフェイスとバージョニング契約を持たせて切り出す。
- **Extractor plugins (#1937)** — `CodeIndex.Indexer.Extensibility.ISymbolExtractor` と `IReferenceExtractor` は、サポート対象となる唯一の assembly-extension surface です。`cdidx` は既定でユーザー所有の `~/.cdidx/plugins/` ディレクトリから trusted plugin DLL を検出します。workspace `.cdidx/plugins/` の DLL discovery は、process が `CDIDX_TRUST_WORKSPACE_PLUGINS=1`（`true`、`yes`、`on` も可）を設定しない限り fail-closed です。workspace DLL のロードは checkout が提供するコードを実行するためです。plugin assembly は `[assembly: CdidxPlugin(minApiVersion: 1, maxApiVersion: 1)]` を宣言し、どちらかまたは両方の interface を実装する public parameterless type を公開する必要があります。plugin が新しい拡張子を所有する場合は `FileExtensions` を設定し、`FileIndexer` がそのファイルを plugin language へ route できるようにします。plugin は parent ではなく bounded worker 内で実行しますが、これは process isolation であり security sandbox ではないため、信頼できるローカル DLL だけをインストールしてください。この狭い契約により、チームは CodeIndex を fork せず DSL 固有の symbol/reference を追加できますが、一般的な library/SDK embedding API ではありません。

  Plugin DLL discovery は、directory ごとに `ExtractorPluginRegistry.MaxPluginAssemblyCandidatesPerDirectory` 件、process 全体で `ExtractorPluginRegistry.MaxPluginAssemblyCandidatesTotal` 件までに制限されます。各 candidate は `ExtractorPluginRegistry.MaxPluginAssemblyBytes` bytes 以下でなければなりません。discovery の切り詰めや oversized skip は、`status --json` / MCP `status` の `extractors.diagnostics` に報告されます。

  登録は、正規化済み workspace identity と language を key にする immutable workspace snapshot
  から解決されます。優先順位は決定的で、`extractors.registration_precedence` に高い順で
  built-in、user plugin、user pattern、workspace plugin、workspace pattern として公開されます。
  workspace plugin の assembly context と diagnostic は所有 snapshot に属し、snapshot の置換は
  その workspace の古い context だけを unload 開始するため、他 workspace の active 登録を書き換えません。
  reference purge、status/language reporting、database graph predicate の supported language は
  active workspace snapshot から解決されます。長時間実行 host が保持する workspace snapshot は
  LRU で最大32件です。replace、evict、MCP shutdown は snapshot を terminal に retire し、in-flight
  plugin load が retired state に到達した場合は遅延 commit を拒否して local context を unload します。

## reference_kind フィルタの対応表

グラフ系エントリポイントは、用途別に意図的に異なる `reference_kind` の部分集合だけを辿る。設計上の分割は **呼び出しグラフ vs 依存グラフ** に対応する。既定の `callers` / `callees` は実行可能な call、construction、subscription semantics に限定する一方、`hotspots` と `impact` は closure や generic invocation edge を含む、より広い依存関係寄りの traversal を維持する。`deps` と `impact` の heuristic file-level fallback はコンパイル時の依存グラフをモデル化するため、`[JsonConverter(typeof(User))]` や `@Inject(User.class)` も `User` への本物の依存として metadata エッジを含める。`deps` は forward / reverse とも同じ SQL 関数 (`DbReader.GetFileDependencies`) を共有するため、両方向で常に同じ kind 集合を出す。

| エントリポイント | 方向 | 辿る reference_kind | 実装 |
| --- | --- | --- | --- |
| `references` (CLI / MCP) | symbol 中心 | すべての `reference_kind` 行 (`--kind` 指定時は絞り込み) | `DbReader.GetReferences` |
| `callers` / `callees` (デフォルト) | source ↔ container | `('call', 'instantiate', 'subscribe', 'unsubscribe', 'razor_event_binding')` (= `CallableReferenceKindsSql`)。公開 row では event variant を `subscribe` に canonicalize | `DbReader.GetCallers` / `DbReader.GetCallees` |
| `impact` callers mode | 推移的 forward (BFS) | `GetCallersExact` 経由で `('augmentation', 'call', 'instantiate', 'generic_type_argument', 'subscribe', 'unsubscribe', 'razor_event_binding', 'friend', 'consumes_hook', 'capture', 'project_reference')`。solution の project path は project container 名とも照合 | `DbReader.GetTransitiveCallers` |
| `impact` file-hint fallback | reverse (定義ファイル → 依存先) | 全 kind。metadata 専用行は `IsMetadataTargetUnambiguous` と structured-type evidence で gating | `DbReader.GetFileDependencyHintsToResolvedType` |
| `deps` (デフォルト = forward) | source file → target file | 全 kind。metadata 行は class-like かつ metadata-eligible な target (`has_metadata_target_kind`) と一意解決 (`target_ambiguity`) を要求。MSBuild の import / project reference は共有 package 名との一致ではなく、宣言元 project 相対の path として解決 | `DbReader.GetFileDependencies` |
| `deps --reverse` | target file → source file | forward `deps` と同じ SQL を共有 | `DbReader.GetFileDependencies` |

`deps --symbol`、`--symbol-family`、`--suppress-noise` は、候補の ranking と `--limit` より前に logical-reference と target-candidate の SQL scope へ push down される。cycle と cross-workspace の read も、各候補上限より前に同じ filter を適用する。そのため `reference_count`、ranking、`symbol_filter` の before/after counter は、絞り込み前の workspace 全体ではなく SQL で絞り込まれた scope を表す。長時間の SQLite dependency read では query token による command cancellation も登録する。

実運用上の帰結: クラスのようなシンボルに対する `impact <ClassName>` は、member-level の caller が存在しない場合 heuristic file-dependency-hint fallback (metadata エッジを含む) を返し、一方の既定 `callers <ClassName>` は実行可能 edge だけを返す。両方とも個々の契約上は正しいが、件数は一致しない。差分を埋めるには `references <ClassName> --kind attribute`（または `annotation`）を使うか、`callers` / `callees` に明示的に対応する非既定 kind を渡し、既定 call graph が意図的に落としている edge を確認する。

`impact --json` と MCP `impact_analysis` は、0 件診断を structured routing field として返します。`zero_result_reason` は端末向けの短い理由のまま残し、`impact_failure_chain` は `definition_not_found`、`callable_filter_fails`、`multiple_definitions`、`multiple_definition_files`、`graph_unavailable`、`depth_requested_zero`、`no_callers` などの失敗前提や traversal 状態を順序付きで列挙します。`suggestion_type` は prose の `suggestion` を `resolution`、`traversal`、`precondition` に分類します。CLI `impact --strict` は chain に resolution / precondition failure が含まれる場合は `FeatureUnavailable` で終了しますが、真正な `no_callers` traversal 結果は成功として扱います。

`definition --json` と MCP `definition` の結果は、既存の symbol metadata で同名定義を区別できる C# 定義に対して `disambiguator` を含む場合があります。現行値は method signature 用の `overload(...)`、`partial-class` / `partial-struct` / `partial-interface`、`extension-method-on(<receiver>)` です。overload や receiver metadata を持たない言語ではこの field を省略します。

<a id="cloud-claude-code-bootstrapnet-sdk-なし"></a>

## Cloud AI コーディングハーネス bootstrap（Claude Code / Codex、.NET SDK なし）

> **Maintainer・認可オペレーター向け** — 全体の索引は [MAINTAINERS.md](MAINTAINERS.md#maintainer-と認可オペレーター向け) を参照。エンドユーザーは読み飛ばして構いません。

このセクションでは、[CLOUD_BOOTSTRAP_PROMPT.md](CLOUD_BOOTSTRAP_PROMPT.md#日本語) に従う Cloud AI コーディングセッション（例: Claude Code / OpenAI Codex）が、.NET SDK がインストールされていないコンテナにもかかわらず、動作する `cdidx` バイナリと SQLite ランタイムを手に入れるまでの仕組みを詳述する。インストールパスのリグレッションは `dotnet build` が動く環境では不可視なため、Cloud セッションは公開リリース体験のカナリアとなる（「炭鉱のカナリア」に由来する比喩。ここでいうカナリアはペットとして飼われる小型の鳴鳥で、体が小さく呼吸も速いため人間より遥かに少ない量の有毒ガスで中毒症状を起こす。かつて炭鉱ではこの性質を利用し、人間より先に一酸化炭素などの有毒ガスに反応して鳴き止む・倒れるカナリアを坑内に連れて入り、作業員がまだ気付けない危険を早期に検知する生体センサーとして使っていた。そこから転じて IT では、本番のユーザーが被害を受ける前に異常を真っ先に検知する役割を指す）。各層を理解することが重要である理由はここにある。

bootstrap prompt では、maintainer が押さえるべき cloud 向け installer knob も明示している。`CDIDX_GITHUB_BASE_URL` と `CDIDX_GITHUB_API_BASE_URL` は、egress 制限付きセッションで release download host と latest-release API host を別々に差し替えるためのもの。組み込みの `--self-test-local-mirror` 経路は、非空の `CDIDX_INSTALL_DIR` を与えない限り実 `~/.local/bin` install を汚さないよう隔離されている。非空の `CDIDX_INSTALL_DIR` が *指定されている* ときも、self-test はリスクのある対象 — よく使われるシステムパス（`/usr/local/bin`、`/usr/bin`、`/opt/homebrew/bin`、`/opt/local/bin`）、`$HOME/.local/bin`、そして既に `cdidx` 実体が存在する任意のディレクトリ — への書き込みを拒否して abort する。mock payload は `--version` にしか応答しないため、実インストールが無言で機能不全になるのを防ぐためである。隔離された tempdir に戻すなら `CDIDX_INSTALL_DIR` を unset すればよく、どうしても現地で mock layout を確認したい場合は CLI フラグ `--self-test-allow-overwrite` を渡してガードを解除する。エスケープハッチは意図的に CLI 専用とし、呼び出し側の env に残った `SELF_TEST_ALLOW_OVERWRITE=1` は継承しない（古い env var による silent bypass を防ぐため）。self-test には引き続き `python3` と `127.0.0.1` への loopback listen 権限が必要で、sandbox によっては完全に禁止される。その場合、この self-test はより制約の弱い shell か、事前に用意した mirror に対して実行する必要がある。

Codex cloud セッションには、もう 1 つ repository-local な制約があります。追跡対象の `.codex/hooks.json` Bash guard は、汎用ネットワーク download と汎用 global `cdidx` 使用をブロックします。そのため guard には、公式 installer と repo-local installer bootstrap だけに対する意図的に狭い例外があります。許可するのは、`curl -fsSL https://raw.githubusercontent.com/Widthdom/CodeIndex/.../install.sh | bash` の exact な形と、installer がサポートする flag（`--doctor`、`--self-test-local-mirror`、`--self-test-allow-overwrite`、`--reinstall-real`）を伴う直接の repo-local `bash ./install.sh ...` 呼び出しです。さらに `CLOUD_BOOTSTRAP_PROMPT.md` に載せている、完全展開済み install path を表示する resolver command と、その path を直接使う固定 JSON-RPC `initialize` pipe の exact な形も許可します。一方で、任意の download-and-execute command、未知の installer flag、`install.sh` を shell-control wrapper で包む形、裸の `cdidx`、`~/.local/bin/cdidx`、`$HOME/.local/bin/cdidx`、その他の path-qualified global `cdidx` binary、`$CDIDX` / `${CDIDX}` variable call は引き続き拒否します。インストール後の Codex operator は、`$HOME/.local/bin/cdidx` を完全展開済み絶対パスへ解決し、その literal path を no-SDK の code-search command すべてに貼り付けて、`CLOUD_BOOTSTRAP_PROMPT.md` の tripwire guidance と揃えてください。この例外が解除するのは repository guard だけであり、`CONNECT tunnel failed, response 403` のような upstream proxy / egress policy deny は迂回できません。

mock に頼らないリリース前検証として、`install.sh --reinstall-real <version>` は指定タグを隔離された `/tmp/cdidx-reinstall-real.XXXXXX` にダウンロード・インストールしたうえで、`cdidx --version` を走らせて報告されたバージョンが要求タグと一致することを検証し、さらに `/tmp/cdidx-reinstall-scratch.XXXXXX` に極小の Python プロジェクトを生成して `cdidx . --db <scratch>/.cdidx/codeindex.db` と `cdidx search greet --db <...>` を通し、出力中にスクラッチシンボルが現れることを確認する。出力は既定のユーザー経路を検証するために人間向けフォーマットを意図的に使う。現在の release バイナリは trim 済みだが、CLI JSON DTO は source-generated serializer でカバーされるため `--json` も動作する想定である。`JsonOutputFailure` 経路は serializer 登録を欠いた古いバイナリや custom binary 向けの fallback に限られる。これにより、新しいバイナリの上で実インデックス経路（シンボル抽出、ネイティブ SQLite ロード、FTS5 検索）まで実際に動くかを確認できる。`--self-test-local-mirror` のモックは `--version` しかスタブしないため、インデックスや検索経路の回帰はそちらでは素通りしてしまう。`--reinstall-real` は `CDIDX_INSTALL_DIR` を意図的に無視するので、検証モードで壊れたビルドが実インストールを上書きすることはない。temp インストールディレクトリとスクラッチディレクトリは、正常終了でも失敗でも `trap` によって確実に片付けられる。

インストール前のネットワーク診断として、`install.sh --doctor [vX.Y.Z]` は有効な proxy 環境変数を表示したうえで（各値は `redact_proxy_userinfo` ヘルパーを通して `http://alice:hunter2@proxy:8080` のような URL userinfo を `http://<redacted>@proxy:8080` として出力するため、reachability 診断に必要な host/port は保ちつつ、共有 log / issue / サポート窓口に資格情報が流出しない）、指定バージョン（省略時は同梱 `version.json`）で installer が叩く 3 つの upstream URL — latest-release API endpoint、リリース tarball asset、`sha256sums.txt` — を probe する。各 probe は `curl -sSI` を使うので数 MB のリリース tarball を実ダウンロードしない。`CONNECT tunnel failed, response 403`（curl exit 56）を検出したら、既存の `is_proxy_tunnel_403` 経路と同じ定型ガイダンス（「拒否は TLS 前の upstream proxy / egress policy 側で起きている。経路差し替えだけでは解消しない。network 管理者に artifact 配信経路のいずれかを allow-list してもらうか、`CDIDX_GITHUB_BASE_URL` / `CDIDX_GITHUB_API_BASE_URL` を到達可能な内部 mirror へ向ける」）を再利用し、ユーザーに次の一手を 1 つに絞って提示する。doctor はインストールを一切行わず、`/tmp` の外に書き込まず、最初の失敗で短絡せずに全 probe を走らせる（1 つの network-policy deny が他を隠さない）ため、全 probe が 2xx/3xx を返したときだけ exit 0、それ以外は exit 1 を返す。

"silent host" で端末 stderr が握りつぶされるケースに備えて、配布済み/
常用実行では stderr と最小限のライフサイクル情報をユーザー単位の日次
ログにも複写するようになっている。保存先は `CDIDX_GLOBAL_TOOL_LOG_DIR`、
`XDG_STATE_HOME/cdidx/logs/`、`XDG_CACHE_HOME/cdidx/logs/`、
`XDG_RUNTIME_DIR/cdidx/logs/`、platform default の順に選ばれます。
platform default は Windows では `%LOCALAPPDATA%\cdidx\logs\`、macOS では
`~/Library/Logs/cdidx/`、Linux では `~/.local/state/cdidx/logs/` です。
platform candidate がすべて使えない場合、最後の temp fallback は OS temp root
配下のユーザー別 hashed `cdidx-u.../logs` ディレクトリです。各 candidate は
logger が採用する前に create/write/delete の往復で probe されるため、
read-only な state/cache/runtime mount は最初の log write を失うのではなく
次の candidate へ fall through します。ファイル名は
`stderr-YYYYMMDD.log`、ファイル内 timestamp は invariant culture の
ISO-8601 UTC（`yyyy-MM-ddTHH:mm:ss.fffZ`）で、logger は新しい 30 日次
ファイルだけを保持します。`CDIDX_LOG_FORMAT` / `--log-format` は text と
JSONL を切り替え、`CDIDX_LOG_RETAIN` / `--log-retain-count` は保持ファイル数、
`CDIDX_LOG_MAX_SIZE_MB` / `--log-max-size-mb` または
`CDIDX_GLOBAL_TOOL_LOG_MAX_BYTES` は size-rotation cap を設定します。
サイズ上限の既定は 50 MiB、受け付ける値の上限は 1024 MiB / 1 GiB です。
通常の開発/テストサイクルでワークツリー直下に永続ログが増えないよう、
`src/CodeIndex/bin/...` と `tests/.../bin/...` からのリポジトリ内開発実行は
既定で対象外です。完全に無効化したい場合は `CDIDX_DISABLE_PERSISTENT_LOG=1`
を設定し、この toggle は `1`、`true`、`yes`、`on` を大小文字非依存で
受け付けます。テストやパッケージングで保存先を切り替えたい場合は
`CDIDX_GLOBAL_TOOL_LOG_DIR` を使います。
local package smoke test や launcher diagnostics で executable path が
development build に見える場合でも lifecycle logging を強制するには
`CDIDX_FORCE_GLOBAL_TOOL_LOG=1` を設定します。両方が設定された場合でも
`CDIDX_DISABLE_PERSISTENT_LOG` が優先されます。
未処理例外は stderr を簡潔に保ちつつ、post-mortem diagnostics 用に完全な
exception chain と stack trace を lifecycle log へ書きます。ファイル単位の
index failure には active extraction phase と上限付きの安全な detail も記録し、
隔離 symbol worker の failure には exception category と redaction 済み origin
frame を含めます。parallel full-scan boundary をまたぐ exception も元の
extraction stack を保持します。また長い method signature があっても、
oversized stack frame は redaction 済み source-line suffix を失わずに保持します。記録される
command argument は既定で最小限 redaction されます。secret らしい
`--flag=value` pair、secret らしい flag の直後の値、URI password、長い
token 風の hex/base64 文字列は `<redacted>` に置換されます。
`CDIDX_LOG_REDACT=none` は制御されたローカル debugging 用に raw argument を
保持し、`CDIDX_LOG_REDACT=full` は path らしい argument も stable hash に
置き換えます。

### 構成要素

`cdidx` が動作するためには、4つのアーティファクトが3つの正しい場所に収まる必要があり、
さらにユーザー環境にはインストールされないがリリースには同梱されるサプライチェーン用の
追加アセットが1つある:

| アーティファクト | 由来 | 最終配置先 | 必要とする処理 |
| --- | --- | --- | --- |
| `cdidx`（trim 済み自己完結型シングルファイルバイナリ） | `release.yml` 内の `dotnet publish -r <rid> --self-contained -p:PublishSingleFile=true -p:PublishTrimmed=true` | `$HOME/.local/bin/cdidx` | ユーザーの `PATH` |
| `libe_sqlite3.so`（Linux）/ `libe_sqlite3.dylib`（macOS） | `Microsoft.Data.Sqlite`（SQLitePCLRaw）NuGet のネイティブ資産。publish 出力に同梱される | `$HOME/.local/bin/`（バイナリの隣） | `SqliteConnection` 静的コンストラクタ → P/Invoke |
| `version.json` | リポジトリルート。`CodeIndex.csproj` が `Content` として publish 出力にコピー | `$HOME/.local/bin/`（バイナリの隣） | `AppContext.BaseDirectory` 経由の `ConsoleUi.LoadVersion()` |
| `sha256sums.txt` | `release.yml` がパッケージング後に計算（tarball/zip と SBOM 双方をカバー） | インストール中は一時ディレクトリに保持のみ | `install.sh` の整合性チェック / SBOM 利用側 |
| `cdidx.sbom.cdx.json` | `release.yml` が `linux-x64` lane で 1 度だけ `dotnet CycloneDX` を実行（内容は RID 非依存）し、`CodeIndex-sbom` アーティファクトとして upload。`create-release` が `release-files/` にコピーして `sha256sums.txt` の対象に含める | GitHub release のアセットとして公開、ユーザー環境にはインストールしない | コンプライアンスレビュー（SOC2 / FedRAMP 系）/ サプライチェーンスキャナー（Snyk / Trivy / Grype） |

最初の3つは `release.yml` により `CodeIndex-<rid>.tar.gz` にパッケージされる。クリーンインストールは同じレイアウトをユーザーの環境で再現する必要がある。ランタイムファイルが欠けると、後述の診断表にある症状のいずれかが発生する。

```mermaid
flowchart LR
    subgraph Repo["リポジトリ（真実の源）"]
        V[version.json]
        C[CodeIndex.csproj]
    end
    subgraph CI["GitHub Actions — v* タグでの release.yml"]
        P["dotnet publish<br/>--self-contained<br/>PublishSingleFile=true<br/>PublishTrimmed=true"]
        T["tar czf<br/>CodeIndex-&lt;rid&gt;.tar.gz"]
        H["sha256sums.txt"]
    end
    subgraph Tarball["リリース tarball の中身"]
        B[cdidx]
        L["libe_sqlite3.so<br/>（macOS では .dylib）"]
        J[version.json]
    end
    subgraph User["install.sh 実行後のユーザー環境"]
        UB["~/.local/bin/cdidx"]
        UL["~/.local/bin/libe_sqlite3.so"]
        UJ["~/.local/bin/version.json"]
    end
    V -->|ビルド時に読む| C
    V -->|Content として複製| P
    C --> P
    P --> B
    P --> L
    P --> J
    B --> T
    L --> T
    J --> T
    T -->|install.sh: ダウンロード + 検証| H
    T -->|install.sh: 展開 + コピー| UB
    T --> UL
    T --> UJ
```

### フェーズ1 — ワンライナーでのダウンロード

ここでいう「ワンライナー」とは、ターミナルにコピー＆ペーストで貼り付けて Enter を押すだけで完結する、1行のシェルコマンドのこと。インストーラをダウンロードして実行する複数ステップを `curl` とパイプ `|` で1行につないでいるためこう呼ぶ（例: `curl -fsSL …/install.sh | bash` は「install.sh を取得 → そのまま bash に流し込んで実行」を1行で行っている）。

プロンプトから実行されるコマンド:

```bash
curl -fsSL https://raw.githubusercontent.com/Widthdom/CodeIndex/main/install.sh | bash
```

`install.sh` が順に行うこと（`install.sh` 参照）:

1. **preflight 後にプラットフォーム検出。** release 処理前に、`curl`、archive/temp/checksum tool、manifest traversal で使う `find` / `sed` / `sort` を1つの宣言済み dependency list から確認する。その後 `uname -s` / `uname -m` を `<os>-<arch>` RID に正規化し、release asset の download 前にリリースワークフローが publish する一覧（`linux-x64`、`linux-arm64`、`osx-arm64`、`win-x64`、`win-arm64`。詳細は [プラットフォームサポート](docs/platform-support.md#プラットフォームサポート)）と照合する。自己完結型バイナリは glibc にリンクされているため、Alpine / musl は先頭で明示的に拒否する。`osx-x64` などの未対応 RID は、検出 RID、対応一覧、NuGet global tool / source build の代替手段、公式 platform support をリクエストする issue link を含むエラーで拒否する。
2. **binary を実行せず既存 metadata を確認。** `INSTALL_DIR/version.json` があれば、network 処理前に version を読みます。再利用してよいか判断するために、未検証の既存 `cdidx` を実行することはありません。
3. **バージョン解決。** 明示引数がある場合は `v` プレフィックス付き・無しの両方を受け付ける（`v1.8.0` / `1.8.0`）。引数なしでも GitHub API（`/repos/Widthdom/CodeIndex/releases/latest`）を叩いて latest tag を解決し、`jq` があれば `tag_name` 取得に使い、無ければ portability のため従来どおり `grep` + `sed` にフォールバックする。そのうえで installed version が latest tag と一致し、保存済み release checksum receipt を固定済み release workflow/tag または GPG signer に対して再認証し、その receipt が `MANIFEST.sha256` を認証し、critical/legal artifact がすべて再ハッシュできた場合だけ download を skip する。receipt、manifest、binary、`version.json`、native SQLite asset、notice は regular file、binary は executable であることも必須にする。差異のある artifact 名を報告して replacement を完全に download/stage してから、file ごとの move と rollback で promotion するため、その短い maintenance window 中は concurrent `cdidx` invocation を避ける。壊れた `v0.0.0` install や必須隣接資産欠落 install は再インストール対象として扱う。HTTP 失敗も `403` rate limit / `404` / `5xx` / 実際の curl network error を分けて案内する。
4. **明示バージョン指定時は再インストールまたは切り替えに進む。** 引数なし再実行も latest release を対象にするが、明示ターゲット版では、同版でも必ず再インストールへ進み、別版なら切り替えへ進む。壊れた `v0.0.0` install や、同版でも必須資産が欠けている install も置き換え対象として扱う — これは意図した挙動。
5. **ダウンロード。** `CodeIndex-<rid>.tar.gz` と `sha256sums.txt` を `mktemp -d` のディレクトリ（trap で自動クリーンアップ）に取得。
6. **provenance を検証してから checksum を検証。** 既定の strict policy は、`sha256sums.txt` の GitHub attestation、または `CDIDX_RELEASE_GPG_FINGERPRINT` と一致する signer の有効な GPG signature のどちらかを必須にする。この独立した proof が成功してから manifest を信頼し、`sha256sum` / `shasum` / `openssl`（利用可能なもの）で archive の SHA256 を比較する。`CDIDX_VERIFY_POLICY=compat` は、provenance が無いまま続行するときに warning を出す、監査対象の明示的 opt-in である。checksum 不一致なら引き続き `INSTALL_DIR` に一切ファイルを置かず中断する。
7. **専用サブディレクトリへ展開。** `tar xzf … -C ${tmpdir}/extract` で、展開物がダウンロード済みアーカイブやチェックサムと混ざらないようにする。
8. **コピー前に展開済み payload 全体を検証。** `cdidx`、`version.json`、OS ごとに必須の native SQLite ライブラリ（Linux は `libe_sqlite3.so`、macOS は `libe_sqlite3.dylib`）がすべて揃っていることを確認する。不足があれば `INSTALL_DIR` に触る前に中断するため、健全な install を部分 payload で壊さない。
9. **認証済み receipt と manifest を保存し、資産一式を staging。** 上記検証が通ってから、installer は `INSTALL_DIR` 配下の staging ディレクトリへ `cdidx`、検証済み `MANIFEST.sha256`、独立に認証済みの release checksum receipt（利用可能なら detached signature も）、必須隣接資産をコピーし、receipt/manifest を read-only、staged binary を executable にする。その後、既存ファイルを backup ディレクトリへ退避し、runtime asset を先に、binary を最後に rename で昇格させる。途中で失敗した場合は backup から rollback するため、健全な install を半更新状態にしない。これにより、見かけ上は成功しても後で `v0.0.0` や `DllNotFoundException` で落ちる半壊れ install を防ぐ。
10. **PATH ガイダンス。** `INSTALL_DIR` が `PATH` に無ければ、シェル別のスニペット（`bashrc` / `zshrc` / `fish_add_path`）を表示する。

current release の成功後は `ls -a $HOME/.local/bin/` に `cdidx`、`libe_sqlite3.so`（Linux の場合）、`version.json`、`MANIFEST.sha256`、hidden release checksum receipt が並んで見える。それ以外の current-release layout はバグである。authenticated manifest receipt 導入前の legacy release も install はできるが、fresh download なしの再利用対象にはならない。

```mermaid
sequenceDiagram
    autonumber
    participant U as ユーザーシェル
    participant S as install.sh
    participant API as api.github.com
    participant GH as github.com/releases
    participant TMP as mktemp -d
    participant FS as ~/.local/bin
    U->>S: curl | bash
    S->>S: detect_platform (uname)
    Note over S: musl / osx-x64 は早期に拒否
    S->>FS: 既存 version.json を読む（cdidx は実行しない）
    alt 引数なし
        S->>API: GET /releases/latest
        API-->>S: tag_name（例: v1.8.0。実際の値は GitHub Releases による）
        alt manifest 再ハッシュ済み install が latest と一致
            S-->>U: latest 比較後に exit 0
        else upgrade または repair が必要
            S->>FS: 解決した latest 版へ切り替え/再インストール
        end
    else 明示バージョン指定あり
        S->>S: 明示バージョンを正規化
        S->>FS: 同版でも再インストールへ進む
    end
    S->>TMP: mkdir、trap でクリーンアップ
    S->>GH: GET CodeIndex-{rid}.tar.gz
    S->>GH: GET sha256sums.txt
    S->>S: sha256sum / shasum / openssl で検証
    S->>TMP: tar xzf -C extract/
    S->>S: extract/ 内の cdidx と必須資産を検証
    S->>FS: 必須ファイル + MANIFEST.sha256 を .cdidx-stage.* へコピー
    S->>FS: staged cdidx に chmod +x
    S->>FS: 既存ファイルを .cdidx-backup.* へ mv
    alt backup 退避で失敗
        S->>FS: 退避済みファイルだけ元へ戻す
        S-->>U: 既存 install を置き換える前に中断
    else backup 完了
        S->>FS: staged runtime asset を先に昇格
        S->>FS: staged cdidx を最後に昇格
        alt promotion で失敗
            S->>FS: 新しく昇格したファイルだけ削除
            S->>FS: backup から旧ファイルを復元
            S-->>U: rollback して中断
        else success
            S-->>U: "Installed cdidx to ~/.local/bin/cdidx"
        end
    end
```

### フェーズ2 — 初回起動: `cdidx --version`

`Program.cs:12` が `Main` の先頭で `ConsoleUi.LoadVersion()` を呼ぶ。そのメソッド（`src/CodeIndex/Cli/ConsoleUi.cs:268-285`）が行うこと:

1. `AppContext.BaseDirectory` — Linux の単一ファイル自己完結型実行可能ファイルでは、展開された `cdidx` バイナリが置かれたディレクトリに解決される（.NET の単一ファイルホストは一時ディレクトリに展開するが、*apphost* ディレクトリ（`~/.local/bin/`）を `AppContext.BaseDirectory` として公開する）。
2. `Path.Combine(exeDir, "version.json")`。存在すれば JSON を解析し `version` 文字列を返す。
3. フォールバック: `AppDomain.CurrentDomain.BaseDirectory` を試す。
4. 最終フォールバック: リテラル文字列 `"0.0.0"` を返す。

インストーラが `version.json` をバイナリの隣に置き忘れると、`--version` が `cdidx v0.0.0` を返す。これは見た目の問題だけではない。同じ文字列が MCP の `serverInfo.version` や `status --json` の `version` フィールドにも使われるため、AI クライアントまで無意味なバージョンを見ることになる。これが、壊れたインストールパスが最も顕在化しやすい箇所である。

```mermaid
flowchart TD
    A[Program.Main] --> B["ConsoleUi.LoadVersion()"]
    B --> C{"exeDir/version.json<br/>は存在する?"}
    C -->|yes| D[JSON 解析 → 'version' を読む]
    C -->|no| E{"CurrentDomain.BaseDirectory/<br/>version.json は存在する?"}
    E -->|yes| D
    E -->|no| F["'0.0.0' を返す（フォールバック）"]
    D --> G["バージョン文字列を返す"]
    G --> H["cdidx --version は 'cdidx vX.Y.Z' を表示<br/>MCP serverInfo.version = X.Y.Z<br/>status --json .version = X.Y.Z"]
    F --> I["cdidx --version は 'cdidx v0.0.0' を表示<br/>→ 壊れたインストールパスのシグナル"]
```

### フェーズ3 — SQLite を最初に呼び出すコマンド: `cdidx .`（index）

これはスタック全体をエンドツーエンドで駆動する。

1. **バイナリ起動。** 自己完結型ホストがマネージエントリポイント（`Program.Main`）を解決。
2. **CLI ルーティング。** `Program.cs` が `IndexCommandRunner.Run(args, jsonOptions)` に振り分け。
3. **DB パス解決。** `DbPathResolver` が `--db` 指定が無い限り `<projectPath>/.cdidx/codeindex.db` を算出し、`.cdidx/` ディレクトリを作成する。同じヘルパーは `status` / `map` / `inspect` の query-time workspace root 解決も担う。`--db` を付けない query は既定の `.cdidx/codeindex.db` sibling path をそのまま正とし、explicit DB は `codeindex_meta.indexed_project_root` を読む。保存済み root metadata を持たない legacy explicit DB は、明示パス自体が `.../.cdidx/codeindex.db` でも `project_root` / `git_head` / `git_is_dirty` / `indexed_head_commit` / `worktree_head_changed` を未設定のまま返す。`WorkspaceMetadataEnricher` は利用可能な場合、最新の成功 index stamp である `indexed_head_sha` と runtime HEAD を比較し、legacy DB だけで従来の full-scan 限定 `indexed_head_commit` に fallback する。index 構築後に worktree の branch / HEAD が切り替わったときは `worktree_head_changed=true` を surface して `status` が WARN を出せるようにする (issues #1512 and #3367)。加えて index 成功時 (full scan / partial update 問わず) に `IndexCommandRunner` が `codeindex_meta.indexed_head_sha` / `indexed_head_branch` / `indexed_head_timestamp` も best-effort で stamp する（`git` 失敗は index 成功を妨げない）。`status` はこれらを `indexed_head_sha` / `indexed_head_branch` / `indexed_head_timestamp` として返し、query 時に `git merge-base --is-ancestor` + `git rev-list --count` で算出する `commits_ahead_of_indexed_head` も付与する。indexed SHA が未知、または force-push / divergent history で現 `HEAD` の祖先でなくなった場合は `null` を返し、consumer が divergent worktree を「最新」と誤読しないようにする。`indexed_head_commit` (#1508 / #1512、full scan 限定) と異なり、#1509 のトリプルは成功した full、`--files`、`--commits`、`--changed-between` run ごとに更新され、失敗または rollback された run では直前値を維持する。query envelope の `metadata.indexed_at_head_sha` と MCP / status の `indexed_head_sha` は同じ最新成功 stamp を読み、この key が無い legacy database だけ full-scan 限定 `indexed_head_commit` に fallback するため、update mode によらず cross-session のドリフト検出と response 間の整合性が保たれる。
4. **SQLite オープン。** `IndexCommandRunner` が `new DbContext(dbPath)` を構築し、内部で `new SqliteConnection(...)` が呼ばれる。**ネイティブライブラリの解決はこの時点で行われる。** `SqliteConnection` の静的コンストラクタが `SQLitePCL.Batteries_V2.Init()` を呼び、それが `SQLite3Provider_e_sqlite3` 上で `sqlite3_libversion_number()` を起動し、`e_sqlite3` への P/Invoke に到達する。Linux の .NET 動的ローダは次の順で探す（失敗時のエラーメッセージを参照）:
   - `${apphost_dir}/libe_sqlite3.so`
   - `${apphost_dir}/e_sqlite3.so`（および `lib` プレフィックスなしのバリエーション）
   - 次に OS の通常の `dlopen` 検索パス（`/lib`、`/usr/lib` など）
   自己完結型 publish は `libe_sqlite3.so` を publish 出力に同梱し、リリース tarball に含め、修正後の `install.sh` がバイナリの隣に置くため、最初のプローブで成功する。これが欠けていると、`SqliteConnection` のインスタンス生成時点で `DllNotFoundException: Unable to load shared library 'e_sqlite3'` が送出され、**ユーザーコードが実行される前にプロセスが終了する**。
5. **スキーマ初期化。** `DbContext.ctor` が `PRAGMA journal_mode=WAL`、`PRAGMA busy_timeout=5000`、`CREATE TABLE IF NOT EXISTS` / `CREATE VIRTUAL TABLE IF NOT EXISTS fts_chunks USING fts5 (…)` / トリガー DDL を実行する。成功は、ネイティブライブラリがロードできるだけでなく、FTS5 がビルドに含まれた動作する SQLite であることも証明する（SQLitePCLRaw の同梱ビルドは常に FTS5 有効）。
6. **スキャンと書き込み。** `FileIndexer` がプロジェクトツリーを走査し、ファイルを読み、言語を検出し、チャンク分割し、シンボルと参照を抽出し、`DbWriter` がトランザクションあたり500件ずつ UPSERT する。write batch は transaction の外で `codeindex_meta.batch_in_progress=true` を先に stamp し、同じ transaction 内で rows と readiness metadata を commit するときに marker を clear する。crash により marker が残った DB を次回 writable open すると readiness bit を落として rebuild を促す警告を出すため、stale な trust metadata を clean と誤読しない。進捗は `ConsoleUi.SetProgressTheme()` でレンダリング。
7. **FTS optimize。** 書き込みのコミット後、`INSERT INTO fts_chunks(fts_chunks) VALUES('optimize')` を実行。
8. **サマリー表示。** `Files / Chunks / Symbols / Refs / Elapsed`。

`Done.` が見えれば、ネイティブロード、SQLite 初期化、WAL セットアップ、FTS5 利用可能、トリガー同期、バッチ書き込みまで全ての先行ステップが成功したことを意味する。

```mermaid
sequenceDiagram
    autonumber
    participant OS as Linux 動的ローダ
    participant Host as .NET apphost (cdidx)
    participant Main as Program.Main
    participant CU as ConsoleUi.LoadVersion
    participant IR as IndexCommandRunner
    participant Ctx as DbContext
    participant Conn as SqliteConnection
    participant PCL as SQLitePCL.Batteries_V2
    participant SO as libe_sqlite3.so
    OS->>Host: execve(cdidx)
    Host->>Main: マネージエントリ
    Main->>CU: LoadVersion()
    CU-->>Main: "1.8.0"
    Main->>IR: Run(args)
    IR->>Ctx: new DbContext(dbPath)
    Ctx->>Conn: new SqliteConnection(connStr)
    Conn->>PCL: 静的コンストラクタ → Init()
    PCL->>SO: P/Invoke sqlite3_libversion_number()
    alt libe_sqlite3.so がバイナリの隣にある
        SO-->>PCL: OK
        PCL-->>Conn: プロバイダ登録
        Ctx->>Ctx: PRAGMA journal_mode=WAL
        Ctx->>Ctx: PRAGMA busy_timeout=5000
        Ctx->>Ctx: CREATE TABLE IF NOT EXISTS ...
        Ctx->>Ctx: CREATE VIRTUAL TABLE fts_chunks USING fts5(...)
        Ctx->>Ctx: CREATE TRIGGER（chunks ↔ fts_chunks 同期）
        IR->>IR: FileIndexer スキャン + DbWriter バッチ UPSERT
        IR-->>Main: "Done."
    else libe_sqlite3.so が無い
        SO--xPCL: dlopen 失敗
        PCL--xConn: DllNotFoundException
        Conn--xMain: ユーザーコード実行前にクラッシュ
    end
```

### フェーズ4 — SQLite 読み取りパス: `cdidx status`、`cdidx search`

`cdidx status` は `DbReader.GetStatus(...)` を実行し、`files`、`chunks`、`symbols`、`symbol_references` に対して少数の `SELECT COUNT(*)` / `SELECT … GROUP BY` を発行する。これで読み取りパス（`TryMigrateForRead` による読み取り時スキーマ移行を含む）が動くことが証明される。

`cdidx search "<query>" --path install.sh --snippet-lines 4` は `DbSearchReader` を通る。順に:

1. ユーザークエリを FTS セーフにするためトークン単位で引用化する（`--fts` 指定時は除く）。
2. パスフィルタ付きで `SELECT … FROM fts_chunks JOIN chunks …` を実行。
3. `SearchSnippetFormatter.Format` が一致中心のコンパクトなスニペットをハイライト付きで再構成する。

スニペットが返れば、FTS5 仮想テーブル、コンテンツ同期トリガー、`SearchSnippetFormatter` によるスニペット整形までが一通り正しく連携していることが確認できる。

### フェーズ5 — MCP パス: `cdidx mcp`

session は明示的な初期化前、初期化中、初期化済み、shutdown 中、closed の各 phase を進む（#4848）。`initialize` の client identity、caller、roots、capabilities は frame-local な draft に保持し、protocol 交渉と success response の serialization が完了した後だけ commit する（#4540）。拒否された handshake、`CDIDX_MCP_RESPONSE_MAX_BYTES` fallback、serializer failure は初期化の取得を取り消すため、失敗 request の metadata を引き継がない修正済み retry を行える。frame cleanup は deferred initialize draft を seal するため、response serialization 後に timeout で遅れて到着した worker も claim を解放し、session を初期化中 phase に残留させない。commit 成功後の重複 `initialize` はすべて拒否され、確立済み session を置き換えられない。成功した commit は initialization lifecycle、caller、client info、capabilities、roots を単一の immutable snapshot として公開するため、並行中または drain 中の request は完全な 1 世代だけを参照する。`roots/list` refresh は client response の待機中に roots-change notification が snapshot を無効化していない場合だけ公開される。この保証は server-side JSON-RPC serialization の境界に適用され、HTTP 配送には後述する別の fail-closed 境界がある。

`{"jsonrpc":"2.0","id":1,"method":"initialize","params":{}}` を `cdidx mcp` にパイプすると別のコードパスが走る:

- `McpServer` が stdin/stdout を持ち、JSON-RPC 2.0 フレームを解析する。
- レスポンス構築は `JsonSerializer.Serialize<T>(...)` ではなく、`System.Text.Json.Nodes.JsonObject` / `JsonArray` を**手組み**する。これが、トリミング済みバイナリでリフレクションベースのシリアライズが無効でも MCP パスが動き続ける理由。
- `initialize` レスポンスは `protocolVersion`、`capabilities`、`serverInfo.name`、`serverInfo.version`（`ConsoleUi.LoadVersion()` — `version.json` が源）、および AI クライアントにツール選択を案内する長い `instructions` 文字列を返す。capability object が公開するのは server 提供の `tools`、`resources`、`prompts`、`logging` だけで、client 提供の `roots` と `sampling` は server capability として公開しない。その後 client が handshake 完了を示す `notifications/initialized` を送信する。交渉済み protocol が roots に対応し、client が `roots` capability を提示し、transport が server-to-client request を配送できる場合、cdidx は `roots/list` request を送る。capability がなければ送信しない。この handshake 起点の refresh は session ごとに1つの task へ集約し、transport teardown で drain する。bounded drain が期限切れになっても最後の stdio writer / disposal barrier で保持するため、`notifications/initialized` が再送されても detached client request は増殖せず、transport resource の dispose と競合しない。server 側から `notifications/initialized` 自体は生成しない（#4433）。HTTP session は `CDIDX_MCP_KEEP_ALIVE_INTERVAL_S` を設定した場合、opt-in の keep-alive notification を `/events` で受け取れる。HTTP transport では out-of-band 通知は接続済みの `/events` SSE stream にだけ配送され、POST のみのクライアントは initialize response だけを受け取り、別通知 frame は受け取らない。
- すべての request frame は厳密な `"jsonrpc":"2.0"` member を持つ必要があり、`initialize` 以外の応答対象 method は初期化成功まで拒否される（#4468）。1 session で受理できる `initialize` は1回だけで、並行または後続の試行には構造化された `-32600` / `duplicate_initialize` error を返す。初期化中、shutdown 中、closed の phase でも応答対象 method を拒否する（#4848）。stdio の EOF、不正 UTF-8、oversized input は、grace period、cancellation、post-cancel deadline の共通 bounded teardown を使う（#4543）。不正入力の protocol-error write と非同期 shutdown-cancellation callback も同じ deadline に含めるため、writer、write gate、callback の停止で teardown が無期限に残らない。`notifications/shutdown` は read と request action を cancel するが、起点の transport completion（HTTP では `204 No Content`）は cancel しない。初回 drain snapshot 後に開始した callback と concurrent loop の全終了経路も bounded drain に含める。stdio input は速やかに close し、output dispose は response writer に到達し得る accepted task の完了まで defer する。最終 diagnostic には未完了カテゴリごとの状態を記録し、外部 transport または process cancellation はどちらの cleanup window も中断できる。
- 独立した stdio request と HTTP POST は、設定された MCP request 上限まで並行実行する（#4536）。実行 slot が全て使用中でも read loop は cancellation/client-response frame を受け続ける。accepted-frame backlog は execution 上限 + 64 に別途制限し、超過 request には retry-safe な `-32003` / `server_busy` を返す。request id は protocol/gate 待機前に登録し、execution timeout は slot 取得後に開始し、timeout 後も cancellation を無視して動く action は実際に drain するまで slot を保持する。initialize など session mutation の受信順は protocol barrier で維持し、可変な request state は `AsyncLocal` または request-scoped snapshot に置き、shared writer tool は直列化する。JSON-RPC batch の各 item も同じ global execution slot を個別に消費する（#4545）。基本 `IMcpTransport` loop は outer frame slot を確保しないため、`maxConcurrency: 1` の single request は `_concurrencyGate` を1回だけ取得し、single request と batch item は dispatch 時だけ slot を消費する。
- advertised capability には server 提供の `tools`、`resources`、`prompts`、`logging` だけが含まれ、client 提供の `roots` と `sampling` は含まれない。`resources/list` はインデックス済みファイルを `cdidx://file/<path>` URI としてページングし、世代対応の不透明 keyset cursor を返す。ページ間でインデックス済みファイルが変わった場合は、再開必須の stale-index error を明示的に返す。任意の `maxBytes`（4,096〜1,000,000、既定 1,000,000）で JSON-RPC envelope 全体を制限し、省略件数と継続理由を `_meta.response_controls` に有界な形で返す。`resources/read` は inclusive な `startLine` / `endLine` と UTF-8 本文の `maxBytes`（最小 4 byte、既定 64 KiB、最大 128 KiB）を任意指定として受け付ける。各ページは論理行 1,000 行でも上限化される。成功レスポンスは標準の `contents` item を維持し、`result._meta` に実効範囲、返却 byte 数、切り詰め理由、不透明な `nextCursor` を追加する。継続時は行境界を再送せず、その cursor と任意の新しい `maxBytes` を渡す。cursor は index 済みファイル版に結び付くため、resource 変更後は stale として失敗する。database reader は長い単一行を含め、managed response string を構築する前に incremental SQLite BLOB read で範囲と byte 上限を適用する。server は MCP レスポンス上限と active transport のレスポンス上限のうち小さい方から実効本文 budget を算出し、JSON-RPC envelope と最悪ケースの JSON escape に必要な領域を確保する。1 つの JSON-RPC batch に複数の `resources/read` call がある場合は aggregate frame 上限を共有し、各 item を frame の残り領域に合わせて budget 化する。page 化できない item が割当内に収まらない場合は、元の request ID を保持した構造化 `batch_response_budget_too_small` error に置換する。file metadata の取得、cursor 検証、chunk BLOB 読み取りは単一の deferred SQLite read snapshot 内で実行するため、並行 reindex によって異なる resource 版が混在しない。実際に空の index 済みファイルは空の成功レスポンスを返すが、非空 resource の content 欠落、chunk coverage の不足、安全上限を超える chunk topology は部分的または空の成功として返さず、構造化された `index_missing`、`index_stale`、`index_corrupted` error として失敗する。専用の range partial index がない read-only または immutable な legacy database では、既存の `idx_chunks_file` index を使い、SQLite VM-step budget 内で metadata-only の predecessor / candidate query を実行する。budget 超過時は無制限に scan せず、構造化された `resource_bounded_read_index_unavailable` を返す。stable reason には `resource_content_unavailable`、`resource_bounded_read_index_unavailable`、`resource_chunk_coverage_incomplete`、`chunk_limit_exceeded`、`chunk_candidate_scan_limit_exceeded`、`resource_file_metadata_inconsistent`、`resource_chunk_topology_invalid`、`scan_limit_exceeded` がある。`logging` は MCP `notifications/message` を示し、`logging/setLevel` は `debug`、`info`、`notice`、`warning`、`error`、`critical`、`alert`、`emergency` を受け付ける。
- `resources/templates/list` は正確な既知 path の直接解決用に `cdidx://file-path/{path}` を公開し、成功した read は canonical な `cdidx://file/<path>` identity を返す。`resources/list` の `path`、`lang`、`includeGenerated` filter は server-side で有界に適用され、継続 cursor は canonical filter と generation の両方に結び付く。generated file は既定で list と read から除外され、明示的な `includeGenerated: true` が必要になる。
- `initialize` の `instructions` はこれらの resource template / list control を直接案内し、各 `resources/list` response は accepted extension parameter と上限を `_meta.discovery_contract` に公開する。これにより AI client は標準外の protocol extension を推測する必要がない。
- `protocolVersion` は**ハードコードではなく交渉**で決まる（#1554）。サーバーは
  `McpServer.SupportedProtocolVersions`（新しい順: `2025-06-18`,
  `2025-03-26`, `2024-11-05`）を保持し、`initialize` パラメータから
  クライアント要求バージョンを読み取って、対応集合にあればそれを返し（合意）、
  未指定／非文字列なら既定の最新バージョンに fallback し、対応外なら
  `error.data` に `requestedVersion` と `supportedVersions` を入れた JSON-RPC
  `-32602` で拒否する。これにより将来 MCP 仕様が改訂されても、wire format が
  黙ってずれるのではなく actionable な handshake 失敗として表面化する。配列を
  新バージョンで更新する際は `ProtocolVersion` を先頭エントリと揃えて意図的に
  bump する。lifecycle transport の回帰テストは version echo だけでなく、Codex の
  `2025-06-18` handshake から `notifications/initialized`、`tools/list` までを通す。
  client identity、caller、roots、capabilities は切り離した initialize draft へ解析し、
  protocol 交渉と success response の serialization が完了した後だけ commit する
  （#4540）。拒否された handshake、`CDIDX_MCP_RESPONSE_MAX_BYTES` fallback、
  serializer failure は初期化の取得を取り消し、確立済み session state を変更しない。
  成功時は lifecycle とすべての metadata を単一の immutable snapshot として同時に
  公開する。以後の重複 initialize は拒否され、進行中の古い `roots/list` response は
  roots-change notification が snapshot を無効化した場合に公開できない。この保証は server-side
  JSON-RPC serialization の境界に適用され、HTTP 配送には別の fail-closed 境界がある。
- **認証ミドルウェア**（#1559）。`McpServer` はパース済み JSON-RPC リクエストごとに、メソッド抽出 *後*・dispatch *前* で `IMcpAuthenticator` を呼ぶ。既定の `LocalStdioAuthenticator` は permissive で（従来の stdio 動作を維持し、呼び出し元を `stdio` / `local` でタグ付けする）、stdio では `CDIDX_MCP_AUTH_TOKEN` を設定すると `TokenMcpAuthenticator` に切り替わる。未設定または空文字の token だけが permissive で、空白のみ・空白文字入り・制御文字入り・4096 文字超の token は設定値として拒否する。`TokenMcpAuthenticator` は応答が必要な全リクエストに対し、`params.auth.token` が一致することを要求し、比較は `CryptographicOperations.FixedTimeEquals` による定数時間比較で行う。HTTP はこの body token ゲートを重ねず、`ProgramRunner` が `CDIDX_MCP_HTTP_TOKEN` を優先し、未設定なら `CDIDX_MCP_AUTH_TOKEN` を fallback として bearer secret に解決して、`Authorization: Bearer ...` の transport check に一本化する（#3156）。HTTP bearer 値は `Bearer ` の後ろを trim せず完全一致で扱い、空白文字・制御文字・4096 文字超は hash 前に拒否する。JSON-RPC body token ゲートの失敗は統一された JSON-RPC `-32001 "Unauthorized"` を返し（#1530 の sanitization 方針に従い、ワイヤでは未提示と不一致を区別しない）、`BuildAuthFailureLog` が詳細を stderr に書き出す。handshake 制御の `notifications/initialized` は認証せず short-circuit できるが、それによって送る roots request は認証済み initialize で commit した capability snapshot に制限される。一方、state-changing notification（`$/cancelRequest`、`notifications/cancelled`、`notifications/roots/list_changed`、`notifications/shutdown`、`notifications/exit`）は cancellation / roots / lifecycle state を変更する前に認証する。認証失敗時も notification は応答を返さず、bounded な stderr 診断だけを残す（#4537）。このミドルウェアが将来 transport の差し替え seam になる — ネットワーク listener は別の `IMcpAuthenticator` を提供しつつ、`McpCallerIdentity`（`Source` + `Subject`）の形を保ち、監査ログ（#1562）から再利用できる。

MCP は独立したシリアライズ戦略（オブジェクトを JSON などの転送形式に変換する方式のこと。CLI の `--json` 側は .NET 標準の `JsonSerializer` に任せる方式、MCP 側は `JsonObject` を手で組み立てる方式と、別の手段を採っている）を採るため、「そもそもバイナリは走るのか?」を確かめる最も頑健なスモークテスト（デプロイや起動直後に行う、基本動作だけを短時間で確認する簡易テストのこと。詳細な正しさではなく「煙が出ていないか＝致命的に壊れていないか」を見るためこの名で呼ばれる）となる — .NET ホスト、`Program.Main`、CLI ルーティング、`ConsoleUi.LoadVersion()` に負荷をかけるが、SQLite には触れない（`search` など MCP の*ツール呼び出し*は SQLite に触れるが、`initialize` 単独では触れない）。

#### プラガブルなトランスポート（`IMcpTransport`）— issue #1558

`McpServer.RunAsync` は 2 つに分かれている。public な stdio エントリポイント（`StdioMcpTransport` と legacy な stdin/stdout のペアを構築する）と、JSON-RPC ループ本体を持つ internal な `RunAsync(IMcpTransport, CancellationToken)` で、後者はトランスポート非依存。`IMcpTransport` 契約は以下のとおり:

- `Task<string?> ReadFrameAsync(CancellationToken)` はリクエストフレームを 1 つ文字列で返すか、ストリーム終端を示す `null` を返す（stdin クローズ、HTTP listener キャンセル等）。stdio EOF では、MCP ループは受理済み request に bounded な grace/cancel/post-cancel drain を適用し、terminal malformed-input write と非同期 cancellation callback も同じ最終 deadline に含めて終了する（#4543）。
- `Task WriteFrameAsync(string?, CancellationToken)` は応答フレームを 1 件書く。`null` は「これは通知だった」を意味し、stdio は何も書かず、HTTP は処理中のリクエストを `204 No Content` でクローズする。
- 基本契約は厳密に「1 read → 1 write」。transport は `IConcurrentMcpTransport` を実装しない限り再入を明示的に拒否する。並行対応 transport の `McpTransportFrame` は入力 frame 1 件専用の response writer を保持するため、複数 request の完了順が前後しても HTTP 応答が別の POST に結び付かない（#4536）。base loop は outer concurrency permit を取得せず、dispatch が global execution permit を1つだけ取得する。base transport の shutdown completion も bounded terminal-write drain に参加する。
- `IAsyncDisposable` により、各トランスポートが自身のカーネル側リソース（ファイルハンドル、listener プレフィックス）を `McpServer` と結合せずに解放できる。

`StdioMcpTransport` は #1558 以前と同じ stdio framing を維持しつつ、入力を strict UTF-8 として検証する（BOM 自動検出は無効にし、UTF-16/UTF-32 フレームが encoding を切り替えられないようにする。出力は BOM なし UTF-8、64 KiB バッファ、`AutoFlush = true`）。不正な UTF-8 バイトは transport decode failure として表面化し、MCP ループはバイトを U+FFFD に黙って置換せず、invalid UTF-8 のヒント付き JSON-RPC `-32700` に変換する。teardown では read を unblock するため input を即時 close し、output dispose は write に到達し得る accepted task の aggregate 完了まで defer する。

JSON-RPC batch array は最大 100 item を受け付ける。独立 item は global request 上限の範囲で並行実行し、initialize/session mutation と重複 request ID は入力順 fence になる。cancellation control の実行前に、server は通常 batch request の全 unique ID を durable に事前登録する。このため queue 待ち target も 64 件 / 5 秒の短命な scheduler-race tombstone cache に依存せず cancellation でき、dispatch 前に return した item は登録を解放して ID を安全に再利用できる。item ごとに cancellation/error context を分離し、完了順が前後しても response item は入力順で出力する。notification-only batch は response を返さず、空 batch と nested batch は `-32600` を返す。HTTP bearer 認証と session 検証は out-of-band cancellation より先に行う。cross-frame control は queue admission 前に一度だけ抽出し、同じ batch 内 ID を対象にする control は raw batch に残して server の durable 事前登録で解決する（#4545）。

`HttpMcpTransport`（同じく #1558）は `System.Net.HttpListener` をラップする:

- transport は server process ごとに論理 MCP client session を 1 つだけ扱う。最初に成功する `initialize` は `Mcp-Session-Id` なしで受理でき、その response が標準 `Mcp-Session-Id` header に新しい identifier を返す。以後のすべての POST と `GET /events` は完全に同じ値を提示する必要がある。欠落または誤った identifier は transport 境界で拒否し、`McpServer` の caller、roots、capability state へ到達させないため、別 client は確立済み session を置き換えられない。identifier は process scope で、再起動後に変わる。client は opaque な session selector として非公開に保つ必要があるが、認証の代替ではなく bearer-token gate とは独立している。identifier 欠落は `400` / `session_required`、不正・曖昧値は `404` / `session_not_found`、最初の initialize が pending 中の競合 headerless initialize は `409` / `session_initialization_in_progress` を返し、分類は `X-Cdidx-Mcp-Rejection` に入る。
- HTTP POST 1 件 = JSON-RPC フレーム 1 件で、対応する応答は HTTP レスポンスのボディ（`200 OK` / `application/json; charset=utf-8`）に乗る。通知は `204 No Content`。`GET /events` は独立した `text/event-stream` subscription を開き、確立済み session は複数 subscription を保持して各 server notification をすべてで受信する。サーバーは `CDIDX_MCP_KEEP_ALIVE_INTERVAL_S` で keep-alive notification が opt-in された場合を除き、自発的な frame を送信しない。長寿命の event stream は独立した admission semaphore を使い、POST handler capacity を消費しない。`/` への POST 以外は `405 Method Not Allowed`。session 検証後、空 / 空白のみのボディは stdio の空行と同じ扱いで `204 No Content` を返し、ループは殺さない。リクエスト本文は `CDIDX_MCP_HTTP_MAX_REQUEST_BYTES`（既定: 1,000,000 bytes、最大: 16,777,216 bytes）で制限し、超過時は全量を buffer する前に `413 Payload Too Large` を返す。保留中 request queue は `CDIDX_MCP_HTTP_MAX_QUEUE_DEPTH`（既定: 64、最大: 1,024）、POST などの短命 handler task は `CDIDX_MCP_HTTP_MAX_CONCURRENT_HANDLERS`（既定: 64、最大: 1,024）、独立 gate 上の同時 `/events` stream は `CDIDX_MCP_HTTP_MAX_EVENT_STREAMS`（既定: 16、最大: 1,024）で制限し、満杯時は無制限に work を保持せず `Retry-After: 1` 付きの `429 Too Many Requests` を返す。bearer 認証と session 検証後、mixed batch の cross-frame cancellation notification は queue 判定より先に out-of-band 処理し、same-batch target の control は raw batch に残す。残りの raw item だけが queue capacity を消費する。limit 環境変数は未設定の場合だけ既定値を使い、設定済みの非数値、ゼロ、負数、最大値超過は正確な変数名と受理範囲を示して listener 起動前に失敗する。`/healthz` は両 admission capacity と `http_separate_event_stream_handlers: true` を報告する。
- request body は read admission から queue、MCP 実行、HTTP response 完了、および cancellation 後の detached drain まで process-wide の weighted byte reservation を共有する。`CDIDX_MCP_HTTP_MAX_IN_FLIGHT_REQUEST_BYTES` の既定値は 64 MiB、最大値は 1 GiB で、request 単位 body limit 以上でなければならない。既知長は最初の read 前に全量を atomic に予約し、chunked body は各 bounded read 前に予約する。枯渇時は `request_body_budget_limit` 付き HTTP 429 を返す。handler owner、queue、pending frame、writer は idempotent な reservation 1 件の所有権を移す。cancel 済み isolated action が token を無視する場合は `McpServer` の concurrency lease と byte reservation の両方を completion continuation へ移し、切断の反復で設定上限外の work を蓄積させない。shutdown は単一 queue reader と直列化して queued / pending request を drain する。health は limit、local / process の現在値、peak、process scope、rejection count を報告する。
- POST lifetime は body validation 前に開始し、read 単位の idle deadline と total deadline の両方で制限する。`CDIDX_MCP_HTTP_BODY_IDLE_TIMEOUT_MS` の既定値は 30,000 ms、最大値は 600,000 ms、`CDIDX_MCP_HTTP_REQUEST_TIMEOUT_MS` の既定値は 120,000 ms、最大値は 3,600,000 ms で、total deadline は idle deadline 以上でなければならない。total deadline は body read、queue、MCP dispatch、tool / SQLite work、response 完了までを含む。linked-list queue により cancelled queued request を O(1) で除去し、single-winner lifetime state により timeout、disconnect、shutdown、normal write が競合しても reservation、response、timer、semaphore cleanup を idempotent に保つ。`IRequestLifetimeMcpTransport` は選択中 HTTP request token を `McpServer` の current request token に link し、cancellation を tool dispatch と `DbReader.Cancellation` まで伝播する。session 確立後の応答を伴う JSON-RPC frame は queue admission 後に bounded な chunked-response probe を開始し、flush する ASCII space を有効な JSON 先頭空白として post-body disconnect を検出する。probe または SSE output の開始後は、起点 POST が cancel されても stream-owned の bounded write lifetime が serialization gate 内で完了または abort し、probe write timeout は matching request を terminal に cancel する。headerless initial `initialize` は response header commit 前に `Mcp-Session-Id` を返せるよう probe を開始せず、notification も probe を使わず `204 No Content` を維持する。request log は `timeout:http_request_body_idle`、`timeout:http_request_lifetime`、`timeout:http_disconnect_probe_write`、`client_disconnected` を使い、health は両方の有効 deadline と timeout、disconnect、queued-cancellation count を報告する。
- POST は `application/json` Content-Type を 1 件だけ受理し、charset は省略または UTF-8 のみとする。strict UTF-8 decode を使うため、未対応 media type / charset は queueing 前に `415`、不正 UTF-8 は `400` で拒否する。native client 向けに `Origin` 欠落は受理するが、present Origin は listener の scheme・host・port と完全一致する単一値だけを許可する。malformed、`null`、ambiguous、cross-origin 値は認証前に `403` とし、CORS preflight は `Access-Control-Allow-*` header を出さず拒否する（#4549）。
- SSE stream lifetime は active stream registry と上限付き active-stream counter だけで表現する。idle stream には最小限の SSE comment heartbeat を送り、切断済み client を検出して stream slot を解放する。その registry entry が削除された後に完了済み stream task を保持しない。
- `ResolveListenSpec("host:port")` は prefix を事前に解決するため、CLI が stderr に `Listening on http://...` を出せる。ポート `0` は一時 `TcpListener` を probe して空きポートを取得する。probe から `HttpListener.Start()` までの TOCTOU window は、本トランスポートが local-only / single-tenant 想定であるため許容する。ワイルドカードホスト `+` / `*` はパース時点で拒否する。
- 共有秘密認証は secure by default: loopback を含むすべての HTTP listener が `Authorization: Bearer <token>` を要求し、定数時間で比較する。`CDIDX_MCP_HTTP_TOKEN` が未設定なら `CDIDX_MCP_AUTH_TOKEN` を bearer secret として fallback し、両方が設定されている場合は前者を優先する。HTTP クライアントが `params.auth.token` も送る必要はない。token 未指定／空文字では loopback も起動を拒否し、明示的な `--allow-unauthenticated-http` だけが unsafe な loopback 例外となる。この flag は non-loopback では拒否する。設定 token は 1-4096 文字で、空白文字・制御文字・カンマを含んではならない。受信 bearer token は `Bearer ` 接頭辞の後を trim せず完全一致で扱い、空白文字・制御文字・カンマを含む値または 4096 文字超の値は hash 前に拒否する。bearer 認証は `Mcp-Session-Id` 契約とは別に評価され、確立済み session では両方が必要になる。
- 任意のリクエストループログ: `ProgramRunner` は `HttpMcpTransport` を `GlobalToolLog` に接続するため、lifecycle log が有効な場合は HTTP リクエストごとに `mcp_http_request` 行を 1 件記録する。記録内容は method、path、status、duration、auth outcome、remote peer、correlation id、および利用可能な場合は opaque な JSON-RPC request-id token とその型・decode 後の値長である。caller-controlled な method、path、remote peer は 256 文字を上限に `...<truncated>` marker 付きで切り詰める。リクエスト/レスポンス本文や JSON-RPC id の生値は含めない。
- キャンセルは `_listener.Stop()` に接続するため、シャットダウン時に `GetContextAsync()` が unblock する。`HttpListenerException` / `ObjectDisposedException` は EOS と同じ扱いで MCP ループを stdin クローズと同じ経路で終了させる。

ワイヤー選択は `ProgramRunner.RunMcp` で行う。`--transport stdio|http`、`--http-listen <host:port>`、loopback 専用の `--allow-unauthenticated-http` は下流の引数解析より前に取り除かれ、HTTP bearer token 解決は `CDIDX_MCP_HTTP_TOKEN` を先に見て、未設定なら `CDIDX_MCP_AUTH_TOKEN` に fallback する。ディスパッチは旧来の stdio 経路または `RunMcpHttp` に着地する。プラガブルなシームは JSON-RPC 順序不変条件を両トランスポートで同一に保つので、既存の McpServer テスト群（`ProcessLineAsync` を叩く）は引き続きメソッド単位の挙動をカバーし、新トランスポートのワイヤーレベル契約は `HttpMcpTransportTests` がカバーする。

#### JSON-RPC request id の telemetry 境界 — issue #4551

`McpRequestIdTelemetry` は、client-supplied な JSON-RPC id と observability data
の間に置く唯一の変換境界である。random な process-local salt を使った
HMAC-SHA256 から固定長の `rid:v1:...` token を導出する。id の生値は JSON-RPC
wire と、response echo / routing / cancellation に必要な protocol 内部だけに残し、
log、metric、trace、activity、audit event へコピーしてはならない。

token は長さだけでなく cardinality も process 単位で制限する。process ごとに最大
4,096 件の distinct id までは個別 token を保持する。その budget を使い切った後の
未観測 id はすべて、process-salted な固定長 overflow token 1 個へ集約し、登録済み id
の token は維持する。したがって 1 process 内の `request_id` は最大 4,097 distinct 値である。

同伴 metadata は内容を保持しない。`request_id_type` は `string`、`number`、
`null` のいずれかで、`request_id_length` は string なら decode 後の値の UTF-16 code unit 数、
number なら JSON text の文字数、`null` なら `0` である。stderr の correlation
prefix と invocation JSON、Activity tag（`rpc.request_id`、
`rpc.request_id_type`、`rpc.request_id_length`）、MCP metrics、audit JSONL、
HTTP request log、timeout diagnostics / status は、すべて同じ token/type/length
tuple を使う。CLI metrics には JSON-RPC request がないため、これらの field を
省略する。token が deterministic なのは同一 process 内だけで、server process 再起動時に
salt が変わり、process をまたぐ相関は意図的に切れる。新しい telemetry surface の
test には credential 風 id を含め、生値が現れないことを検証する。

#### 構造化エラーエンベロープとサーバーコード — issue #1581

MCP のエラー応答は JSON-RPC の `error` オブジェクトでも、MCP ツール結果エラー（`isError: true`）でも、共通の canonical な `data` エンベロープを必ず載せる。クライアントは人間向けの `message` 文字列をパースする代わりに、安定した機械可読カテゴリで分岐できる。
必須文字列ツール引数について、人間向け message は引数自体が無い場合だけ `Missing required parameter: <name>` となる。引数は存在するが空または空白のみの場合は `Parameter "<name>" cannot be empty or whitespace-only` を返す（#2145）。

- `data.category` — 安定ワイヤ識別子（下表参照）。
- `data.suggestion` — オペレータが取れる次のアクション（英語）。
- `data.retry_safe` — サーバ状態を変えずに同じリクエストを再送できる場合 `true`（例: キャンセル、レート制限、スキーマが古い）、それ以外は `false`（例: DB 破損、未知ツール）。

定数は `src/CodeIndex/Mcp/McpErrorEnvelope.cs` に集約され、構築ヘルパー `McpErrorEnvelope.BuildData(category, suggestion, retrySafe, extraData)` が唯一の生成点となる。`extraData` でカテゴリ固有フィールド（例: rate-limited の `tool` / `retry_after_ms`）を合流でき、canonical キーを先に書き込むので `extraData` は上書きできない。

JSON-RPC 2.0 は `-32700` と `-32600..-32603` を仕様自身、`-32000..-32099` をサーバー実装に予約している。cdidx は server レンジ内でコードを割り当てる。標準コード（`parse error` / `invalid request` / `method not found` / `invalid params` / `internal error`）は JSON-RPC エンベロープ自体に引き続き使い、下記コードは cdidx 固有カテゴリをカバーする。

| サーバーコード | カテゴリ | `retry_safe` | 発火条件 |
| --- | --- | --- | --- |
| `-32000` | `rate_limited` | `true` | caller-wide pre-validation bucket または secondary known-tool bucket による拒否（#1560 / #4547）。後方互換のため legacy フィールド `error_category` / `tool` / `caller` / `retry_after_ms` も canonical envelope と並べて維持する。`retry_after_ms` は必要なすべての token refill と bucket-cap capacity 制約が再試行を許可できる最短時刻を表す。 |
| `-32001` | `permission_denied` | `false` | トークン認証失敗（`TokenMcpAuthenticator`、#1559）。ワイヤは汎用のまま、stderr に詳細を書く。 |
| `-32003` | `server_busy` | `true` | bounded concurrent-frame admission backlog が満杯（#4536）。`data.retry_after_ms` の後に再送する。 |
| `-32010` | `index_missing` | `true` | DB パスが無いか、対象ツール呼び出しのためにオープンできなかった。オペレータが `cdidx index <projectPath>` を実行した後は同じリクエストを安全に再送できる。 |
| `-32011` | `index_stale` | `true` | SQLite が `no such table` / `no such column` を返した。古い cdidx で書かれた DB に新しい binary を当てた状態で、`cdidx index <projectPath> --rebuild` が必要。 |
| `-32012` | `index_corrupted` | `false` | SQLite が `database disk image is malformed` / `file is not a database` / `file is encrypted` を返した。読めないので運用側で削除して再構築。 |
| `-32015` | `request_cancelled` | `true` | MCP リクエストがキャンセルされた（クライアント切断、シャットダウン等）。必要なら再送でよい。 |

下記カテゴリは標準 JSON-RPC コードに乗る。

| 標準コード | カテゴリ | `retry_safe` | 発火条件 |
| --- | --- | --- | --- |
| `-32700` | `parse_error` | `false` | フレームが JSON として解析できなかった。 |
| `-32700` | `message_too_large` | `false` | フレームがパース前にバイト上限を超えて拒否された。フレームリーダーは「読めない」という意味で parse-error と同じコードを使う。 |
| `-32600` | `invalid_request` | `false` | パースはできたが JSON オブジェクトでない、JSON-RPC 必須フィールド欠落、または不正な `id` を含む。有効な request id を復元できない場合（トップレベルの scalar / `null` フレーム、不正なオブジェクト、不正な batch 要素を含む）、応答には `id:null` を含める。文字列と数値の request id は echo / audit serialization 前に上限を適用し、過大な id では `data.max_request_id_chars` / `data.max_request_id_bytes` も併記する。 |
| `-32601` | `method_not_found` | `false` | 未知 JSON-RPC メソッド（`initialize` / `tools/list` / `tools/call` / `ping` / サポート対象 `notifications/*` 以外）。 |
| `-32601` | `tool_disabled` | `false` | `CDIDX_MCP_TOOLS_ALLOW` / `CDIDX_MCP_TOOLS_DENY`（#1561）で無効化された既知ツール。ワイヤコードは #1581 以前のクライアント契約を保つため `-32601` のまま維持し、envelope のみ additive に追加する。`data.tool` に無効化されたツール名を含める。 |
| `-32602` | `tool_unknown` | `false` | `tools/call` がサーバー未実装の MCP ツール名を指定した（typo またはバージョン不整合）。`data.tool` に未知の名前を含める。 |
| `-32602` | `missing_parameter` | `false` | `tools/call` リクエストに必須 `params.name` 文字列が無い。 |
| `-32602` | `invalid_argument` | `false` | ツールが引数 shape を拒否した（#1554 のプロトコルバージョン交渉ミスマッチもここで、`data.requestedVersion` / `data.supportedVersions` を併載）。 |
| `-32602` | `regex_timeout` | `true` | ユーザー指定 regex が実行中に bounded match timeout を超えた場合。例: `find_in_file` の regex scan。`data.error_code` に CLI と揃えた stable code を含める。 |
| `-32603` | `internal_error` | `false` | 未処理例外の fallback バケット。ワイヤメッセージは #1530 の sanitization に従い汎用のまま、stderr に例外型を出す。 |

分類器 `McpErrorEnvelope.ClassifyException(ex)` は未処理例外を例外型と一部の `SqliteException.Message` サブストリングから `index_stale` / `index_corrupted` / `request_cancelled` / `internal_error` にマッピングする — 生メッセージはワイヤに乗らない（#1530）。`ProcessFrame` の JSON-RPC catch-all と `tools/call` の catch-all が同じ分類器を使うため、ツール呼び出し途中で `SqliteException` が起きても `error.data` でも `result.structuredContent` でも同じ `index_stale` envelope が surface する。

`McpServer.cs` 側の構築点は 2 か所:

- `CreateErrorResponse(... category, suggestion, retrySafe, extraData?)` は JSON-RPC `error` オブジェクトを組み立て、`error.data` に envelope を載せる。
- `CreateToolErrorResponse(id, message, category, suggestion, retrySafe, extraData?)` は MCP ツール結果エラー shape（`isError: true`）を組み立て、`result.structuredContent` に envelope を載せる。カテゴリ未指定の旧 call site でも envelope が抜けないよう、2 引数オーバーロードは `invalid_argument` / `retry_safe=false` を既定とする。

新しいエラー経路を足すときは、上記表から最も具体的なカテゴリを選ぶ（必要なら一覧を拡張し、本セクションと `McpErrorEnvelope.cs` を同期更新する）。テストは `tests/CodeIndex.Tests/McpServerTests.cs` の `ErrorResponse_*` / `ToolResult_*` / `ClassifyException_*` / `BuildData_*` が envelope contract をカバーしているので、カテゴリ追加時はテストも併せて拡張すること。

### release の `--json` 方針と trimmed fallback

`release.yml` は自己完結バイナリを `-p:PublishTrimmed=true` で publish する。`status --json` や `index --json` などの CLI JSON payload は `CliJsonSerializerContext` の source generation でカバーしているため、reflection-based `System.Text.Json` に依存しない。release verify step は tarball を install したうえで `status --json` を実行し、この性質が黙って壊れないようにしている。

プロジェクトは `PublishTrimmed=true` 時の Roslyn compile-time trim analyzer を無効化している。.NET 8 analyzer が RID-specific publish の開始前に AD0001 で失敗することがあるためで、trimming 自体を無効にしているわけではない。ILLink の publish-time pass は引き続き実行され、trim analysis warning も出力される。

`JsonOutputFailure` は、手動変更ビルドや実験的なビルド向けの防御として残している。.NET 8 では trimming が暗黙に `JsonSerializerIsReflectionEnabledByDefault=false` を設定する。ソース生成済み `JsonTypeInfo<T>` を持たない reflection serializer 経路に custom build が到達した場合、CodeIndex は serializer failure を database error と誤分類せず、専用 stderr メッセージと終了コード `4`（`FeatureUnavailable`）で fail-fast する。新しい CLI JSON DTO を追加するときは、release artifact の trim を維持できるよう `CliJsonSerializerContext` への登録も同時に行う。

```mermaid
flowchart TD
    B["公式 release バイナリ<br/>PublishTrimmed=true でビルド<br/>source-generated CLI JSON が有効"]
    B --> R{ユーザーコマンド}
    R -->|cdidx status / index --json| A["CLI ランナーが<br/>JsonSerializer.Serialize&lt;T&gt;(value) を呼ぶ"]
    R -->|cdidx search / status（--json なし）| H["人間向け出力<br/>（JSON 不使用）"]
    R -->|cdidx mcp| M["McpServer が<br/>JsonObject / JsonArray を<br/>手組みして Write()"]
    A --> AX["成功"]
    H --> HX["成功"]
    M --> MX["成功"]
    T["source-generated CLI DTO coverage の無い<br/>custom build"] --> TX["JsonOutputFailure fail-fast<br/>終了コード 4（FeatureUnavailable）"]
```

### 診断表: 症状 → 原因 → 対処

| 症状 | 根本原因 | 対処 |
| --- | --- | --- |
| `cdidx --version` が `cdidx v0.0.0` | `version.json` が `$HOME/.local/bin/` のバイナリの隣に無い | 修正後の `install.sh` を再実行。`ls $HOME/.local/bin/version.json` を確認 |
| 任意のコマンドで `DllNotFoundException: Unable to load shared library 'e_sqlite3'` | `libe_sqlite3.so`（または `.dylib`）がバイナリの隣に無い | 修正後の `install.sh` を再実行。`ls $HOME/.local/bin/libe_sqlite3.*` を確認 |
| `install.sh` のエラー: `musl-based Linux (e.g. Alpine) is not supported` | コンテナが musl libc を使用 | glibc ベース（debian/ubuntu）に切り替えるか、SDK のある環境で `dotnet tool install -g cdidx` を使う |
| `install.sh` のエラー: `macOS x86_64 (Intel) binaries are not published` | Intel Mac が `osx-x64` RID に到達 | .NET SDK のある環境で `dotnet tool install -g cdidx` を使うか、`dotnet publish src/CodeIndex/CodeIndex.csproj -c Release -r osx-x64 --self-contained true` で source build する |
| `install.sh` のエラー: `Checksum mismatch!` | tarball の改ざんまたは転送時破損 | 再実行。それでも起きるならリリースページの `sha256sums.txt` と tarball を確認 |
| `Error: --json is not available on this trimmed build.` | 手動/custom build が source generation 未対応の reflection-based `JsonSerializer` 経路に到達した | 公式 `install.sh` release または NuGet グローバルツール版を使う、`--json` を外す、MCP を使う、または publish 前に不足 DTO を `CliJsonSerializerContext` へ追加する |
| 明らかにファイルのあるリポジトリで `cdidx status` が `Files: 0` | インデックス DB を作っていない、あるいは別の `--db` を指している | 先に `cdidx <projectPath>` を実行。`.cdidx/codeindex.db` の存在を確認 |
| 全コマンドが `index fresh` だが結果は明らかに古い | 別の作業コピーにインデックスを張っている | `cdidx . --commits HEAD` または `cdidx . --files <paths>` を再実行 |
| ホストが stderr を握りつぶし、ユーザーには「cdidx がうまく動かない」だけが見える | シェルやランチャーが端末 stderr を回収または破棄している | `cdidx status --log-path` を実行し、解決されたユーザー別ディレクトリの日次永続 stderr ログ（`stderr-YYYYMMDD.log`）を確認する。意図的に痕跡を残したくないときだけ `CDIDX_DISABLE_PERSISTENT_LOG=1` で無効化する |

### なぜこれが重要か

Cloud セッションは開発ループの中で `dotnet build` にフォールバックできない唯一の環境である。壊れたインストールパスは、SDK を持つ開発者には可視化されない — ローカルで再ビルドすれば済んでしまうためである。bootstrap プロンプト、スモークテスト、および本セクションを整備しているのは、ユーザー向けインストールフローにおけるリグレッションが、リリース後の実ユーザーではなく、次に Cloud セッションを開いた者によって検出されるようにすることを意図している。

## Regex timeout と redaction fallback policy

Regex timeout の挙動は `RegexTimeoutPolicy` (`src/CodeIndex/Diagnostics/RegexTimeoutPolicy.cs`) に集約する。新しい timeout 経路を追加する前に、カテゴリ文字列、ユーザー向け timeout メッセージ、redaction fallback をこの policy に置くこと。

契約:

- **indexing と configured extractor pattern。** indexing 診断は `regex_timeout`、configured pattern 診断は `pattern_regex_timeout` を使う。実行を完了できるよう、影響を受けたファイルまたは pattern を skip し、病的な pattern 入力を漏らさず bounded diagnostics を報告する。
- **query/find と MCP find。** CLI の human/JSON エラーと MCP error envelope は、同じ timeout duration 表記で `regex_timeout` を使う。CLI flag と MCP tool argument が異なる箇所だけ、復旧 hint を surface 別にする。
- **redaction surface は fail closed。** `DiagnosticRedactor`、`GlobalToolLog`、MCP audit の argument value は、対象値を設定済み redaction placeholder へ置換する。sensitive name 判定は `SensitiveNameClassifier` を使い、区切り文字と大小文字を正規化して共有 credential fragment を確認するため、diagnostic と audit の redaction がずれない。`DiagnosticSanitizer` は `[message omitted after sanitization timeout]` でメッセージ全体を省略する。`SuggestionStore` は `redaction_timeout` を記録し `[REDACTED:redaction_timeout]` を永続化する。GitHub API response body は `[response body omitted after redaction timeout]` に置換する。
- **bounded extraction helper。** `BoundedRegex` は extraction を best-effort に保つため、operation に応じて empty matches / `false` / 元入力を返し、capture scope が有効な場合は timeout diagnostics を記録する。`EnumerateMatches` は consumer が次の結果を要求したときだけ `Match.NextMatch` で進むため、bounded extractor loop は dense match collection の残りを実体化せず停止できる。

## メトリクス出力

`MetricsSink` (`src/CodeIndex/Cli/MetricsSink.cs`) は CLI コマンドと MCP ツール呼び出し向けの opt-in JSONL metrics sink です。`ProgramRunner.Run` が session を所有するため、command dispatch、MCP request、最後の command-level metric が 1 つの上限付き lifetime に収まります。`MetricsSink.Record` は caller 上で出力先 IO を実行してはいけません。公開済み event budget 内で serialize し、上限付き multi-writer queue へノンブロッキングで書き込みます。単一の background reader がレコードを上限付き batch で drain し、append と flush を実行します。

caller と operator が依存できる契約:

- **ノンブロッキングな overload。** queue が満杯または complete 済みの場合、新しい event を drop して明示的な drop counter を増やします。producer は queue の空きを待たないため、sink が block しても、上限付き serialization と enqueue work を超えて producer / MCP response の latency を延ばしません。ただし外側の CLI invocation は `ProgramRunner.Run` が返る前に、上限付き session-disposal deadline まで metrics drain を待つ場合があります。
- **batch failure の意味論。** write または rotation failure では、影響を受けた batch を drop として計数し、再送しません。例外が投げられる前に append が一部成功している可能性があり、同じ bytes を再送すると record の重複や JSONL stream の破損を招くためです。後続 batch は上限付き exponential backoff の後に試行し、次の batch が成功すると現在の degraded 状態を解除して recovery を記録します。
- **上限付き diagnostics。** session 内で最初の runtime sink failure が発生したときだけ、設定パスを含まない sanitization 済みの上限付き警告を 1 回出します。その後の失敗は stderr を大量出力せず、counter と上限付き `last_failure` category を更新します。MCP の full status は常に `mcp_session.metrics` を公開し、未設定時は `enabled:false` です。有効な sink は `enabled`、`path`、`max_bytes`、`bytes_written`、`disposed`、`degraded`、`queue_capacity`、`queue_depth`、`queued_event_count`、`written_event_count`、`dropped_event_count`、`queue_full_drop_count`、`serialization_failure_count`、`write_failure_count`、`rotation_failure_count`、`batch_flush_count`、`consecutive_failure_count`、`recovery_count` を公開し、`next_retry_at`、`last_recovery_at`、`last_failure` は任意です。MCP ping は常に同じ object を `metrics` として返します。metrics は任意の telemetry であり、MCP ping や HTTP health / liveness を degraded にしてはいけません。
- **上限付き shutdown。** session の dispose は producer side を complete にし、上限付き drain deadline までだけ待ちます。その deadline までに write 完了を確認できなかったレコードは `dropped_event_count` を増やし、shutdown が出力先を無期限に待つことはありません。正常終了時に最後の CLI / MCP command metric を保持するのも、この bounded drain です。
- **schema の安定性。** batching と recovery は、公開済みの 1 行 1 object schema を変更しません。既存 field の rename や repurpose は output contract の破壊的変更なしには行えません。

## MCP 監査ログの出力

`AuditLogSink` (`src/CodeIndex/Mcp/AuditLogSink.cs`) は MCP サーバーごとのオプトイン JSONL 監査ログ (#1562)。所有者は `ProgramRunner.RunMcp` で、`McpServer` の internal コンストラクタオーバーロード経由で渡される。ほかの呼び出しサイトは関与しない。`tools/call` ごとに必ず 1 レコード生成し、引数欠落（`tool="(missing)"`) や未知ツール (`error_code=-32602`) も含む — リクエスト形状を変えることで監査から消えるのを防ぐためである。

下流コンシューマが依存できる契約:

- **フィールドの安定性。** `timestamp`、`tool`、`arg_keys`、`arg_lengths`、`elapsed_ms`、`error_code` は全レコードで出力する。`caller`、`caller_version`、`request_id`、`request_id_type`、`request_id_length`、`request_id_truncated`、`arg_key_lengths`、`arg_keys_truncated`、`arg_key_truncation_reasons`、`arg_values`、`arg_values_redacted`、`arg_values_truncated`、`arg_values_truncation_reasons`、`arg_values_serialized_bytes`、`arg_values_max_bytes`、`result_count`、`checked_root_identity`、`error` は値が non-null または true のときだけ含める。既存フィールドの改名や流用は破壊的変更扱い（CLI `--metrics` と同じ運用）。
- **request id のプライバシー。** `request_id` は前述の process-salted な固定長 token で、JSON-RPC wire の値ではない。token がある場合は `request_id_type` と decode 後の値長を示す `request_id_length` を同伴する。token 自体がすでに bounded なため、legacy の truncation guard は通常付かない。
- **エラーコード意味論。** `0`=成功、`1`=MCP ツールエラー (`isError: true`)、負値=JSON-RPC エラーコードそのまま（例: invalid params なら `-32602`、internal error なら `-32603`）。同伴する `error` 文字列は `jsonrpc_error` / `tool_error` / `missing_tool_name` / サニタイズ済み例外型名のいずれか。`McpServer.BuildSanitizedToolErrorMessage` が `ex.Message` をワイヤーと audit から除外している（#1530）。
- **result count。** `ExtractResultCount` は `structuredContent.count` を優先し、無ければ `structuredContent.results.length`、いずれも無ければ省略する。ツールエラー / JSON-RPC エラー時も省略する（`0` ではなく欠落）。
- **MCP index の root identity。** `index` は要求された root を canonical に解決して platform filesystem identity を取得し、run 中は no-follow の directory handle を保持します。directory enumeration は Linux / macOS / Windows のすべてで handle-relative に行い、認可済み filesystem seam は各 directory / file の open 前 identity、実際に開いた handle identity、open 後の canonical containment を内容の利用前に照合します。language-map / pattern sidecar は認可済み project tree 内に限定して同じ seam から開き、より広い user 設定と executable workspace plugin を含まない authorization-scoped snapshot に cache します。root、ancestor、link、entry identity の変化時は上限付きの `permission_denied` tool error と `authorization_failure_reason` を返し、成功時/dry-run の structured output と、response が error path で生成された場合を含む認可後の全 audit record は同じ固定長 opaque `checked_root_identity` を保持します。包含 repository root は認可範囲内の場合だけ ignore rule に使い、範囲外なら discovery を要求 project root 内に限定します。
- **引数のプライバシー。** `arg_keys` / `arg_lengths` は常に記録するので呼び出しの *形状* は復元できるが、引数キー数と表示キー長は capped され `arg_keys_truncated` で明示される。`arg_values` は `--audit-log-include-values` に gated（cdidx クエリにはソース片や secret 風文字列が混入しうる）。echo は sanitize と budget を適用した clone として作り、diagnostic / audit 共有 taxonomy で分類された secret 風キーや既知 token pattern は `[REDACTED]` に置換し、depth / object property / array item / total node / string length / serialized byte / event byte の上限に達した場合は値を書き出す前に `arg_values_truncated` を記録する。
- **呼び出し元の特定。** 公開済み initialize snapshot は、成功した `initialize.clientInfo` の bounded な client name/version を保持し、同一セッション内で再 `initialize` が成功すれば置き換える。複数の受理済み handshake が走る長寿命 MCP ループでも、*現在接続中の*クライアントに対して記録が紐付く。protocol 交渉に失敗した initialize は audit attribution や他の session state を上書きしない（#4540）。
- **ローテーション。** 1 レコードごとに open-append-close する。外部 `tail -F` の追従と rename 時の close-state 維持のため。`_bytesWritten >= MaxBytes` を超えた時点で `RotateLocked` が `<path>.(RotationKeep-1)`（現在は `<path>.2`）を破棄し、生存スロットを 1 つ古い側へ寄せ、`<path>` を `<path>.1` へ移す。`RotationKeep = 3` なので `<path>.3` は決して生成されない（`AuditLogSinkTests.Record_KeepsAtMostThreeFiles_DropsOldestOnRotationOverflow` で常時検証）。
- **queue / shutdown accounting。** `queued_record_count` は channel write 成功後だけ、`written_record_count` は file append 成功後だけ増えます。`shutdown_abandoned_record_count` は上限付き shutdown deadline を超えた時点の pending record 数を保持する単調な snapshot で、`shutdown_flush_timed_out` は deadline failure 後も true のままです。abandoned は drop category ではありません。`Shutdown` の return 後に background writer が完了する可能性があるため snapshot は減らさず、`queued = written + dropped + abandoned` の invariant には使用できません。
- **status / degradation。** MCP の full status は有効な sink を `mcp_session.audit_log` に公開し、MCP ping / HTTP health は同じ live object を `audit_log` として返します。`enabled`、`path`、`include_values`、`max_bytes`、`bytes_written`、`disposed`、`queue_capacity`、`queue_depth` に加え、両 surface は `queued_record_count`、`written_record_count`、`dropped_record_count`、`queue_full_drop_count`、`serialization_failure_count`、`write_failure_count`、`rotation_failure_count`、`rotation_cleanup_failure_count`、`rotation_degraded`、任意の `last_drop_reason`、`last_rotation_failure` を公開します。positive な dropped count または rotation degradation は top-level MCP ping / health status を degraded にします。shutdown 専用の abandonment / timeout field は `AuditLogShutdownResult.Diagnostics` と上限付き stderr diagnostic に残します。shutdown 完了時点では MCP server が停止済みだからです。
- **best effort / strict shutdown。** serialization、queue-full、IO、rotation failure が本体ツール呼び出しを crash させることはありません。既定の shutdown は best-effort のままですが、flush deadline を超えると、audit path を含まない count-only の上限付き warning を stderr に正確に 1 回出します。`--audit-log-strict` は `--audit-log` を必須とし、shutdown が未完了なら `ProgramRunner.RunMcp` は本来成功する exit だけを `CommandExitCodes.RuntimeError` (`10`) に変更し、既存の nonzero exit はすべて保持します。構築時の不正 path は引き続き constructor が早期失敗させ、tool dispatch 前に operator が startup misconfiguration を認識できます。

フラグパーサ (`ProgramRunner.TryConsumeAuditLogFlags`) は `QueryCommandRunner.ParseArgs` より前に走り、audit 関連トークンのみを消費します。`--db` と `--` 以降はそのまま残し、既存の escape semantics を保ちます。`--audit-log-include-values` と `--audit-log-strict` は `--audit-log <path>` を必須とします。値の echo も strict durability も、出力先が設定されていなければ意味を持たないためです。

## report artifact 契約

`ReportBundleWriter` は完全な gzip/tar sibling file を staging し、`AtomicFileWriter` で公開します。`ReportCommandOptions.Overwrite` が明示的な `--overwrite` から設定されていない限り no-overwrite move を使います。明示的な置換では atomic な filesystem backup を作り、親 directory の durability flush が完了するまで保持します。公開に失敗した場合は error を返す前にその backup を復元します。`support-manifest.json.bundle.members` が archive member の正式な一覧です。`db_inspected` と `db_diagnostics_included` は read-only の診断収集を表し、`db_member_included` は archive membership を表して現在は常に `false` です。legacy の `db_included` は `db_inspected` の additive compatibility alias として残します。

`LastFailureEventStore` schema 3 は、従来の binary version と UTC timestamp に加えて、不透明な `workspace_id`、`database_id`、`run_id` provenance を持ちます。failure capture は command が実際に使った index / query option parser から実効 DB を導出し、positional ordering と `--` literal sentinel も同じ規則で扱います。report の既定 DB は通常の query precedence（`CDIDX_DATA_DIR`、active workspace、XDG、ancestor workspace）で解決するため、診断収集と correlation は同じ database identity を使います。report は workspace、database、binary version が一致し、event が24時間以内で、未来方向の clock skew が5分以内の場合だけ保存 event を同梱します。それ以外は archive から除外し、manifest には上限付きの `last_failure.disposition` / `reason` と、検証済みの不透明 provenance field だけを出力します。workspace / database の raw path はこれらの field に入りません。

## コーディング規約

- コメントは英日併記（例: `// Enable WAL mode / WALモードを有効化`）
- ドキュメント（README, CHANGELOG）は前半英語、後半日本語の構成。
- 不要な本番パッケージは入れない。test-only package は、テストハーネスの改善に明確に寄与し、`tests/CodeIndex.Tests/` に閉じる限り許容されるが、本番依存ルールを緩めるものではない。

## センシティブバッファの方針

認証情報、リクエスト payload、ローカルファイルの byte、その他 user-controlled content
を保持しうる pooled byte buffer は、その buffer が再観測される前に clear する必要があります。
センシティブ境界では素の `ArrayPool<byte>.Shared.Return(...)` より、方針を名前で示す helper を優先してください。

- **Token material** は rented array を返すときに full-buffer clearing を使います。
  `McpAuthenticationLimits.HashToken` は token として実際に書いた UTF-8 byte だけを hash し、
  その used range をすぐ zero 化したうえで、rented array を `clearArray: true` で返すため、
  過去の rent 由来の未使用 byte も消去されます。
- **LSP request payload** は rented buffer を返す前に used payload range だけを clear します。
  lease が宣言済み content length を保持しているため、used range clearing が安定した契約です。
  その範囲外の byte は payload ではなく、リクエストごとに full rented-array clear を強制しません。
- **Bounded HTTP copy buffer** は installer、archive、response byte を private storage に書く前に
  通す可能性があるため、センシティブとして扱います。返却前に rented copy buffer 全体を clear します。
- **ASCII protocol header と生成された JSON/report byte** は pooled / accumulated storage を使っていても、
  それだけではセンシティブ扱いにしません。明示的な maximum-byte budget は必要ですが、call site が
  認証情報や source payload を運び始めない限り clearing は必須ではありません。

`ArrayPool<byte>` や in-memory accumulation（`MemoryStream`、captured `Utf8JsonWriter` output、
report/archive buffer）を追加するときは、まず data を sensitive bytes、bounded non-sensitive payload、
generated JSON、archive/report bytes、diagnostic snippet に分類してください。Sensitive path は
`SensitiveBufferPolicy.ReturnSensitiveTokenBuffer`、`ReturnSensitivePayloadBuffer`、
`ReturnSensitiveCopyBuffer`、`ClearUsedSensitiveBytes` を経由させてください。これらの helper 名は
security audit で positive evidence として拾えるようにしています。Bounded generated JSON capture は
`SensitiveBufferPolicy.GetBoundedGeneratedJsonInitialCapacity` を使ってください。Sensitive path には
cleared range を証明するテストが必要です。Bounded accumulation path には maximum byte budget を
証明するテストまたは定数が必要です。

## カスタム言語抽出

下流ユーザーは `cdidx` を再ビルドせずに軽量な言語対応を追加できます。

- 拡張子 alias は `~/.config/cdidx/langmap.yaml` と、最初に見つかった workspace
  祖先の `.cdidx-langmap.yaml` から読み込まれ、workspace 側が user 側を上書きします。
  信頼済み suffix override は built-in の完全一致 filename、filename-prefix、extension rule
  より先に評価されます。最も近い workspace map を probe または read できない場合、その subtree
  では親 map を再利用せず ancestor workspace 探索を停止します。`languages --json` と MCP の
  `languages` tool は sanitization 済み失敗を `language_map_diagnostics` に公開し、実効順序を
  `detection_policy.precedence` で示します。
- regex ベースのシンボルパターンは `.cdidx/patterns/*.yaml` と
  `~/.config/cdidx/patterns/*.yaml` から読み込まれます。sidecar は symlink ではない
  pattern directory 配下の通常ファイルのみが対象で、探索候補は pattern directory ごとに
  128 件まで、各ファイルは 64 KiB / 128 ルール、immutable な workspace snapshot ごとに
  configured rule 128 件に制限され、
  regex match には 100 ms の timeout が付きます。各 sidecar は path・rule・budget を commit する前に
  一時状態で parse / compile され、`SymbolKindCatalog` に対して kind が検証されます。拒否された内容は
  fingerprint によって重複診断を抑制し、内容または metadata の変更時、および一時的な read failure の
  回復後にはプロセスを再起動せず再試行されます。workspace 探索には明示的な trust root が必要で、
    その root を確認した時点で停止し、それより上の ancestor は探索しません。その境界内の nested
    sidecar は、対象 file の上限付き extraction worker 内で読み込まれます。path identity は実際の
  filesystem の case-sensitivity に従うため、case-sensitive volume では大小文字だけが異なる sidecar も
  別々に扱われます。`status --json` の `extractors.pattern_configs[]` は、受理済み file の
  sanitization 済み path、workspace/user provenance、正規化済み language、rule count を報告します。
  reindex は workspace snapshot を atomically に置換するため、以前の rule budget と timeout state は
  他 workspace を変更せず回収可能になります。timeout した rule は所有する workspace snapshot 内だけで
  上限付きの1分間 cooldown に入り、workspace-scoped diagnostic を出します。
- `cdidx test-extractor --language <lang> --file <path> --json` は index を作らずに
  symbol extraction だけを実行し、`--expect-symbols <json>` で fixture JSON と比較できます。
  source と expectation file はそれぞれ 4 MiB に制限されます。

query 側の `--lang` 解決は、別の組み込み一覧ではなく、この workspace-aware な
extension / extractor registry を共有します。登録済み language ID、alias、拡張子形式の
表記は registry の canonical ID に解決されます。未知の値は上限付き edit-distance 候補を
伴う `E010_USAGE_ERROR` になり、未登録 plugin ID には明示的な escape hatch
`--allow-unknown-lang` を使います。この場合、前後空白を除いた表記を database filter まで
保持します。

最小例:

```yaml
# .cdidx-langmap.yaml
entries:
  - extension: ".kts.in"
    language: "kotlin"
```

```yaml
# .cdidx/patterns/toydsl.yaml
language: "toydsl"
extensions:
  - extension: ".toy"
patterns:
  - kind: "class"
    regex: "^entity (?<name>\\w+)"
```

各 regex は `name` という名前付き capture を公開することを推奨します。存在しない場合、
`cdidx` は match 全体の文字列を symbol 名として使います。無効、symlink、過大、または
上限超過の sidecar は stderr の診断付きで skip されるため、壊れたローカル実験が indexing を止めません。
拒否された sidecar は rule budget を消費せず登録もされず、修復後の次回探索で load 対象に戻ります。

## SQLite reader のデバッグ

`Database/DbDebug.cs` は `ExecuteTrackedReader` / `TrackedRead` の最後に流れた SQL、パラメーター、行ごとの状態を記録し、ループ途中で `SqliteException` が発生した場合に再現に十分な文脈を stderr へダンプする。インデックス済みのソースバイトが想定外の経路に漏れないよう、ダンプ経路はゲート制御されている:

- **既定はオフ。** `CDIDX_DEBUG=`（未設定）であれば `DbDebug.DumpToStderr` は no-op。
- **`CDIDX_DEBUG=1`（または `unsafe` 以外の値）** で **redacted** モードを有効化する。SQL 本文とパラメーター名はそのまま出るが、各行の列は raw payload ではなく kind / length / SHA-256 prefix で表される。CI、共有ログ、バグレポートで使うのはこちら。
- **生テキストのダンプには 2 段階の opt-in が必要。** `CDIDX_DEBUG=unsafe` 単独では redacted モードへフォールバックし、一度だけ stderr に警告（`CDIDX_DEBUG=unsafe was ignored: pass --debug-unsafe on the command line ...`）を出す。unsafe 経路は同じプロセスが CLI フラグ `--debug-unsafe` も受け取った場合にのみ有効になる。`ProgramRunner.TryConsumeDebugUnsafeFlag` が通常の引数解析より前にフラグを `args` から取り除き、`--` クエリエスケープ以降のトークンは尊重する。これにより、シェルの profile や CI に残った古い環境変数だけで、サイレントに索引済みソースバイトを stderr に出してしまう事故を防ぐ。
- テストはプロセス内ゲートを `DbDebug.ResetForTesting()` でリセットする。本番コードは CLI フラグハンドラーからのみ `DbDebug.EnableUnsafeForProcess()` を呼び出す。

これに対応して、MCP サーバーも JSON-RPC 応答に生の `Exception.Message` を埋め込まなくなった。`McpServer.BuildSanitizedToolErrorMessage` と `BuildSanitizedLoopErrorMessage` はツール名（既知の場合）と例外型のみを返すため、AI クライアントはインデックス済みテキストを取り込まずにリトライ戦略を分岐できる。詳細メッセージとスタックトレースはローカル診断用に `BuildToolErrorLog` / `BuildUnhandledLoopErrorLog` から従来どおり stderr に出力される。
