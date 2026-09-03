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
| `make mcp-smoke` | Build once and run MCP help from that configuration's output. |

Use `FRAMEWORK=net9.0 make test` to match the net9 CI lane. On systems without
`make`, run the same tasks as `./dev.sh build`, `./dev.sh test`, and so on.

Development contracts:

| Area | Contract |
|---|---|
| Formatting and warnings | CI enforces repository formatting with `.editorconfig` and treats compiler warnings as errors through `Directory.Build.props`, so local changes should pass the format check before opening a PR. Existing trim-analysis warnings are explicitly listed in `WarningsNotAsErrors` until they are fixed without blocking ordinary compiler-warning enforcement, and ILLink keeps reporting trim warnings without failing trimmed publish smoke tests. |
| CLI help | `cdidx --help` stays brief, `cdidx --help-all` prints the full command/flag/example reference, `cdidx --help-flags` prints only shared flag tables, and `cdidx <command> --help` prints one command's usage line. Keep new commands visible in the brief summary only when they are a primary user workflow; every command must remain present in the full help and command-specific usage table. |
| `index --dry-run` mutation estimates | Dry-run remains strictly non-mutating: it does not create or lock the database and does not alter sources or index artifacts. It uses read-only stat/checksum data together with the production cap-issue, symbol-filter, extractor-version, graph-contract, forced extractor/config refresh, hotspot-marker trust, and C# workspace reuse gates to project unchanged skips; projects updates, content-policy skips, deletes, purges, and symbol/reference cap hits; and performs the normal chunk/symbol/reference/content-diagnostic extraction for at most 100 planned update files. Scoped `--files`, `--commits`, and `--changed-between` previews run a read-only C# preflight that reuses production static-interface and qualified member-read detection and expands candidate paths before counts, limits, and samples are finalized. `projection_authoritative`, `projection_unavailable_reasons`, and the C# workspace expansion status/reason distinguish exact projections from lower bounds when a candidate cap, scan failure, snapshot gap, or unavailable preflight prevents safe expansion. `estimated_table_mutations` counts projected delete-and-insert row operations, not final table cardinality. Its nullable values must stay synchronized with `estimated_table_mutation_details`: `source` identifies filesystem planning or parse-only plus index-snapshot input, `confidence` is `exact`, `estimate`, or `unknown`, and stable `unknown_reasons` cover candidate truncation, parse-estimate truncation/failure, C# workspace expansion, unreadable index snapshots, and partial-index table absence. A numeric zero is reserved for measured no work and remains qualified by its confidence. Parse-only estimates intentionally omit post-extraction hook mutations and post-preflight cross-file child-row materialization, so child-table metrics become explicitly unknown when C# workspace expansion applies instead of retaining incomplete parse totals. |
| `cdidx validate` | This is the user-facing integrity scan for indexed content issues such as replacement characters, BOMs, NUL bytes, mixed line endings, UTF-16 BOMs, and likely non-UTF8 content. Keep its CLI usage, README entry, and help summary in sync when adding validation issue kinds or filters. |
| `cdidx doctor` | This is the copy-pasteable environment summary for support requests. Keep it redacted by default: secret-like `CDIDX_*` values must not be printed, and new diagnostic fields should be stable enough for issue triage. Full environment inventory filters (`--env-domain`, `--env-category`, and `--env-sensitivity`) use case-insensitive exact values and compose with AND; filtered JSON summaries describe the returned inventory rather than the global catalog. `--max-json-bytes` is valid with `--json --env-inventory=full` or `--integrations --json`, counts the serialized UTF-8 document plus its newline, and returns a structured usage error rather than an oversized successful document. The `github` block reports `proxy_default_credentials` as `enabled` / `disabled` and the bounded `max_request_timeout_s`; never print proxy credential material or raw secret values. `license --json` returns the versioned `license`, `commercial_use`, `trademark`, and controlling `documents` contract. |
| Exception diagnostics | User-facing CLI, JSON, MCP, file-issue, and local diagnostic output must not echo raw `ex.Message` directly. Route exception prose through `CommandErrorWriter.FormatSanitizedExceptionMessage`, `DiagnosticSanitizer.ForMessage`, or an existing bounded `DiagnosticRedactor` helper, and use stable error codes/categories when the message is not needed for recovery. Intentional broad catches should match the `risky-code/broad-exception-catch` taxonomy and normalize to bounded diagnostics, private best-effort suppression, or a documented fallback. |
| Shell completions | Generated shell completion scripts include a comment with the `cdidx` version that produced them. Completion candidates come from `CliFlagSchema`: `ValueKind` / `CommandValueKinds` select contextual path, project, repository, language, and symbol-kind behavior; `ValueDomain` / `CommandValueDomains` define exhaustive finite choices; `SubcommandValueDomains` narrows choices for a nested verb without widening its parent command; `CompletionSubcommands` restricts a flag to exact nested verbs; `ParentCompletionCommands` retains it for a valid default parent operation; and `SupplementalCompletionValues` preserves real reserved literals for mixed inputs such as path-or-`github`. Command discovery must skip schema-defined leading global options and their separated or inline values before resolving command and nested-command context. Display placeholders such as `<name\|path>` are metavariables and must never be parsed into candidates. Dedicated parser/help inventories and every generated Bash, zsh, fish, and PowerShell context must have bidirectional tests: every public accepted flag is registered and rendered, and no sibling context advertises a rejected flag. When command or flag schema changes, update those completion tests and keep the README guidance that installed completions should be regenerated after upgrades. |
| Target frameworks | The production CLI and NuGet tool packaging target `net8.0`. The test project multi-targets `net8.0;net9.0`, and CI runs the test suite on both frameworks across Linux, Windows, and macOS. Use a .NET SDK that can restore and run both target frameworks when validating the full CI-equivalent test matrix. |
| SDK selection | `global.json` pins the repository SDK to `9.0.301` with `rollForward` disabled. CI installs both `8.0.413` and `9.0.301` explicitly: `8.0.413` provides the `net8.0` runtime lane, while `9.0.301` is the selected SDK for restore, build, test, publish, and changelog validation. When rolling SDKs, update `global.json`, every `actions/setup-dotnet` version list, the Docker build image, and this guide together. |
| GitHub Actions policy | Workflows pin hosted runners to versioned labels (`ubuntu-24.04`, `windows-2022`, `macos-14`), keep the top-level `contents` permission read-only by default, limit `continue-on-error` to failure-path diagnostic artifact upload, give every upload artifact explicit retention, and bound every artifact download by pattern and path. NuGet cache keys use workflow + runner OS + the exact reachable `packages.lock.json` restore graph, explicitly exclude `global.json` and unrelated locks, avoid broad restore-key fallbacks, and keep version-pinned tool caches separate. `PackagesLockTests` owns the exact restore/cache graph contract; `CiWorkflowTests.GitHubActionsWorkflows_FollowRunnerArtifactCacheAndContinueOnErrorPolicy` enforces the general workflow checklist. |
| Test diagnostics | CI uses `tests/CodeIndex.Tests/CodeIndex.Tests.runsettings` plus VSTest blame crash/hang collection and a bounded one-time retry to distinguish repeatable failures from pass-on-retry flakes. The Build and Test workflow splits `ubuntu-24.04` / `net8.0` into complementary coverage shards; Windows and macOS use the same complementary net8 split without coverage overhead, while the Ubuntu net9 compatibility lane runs the full suite. For test suite structure, shared helpers, state-isolation rules, timeout diagnostics, and test-writing conventions, see [TESTING_GUIDE.md](TESTING_GUIDE.md). |
| Mutation testing | The weekly `Mutation testing` workflow runs Stryker.NET against `src/CodeIndex/Database/DbWriter.cs` using `stryker-config.json`. Keep this scope focused on transaction, savepoint, rollback, and batch-write behavior unless the runtime budget is intentionally expanded. The workflow caches the pinned `dotnet-stryker` 4.14.0 tool and NuGet packages, updates the tool only on cache misses, and keeps mutation score gates at high 75, low 70, and break 65 so changes that weaken rollback or savepoint coverage fail outside the regular PR test path. |

## CI / Artifact Distribution

| Workflow | Command or setting | Notes |
|---|---|---|
| Read-only database queries | `cdidx status --db /artifacts/codeindex.db --read-only --json`; `cdidx search AuthService --db /artifacts/codeindex.db --immutable` | Query commands accept `--read-only` (alias `--immutable`) to open an existing CodeIndex database through SQLite's immutable read-only URI mode. Use this for CI artifacts, mounted caches, and sandboxes where creating or updating `codeindex.db-wal` / `codeindex.db-shm` sidecars is not allowed. |
| Mutating commands | `index`, `backfill-fold`, `optimize`, `vacuum` | These require writable storage and reject read-only database opens. |
| Reusable index artifact | `cdidx export codeindex.cdidx.zip`; `cdidx import codeindex.cdidx.zip --db <path>`; `cdidx import codeindex.cdidx.zip --dry-run --json` | Run export after indexing and upload the archive. Export refuses an existing destination unless `--overwrite` is explicit, publishes atomically from an owner-only temporary file, and verifies POSIX mode `0600`. Successful export JSON adds final archive byte size and SHA-256 plus the complete immutable manifest while retaining the prior result fields. Consumers import before query commands, or use `--dry-run` / `--check` to validate the archive without replacing the destination DB. Use `--prune-paths` when the archive comes from another checkout and the restored DB should advertise the import target project root; imports targeting `.../.cdidx/codeindex.db` use the sibling project directory, while other DB paths fall back to the process current directory. The archive contains only `manifest.json` plus `codeindex.db`; import validates ZIP entry names through `ZipArchiveSafetyPolicy` and rejects absolute, parent-directory, backslash, NUL, non-canonical, duplicate, and extra entries before extraction. The manifest carries bounded summary/readiness metadata including row counts, readiness bits, writer/indexed-head metadata, schema contract stamps, and unknown-extension summary when available. Import validates manifest format, manifest `user_version`, `database_sha256`, present summary counts, and the embedded SQLite file as a CodeIndex database before replacing the destination DB. Import rejects archive `codeindex.db` entries whose compressed or uncompressed metadata exceeds 8 GiB, and the extraction stream is also capped at 8 GiB. |
| Maintenance checkpoint and managed rollback | `cdidx db checkpoint <name> [--dry-run]`; `cdidx db checkpoints --list|--delete <name>|--prune --keep <n> [--dry-run]`; `cdidx db restore <name> [--dry-run] [--no-backup]`; `cdidx db restore-backups --list|--prune --keep <n>|--restore <id> [--dry-run] [--no-backup]` | Checkpoint snapshots `codeindex.db` plus existing WAL/SHM sidecars before risky maintenance. Import and both restore forms create a consistent, verified managed SQLite rollback snapshot before replacing an existing DB unless `--no-backup` is explicit. Managed directories use `<db>.restore-backup-<id>/` and contain a bounded manifest plus one standalone database payload; the manifest records SHA-256, byte count, supported `user_version`, provenance, and an optional source identifier, but no local absolute source path. `restore-backups --list` exposes the ID and provenance while retaining legacy directory metadata; existing prune retention remains compatible. `restore-backups --restore <id>` revalidates the directory boundary, manifest, payload hash, schema, and combined staging/rollback free space, then performs an atomic replacement with transient rollback-on-failure. Its `--dry-run` reports every validation and planned backup without mutation. Checkpoint delete/prune and restore-backup prune require an explicit mutation action, and checkpoint prune skips all deletion if its bounded 1,000-directory scan is truncated. Checkpoints live under `<db>.checkpoints/<name>/`. `backfill-fold` preflights folded rows, persisted folded values, and readiness under the index lock, creates an automatic checkpoint only when a mutation is required, accepts `--checkpoint` to force a snapshot for an already-complete DB, and accepts `--no-checkpoint` to skip mutation protection explicitly. JSON reports `checkpoint_skipped` plus `checkpoint_skipped_reason` (`already_complete`, `dry_run`, or `disabled_by_option`), and human output reports the same decision. |
| Checkpoint operation plan | `cdidx db checkpoint <name> --dry-run [--json]` | Dry-run builds an immutable plan that separates source DB/WAL/SHM files and bytes from every output, including the versioned `manifest.txt`, its SHA-256, estimated final bytes, destination/conflict status and policy, sidecar/compression/metadata policy, and remaining uncertainty. Execution creates a fresh plan from current sources, validates source metadata and SHA-256 before and after copying, verifies every planned output, and refuses atomic publication when inputs drift. Legacy JSON `files` / `bytes` fields remain available; use `source_files` / `source_bytes`, `planned_output_files` / `estimated_output_bytes`, and `final_output_bytes` for the explicit contract. |
| Binary compatibility | [COMPATIBILITY.md](COMPATIBILITY.md) | Database compatibility across `cdidx` binary upgrades and downgrades is documented there. Keep that policy updated whenever readiness bits, `codeindex_meta` contract stamps, or rebuild requirements change. |
| Fold backfill preview and recovery | `backfill-fold --dry-run`; `backfill-fold --checkpoint`; MCP `backfill_fold` with `dry_run: true` or `force: true` | Dry-run previews folded-key rows without mutating the DB or stamping FoldReady. `--checkpoint` explicitly preserves a snapshot even when CLI preflight finds no mutation, while the default completed no-op leaves no checkpoint artifacts. MCP accepts the same preview and can force rewriting all folded keys when an operator needs to recover from suspicious fold metadata or row state even though the stored version/fingerprint appears current. Non-dry-run row rewrites are resumable after interruption: completed row updates remain durable, and final FoldReady metadata is stamped only after verification succeeds. MCP responses include `progress.rows_done`, `progress.rows_total`, and `progress.fraction` so clients can report and retry long backfills. |

Portable archive trust is scope-aware. Unfiltered exports set `scope.represents_entire_source_database` and preserve completeness, indexed-HEAD, run, and unknown-extension metadata. Filtered exports normalize only the archive snapshot to `index_complete=false` with `partial_archive`, remove source-wide HEAD/run provenance, and omit unavailable unknown-extension summaries. Import repeats that normalization for legacy manifests without scope metadata, while current full-snapshot manifests preserve trust. Scoped indexing of a partial archive falls back to a full workspace scan before it may clear `partial_archive`.

Portable archive path privacy is opt-in for compatibility. Default exports retain
`manifest.project_root`, the snapshot's `indexed_project_root`, requested scope
values, and resolved success paths. `--redact-paths` must operate only on the
private copied snapshot: resolve and apply scope first, delete the copied project
root, replace absolute POSIX/Windows/file-URI scope values and persisted
flat and grouped path-sample values with `[redacted]`, and fail closed by deleting
or emptying malformed or over-budget path metadata from the copy. Losing a
workspace-verification pending-path identity must also stamp its coverage marker
incomplete. Run exactly one final
`VACUUM` after scope and redaction before computing
`database_sha256`. Successful manifest and export JSON must keep
`path_redaction_requested`, `path_redaction_complete`, and bounded stable
`path_redaction_omitted_categories` synchronized. Redacted success output must
not repeat the resolved archive, database, or source-root paths. Repository-relative
indexed paths, source content, hashes, readiness, and commit provenance are not
redaction targets. Import derives its destination project root from the destination
DB path/current directory and must tolerate an absent source root in both execution
and dry-run modes. Imports accepting `path_redaction_complete=true` must verify
the manifest root/scope values and known embedded path metadata before reporting
that claim as complete.

Checkpoint plan drift detection covers DB/WAL/SHM content changes and sidecar appearance or disappearance through the final pre-publication validation. The plan's `uncertainty` value records the remaining post-validation race; copied outputs are independently hash-verified against the plan before atomic publication. DB/WAL/SHM candidates must pass native regular-file type validation before hashing so Unix FIFOs cannot block planning. `metadata_policy` reports `owner_only_files_and_directories` on POSIX and the actually inherited `inherited_windows_acls` policy on Windows. A database payload whose output name collides with `manifest.txt`, including filesystem-equivalent casing, makes the plan not ready and is rejected before mutation.

## Filesystem Permissions

| Artifact | POSIX permission and behavior |
|---|---|
| `.cdidx/` | Created with mode `0700`. |
| `codeindex.db` plus WAL/SHM sidecars | Mode `0600` is applied when the files exist. Enforcement defaults to best-effort and can be made strict with `CDIDX_DB_PERMISSION_POLICY=strict`. |
| `suggestions-*.json` suggestion stores | Written atomically with owner-only mode `0600` on POSIX. |
| Portable export archives | Written atomically through the `Sensitive` profile and verified as owner-only mode `0600` on POSIX because they contain indexed source text. Existing destinations require explicit `--overwrite`. |
| Import staging databases plus WAL/SHM sidecars | Non-dry-run imports create the hidden same-filesystem staging DB with `FileMode.CreateNew` and POSIX mode `0600` before the first archive byte is written. The main DB and any SQLite WAL/SHM sidecars are re-hardened and verified as owner-only between extraction, manifest/SQLite validation, trust-metadata rewriting, optional path pruning, rollback-backup creation, and atomic replacement. Dry-run staging retains its owner-only temporary directory and the same private-file creation path. |
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
dotnet ./src/CodeIndex/bin/Debug/net8.0/cdidx.dll search File.Delete --path src/ --exclude-tests --exact-substring --count-by file --limit 80 --db .cdidx/codeindex.db
dotnet ./src/CodeIndex/bin/Debug/net8.0/cdidx.dll search Directory.Delete --path src/ --exclude-tests --exact-substring --count-by file --limit 80 --db .cdidx/codeindex.db
dotnet ./src/CodeIndex/bin/Debug/net8.0/cdidx.dll search File.Move --path src/ --exclude-tests --exact-substring --count-by file --limit 80 --db .cdidx/codeindex.db
dotnet ./src/CodeIndex/bin/Debug/net8.0/cdidx.dll search --recipe filesystem-mutation-boundaries --format count --limit 80 --db .cdidx/codeindex.db
```

| Surface | Ownership and boundary policy | Cleanup or rollback policy |
|---|---|---|
| `AtomicFileWriter` file delete/move helpers | Used for caller-selected output paths after the caller has accepted or validated the destination. Temp files are generated as collision-resistant siblings of the target, so replacements stay on the same filesystem boundary and do not follow a separate temp-root policy. | Writes flush the temp file, rename over the target, and flush the parent directory on Unix. Pre-move temp cleanup is best-effort; post-replace parent-directory flush failures are explicit command failures because the target has already changed. |
| `cdidx import` / `cdidx export` temp databases and sidecars | Import temp DBs are either hidden siblings in the destination DB directory or owner-only `codeindex-import-*` temp directories for dry-run. Hidden import siblings use collision-rejecting `CreateNew` and owner-only POSIX creation before extraction; the staged DB/WAL/SHM set is re-hardened through validation and rewriting while preserving same-filesystem atomic replacement. Export snapshots live in owner-only `codeindex-export-*` temp directories. Destination DB replacement rejects overlapping export outputs and rolls back through backup sidecars. | Temp DB, WAL, SHM, and empty temp-directory cleanup failures are warnings that do not hide the import/export error. Replacement failure reports residual diagnostics so operators can inspect the destination state. |
| `cdidx db` checkpoint restore staging and restore-backup pruning | Checkpoints, restore staging, and restore backups are derived from the resolved DB path. Recursive cleanup uses `FileSystemBoundary.TryValidateDirectoryCleanupTarget` with the DB parent as safe root and an expected `codeindex.db.restore-*` style prefix. Checkpoint payload files must be regular files, not symlinks, reparse points, or devices. | Restore creates backups before replacement and attempts rollback on failure. Temporary-directory cleanup and restore-backup prune failures become bounded diagnostics or warnings without deleting outside the validated root. |
| Upgrade installer script and temp directory | Upgrade downloads use owner-only `cdidx-install-*` directories under `Path.GetTempPath()`. Recursive directory cleanup validates the temp root, required prefix, and symlink/reparse/device status before deletion. Install-directory write probes run only after install-directory validation rejects roots, symlinks/reparse points, and unsafe POSIX modes. | Installer script and temp-directory cleanup failures are warnings. The install operation reports its own result separately from secondary cleanup failures. |
| `.cdidx` write probes and case-sensitivity probes | Write probes are freshly generated files under the already resolved install directory, `.cdidx` directory, or `.cdidx/probes` directory. Probe directories are created owner-only and are under the workspace data directory. | Probe files are deleted after the check. Case-sensitivity probe-directory cleanup revalidates the workspace data/probe root, expected directory name, and symlink/reparse/device status before deleting created empty probe directories; rejected cleanup records bounded diagnostics and suggests removing stale `.cdidx/probes` entries when no `cdidx` process is running. |
| Legacy scan checkpoints | Full scans neither create nor consume `.cdidx/scan-checkpoint.json`. After the first immutable scan-input barrier, successful and partial runs delete any legacy file without parsing it. | Delete failures are warnings in human output and `CliJsonMessage` entries in JSON output; indexing continues without relying on stale HEAD-only state. |
| Git hook staging | Hook installation first resolves the current invocation to an absolute, runnable executable path; a `dotnet` launcher remains classified as a host before its symlink leaf resolves, the host also pins the absolute `cdidx.dll`, other symlinked executable leaves resolve to their final target, and every argv/path is POSIX-shell quoted. The managed block stores a bounded versioned provenance manifest and visible source/version comments, and the complete generated script must fit the same 64 KiB management-read bound before any preview or write. Installation then classifies create, managed replacement, custom-hook chain, or exact no-op. `hooks install --dry-run` returns that plan, managed script, and executable diagnostics before directory creation or staging, while a real mutation writes a private staged hook script inside the repository hook directory and then replaces the hook file through `File.Replace` with a backup path when needed. | Exact executable UTF-8/no-BOM managed-hook reruns return `already_installed` without rewriting. Other encodings and non-executable managed hooks are replaced so Git can execute them. `hooks status` parses but never executes the stored invocation; it first verifies source/argv cardinality and that the managed block matches the manifest, then checks path availability and effective POSIX execute access, obtains the current version only from the matching running invocation or a bounded adjacent `version.json`, and reports malformed, tampered, missing, unverifiable, or skewed provenance with support-safe diagnostic argv. If a staged script was not moved into place, cleanup is best-effort and recorded as hook warnings. Failure to delete a managed hook is a command error because that is the requested mutation. |
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

`cdidx.workspace.json` and `.cdidx-workspace.json` declare monorepo members without adding a YAML dependency. Workspace manifests are capped at 64 KiB, 16 JSON nesting levels, 1024 members, 4096 characters per member path, and 255 characters for `default_db_name`. The supported schema is additive: `members` is an array of member paths that must be relative to and resolve under the manifest directory, `index_strategy` is `per_member` or `single` with unknown values rejected, `default_db_name` is a plain file name that overrides `codeindex.db`, and `shared_ignores` is reserved for shared ignore policy. Invalid `members` entries are rejected with bounded diagnostics, and valid entries are normalized and deduplicated with the workspace path casing policy before DB paths are materialized. `cdidx workspace list` and `cdidx workspace status` report member DB paths. `workspace status` also reports each member's project-directory and database existence as the unambiguous sibling fields `project_exists` and `db_exists`; the older `exists` field remains a compatibility alias for `project_exists`. Per-member `index_health` reports probe status and stable reason, a structured `repair_action`, schema compatibility, exact workspace freshness, timestamps, index completeness, and graph readiness. It probes at most 64 distinct existing member databases per invocation, reuses a probe when members share a database under the `single` strategy, and marks later members as `not_checked` with a top-level truncation summary.

This repository's checked-in manifest deliberately selects `index_strategy: single`. The canonical dogfood topology indexes the repository root into `.cdidx/codeindex.db`, which keeps source, tests, root documentation, scripts, workflows, and agent policy in one searchable database while `workspace status` reuses that database for every declared member. Repository dogfood CLI queries, MCP, LSP, and maintenance commands (`optimize`, `vacuum`, `db integrity`, and related previews) always pass `--db .cdidx/codeindex.db`; running from the root alone does not pin the database because `CDIDX_DATA_DIR` and active-workspace settings take precedence over CWD discovery. Repository validation runs the explicitly pinned `status --check --json` plus manifest-driven `workspace status --check --json`; neither workflow creates per-member databases.

```bash
dotnet ./src/CodeIndex/bin/Debug/net8.0/cdidx.dll status --check --db .cdidx/codeindex.db --json
dotnet ./src/CodeIndex/bin/Debug/net8.0/cdidx.dll workspace status --check --json
```

`member_health_summary` aggregates healthy, degraded, and missing members, reports the exit code that enforcement would use, and deduplicates recommended action codes. `cdidx workspace status --check` returns success `0` only when every required member is `ready`; it returns not-found `2` when the manifest is missing, the manifest has no members, or any required project/database is missing, and stale-index `5` for every other degraded aggregate (including stale, incomplete, incompatible, invalid, unavailable, or probe-limit-skipped members). Missing takes precedence over degraded in a mixed workspace. Without `--check`, `workspace status` remains informational and returns `0` after a successful report. Invalid manifest schema or safety failures remain usage exit `1` and, in JSON mode, are returned as a structured `workspace_manifest_invalid` error instead of falling through to the top-level crash handler. Repair commands are emitted as `name` plus `args[]`, not shell-quoted strings, so paths remain portable across Windows and POSIX shells.

`cdidx workspace use <name-or-relative-path>` writes an existing manifest member or `default` workspace to the per-user config directory and rejects missing manifest members. A directory name remains a shorthand when it identifies exactly one member; repeated directory names remain ambiguous. A manifest-relative path selects the exact normalized member, accepts either slash spelling, and stores the canonical forward-slash relative path in active workspace state. Manifest-member selections also persist `manifest_member: true`, so members named `default` or `env` remain distinguishable from the reserved non-manifest states. Active workspace names share the manifest member path's 4096-character bound. `cdidx workspace clear` (also available as `workspace deactivate`) removes that persisted selection instead of rebinding `default` to the current directory. When `CDIDX_ACTIVE_WORKSPACE` is set, clear reports that the environment override must be unset because it takes precedence over persisted state. Query DB resolution keeps existing precedence: explicit `--db`, then explicit `--data-dir` / `CDIDX_DATA_DIR`, then active workspace state, then ancestor/CWD discovery.

### Observability

CodeIndex exposes an opt-in `ActivitySource` named `CodeIndex`. MCP JSON-RPC frames create `mcp.request` server spans and SQLite commands routed through tracked database helpers create `db.query` spans. MCP callers can pass W3C trace context as `params._meta.traceparent`; when present, the MCP span uses that trace as its parent. No exporter dependency is bundled, so spans are emitted only when the host process installs an OpenTelemetry/Diagnostics listener.

Set `CDIDX_SLOW_QUERY_MS=<milliseconds>` to write slow SQLite command diagnostics to stderr. Query commands also accept `--profile` for a JSON profile block and `--slow-query-ms <milliseconds>` for command-scoped profiling. Slow-query SQL diagnostics are single-line, length-bounded, and redact SQL string/blob/numeric literals before they reach stderr or the global tool log; the logged SQL is intended for operation/shape debugging, not value recovery.

### Resource-Boundary Contracts

| Path | Contract |
|---|---|
| Worker protocol JSON | Isolated worker stdin frames are read through `BoundedLineReader`. The symbol-worker client serializes small request metadata directly to UTF-8, then JSON-escapes source content through fixed-size pooled buffers while writing the newline-framed request to the process stream; it must not retain a source-sized JSON byte array. The writer counts emitted bytes and rejects the frame before exceeding its negotiated cap. The worker serializes responses directly to stdout, and the client reads each bounded response frame as UTF-8 bytes for direct deserialization. This avoids additional source-sized JSON strings and encoding buffers in each direction for every source file. The default frame cap is 32 MiB for both characters and UTF-8 bytes. When a larger `--max-file-bytes` setting needs JSON-escaping headroom, the protocol frame cap may expand up to `WorkerProtocolLineLimits.MaxExtendedLineUtf8Bytes` (384 MiB), never to `int.MaxValue`. `WorkerProtocolJsonValidator` rejects payloads over the negotiated character/UTF-8 byte cap before `JsonDocument.Parse`, parses with `DefaultMaxJsonDepth` (32), rejects more than 1,000,000 object properties, and rejects strings longer than the frame cap. |
| User regex find | `find --regex` keeps the classic .NET regex engine for lookaround/backreference compatibility, adds `RegexOptions.CultureInvariant`, adds `IgnoreCase` unless `--exact` is set, and uses `BoundedRegex.DefaultMatchTimeout` per match. Timeouts surface as `E014_REGEX_MATCH_TIMEOUT` / `regex_timeout` in CLI JSON, and human output includes the same recovery hint. `find --all` additionally applies candidate-file and line-scan caps before walking the whole index. |
| Shared regex construction | Production regex construction is centralized through `BoundedRegex`, `RegexRegistry`, or `RegexTimeoutPolicy`. Use `BoundedRegex` for extractor patterns and bounded static regex APIs, `RegexRegistry` for raw BCL regex factories that must preserve timeout exceptions (`find --regex`, ignore glob regexes, generated-code path patterns), and `RegexTimeoutPolicy` for diagnostic/redaction surfaces. `RegexRegistry` owns the named ignore-glob timeout (100 ms), generated-code pattern timeout (50 ms), and find-regex factory using `BoundedRegex.DefaultMatchTimeout`. Search-audit recipes treat only `BoundedRegex` aliases and `RegexRegistry.cs` as centralized positive evidence, so new production raw constructors require a deliberate factory or generated-regex entry plus tests. |
| Filesystem traversal helpers | `FileSystemTraversalPolicy` keeps top-directory-only enumeration explicit (`IgnoreInaccessible=false`, no implicit recursion) and exposes opt-in `CancellationToken` / entry-budget options. Expected traversal failures are classified centrally so command diagnostics share the same permission, I/O, invalid-path, unsupported-path, path-too-long, and budget-exceeded taxonomy. Existing-child case probes retain one exact-name set capped by `CaseSensitivityProbeDirectory.MaxExistingChildProbeEntries` (4,096), return unknown on truncation so callers use the isolated-write or cached root-policy fallback, and propagate available cancellation tokens. |
| `MaxValue` sentinels | `int.MaxValue` may be used only as an internal sentinel when the next operation clamps before SQL limits, allocation, traversal, payload sizing, or timeout conversion. User-influenced values must be reduced to named practical constants before multiplication, buffer sizing, protocol framing, or query expansion. |

### Indexing pipeline

```
Directory scan / shared path filter (built-in skip lists + `.gitignore` / `.cdidxignore` + directory symlink policy + reparse/Windows Hidden/System attribute pruning)
  → Parallel extraction workers (`--parallelism`, `CDIDX_INDEX_PARALLELISM`; default CPU count capped at 8, explicit maximum 16) read UTF-8, split chunks, extract symbols/references, and validate content
  → Single SQLite writer checks unchanged-file reuse, UPSERTs file records, runs post-extraction hooks, and inserts chunks + symbols + references + issues in per-file transactions
  → Populate FTS5 index
```

Explicit numeric `index` options are validated before database setup or worker
startup. The inclusive ranges are `--parallelism` 1..16,
`--max-file-bytes` 1..2147483647 bytes, `--max-symbols-per-file` 1..50000,
`--max-references-per-file` 1..1000000, `--dry-run-path-limit` 1..1000000,
`--watch-pending-path-limit` 1..262144, and `--debounce` 0..60000 ms. Any
explicit zero outside those ranges, negative, overflowed, non-numeric, or
over-limit value returns structured `E010_USAGE_ERROR` output and exit code 1;
an invalid occurrence is not hidden by a later duplicate. For compatibility,
invalid `CDIDX_INDEX_PARALLELISM`, `CDIDX_MAX_FILE_BYTES`, and
`CDIDX_INDEX_WATCH_PENDING_PATH_LIMIT` values still warn and fall back or clamp,
but JSON warnings identify the environment source and the effective value.

Every loaded text file is normalized and analyzed in one UTF-16 walk. The
resulting `NormalizedContentFacts` is the shared source for normalized line
count, oversized-line and FTS-token diagnostics, conflict-marker detection,
replacement-character counts/lines, and 80-line/10-line-overlap chunk slices.
Full scans, scoped updates, dry runs, and MCP indexing pass those facts through
the same language-independent loader, validation, extraction, and persistence
paths instead of rescanning content in each consumer. Keep compatibility
overloads for callers that do not own normalized facts, but do not route normal
indexing back through them. Files of 80 lines or fewer retain no chunk-boundary
array, short files skip impossible FTS-token tracking, and high-ratio invalid
UTF-8 decode replacements retain only the aggregate count used by
`non_utf8_likely` rather than a line number for every damaged line.

Isolated symbol extraction uses one byte-oriented newline protocol for every
language. The parent still serializes each request once with
`JsonSerializer.SerializeToUtf8Bytes`, writes those bytes followed by LF, and
reads the bounded UTF-8 response frame. The child opens standard input as a raw
stream, reads it with `BoundedLineReader.ReadUtf8LineAsync`, validates the byte
payload, and deserializes directly from that span; the `TextReader` overload is
diagnostic-only. Do not reintroduce a `Console.In` / decoded-string copy in the
production path. Preserve the negotiated frame/byte bound, JSON depth/property/
string bounds, CRLF stripping (including buffer boundaries), an unterminated
final frame, stable EOF, and cancellation of a pending read. Invalid UTF-8 and
malformed JSON must keep returning the sanitized exception category without
echoing request content or secrets. This contract is shared by every language
request routed through the symbol worker, including built-in and custom-pattern
configurations.

Parallel full scans use shared dynamic work claiming for the main extraction
body. To keep a large file near the input tail from starting only in the final
worker wave, they probe at most the last `min(4 * workers, 64)` work items and
claim known, indexable sizes largest-first. Equal sizes, unavailable metadata,
and files already above the configured size cap retain their original order.
Workers consume that scheduled suffix before the unscheduled prefix, so its
largest eligible candidates enter the first worker wave; after the schedule is
exhausted, prefix ordinals resume in their original order. Together those two
segments form an exactly-once permutation, and only then does the existing
sparse logical-file mapping apply. The target array and logical file indexes
never move, serial hook/filter paths do not probe, and the bounded completion
queue still publishes in completion order. Keep this tail probe and its
schedule state independent of repository size; an all-file metadata pass can
regress network and virtual filesystems.

Parallel full-scan workers also carry symbol-preparation state to the single
persistence consumer. Reuse the worker's family-scope key and completed C#
source observation instead of resolving or observing them again per file. Keep
`FamilyScopeApplied` separate from the nullable key: a resolver may legitimately
return `null`, while a symbol-capped or otherwise unprepared payload also has no
key. A symbol cap carries the completed C# observation but no applied scope, a
reference cap carries both completed stages, and generated-code suppression
carries neither. Serial filter/hook fallbacks still perform each stage once on
the consumer, before hook mutation, and must preserve the rebuilt family-key
parity. This handoff belongs only to parallel full scans; scoped update, MCP,
and dry-run paths retain their existing preparation boundaries.

Scoped updates may also parallelize extraction when the immutable C# prepass is
authoritative and finds static-interface contracts. This path requires at least
two snapshotted C# targets, `--parallelism > 1`, no active symbol-kind filter,
no content-load test seam, and no post-extraction hooks. Each window contains
only consecutive, snapshot-validated C# targets; ambiguous language reuse stays
nullable so content-aware detection can still reject a target. A fixed pool of
at most `min(parallelism, authoritative snapshot count)` workers processes no
more than `2 * workers` payloads per window. All extraction in that window
finishes before the single consumer persists results in target order. SQLite
writes, hooks, readiness changes, byte accounting, and user progress remain on
that consumer. File stats are checked before scheduling, after loading, and
immediately before persistence; probe failures or an ineligible boundary fall
back to the ordinary serial per-file error boundary.

The `2 * workers` limit is a count bound, not a byte bound: a slow tail may hold
the other extracted chunk/symbol/reference payloads until the ordered write
barrier completes. Workers do not retain raw file bytes after extraction. An
over-limit collection is discarded before publication, symbol caps also discard
downstream payloads, and only the bounded surviving payload, issue, and
source-contract evidence are retained. Do not enlarge this window or add another
payload copy without peak-RSS and large-file parity measurements. Cancellation
aborts queued work and waits asynchronously for already-running workers. A global
no-progress watchdog observes phase and validated-load progress; terminal stalls
preserve the active phase and required readiness/batch markers. Before a later
fatal becomes terminal, source-negative workspaces retry the natural serial
suffix when an earlier target's source-contract candidate is positive or not yet
evaluated. Only symbol-confirmed evidence is published as persisted source
evidence; lexical candidates can cause a conservative serial fallback only.

The single writer reuses multi-row chunk, symbol, reference, and reference-line
commands by row-count shape through the bounded `PreparedCommandCache`. SQL text
and typed parameter schemas are created only on a cache miss; each execution
reassigns parameter values by ordinal. Keep new bulk-write paths on this bounded
cache so large indexes do not rebuild equivalent SQLite commands per file.

Fresh CLI scans and explicit rebuilds defer the query and graph secondary
indexes on `symbol_references` until raw reference persistence completes. The
reverse candidate-symbol lookup remains available during raw persistence and is
dropped only when an actual graph refresh is about to delete or materialize
candidate rows, so marker-only and high-cardinality no-op updates do not rebuild
that whole index. Candidate construction uses the `(reference_id, symbol_id)`
primary key without maintaining the reverse B-tree. Immediately after candidate
construction, restore `idx_symbol_ref_candidates_symbol` before renting or
preparing the separate resolution command, so target-fact materialization can use
bounded `(symbol_id, reference_id)` existence seeks. The candidate primary key
remains available for reference-scoped materialization and resolution, and the file
and reference-line maintenance indexes normally remain available during the load.
The sole exception is an authoritative
empty-database first CLI full scan whose transaction-local recheck still owns the
fresh-resolution claim: while it persists raw references, it also defers
`idx_symbol_refs_reference_line` and `idx_reference_lines_file_line`. It restores
both before the first candidate, deferred-graph, mutual-recursion, or completion
boundary (and therefore before fresh planner statistics). Rebuilds, existing-database
full scans and updates, a fresh-claim race, and recoverable MCP indexing keep both
indexes throughout. Their drop and restore DDL remains in the caller-owned outer
transaction, so cancellation or failure rolls the schema back atomically. Identity
and resolution finalization otherwise continues without query indexes. The
guard forces any active dirty graph scope onto its full-refresh plan before the
indexes disappear. While that force-full plan is known, do not populate the
dirty-file/name/reference TEMP scope: fresh indexes and rebuilds never consume
it, and per-batch tracking otherwise adds avoidable set materialization across
every language. Immediately before mutual-recursion evaluation, its graph
transaction restores only the unresolved-folded, legacy NOCASE, and resolved
reverse-edge indexes; the remaining query indexes return after the mutual update.
The full mutual-recursion update materializes one desired flag per call-like or
non-canonical row before applying changes. Keep the correlated reverse-edge
expression single-evaluation: repeating it in both `SET` and `WHERE` causes
fresh large graphs to perform the same random B-tree probes twice.
When a TypeScript augmentation rebuild owns the sole graph pass, restore every
ordinary graph/query index before readiness, then drop the reverse candidate-symbol
lookup immediately before augmentation candidate population. Restore that lookup
immediately after population and before preparing resolution. Transactional rollback
and recoverable disposal still repair failures before that boundary; successful
resolution and readiness observe the canonical candidate lookup without maintaining
the reverse B-tree row by row during candidate inserts. MCP uses the recoverable
lifecycle whenever its established
dirty-byte policy selects FTS bulk loading, and restores every index on completion
or disposal. Schema initialization and read repair must use the same canonical
index catalog so every path converges on an identical schema.

Reference context text is normalized into `reference_lines`; the legacy
`symbol_references.context` column is therefore written as a SQL `NULL` literal
rather than bound once per reference. Reference-line materialization builds a
batch-local key-to-ordinal map and releases it as soon as the unique input array
is built. Both new-file `RETURNING` and replacement lookup SQL project only the
materialized ID plus the batch-local input ordinal; they must not return context
text or reconstruct tuple keys in managed code. Ordinal bounds, duplicates,
missing rows, and non-positive IDs fail closed before `symbol_references` binding.
Preserve this path across atomic file windows so large multi-language reference
sets do not copy or rehash file/line/context tuples after deduplication. Atomic
window sizing uses the worst-case rows-per-statement bound and leaves the
materializer as the only tuple-hash pass; do not restore a duplicate key-sizing
set before it.

Use the same secondary-index deferral for an existing-database full scan when
the established FTS dirty-byte policy selects bulk loading. Scoped updates have
no authoritative workspace-wide byte estimate, so they use a conservative
recoverable boundary: at least 64 targets and at least 60% of the indexed file
count. When that raw boundary is met but no cleanup/graph/FTS work is already
pending, compare a path-filtered reusable-stat snapshot with every target before
staging. Estimate targets that can mutate indexed state (including filtered
deletions and duplicate-hardlink cleanup) and stage reference and hotspot
secondary indexes only when that count crosses the same 64-target / 60% boundary;
non-mutating skips, unchanged targets, and sparsely mutating target sets keep every index in place.
Any preflight uncertainty keeps staging enabled. This preflight is only a cost
decision—the file loop must repeat its live authoritative lookup so changes after
the snapshot are still indexed with all indexes present.
Keep the query-only set deferred through identity and resolution work, except that
the candidate-symbol reverse lookup returns between candidate population and
resolution-command preparation. Restore the three reverse-edge indexes immediately
before mutual recursion, then
restore the remainder after that update. Small scoped updates must keep every
index in place so a fixed rebuild cost does not dominate the update.

C# reference-graph finalization materializes reference arity, invocation arity,
member-receiver, definition arity, constructor arity, constructor binding sensitivity,
and value-type facts once per applicable row in TEMP tables. Full, scoped, and
retained-graph rebuilds must then materialize project/file-local type identities,
constructor-owner identity and arity facts, and one primary-keyed instantiation-family
fact row for every eligible type declaration and constructor. Before property-receiver
normalization, also materialize C# field/property target identities into a primary-keyed
TEMP fact set. Populate all fact sets before
property-receiver normalization, candidate construction, and resolution. Keep
candidate SQL on primary-key fact lookups instead of rebuilding identity strings,
rescanning constructor-owner ranges, or re-entering managed SQLite scalar functions
for every join candidate. Scoped refreshes must limit symbol facts to their
lookup-name set and derive identity facts from that bounded population; full and
retained rebuilds use the complete C# symbol-fact population. Property-receiver
normalization must likewise drive from flagged reference facts and the target fact
primary key; scoped target materialization is restricted to its lookup-name set.
Instantiation-family materialization must drive from the already-bounded type and
constructor identity facts into persistent symbols by primary-key seek. Ranks 0–4
join those facts by symbol ID. The lower-rank binding-sensitive flag includes every
partial type declaration and constructor in an identity, while the rank-5 flag
includes constructors plus only the deterministic representative type declaration.

Language-independent scope ranks 1–4 must build their shared reference/name/language
candidate relation once in a materialized CTE, assign each reference/symbol pair its best
applicable rank, and retain every candidate tied at the reference's minimum rank. Keep source
symbol attribution optional so the rank-3 same-file fallback survives missing source identity,
and keep scoped refreshes driven from dirty reference IDs into the reference primary key.

After rank 0–4 candidate construction, graph finalization materializes the
distinct matching reference IDs into a compact `WITHOUT ROWID` TEMP table. All
language-independent and C# rank-5 fallbacks consult that set instead of probing
the physical candidate table, while the persisted row-per-symbol candidate and
ambiguity contracts remain unchanged. Scoped refreshes must build the set by
driving from dirty reference IDs into the candidate primary key, and every graph
pass must clear it before materialization so retries cannot observe stale rows.

The unqualified C# rank-5 instantiation fallback consumes the shared family facts
instead of rebuilding type and constructor families per reference. Its uniqueness
flag is computed from type declarations only, grouped by folded name, exact BINARY
name, and arity with non-NULL-count plus BINARY min/max identity equality; a row is
eligible only when its identity is that unique type identity. Constructor-only
orphans therefore remain available to lower ranks but never create a global family.
The reference-driven query uses the explicit composite family-fact index and emits
constructors plus only the deterministic representative type. This preserves
project/file-local conflicts, partial rows, overload/default/optional/`params`,
value-type, enum, delegate, unknown-arity, and ambiguity semantics without a
correlated persistent-symbol scan.

Resolution also materializes the nullable target-family key once per candidate-bearing
target symbol into a primary-keyed TEMP fact table. Full, fresh, differential, and
retained refreshes filter that population with indexed candidate-symbol existence
probes; scoped refreshes first deduplicate target symbol IDs reachable from
dirty-reference candidates. Candidate construction and resolution stay in separate
SQLite commands, and the resolution command is rented and prepared only after the
reverse lookup is restored. Resolution joins candidates to facts by symbol ID instead
of rebuilding the language/path/container/name key for every physical candidate.
Singleton-family detection preserves `COUNT(DISTINCT)` NULL semantics without a
per-group distinct set: at least one non-NULL key must exist and the BINARY minimum
must be null-safely `IS` the BINARY maximum. All-NULL groups remain non-families,
NULL plus one non-NULL family retains that family, duplicate keys collapse, and
binary-distinct keys remain ambiguous. A single physical legacy candidate with a
NULL key still resolves by ID.

Repository-wide incremental scans load stat-reuse candidates with one SQLite
statement before the C# contract prepass and parallel extraction. Each candidate
is still compared with a fresh filesystem size and UTC modification time, and
language extractor versions, extraction caps, stale issue metadata, and generated
code suppression must all remain part of the snapshot eligibility contract. Do
not replace this snapshot with per-file database probes in either CLI or MCP.
CLI full scans, scoped updates, and MCP indexing also load the workspace pattern
configuration once before this prepass. Their C# candidate extraction must reuse
that loaded snapshot instead of refreshing default plugins for every candidate;
direct prepass callers retain the discovery-enabled default unless they explicitly
prove the snapshot is already loaded.
Rows with missing or invalid legacy stat values are excluded so normal checksum
reuse or reindexing can repair them, and CLI/MCP cancellation must interrupt the
snapshot query as well as the later extraction pipeline.

Only a first-time full index that started with no indexed files may reuse raw
built-in C# symbol artifacts from the static-interface prepass. The CLI excludes
rebuilds and symbols-only runs; MCP evaluates the same empty-database condition
before any rebuild mutation. The cache never owns source text or bytes: the main
pass still performs its authoritative content read and hook, stat-snapshot and
TOCTOU validation, and checksum calculation. It consumes a normalized-path
artifact once only after that checksum matches. Generic cache admission keeps
deep-clone isolation for direct callers. The fresh-index production path instead
materializes both workspace lookup snapshots first, then transfers ownership of
each admitted per-file symbol list and releases the redundant workspace-symbol
fallback list. It enumerates those per-file lists through a non-owning segmented
view while building the snapshots, so neither a transient flattened list nor a
second workspace-sized pointer buffer is required. Main-pass mutation therefore remains isolated from the lookup
snapshots without retaining duplicate `SymbolRecord` objects. Artifact-producing
extraction receives the main pass's absolute file path and project root so
file-local family identities stay identical. File ID assignment, family scope,
source observation, post-extraction hooks,
kind filtering, caps, line validation, persistence, reference extraction, and
bounded-regex issue reporting remain on the normal main-pass path. Incomplete
prepasses, extraction-stall test seams, checksum drift, regex timeouts, and cache
admission limits fall back to ordinary extraction. A timed-out prepass result is
partial and must not make that transient result authoritative. Keep admission
bounded to 131,072 symbols and an estimated 32 MiB rather than an independent
production file-count ceiling. When a bound is reached, admit larger decoded
sources first with original candidate order as the deterministic tie-breaker;
build immutable lookups in semantic order before this admission ordering. Clear
all unconsumed artifacts before reference-graph work begins.

The workspace qualified-pattern lookup needs only raw non-enum type names for
enum-shadowing decisions. Build that conflict set directly; do not call the
per-file type-name builder and discard its normalized and qualified known-type set.

Full-tree consumers that continue into per-file processing opt in to a single
`IndexingFileTargetCollection` during source discovery. Its string view is the
`ScanFilesResult.Files` backing store, while concrete iteration exposes the
lexical path, native/display/index relative paths, reusable language, and
path-only generated-code suppression decision without materializing a second
workspace-sized target array. CLI full scans, real MCP indexing, workspace
freshness checks, and authoritative CLI dry-runs use this view; public/default
`ScanFiles` and `ScanFilesDetailed`, scoped updates, and MCP dry-run remain on
the string-list path. Targets must not retain stat, resolved-symlink,
authorization, file-identity, checksum, or content-derived generated-code
state. Consumers still perform their live authorization, secure open, stat,
and mutation checks. In particular, `.h` and extensionless content/shebang
detections retain a null reusable language so the authoritative content read
redetects them. Freshness ordering sorts integer target indexes rather than
copying the target structs.

Authoritative CLI full scans collect the C#, VB, F#, and MSBuild project-marker
fingerprints during the shared source-directory enumeration. Real MCP indexing
reuses the fingerprints produced by its same source scan instead of walking the
marker tree independently. The CLI pass also builds a budget-independent
directory marker-count snapshot used by
`GetFamilyScopeKey`, so per-file family assignment does not enumerate marker
globs again for every ancestor. Fingerprint budgets and scope completeness stay
independent: an exhausted fingerprint budget may still leave a complete scope
snapshot, while an incomplete discovery discards the scope snapshot and falls
back to the live, fail-closed lookup. An incomplete MCP scan fingerprint never
matches or restamps trust. Scoped updates and direct pre-scan callers retain the
live fallback because they do not own an authoritative full-tree snapshot.

`FileIssue` rows may include nullable `origin` and `severity` metadata.
For `replacement_char`, `origin: source_literal` means the file contains a
valid encoded U+FFFD literal, while `origin: decode_replacement` means the
decoder inserted U+FFFD for invalid bytes. `severity: info` is used for source
literals, and `severity: warning` is used for likely encoding damage.

### Scoped refresh path rules

Scoped `--files` and `--commits` refreshes use the same path policy as full
scans.

| Area | Contract |
|---|---|
| Nested project roots | `FileIndexer` loads ignore files from the resolved rule root through each existing ancestor to the project root's parent. Project-directory rules are loaded during the normal walk. |
| Rule order | Each directory loads `.gitignore` before `.cdidxignore`; later `!` patterns can re-include paths. |
| Unreadable ancestor | Scanning fails closed with a scan error. `ScanFilesResult.AncestorIgnoreDirectories` records the resolved ancestor list for diagnostics. |
| Changed ignore file | A commit-scoped refresh that includes `.gitignore` or `.cdidxignore` falls back to a full scan so newly ignored files are purged. |
| Malformed rule | Reports a scan error, skips that line, and continues the run. |
| Symlink modes | `none` is the default. `internal` follows targets under the workspace root; `all` follows every resolvable target. |
| Target identity | Discovery, dry-run, C# preflight, and content loading share one resolved identity. Stable allowed external targets are indexed; links retargeted after preflight are rejected as source drift. |
| Watch symlink reconciliation | Under `--watch --follow-symlinks internal`, an event for a lexical in-workspace symlink that now resolves outside the workspace remains a reconciliation input. The scoped update removes any stale indexed row without opening or indexing the disallowed target. The polling backend traverses allowed directory links under `internal` / `all` in the full scanner's depth-first order, preserves the scanner-selected lexical alias, deduplicates resolved directory identities to bound cycles, and excludes internal database artifacts reached through directory aliases. |
| Explicit-file preflight | Explicit `--files` updates retain each directly supplied token and its normalization provenance, classify that complete list before any database write, and atomically return `UsageError` when a token is invalid, is a directory, or resolves to a duplicate canonical target. A bare `--files` is rejected before artifacts instead of becoming a full scan. `--project` expansion remains derived scope input in `UpdateFiles` and is not reclassified as a direct `--files` selection. Real and dry-run modes expose the same `rejected_paths` entries (`input_index`, support-safe `path`, stable `reason`) and bounded count/truncation metadata for outside-root, symlink-escaping or symlink-policy-disallowed, nonexistent, filtered, unsupported, and duplicate selections. Canonical duplicate checks use each resolved target's filesystem casing rather than the workspace-wide policy, and `none` mode detects actual symlink/reparse segments instead of treating native spelling normalization as a link. Windows 8.3 ancestor aliases canonicalize to the spelling already established in indexed-path membership (#5122). Indexed missing, filtered, or unsupported paths remain valid cleanup targets; indexed control files that become directories, FIFOs, or policy-disallowed symlinks remain tombstones and are classified without opening or following the object. Existing relevant ancestor ignore files remain valid reconciliation controls. In-workspace extractor/configuration inputs, including missing or deleted inputs used as reconciliation signals, also remain valid; generated code remains a valid explicit target. TypeScript path-alias configuration reads carry the active `none` / `internal` / `all` policy into both in-process and worker extraction, resolve only targets permitted by that policy, and secure-open only regular files. Failure to read indexed-path membership fails closed with `E008_DB_ERROR`. This validation does not change the successful zero-file contract for implicit full scans without `--files`. |
| Symlink warnings | Dangling links and directory-target permission failures are scan warnings. Dry-run exposes them through `warnings_total` and `warnings` while retaining successful exit behavior. Human full-index completion reports the dangling-link count with the user-facing `skipped` label. |
| Windows attributes | Hidden or System paths are rejected before language detection. Clear those attributes on project-owned source because ignore rules cannot re-include the path. |

### FTS maintenance during indexing

| Situation | FTS policy |
|---|---|
| Every incremental write | Increment both `fts_incremental_writes_since_merge` and `fts_incremental_writes_since_optimize`. |
| Merge counter reaches 25 | Run `INSERT INTO fts_chunks(fts_chunks, rank) VALUES('merge', -1000)`. The 1,000-page value is a minimum work target; SQLite may process complete segments beyond it. Reset only the merge counter; the optimize counter keeps accumulating for the `cdidx optimize --dry-run` recommendation. |
| Dirty bytes reach 3/5 of known workspace bytes | Full CLI scans and MCP refreshes use a trigger-free bulk rewrite, FTS rebuild, and full optimize. |
| Fresh index or explicit rebuild | Always use the bulk path. |
| Scoped `--files` / `--commits` refresh | Keep trigger synchronization and incremental merge maintenance. |
| Explicit optimize | `cdidx optimize --db <path>` and `cdidx index <projectPath> --optimize` run a full optimize, reset both counters, and stamp `fts_last_optimized_at`. This may briefly hold the writer lock on large indexes. |

Each FTS5 rebuild temporarily sets that table's effective `automerge` value to
zero. The setting change, rebuild, and restoration of the prior value share one
transaction or nested SAVEPOINT, so cancellation, failure, and process exit
roll back both the rebuilt index and its configuration. The standard and
trigram tables use separate scopes, then the bulk guard performs the existing
final optimize once. FTS5 crisis merging remains enabled as a bounded safety
valve during reconstruction.

Bulk-path estimation and purge safety follow these rules:

- Dirty bytes use the larger current/persisted size for each rewritten file,
  plus persisted sizes for planned deletions, including the old side of a
  rename.
- The comparison total includes readable current-workspace bytes, planned
  deletion bytes, and the positive persisted-minus-current difference for
  rewritten files that shrank. Both sides therefore describe the same
  pre-update footprint.
- A scan error, invalid persisted size, or byte-count overflow makes the
  estimate incomplete and conservatively keeps trigger synchronization.
- Stale-file IDs are planned without mutation before policy selection and are
  deleted only inside the selected bulk guard. Sorted IDs let the C# prepass
  exclude purge-planned rows by binary search; reusable-stat eligibility uses
  an indexed temporary SQL filter instead.
- A path that reappears between MCP planning and scanning cannot reuse a row
  that the same run plans to purge.
- The pre-purge contract-presence query runs only for a non-empty plan. If a
  deletion may remove implicit implementation references, C# is re-extracted
  and MCP invalidates the symbol-name contract at its first mutation. After a
  purge followed by a scan error, the next clean run disables stat reuse,
  repairs those references, and only then restamps the contract.
- Cancellation before the batched delete commits rolls back every deletion.
  Cancellation after a committed bulk purge makes guard abandonment rebuild
  FTS from surviving chunks and restore triggers before exit.
- Full scans and MCP refreshes use a current-target path filter for reusable
  rows only when unused, non-purged indexed rows outnumber current targets.
  Other runs use sorted-ID exclusion to avoid duplicating every current path.

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

An ambiguous `goto` with explicit `--json` uses `E029_QUERY_AMBIGUOUS` /
`ambiguous_query` and identifies `command: "goto"` with exit code 1. The
envelope reports authoritative `match_count` / `total_count`, bounded structured
`candidates`, `returned_count`, `omitted_count`, truncation state, and a
structured `narrowing` object. Candidate materialization is capped at 20 entries
and 16 KiB, and every candidate text field is sanitized and bounded before
serialization. Without explicit `--json`, ambiguity retains the corresponding
coded human diagnostic on stderr. Never auto-select a ranked candidate; callers
must narrow the query or opt into `--all`.

Response-budget preflight failures use `E028_RESPONSE_BUDGET_TOO_SMALL` /
`response_budget` through `CommandErrorWriter.WriteResponseBudgetError`.
They add `requested_bytes`, `effective_bytes`, `minimum_required_bytes`,
`minimum_required_bytes_known`, stable unavailable / uncertainty reason fields,
and a machine-readable `retry` object. `--max-json-bytes` caps normal payloads,
not this diagnostic: object and NDJSON preflights must emit one complete
parseable error object on stdout with empty stderr even when the diagnostic
exceeds the requested cap. A materialized stable payload reports a retryable
minimum; runtime-dependent envelopes mark the minimum uncertain and recommend
headroom. `retry.action=increase_max_json_bytes` includes `recommended_bytes`.
When the minimum exceeds the effective 16 MiB ceiling,
`retry.action=reduce_response_size` instead leaves `option` and
`recommended_bytes` null and reports `maximum_effective_bytes`.

Unsupported `search --regex` / `search --all` diagnostics add a command-specific,
display-only `find` recovery contract. Preserve exact normalized argument
semantics in typed `alternative_command.argv`, render both POSIX-shell and
PowerShell forms from that argv, mark the command as not executed, and report
non-equivalent options or blockers instead of emitting a lossy alternative.
Do not include implicit workspace defaults such as an undisclosed data directory.

Resolve project-config dependency before calling `CdidxConfigFile.Load`.
Process-static commands (`license`, `--version`, help forms, completions, and
subcommand help) must not discover or parse project config. `validate-config`
and `config show` own malformed-config reporting so they retain their
command-specific contracts while valid settings still reach environment consumers.
Other commands load config before those consumers run; malformed config in JSON mode uses `E024_CONFIG_INVALID` /
`configuration` in the shared command-error envelope.

| Failure class | Exit code | Error code | Category |
|---|---:|---|---|
| Usage / invalid arguments | 1 or 7 | `E010_USAGE_ERROR` | `usage` |
| Missing outline path | 2 | `E019_FILE_NOT_FOUND` | `not_found` |
| Invalid configuration | 1 | `E024_CONFIG_INVALID` | `configuration` |
| Hook platform or filesystem failure | 9 | `E025_HOOK_OPERATION_FAILED` | `platform` |
| Hooks outside a Git repository | 2 | `E026_NOT_GIT_REPOSITORY` | `not_found` |
| JSON response budget too small | 1 or underlying command-specific | `E028_RESPONSE_BUDGET_TOO_SMALL` | `response_budget` |
| Ambiguous `goto` query | 1 | `E029_QUERY_AMBIGUOUS` | `ambiguous_query` |
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
root-level `api_version`, injected by the success and typed-error response builders; sanitize/redact values before mutating `JsonObject` /
`JsonNode` instances. Every full `tools/list` definition also publishes a draft
2020-12 `outputSchema` generated by `McpToolOutputSchemas`; its reusable definitions
cover versioned success envelopes, rows, readiness, pagination/truncation, warnings,
and typed tool errors. Success and typed-error variants require a per-tool `tool`
discriminator with a tool-name `const`, typed errors carry the same root `api_version`,
and open compatibility values remain bounded by finite nesting plus property,
array-item, and string-length limits. Keep field names and nesting
aligned with actual structured results, and keep the tool-name switch exhaustive so a
newly registered structured tool cannot ship without an output contract. Compact catalog entries remain
definition-incomplete and direct clients to the full catalog. LSP, quickfix, and SARIF outputs follow their external
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

`cdidx batch` is a CLI-side query loop for editor integrations and scripts that need several query commands against the same DB without spawning `cdidx` repeatedly. Each newline-delimited stdin record may use the established JSON string-array form or the validated `{"command": "...", "args": [...]}` object form. Object input rejects duplicate/unknown properties, missing or blank commands, non-array `args`, non-string values, and the same argument count/length violations as array input. Serial mode opens one `DbContext` / `DbReader`; `--parallel <n>` requires `--json-summary`, is capped at 16 workers, and lazily retains at most one isolated query-only context per active worker slot for reuse across that batch invocation. A retained context is leased to only one command at a time, so SQLite readers are never used concurrently while repeated database open, schema probe, and reader setup costs are avoided. Between items, each retained direct connection or detached snapshot verifies the source SQLite header/WAL generation and file identity, replacing the session whenever freshness cannot be proved so long-lived batches observe later completed index updates. A parent-explicit batch database is injected as one `--db=<path>` token into every database-aware child that omits `--db` before wrapper or option parsing, so data reads, `metadata.db_path`, project-root resolution, Git provenance, freshness, and repair diagnostics all use the same context, including paths that begin with `--`. The implicit default database retains implicit path-resolution semantics. A child-explicit `--db` wins for that item, and child completion, failure, or cancellation restores the thread-local inherited context before the next record. Every form dispatches only commands in the side-effect-free allowlist owned by `CliCommandCatalog`. That schema includes query and read-only discovery surfaces such as `goto` and `audit`; adding a top-level command or a dispatcher arm alone cannot cross the batch safety boundary.

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
stdout/stderr through per-command bounded writers, reuse a separate read-only
SQLite connection and thread-local batch reader per active worker slot, and
buffer only the active worker window. `ScopedConsoleOutput` keeps nested JSON-envelope capture on the
current worker's routed stdout instead of replacing another worker's process-wide
writer. Completed records are committed to the shared output writer in input
order; an ordinary item failure remains isolated. Caller cancellation is
serialized as `batch_cancelled` for a consumed input item and in the final
summary before batch processing stops. Serial and parallel input waits share a
bounded pump for each input reader, so cancellation remains prompt while stdin
is blocked and any line completed in flight stays buffered for a subsequent
batch invocation. Cancellation during database setup still emits the typed
final summary.

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
Every inbound message must first be an object whose `jsonrpc` member is exactly
the string `"2.0"`. Missing, null, non-string, or other-version envelope values
return `-32600` (`Invalid Request`) with a valid request ID preserved; validation
runs before receive-time lifecycle reservation, cancellation fast paths, method
dispatch, state mutation, or database access. `workspace/symbol` separately
requires `params.query` to be a JSON string. Missing, null, or non-string query
values return `-32602` (`Invalid params`) before query-snapshot refresh or symbol
search, while the valid empty string remains a supported workspace-wide query.
Before URI/path resolution, live-document access, or query-snapshot refresh,
one shared coordinate validator checks every supported position/range-bearing
method. `definition`, `declaration`, `references`, `hover`, `completion`, and
`documentHighlight` require one position; `inlayHint` requires an ordered range;
and each optional range supplied by `didChange` is validated before live text
can change. Lines and characters must be JSON integers in the LSP `uinteger`
domain (0 through 2,147,483,647), and range starts must not follow range ends.
Malformed, missing, negative, or overflowing request coordinates return
`-32602` (`Invalid params`); invalid notification coordinates are ignored
without mutating document state. Structural validation does not turn a valid
document no-match into a protocol error: provider lookup retains UTF-16
coordinates and its existing end-of-line/EOF policy, while missing, unreadable,
or out-of-range documents still produce the provider's conservative empty/null
result.
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
After an ordered `exit` notification is accepted, the stdio transport does not
start another input read: it drains the already queued frames, publishes the
shutdown response first, and then returns without waiting for stdin EOF. A valid
shutdown/exit sequence returns process status 0, while exit before shutdown
returns process status 1. The input stream remains caller-owned and is not
closed to force completion.
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

LSP symbol and completion kinds come from one ordinal internal-kind mapping so
document symbols, workspace symbols, and completion items cannot drift. The
deliberate mappings and fallbacks are:

| Internal kinds | LSP `SymbolKind` | LSP `CompletionItemKind` |
|---|---|---|
| `base_image`, `class`, `service`, `specialization`, `type` | `Class` | `Class` |
| `record`, `struct`, `union` | `Struct` | `Struct` |
| `annotation`, `implements`, `interface`, `protocol`, `trait` | `Interface` | `Interface` |
| `enum` | `Enum` | `Enum` |
| Enum-entry shapes stored as `enum`, `function`, or `property` under an enum container | `EnumMember` | `EnumMember` |
| `function` or `method` identified by constructor subkind, declaration keyword, dedicated initializer name, or a language-valid type-name declaration | `Constructor` | `Constructor` |
| `method`, `test.method`, `accessor`, `class_hook` | `Method` | `Method` |
| `add`, `async_function`, `async_generator`, `copy`, `delegate`, `function`, `generator`, `hook`, `lambda`, `procedure`, `route`, `run`, `shell`, `subroutine` | `Function` | `Function` |
| `attribute`, `expose`, `property`, `stopsignal` | `Property` | `Property` |
| `field`, `volume` | `Field` | `Field` |
| `event` | `Event` | `Event` |
| `constant` | `Constant` | `Constant` |
| `operator` | `Operator` | `Operator` |
| `associatedtype`, `type_parameter`, `typealias` | `TypeParameter` | `TypeParameter` |
| `array` | `Array` | `Value` |
| `assembly`, `file_module`, `import`, `module`, `program`, `project`, `stage`, `submodule` | `Module` | `Module` |
| `namespace` | `Namespace` | `Module` |
| `package` | `Package` | `Module` |
| `block data`, `layout`, `object`, `protocol_impl`, `rule` | `Object` | `Class` |
| `reference` | `Variable` | `Reference` |
| `build_arg`, `environment`, `user`, `variable` | `Variable` | `Variable` |
| `value` | `Variable` | `Value` |
| `code` | `String` | `Text` |
| `anchor` | `Key` | `Reference` |
| `heading`, `label` | `Key` | `Text` |
| `workdir` | `Module` | `Folder` |
| Non-catalog `parameter` and unknown/plugin-defined kinds | `Variable` | `Variable` |

Constructors and enum members are semantic refinements because extractors reuse
broader persisted kinds. Constructor detection honors explicit subkind/keyword
metadata, dedicated initializer names, and type-name constructors only in
languages where that declaration shape is valid; same-name methods in other
languages, return-typed same-name methods, JavaScript object/static `constructor`
members, shell functions, and finalizers stay functions. This includes named
Dart constructors, Java compact record constructors, C# verbatim constructor
identifiers, case-insensitive Pascal constructor keywords, and Visual Basic
`New`. Enum-entry detection recognizes decorated, escaped (including C# verbatim
identifiers), indirect, and comma-grouped persisted shapes across C#, Java,
Kotlin, PHP, and Swift while keeping a nested enum as an enum declaration. The current persisted
catalog has no standalone `parameter` kind; the conservative fallback keeps
legacy or plugin-provided parameter-like symbols compatible.

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

C# declaration recovery also treats repeated pattern probes as candidate-local
work. The outer pattern loop and the recoverable-pattern helper may share only a
contiguous prefix of deterministic negative results for the exact
`PreparedLine.MatchLine` at the same candidate start. Do not carry that prefix
into merged multiline property/function inputs, another offset, or another
line. A regex timeout is not a deterministic miss and must never enter the
prefix; pattern order, successful probes, timeout diagnostics, and cancellation
boundaries remain unchanged.

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
The core reference loop owns one `CoreReferenceLineContainerResolver` per
extraction and resets its line coordinates, fallback container, and
language-specific state immediately before each non-empty line. Its bound
container delegates are created once, consumed synchronously by that prepared
line, and must not be retained past it. Exceptions and cancellation abandon the
extraction-local resolver; do not pool it or make it static. C# declaration
generic-parameter discovery may share an empty `IReadOnlySet` only after the
line has no `<` marker. Consumers must keep that set read-only, while any line
with `<` continues through the full callable/type declaration parser.
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
The BCL-regex overloads of `BoundedRegex.EnumerateMatches` return the concrete
`MatchEnumerable` / `MatchEnumerator` value types. Direct `foreach`, the
cap-aware reference wrapper, and SQL match helpers must preserve that static
type so an empty or already-capped scan allocates no iterator object. Conversion
to `IEnumerable<Match>` and LINQ remain supported compatibility paths, but box
the value enumerable and enumerator and therefore do not belong in hot loops.
Creating the enumerable or enumerator must not invoke the regex engine. The
first `Regex.Match` runs only on the first `MoveNext`; later steps must use
`Match.NextMatch` to preserve timeout handling, zero-length progress, `\G`
continuation, explicit `startAt`, and right-to-left ordering. Cap-aware wrappers
check capacity before the underlying `MoveNext` and never look ahead after the
list is full.
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
JavaScript / TypeScript pattern scans likewise share one lazily cached,
column-preserving sanitized-line snapshot between module, supplemental symbol,
and private-scope analysis. Keep the cheap raw `{` / `=>` gate ahead of snapshot
materialization so flat files do not pay for a lexical pass, and treat the cached
lines as immutable rather than copying or sanitizing them again per consumer.

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

### C# top-level synthetic scope contract

C# extractor contract version 13 persists an actionable file-scoped symbol for
compilation units with executable top-level statements. The symbol uses
`kind=function`, `sub_kind=top_level_scope`, and `name=<top-level>`; public
results derive `is_synthetic=true`, qualify the identity as
`<indexed-path>::<top-level>`, and emit an `id:<symbol-id>@g:<generation>`
selector. Selectors are valid only for their active index generation and are
resolved by symbol id, so identical top-level programs in different files do
not share callee identity.

Detection runs after container assignment. It excludes declaration-covered
ranges, imports with any legal whitespace between their C# keywords, comments,
directives, and assembly/module metadata, recognizes
both `using var` and explicitly typed `using Type value = ...` declarations as
executable rather than import directives, then uses the first and last uncovered
executable lines as both source and body bounds.
A top-level local function remains source-declared and containerless; its own
narrower span owns references inside the function, while a synthetic range may
cross it when executable statements occur on both sides. When a local function
and an outside statement share one line, declaration columns retain the local
function while the outside statement belongs to the synthetic scope. Reference extraction
uses the synthetic symbol's persisted id for otherwise containerless calls in
that body. The synthetic scope is not a documented declaration, so an XML-doc
comment before a top-level statement does not attach to it. `outline`,
coordinate `inspect`, and identity-scoped `callees` must
therefore navigate the same row; both CLI and MCP `callees` resolve its selector
by persisted symbol id. Unused-symbol list and count queries exclude this
synthetic entry point because it is executable infrastructure, not removable
dead code. Selector-scoped callee queries fail closed when a readable legacy
schema lacks the persisted source-identity column. If a stored C# extractor version predates this
contract or is missing, and no synthetic row is available, outline reports
`top_level_symbol_support=reindex_required` plus a typed limitation instead of
claiming support. A normal full index re-extracts unchanged C# files before it
stamps this contract, so the documented reindex remediation repairs unstamped
legacy databases without requiring `--rebuild`.

### Symbol Kind Taxonomy

`symbols.kind`, `symbols.container_kind`, and `symbol_references.container_kind` use the public symbol kind taxonomy below. New extractors must register new kind values in `SymbolKindCatalog` before writing them so schema checks, writer validation, CLI filters, and downstream JSON consumers stay aligned.

The ordered `SymbolKinds` and `ReferenceKinds` arrays remain public compatibility
snapshots; callers must treat their elements as immutable after type initialization.
A private canonical ordered taxonomy is the sole source for immutable Ordinal writer
lookups, SQLite schema checks and migrations, and ctags filters. This keeps those
internal contracts aligned even if legacy consumer code accidentally replaces an
element in a public array. Add new values in the catalog source and update the
exhaustive catalog, schema-parity, and public-mutation isolation tests; do not mutate
the public arrays at runtime.

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
| `field` | Field declarations where distinct from properties; C# plain, const, and static readonly fields share the tuple-aware type grammar, and oversized initializer signatures retain declaration metadata while replacing top-level initializer bodies with deterministic markers | Search/filter symbol |
| `file_module` | File-scoped module/package declarations | Namespace-like context symbol |
| `function` | Functions, methods, constructors, delegates, tasks, and callable bindings that do not have a narrower kind | Primary callable definition; participates in callers/callees through reference rows |
| `generator` | JavaScript/TypeScript generator declarations | Callable definition; participates in callers/callees through reference rows |
| `heading` | Markdown headings and language section markers such as C# regions, Python module docstrings, and JavaScript/TypeScript `@module` docblocks | Outline symbol; Markdown headings are definition targets for path-scoped fragment references. Their 1-based inclusive ranges stop at the last physical source line, regardless of a trailing LF/CRLF, and omit body ranges when the heading body is empty. |
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

For C#, `test.method` ownership is determined from the complete leading attribute
block rather than from the immediately preceding physical line. The bounded scanner
reuses the C# lexical masking pass, so multiline attribute arguments and consecutive
attribute lists are supported while generic attribute type arguments stay within their
own item and brackets or test-like names in comments and normal, verbatim, raw,
interpolated, and character literals are ignored. Explicit
`return:`, `assembly:`, and `module:` targets do not classify a method. A declaration-
context gate tracks expression-initializer braces and rejects bracket-led collection
expressions. Only shaped ordinary method declarations with return types can receive the
test taxonomy; fields, indexers, and constructors (including implicit-return partial
constructors) consume their attribute ownership without being reclassified.

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
| `generated_column_dependency` | SQL generated/computed-column expression dependency |
| `generic_type_argument` | Generic type argument attached to an explicit invocation |
| `implement` | Interface implementation relationship |
| `implicit_implementation` | C# implicit interface implementation relationship |
| `import` | Import/include/reference through a module system |
| `instantiate` | Constructor or object creation |
| `join_condition_reference` | SQL join/merge condition column reference |
| `lifetime_reference` | Rust/C#-style lifetime or lifetime-like type reference |
| `member_read` | Non-invoking member/value read, including qualified C# enum constants, constants, static readonly fields, and static properties |
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
`reference_count_exceeded`, extractor failures, reference safety caps, and an
active persisted symbol-kind policy makes
`index_complete=false` with stable `index_incomplete_reasons`.
`reference_graph_complete` additionally requires an available, current graph
generation and repeats graph-specific stable reasons. The normalized
`index_symbol_kind_filter` metadata, the successful-generation
`index_symbol_kind_filter_audit_version` marker, and per-file
`files.symbols_dropped_by_kind_filter` facts are shared by CLI index/status,
workspace health, and MCP indexing/status. An active policy always adds
`symbol_kind_filter_coverage_limited`, including zero-drop runs. A legacy DB
without policy provenance remains readable but adds
`symbol_kind_filter_provenance_unavailable`; this conservative fallback differs
from older completeness metadata, whose compatibility default remains unless
persisted rows prove that work was omitted. An active legacy generation without
the audit marker withholds its aggregate until a whole-workspace refresh has
restamped every per-file fact.

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

`cdidx --check-updates` and `cdidx status --check-updates` query the GitHub latest-release endpoint through `UpdateChecker`, using the same 24-hour cache and `CDIDX_DISABLE_UPDATE_CHECK=1` opt-out as the `--version` hint. `cdidx upgrade --check-only` reuses that check. `cdidx upgrade` is intentionally a thin wrapper around the signed release installer: it downloads `sha256sums.txt` and `install.sh` into a private temporary directory, independently verifies both exact files with `gh attestation verify` pinned to `github.com/Widthdom/CodeIndex/.github/workflows/release.yml` and `refs/tags/<selected-version>`, and only then trusts the manifest checksum and starts the installer. The verifier is selected only from validated known installation paths or the `CDIDX_GH_EXECUTABLE` override; on macOS, known-location discovery enumerates versioned Homebrew Cellar binaries directly so it never launches the `bin/gh` symlink. The override must be an absolute `gh` path (`gh.exe` on Windows) that resolves to a canonical target whose regular-file/image, owner, write mode or ACL, ancestor, executable, and bounded `gh --version` checks all pass. PATH order and the current directory never select the verifier, and an invalid explicit override fails closed instead of falling back. Missing or failed provenance blocks execution by default; `CDIDX_VERIFY_POLICY=compat` is the explicit audited opt-in and reports an unverified bypass rather than verified provenance. Upgrade JSON distinguishes the mechanism from the observed result through `verification_policy`, `manifest_provenance_verified`, `installer_provenance_verified`, `installer_verification_status`, and `provenance_audit_code`; check-only reports `not_attempted`, success reports `verified`, a strict blocked failure reports `verification_failed`, and a compat bypass reports `compat_bypass` plus `compat_provenance_bypass`. Invalid policy values return the normal structured usage-error JSON when `--json` is selected. After verification, the command checks that the current binary directory is writable, sets `CDIDX_INSTALL_DIR` to that directory, and runs the selected release installer.

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
| `index_complete=false` | persisted omission evidence, an active symbol-kind policy, or unavailable legacy policy provenance means full-generation absence authority is unavailable | address `index_incomplete_reasons`, then rerun indexing; rebuild unfiltered for full coverage |
| `symbol_kind_filter_coverage_limited` | persisted include/exclude policy intentionally limits symbol coverage, including zero-drop runs | `cdidx index <projectPath> --rebuild` without the symbol-kind filter |
| `symbol_kind_filter_provenance_unavailable` | a legacy DB has no persisted policy stamp | rebuild with a current binary; negative symbol/graph results remain non-authoritative until then |
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
| Caller-owned write batching | Full-scan and other atomic file writes already run inside one caller-owned transaction, so their language-neutral chunk, symbol, issue, reference-line, and reference inserts cap each statement at 32 parameters. Every batch uses compact, one-origin SQLite numeric slots (`?1` through `?N`) in row/column order; this reduces parameter-name resolution work while preserving the existing statement-size, cancellation, and checkpoint contracts. For operations above 500 rows, persistent `db_writer_batch_checkpoint` records are emitted only when progress crosses a 500-row boundary and at completion, avoiding a synchronous log flush for every tiny statement. Public writer APIs retain the SQLite-variable-limit batch shape and their existing per-batch transaction/SAVEPOINT contract. |
| Prepared savepoint controls | `DbWriter` leases only a fixed, bounded set of control statements from the connection's prepared-command cache: the first nested `sp_1` SAVEPOINT / RELEASE / ROLLBACK trio used by per-file full-index scopes, the atomic metadata savepoints, and the FTS bulk-load owner savepoint. Every lease rebinds the current outer `SqliteTransaction`; a cacheless writer still creates and disposes one command per call. Depth-two and deeper savepoint names remain dynamic and bypass the cache, and cancellation, rollback, terminal-state, and transaction-gate contracts are unchanged. |
| Authoritative-fresh raw insert scope | After the empty-database CLI path revalidates its authoritative-fresh claim inside the caller-owned transaction, only the extraction pipeline's new-file, chunk, symbol, new-file issue, fresh reference-line, and atomic fresh-reference INSERTs may bind and execute through SQLitePCLRaw on the provider-owned connection handle. These native positional bindings use a separate 512-parameter ceiling while provider-backed caller-owned writes retain their 32-parameter limit. Fresh file and reference-line writes are DONE-only: file insertion captures the same connection's positive `last_insert_rowid`, while every reference-line batch reads the greater of `MAX(id)` and `sqlite_sequence.seq`, checks the complete Int64 range, and inserts explicit contiguous IDs with `?1 + input_ordinal`. Reading the floor for every batch preserves AUTOINCREMENT history and observes inserts between batches without retaining rollback-sensitive allocator state. Invalid floors and identity-range overflow fail before the INSERT executes. Every executed fresh identity write validates `sqlite3_changes()` before publishing IDs; a row-count mismatch, constraint, cleanup failure, or cancellation discards the affected prepared statement while the caller's per-file savepoint owns data rollback. The scope preserves exact tail/write-count shapes, batch hooks, row-skip replay, and outer transaction atomicity; a 32-entry LRU retains recurring full and tail statement shapes. The synchronous full-scan persistence consumer and `DbWriter` transaction-owner check keep the non-thread-safe cache single-owner. Every lease resets and clears bindings, errors retain the original step result, cancellation maps SQLite interrupt to `OperationCanceledException`, and all cached statements are finalized before graph/index/FTS work. During this same transaction the three `files_resource_generation_*` triggers are suspended, then recreated only after native statements finalize; the resource-list generation advances exactly once when at least one file was persisted and stays unchanged for an empty repository. Rollback restores both schema and generation atomically. Replacement, incremental, rebuild, symbols-only, fresh-claim race fallback, MCP, and public writer paths remain on Microsoft.Data.Sqlite, retain their established `RETURNING` behavior, and keep per-mutation generation invalidation. |
| Authoritative-fresh source-symbol lookup | Before native statements are prepared, the raw scope creates a connection-local TEMP `WITHOUT ROWID` snapshot with partial indexes for folded name, folded display name, and legacy ASCII `NOCASE` fallback. Each atomic reference collection clears that snapshot and copies the symbols for its distinct source file IDs once through `idx_symbols_file`; references without a container skip materialization. The three indexed probes use `UNION` to preserve the existing name-or-display semantics when one symbol matches more than one branch, then retain the same containing-range and innermost-span/start/id ranking. TEMP schema creation belongs to the caller's outer transaction, while each file savepoint owns its snapshot population and reference writes, so cancellation, failure, and rollback restore both together. Provider-backed, rebuild, incremental, fresh-claim fallback, MCP, and public-writer paths neither create nor query the TEMP table. |
| Authoritative-fresh core secondary indexes | The same revalidated empty-database CLI transaction drops 22 language-neutral secondary indexes on `files`, `chunks`, `file_issues`, and `symbols` before persistence, then builds each B-tree once after every native INSERT statement has finalized and before graph or readiness queries begin. UNIQUE autoindexes remain active, so path and table constraints keep their normal enforcement. `idx_symbols_file` also remains active because fresh-reference insertion copies each relevant file's symbols into its indexed TEMP snapshot once; dropping it would turn every per-file materialization into a full symbol-table scan. Cancellation or failure leaves restoration to the outer rollback, which atomically restores the pre-load schema; rebuild, incremental, fresh-claim race fallback, and MCP paths retain the indexes throughout their writes. Canonical DDL is shared by schema initialization, opportunistic read migration, and the bulk-load guard so the deferred set cannot drift from the completed database contract. |
| Checkpointing | `DbWriter` runs `PRAGMA wal_checkpoint(PASSIVE)` after each outer transaction commit, and SQLite may also checkpoint automatically after the configured 1000-page threshold. Both checkpoint paths are opportunistic: active readers are not blocked, and an uncheckpointed WAL is expected state rather than corruption. |
| Checkpoint result contract | Explicit `PRAGMA wal_checkpoint(TRUNCATE)` paths execute a reader and return a structured result containing SQLite's `(busy, log, checkpointed)` values. Non-zero `busy` or positive remaining pages is unsuccessful with a bounded machine reason. `(0, -1, -1)` is SQLite's successful non-WAL no-op. Instance checkpointing, the static read-only-fallback preflight, query diagnostics, top-level status, and nested connection-policy status preserve the same result and counts. Raw exception text and paths must not enter diagnostics. |
| Crash recovery | If the process is killed after SQLite has committed a transaction but before checkpointing, the next normal opener rolls the WAL forward; no manual recovery step is required. If the process dies before a transaction commits, SQLite rolls that transaction back. |
| Migration transaction and foreign-key ownership | `TryMigrateForRead` checks SQLite's autocommit state before opening a transaction: an active transaction is treated explicitly as caller-owned, so cdidx neither commits nor rolls it back, while unrelated `BEGIN` failures propagate instead of being mistaken for nesting. Rebuild migrations that require disabled foreign keys set and read back `PRAGMA foreign_keys=OFF` before their owned transaction begins, assert the effective disabled mode inside nested rebuild helpers, and restore plus verify the caller's original mode after the transaction is disposed on both success and failure. |
| Schema discovery cache | `DbReader` schema discovery uses a process-level cache keyed by the normalized DB path. Column and index results are stored and returned as immutable `FrozenSet` snapshots, so callers cannot mutate schema decisions shared by other readers. Path states are reference-counted by live `DbSchemaCache` owners, are removed when the final owning `DbContext` is disposed, and are never evicted while an owner remains active. The cache checks `PRAGMA schema_version` before serving a lookup so SQLite DDL performed by cdidx or an external `sqlite3` session invalidates stale snapshots. Manual schema edits outside cdidx are still unsupported operationally; run `cdidx validate` after such edits before trusting query output. |
| Batch trust marker | Index write batches stamp `codeindex_meta.batch_in_progress=true` before starting a mutation transaction and clear it inside the transaction that commits the matching rows and readiness metadata. If the indexer crashes after the marker is written but before the commit clears it, every later open reports `Last batch did not complete; run cdidx index --rebuild to re-index from a known clean state.` without changing readiness metadata. The explicit `index --rebuild` repair path alone demotes readiness before rebuilding. Gracefully handled per-file errors clear the marker after rollback; orphaned markers are reserved for interrupted or crashed batches whose trust metadata should not be treated as clean. |
| Read-only opens and fallback | Query-only commands open with SQLite `Mode=ReadOnly` from the first attempt, retain WAL visibility, and never use writable setup or opportunistic migrations. A write-capable intent may still fall back to read-only when writable journal/WAL setup fails; an explicitly supplied `immutable=1` URI is the opt-in stale-snapshot escape hatch. If a WAL is present and must be observed from storage that cannot expose its sidecars, copy `.db`, `.db-wal`, and `.db-shm` together to a readable location or use a SQLite backup from an environment that can open the full WAL set. |
| Status pragma diagnostics | `status --json` exposes the selected read-only connection under `sqlite_connection_policy` (`active_mode=read_only`, `open_mode=read_only`) and resolved connection values under `db_pragma_settings` (`journal_mode`, `synchronous`, `wal_autocheckpoint`, `busy_timeout_ms`, `page_count`, `freelist_count`, `page_size`, `auto_vacuum`). It also exposes prepared-command cache counters under `prepared_command_cache` (`count`, `capacity`, `hit_count`, `miss_count`, `eviction_count`) for automation and support diagnostics. `maintenance_guidance` derives `wal_state`, `freelist_ratio`, `freelist_state`, `estimated_*_reclaimable`, `auto_vacuum_mode(_name)`, `recommended_command`, and `post_maintenance_follow_up` from those raw metrics without changing the raw values. Its nested `fts_optimization` uses the same pure evaluator as optimize preview and execution, exposing `recommended`, `action`, `reason`, `threshold_writes`, `observed_writes`, and `state` without writing to the database. `status --check --json` adds structured `repair_commands[]` entries with `name`, `action`, `args`, `mutation_class`, `safety_class`, `safety_notes`, compatibility `reason`, and ordered `reasons`. Exact structured identities are deduplicated and aggregate reasons in check priority order; differences in target, options, action, mutation class, or safety semantics prevent merging. Human check output uses the same command set, preserves platform-aware shell quoting, visibly escapes control characters to keep each `[repair]` action on one diagnostic line, and leaves structured JSON `args` unchanged. `last_failed_or_partial_index_run` exposes bounded failed/partial index context (`status`, `mode`, timings, counts, stable error code, reason, `progress_persisted`, and bounded `recovery_hint`) and must not include raw exception text or file paths. |
| Maintenance thresholds | WAL guidance flips to `checkpoint_recommended` at `CDIDX_MAINTENANCE_WAL_WARN_BYTES` (default 64 MiB). Freelist guidance flips to `vacuum_recommended` at `CDIDX_MAINTENANCE_FREELIST_WARN_RATIO` (default `0.20`). Invalid or out-of-range env values fall back to defaults. |
| Post-rebuild reclaim | After a successful CLI or MCP rebuild commits, cdidx evaluates the shared freelist threshold and runs `PRAGMA incremental_vacuum` only when reclaim is recommended and `auto_vacuum=INCREMENTAL`. It never performs an automatic full `VACUUM`; legacy databases report `skipped/auto_vacuum_not_incremental` and retain the explicit `cdidx vacuum` conversion path. Reclaim runs after the index transaction, emits bounded progress/log phases, and persists `last_index_run.rebuild_reclaim` with stable state/reason, duration, before/after logical database sizes and physical main-file samples, page/free-page counts and ratios, and reclaimed page/byte counts. Physical samples can lag while WAL-backed pages await checkpoint; logical sizes and page metrics are the immediate attribution contract. Cancellation, busy/read-only I/O, or another reclaim failure cannot roll back or reclassify the already committed index generation. |
| Maintenance command precedence | `maintenance_guidance.recommended_command` preserves the existing vacuum-then-checkpoint precedence. It returns `cdidx optimize --db <db>` only when WAL and freelist states are both exactly `ok` and the trusted FTS write snapshot reaches its threshold; an `unknown` higher-priority state or a stale/unavailable FTS snapshot never selects an optimize command. |
| Page attribution | `status --json` reads SQLite page ownership without mutating the source. It prefers `dbstat` page bytes and otherwise traverses a bounded b-tree/WAL snapshot (at most 1,000,000 pages and 100,000 schema objects); when a live WAL connection is not already backed by a stable detached file set, the fallback first makes a cancellation-aware private backup of that connection's active read snapshot so a concurrent commit cannot mix generations. `allocated_object_bytes + freelist_bytes + unexplained_residual_bytes` equals `logical_database_bytes`; table/index and internal/leaf/overflow/other page subtotals each reconcile to `allocated_object_bytes`. Payload, unused space, and structural overhead form a second reconciliation. Physical main/WAL/SHM bytes are reported separately. Output is capped at 20 object names, each support-sanitized to at most 128 characters. A failed or inconsistent probe returns `available=false`, a stable `unavailable_reason`, and null/omitted attribution values rather than zeros. |
| Vacuum | `cdidx vacuum` runs `PRAGMA incremental_vacuum` against writable incremental-auto-vacuum DBs, and performs a one-time `PRAGMA auto_vacuum=INCREMENTAL` plus full `VACUUM` conversion for legacy no-autovacuum DBs. `cdidx vacuum --dry-run --json` estimates reclaimable pages/bytes without executing vacuum pragmas: logical metrics come from the artifact-preserving query snapshot; physical `*_before` aliases describe `command_entry`, while physical `*_after` aliases describe the later source-generation-qualified `post_command` observation and are omitted when that tuple cannot be accepted stably. These aliases therefore do not claim one shared observation; use `file_set_observations` for the explicit boundaries. Real `cdidx vacuum --json` reports logical database bytes (`page_count * page_size`) and requested-source main/WAL/SHM file lengths before and after maintenance, plus their physical file-set sum. Each logical before/after PRAGMA group is read from one SQLite read snapshot. Real after fields and maintenance guidance are finalized after a cancellation-aware best-effort truncate checkpoint and closure of the command-owned vacuum connection. An unchanged `data_version` across the logical-after snapshot, command-owned checkpoint, and stable source-state capture establishes the pre-close witness. The post-close query-only observer binds its logical snapshot to one stable requested-source file set. It accepts either the unchanged raw source generation or the specific checkpoint representation in which the witness has a non-empty WAL, the observer has a zero-length WAL, and both expose the same logical size signature (`page_count`, `freelist_count`, `page_size`, and `auto_vacuum`); this second case preserves coherent public size metrics but does not assert that application data stayed on the same generation. If an external writer changes that signature, or another raw generation does not fit that hot-WAL-to-zero-WAL checkpoint shape, all physical after fields and the `db_size_bytes_after` / `wal_size_bytes_after` aliases are unavailable (null in the result DTO and omitted from normal CLI JSON), and WAL guidance is `unknown`. A physical observation is accepted only when two complete file-identity reads agree and bracket the same DB/WAL generation; capture retries at most three times, and persistent instability or metadata-read failure makes the entire physical tuple and its aliases unavailable while WAL guidance becomes `unknown`. A confirmed absent sidecar is zero. `db_size_bytes_*` and `wal_size_bytes_*` remain compatibility aliases for `main_file_bytes_*` and `wal_file_bytes_*`; `pages_reclaimed` and `bytes_reclaimed` remain logical page reclamation rather than a physical file-size delta. Another active connection can legitimately retain WAL/SHM, so main-file or physical-file-set bytes need not equal logical database bytes. `physical_file_set_bytes_*` is the sum of logical file lengths, not filesystem allocated-block usage, and `wal_checkpoint_timing_note` documents the stable post-close observation point. |
| Vacuum file-set observation points | `vacuum --json` exposes `file_set_observations.command_entry`, `post_open_pre_vacuum`, and `post_command` as separate physical snapshots. Command entry is captured before validation opens SQLite, applies pragmas, checkpoints, or hardens sidecar permissions; the post-open snapshot records the source set used with the pre-vacuum SQLite metrics; and the post-command snapshot is revalidated after the command-owned connection closes under the dry-run source-generation or real post-close witness rules. Each snapshot reports `state=captured` with main/WAL/SHM existence flags and byte lengths, or `state=unavailable` with the bounded stable reason `unstable_or_inaccessible`; confirmed absent sidecars use `exists=false` and zero bytes, while empty sidecars use `exists=true` and zero bytes. Compatibility `*_before` file fields and `db_size_bytes_before` / `wal_size_bytes_before` now alias command entry, while `*_after` aliases post-command. Logical `page_count_*`, `freelist_count_*`, and `logical_database_bytes_*` remain SQLite pre/post-vacuum snapshots, so reclaimed pages/bytes are never inferred from command-created sidecar removal. |
| FTS optimize preview | `cdidx optimize --dry-run` and its `cdidx index <project> --optimize --dry-run` alias open a `QueryOnly` snapshot, probe an existing lockfile without creating or acquiring it, and never run write PRAGMAs, schema setup, FTS control inserts, or metadata writes against the source DB/WAL/SHM set. Both entry points preserve an explicitly supplied `file:` URI, including `immutable=1`, for the query connection while filesystem probes use its normalized local path, so status and either preview apply identical stale-snapshot semantics even with a hot WAL. Human and JSON path display uses the shared maintenance policy: caller-relative spelling remains visible by default, absolute inputs become `<redacted>`, and `--show-paths` opts into the resolved absolute database path; internal snapshot and filesystem operations always retain the real path. JSON reports size/freelist/readiness indicators, planned operations, and the same `fts_optimization` recommendation object as status, including the exact threshold and observed writes. A stale batch or unavailable legacy counter/page snapshot suppresses recommendation with a stable reason and state. Real execution reports the same object before and after optimize, including the reset counter, and performs the repair-mode schema initialization or migration check. Object sizes use `dbstat` page bytes when available and a labeled logical-payload fallback otherwise. A real optimize records its elapsed milliseconds so later previews can expose `estimated_duration_ms`. |
| Size and process diagnostics | `status --json` also reports `db_size_bytes`, `wal_size_bytes`, capped `symbol_kinds` / `symbols_by_language` kind maps with `symbol_kind_*` and `symbols_by_language_kind_*` overflow metadata when caps apply, current `process` heap/GC/working-set metrics, `last_index_run` metadata from successful CLI and MCP index runs, and `last_workspace_freshened_at` as the latest successful index/update timestamp. `last_index_run.bytes_read_skipped_file_count` and `bytes_read_incomplete` report whether unreadable files were omitted from the `bytes_read` total, while `last_index_run.diagnostics`, `diagnostic_count`, and `diagnostics_truncated` carry bounded warnings for best-effort index metadata writes that failed after the index data itself was successfully written. `indexed_at` still comes from indexed file rows, so partial or no-op updates can freshen the workspace without moving `indexed_at`. |
| Memory tracing | `index --json --memory-trace` adds a `memory_timeline` block to the CLI index result and persists peak working-set MB into `last_index_run`; dry-run results also emit live `start`, `snapshot`, `scan`, and `finalize` samples but never persist run metadata. `index --dry-run --rebuild` bypasses destructive confirmation because it does not delete or rewrite the index. `CDIDX_MEM_WARN_MB=<mb>` prints a warning when the sampled working set crosses that threshold. |
| Newer schema protection | Writable opens reject databases whose `PRAGMA user_version` contains readiness bits outside the current binary's `CurrentSchemaVersion` mask. Read-only status/query paths may still surface `index_newer_than_reader=true` as a degraded audit signal, but write-capable paths must fail with `E003_SCHEMA_TOO_NEW` so an older cdidx cannot silently rewrite a DB stamped by a newer one. |

Status freshness summaries are classified by one shared evaluator. An
authoritative `status --check` supplies the file-level result. Without that check,
`last_workspace_freshened_at >= latest_modified` can prove a checksum-reused
no-op update fresh only when the worktree is clean and the runtime,
workspace-verified, and latest-index HEAD SHAs all agree, and Git reports no
`skip-worktree` or `assume-unchanged` entry that could hide a later change.
The Git dirtiness probe explicitly requests all untracked files, overriding a
repository-level `status.showUntrackedFiles=no` setting. Missing provenance,
hidden index state, future timestamps, and an ordinary-status dirty worktree are
`unknown`; a later modification is `stale`, and changed HEAD remains conservative.
An authoritative file check may prove freshness in a dirty repository when every
workspace path matches, so already-indexed untracked paths and dirtiness outside a
subdirectory index do not fail check mode. Status-level HEAD/branch drift is still
evaluated before that file check and propagated into checked failures, exit status,
and workspace member health. Ordinary
`head_freshness=head_current` semantics remain distinct from the authoritative
checked `fresh` value.

`vacuum --dry-run` accepts supported local SQLite URI spellings such as `file:/absolute/path/codeindex.db`, Windows `file:/C:/absolute/path/codeindex.db`, and canonical `file:///...` forms. Single-slash paths are canonicalized while retaining their original query string and ignoring URI fragments, and validation plus metric collection use that same query-only URI so an explicit `immutable=1` keeps its stale-snapshot semantics.

### Data directory resolution

When `--db <path>` is omitted, cdidx resolves the SQLite location from a data directory and appends `codeindex.db`. The precedence chain is:

1. `--data-dir <dir>`
2. `CDIDX_DATA_DIR`
3. `XDG_DATA_HOME/cdidx/<workspace-hash>` when `XDG_DATA_HOME` is set
4. `<workspace>/.cdidx`

`--db <path>` remains the most explicit override and bypasses data-directory resolution. `status --json` reports the effective directory as `data_dir` and the selected source as `data_dir_source` (`flag`, `env`, `xdg`, or `workspace`) so automation can audit where the index lives.

### SQLite performance tuning

Every `DbContext` connection sets `PRAGMA cache_size=-65536` (64 MiB), `PRAGMA temp_store=MEMORY`, and on 64-bit processes `PRAGMA mmap_size=268435456` (256 MiB). These are connection-scoped query-performance knobs; they do not alter the on-disk schema and are skipped only where SQLite cannot apply them.

High-churn index runs that enter the bulk-load path temporarily set `mmap_size=0` after the input-snapshot validation barrier. This prevents the SQLite mapping from overlapping the largest managed reference-graph working set. The configured mapping is restored after all write scopes unwind, including failure and cancellation paths; ordinary queries, no-op indexing, and low-churn incremental runs retain the configured value.

Operators can override the defaults with environment variables:

| Variable | Default | Meaning |
|---|---:|---|
| `CDIDX_SQLITE_CACHE_KB` | `65536` | Positive cache size in KiB, up to `1048576`; cdidx applies it as a negative SQLite `cache_size` value so SQLite interprets it as KiB. Invalid or oversized values fall back to the default. |
| `CDIDX_SQLITE_MMAP_BYTES` | `268435456` | Non-negative memory-map window in bytes on 64-bit processes, up to `1073741824`. Use `0` to disable mmap. Invalid or oversized values fall back to the default. |
| `CDIDX_SQLITE_BUSY_TIMEOUT_MS` | `5000` | Non-negative SQLite busy timeout in milliseconds, up to `3600000`. Use a higher value for slow disks or concurrent MCP/index workflows; invalid or oversized values fall back to the default. |
| `CDIDX_PREPARED_COMMAND_CACHE_CAPACITY` | `64` | Positive prepared SQLite command cache capacity per connection, up to `512`. Invalid or oversized values fall back to the default. |

After a successful `cdidx index` run, the writer refreshes SQLite planner statistics so large repositories do not rely on default selectivity estimates for `search`, `references`, `callers`, and related joins. A brand-new index database runs full `ANALYZE` once after the initial population; later successful index runs use SQLite's lighter `PRAGMA optimize`. This maintenance is best-effort and never changes the schema contract.

Truly empty-database bulk loads also perform a separate, targeted planner-statistics refresh immediately before reference-candidate population. An enabled reference-secondary-index bulk-load guard runs `ANALYZE main.files`, `ANALYZE main.symbols`, and `ANALYZE main.symbol_references` exactly once after dropping the candidate reverse index and before preparing the identity-resolution SQL; the TypeScript-deferred path has already restored its ordinary graph/query indexes at this point, while the direct graph path proceeds without that extra restoration phase. The CLI enables this only when it started with no indexed files and is neither rebuilding nor symbols-only; MCP captures the same pre-rebuild empty state explicitly. Existing-database runs, updates, rebuilds, symbols-only runs, and disabled guards retain the prior behavior. Cancellation aborts the indexing operation, while a non-cancellation SQLite failure rolls back the nested statistics savepoint and continues graph construction with the previous planner state. This pre-graph phase has a dedicated testing hook and is independent of final planner maintenance.

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
idx_symbols_display_name_folded ON symbols(display_name_folded) WHERE display_name_folded IS NOT NULL
idx_symbols_file    ON symbols(file_id)
idx_symbols_file_kind ON symbols(file_id, kind)
idx_files_lang_modified ON files(lang, modified)
idx_symbol_refs_file      ON symbol_references(file_id)
idx_symbol_refs_container_kind ON symbol_references(container_name, reference_kind)
idx_symbol_refs_name_kind ON symbol_references(symbol_name, reference_kind)
idx_symbol_refs_name_file ON symbol_references(symbol_name, file_id)
idx_symbol_refs_name_nocase_kind ON symbol_references(symbol_name COLLATE NOCASE, reference_kind)
idx_symbol_refs_name_nocase_file ON symbol_references(symbol_name COLLATE NOCASE, file_id)
idx_symbol_refs_container_nocase_kind ON symbol_references(container_name COLLATE NOCASE, reference_kind)
idx_symbol_refs_symbol_name_folded_kind ON symbol_references(symbol_name_folded, reference_kind)
idx_symbol_refs_symbol_name_folded_file ON symbol_references(symbol_name_folded, file_id)
idx_symbol_refs_container_name_folded_kind ON symbol_references(container_name_folded, reference_kind)
idx_symbol_refs_unresolved_mutual_folded ON symbol_references(container_name_folded, symbol_name_folded)
  WHERE source_symbol_id IS NULL AND target_symbol_id IS NULL AND is_self_reference = 0
    AND container_name_folded IS NOT NULL AND container_name_folded <> ''
    AND symbol_name_folded IS NOT NULL AND symbol_name_folded <> ''
    AND reference_kind IN ('call', 'instantiate', 'subscribe', 'unsubscribe', 'razor_event_binding')
idx_symbol_refs_source_symbol ON symbol_references(source_symbol_id)
idx_symbol_refs_target_symbol ON symbol_references(target_symbol_id)
idx_symbol_refs_resolved_source_target_kind ON symbol_references(source_symbol_id, target_symbol_id, reference_kind)
  WHERE source_symbol_id IS NOT NULL AND target_symbol_id IS NOT NULL
idx_symbol_ref_candidates_symbol ON symbol_reference_candidates(symbol_id, reference_id)
```

Single-column BINARY, NOCASE, and folded reference name/container indexes are
not part of the canonical schema: the retained `*_kind` / `*_file` composites
supply the same equality seeks through their leftmost prefixes. Read migration
removes those six legacy indexes and the former all-row
`idx_symbol_refs_mutual_folded`; unresolved reciprocal lookup uses the partial
index above so resolved and non-call reference rows do not inflate graph-index
rebuilds. Exact-query readiness diagnostics name the retained composite that
backs each folded or NOCASE path.

### Query planner expectations

Hot graph aggregations that constrain `symbol_references.symbol_name` and a
small `reference_kind IN (...)` set must stay indexable through
`idx_symbol_refs_name_kind`. Regression coverage uses `EXPLAIN QUERY PLAN`
before and after `ANALYZE` so this compound index remains the expected plan for
`GROUP_CONCAT(DISTINCT r.reference_kind)` summaries instead of using a
name-only composite-prefix probe plus row-by-row kind filtering (#1922).

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

C# unqualified calls and method groups that match local-function declarations resolve against
the narrowest complete lexical block inside the enclosing callable. Declaration order is not a
visibility gate; overloads from the winning block remain candidates and normal call-arity
filtering selects among them. Parameters, local values, and delegate-valued bindings shadow
local functions before graph resolution, while sibling blocks and unrelated enclosing
callables cannot contribute local candidates. Incomplete callable or block-range evidence stays
`unresolved` instead of falling back to a file-wide same-name edge. The persisted target
identity is shared by references, callers, inspect, impact, and LSP definition/reference reads.

C# type-reference resolution uses `LogicalPartialSymbolGrouper` for declarations that have a
valid logical partial-family identity. Full and scoped refreshes persist the same stable
`family:` target key used by grouped symbol discovery, so multiple physical declarations in one
language/kind/namespace-and-container/generic-arity family produce `resolved_group` rather than
semantic ambiguity. `resolution_candidate_count` deliberately remains the physical declaration
count, and `symbol_reference_candidates` retains every physical symbol row. Search ranking,
grouped hotspots, inspect, dependencies, and impact consume the logical identity while APIs that
list definitions remain deterministic and physical. The reference-identity contract version
must be advanced when this key changes; version 9 forces full, scoped, no-op, and deletion-only
index paths to replace older physical-path target keys before identity-aware reads become ready.

C# common member names are never discarded during extraction. The writer persists their
receiver/type evidence in `target_qualifier`, and reference finalization records
`resolution_state`. Bare-name `references` and unqualified graph discovery remain broad and
retain the stored resolution labels. On a current C# reference-identity contract, however,
exact C# `callers` queries and confirmed C# `impact` traversal require a `resolved` or
`resolved_group` candidate that matches one of the selected definition IDs, including the
resolved polymorphic-dispatch family. Multiple C#
same-name definitions contribute their resolved ID union; unresolved or ambiguous same-leaf
evidence remains inspectable but cannot become a confirmed caller edge or transitive hop. When
no matching definition ID exists, exact callers and impact return zero confirmed rows instead
of traversing the name fallback. CLI/MCP payloads expose `identity_root_available`,
`identity_root_unavailable_reason`, `graph_evidence_confidence`, and
`identity_root_resolution_truncated`; missing roots and capped identity sets set
`authoritative_count: false`, while impact marks a missing root heuristic and strict mode fails
through the stable `no_identity_backed_root` failure code. Legacy or stale identity contracts
may keep the name-based compatibility path, but must label it `name_fallback`, degraded, and
non-authoritative.
Other graph languages retain their existing language-specific exact matching until they expose
the same persisted target-identity contract. When `--lang` is omitted, impact partitions the
same traversal step: C# rows must match resolved candidate IDs, while non-C# rows retain their
language-specific exact-name graph. A root inferred only from non-C# definitions reports
`language_graph`; persisted reference-graph incompleteness makes every impact count
non-authoritative even when the local traversal and identity resolution are otherwise complete.
C# callable hotspots likewise count logical target identities instead of leaf-name aggregates.
Candidate groups contribute only when they collapse to one logical target, name, and kind, and
unresolved rows do not inflate the confirmed count. The CLI `--include-qualified-common-calls` flag and MCP
`includeQualifiedCommonCalls` argument still bypass the query-time common-member noise filter.
Keep dependency edges identity-scoped: completeness options may expose unresolved evidence but
must not convert it into a same-name file dependency.

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

Existing-index, rebuild, and retained-graph finalization paths compute candidate count, minimum
symbol ID, a single-target-family flag from a positive non-NULL count plus BINARY
`MIN(...) IS MAX(...)`, and the stable target key in one correlated aggregate per
reference. Keep these four resolution fields on the row-value assignment path; separate scalar
subqueries multiply the candidate-index and symbol/file lookup work on large graphs. The
language/name families that
are globally unique are aggregated once into the connection-local
`temp.reference_unique_symbol_families` table and reused by non-C#, C#, and C# attribute
fallbacks. Create that temp table in a separate prepared command before preparing the refresh;
SQLite resolves referenced tables while preparing every statement in a command batch.

A true empty-database ordinary CLI full scan (not `--rebuild` or `--symbols-only`) opts into a
separate fresh-resolution contract. Reference inserts persist canonical provisional values
(`unresolved`, candidate count zero, and zero self/mutual flags) without adding bind parameters.
The fresh CTE also assigns `source_symbol_id` from the same-file symbols already persisted before
each file's references, using the ordinary narrowest-containing-range tie-break and a literal input
ordinal to preserve batch order. Finalization therefore omits the all-reference source-identity
UPDATE on this authoritative path. Ordinary full, differential, scoped, rebuild, retained, and MCP
paths keep their established source refreshes.
The early empty observation is advisory: immediately after the authoritative outer write
transaction begins, the CLI rechecks `files`, `symbols`, and `symbol_references` in that
transaction. If another connection committed any row during the pre-write gap, the graph scope
disables fresh insert defaults before the first persisted row and finalization uses the ordinary
full-resolution SQL, including candidate-free references.
Finalization scans `symbol_reference_candidates` once into materialized per-reference facts and
updates only candidate-bearing references by primary key; candidate-free references retain their
provisional values, and the self flag is derived in that same sparse update. The opt-in remains
pending after a failed graph transaction and clears only after the graph commit. Existing-index
updates, rebuilds, retained-graph rebuilds, and MCP indexing keep the established path. In
particular, MCP can durably commit per-file batches before graph finalization, so it must retain
its existing retry semantics until a separately designed recovery contract can cover that state.

Fold readiness has a narrower authoritative-fresh optimization shared by ordinary CLI and MCP
full indexing. Before the first write, `DbWriter` may issue an opaque, one-shot claim only from a
`BEGIN IMMEDIATE` snapshot in which `files`, `symbols`, and `symbol_references` are all empty. The
claim is bound to its writer/connection and captured `PRAGMA data_version`; only that writer may
consume it once, and an intervening commit from another connection invalidates it. When the
built-in extractor pipeline completes successfully, finalization may use the claim to omit the
allocation-heavy read and re-fold of every persisted symbol/reference value, but it still runs
the SQL NULL-completeness check before stamping FoldReady.
The raw `BEGIN IMMEDIATE` helper deliberately performs its post-success cancellation check only
after the caller has recorded rollback ownership. Cancellation at that boundary therefore rolls
back the raw transaction before releasing the writer gate, leaving a warm CLI/MCP connection
usable by the next request.
The run also captures the registry's monotonic accepted-producer mutation generation. Any
generation change invalidates the claim even if a transient custom producer was later removed and
the final registry is built-in-only again; staged workspace replacement commits participate in
that history, while diagnostic-only publication and unchanged missing-directory discovery do not.

This shortcut is deliberately fail-closed. Rebuilds, updates, legacy or existing indexes, public
`DbWriter` readiness APIs, custom plugins or pattern configurations, post-extraction hooks, incorrectly
owned or reused claims, and any externally committed database change use the established full
value validation. The shortcut changes neither the folded values produced during insertion nor
the readiness transaction and rollback semantics.

For C# explicit-interface members, `symbols.name` remains the short display/discovery alias,
while `symbols.name_folded` stores the normalized interface qualifier plus terminal method
generic arity. When that identity differs, `symbols.display_name_folded` stores the short
Unicode-folded discovery alias and `idx_symbols_display_name_folded` keeps unqualified exact
queries indexed. Kind and normalized signature remain independent columns in the canonical
symbol row, so identity comparisons retain qualifier, member kind, arity, and signature without
changing outline or LSP display names. Exact qualified queries normalize generic parameter names
to arity and map indexer spellings `this` and `Item` together; unqualified exact queries use the
short-name alias for discovery and therefore may return explicit and public members. Fold
validation/backfill reconstructs both folds from the persisted signature. A
`CSharpSymbolNameContractVersion` change either refreshes unchanged C# files during indexing or
forces `backfill-fold` / MCP `backfill_fold` into full rewrite mode before v3 is stamped.

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
CLI `inspect --selector 'id:<n>@g:<fingerprint>'` validates the emitted index generation, resolves
the persisted ID directly in the active database, and then enters this same candidate-bundle path.
Keep selector parsing in a typed extensible model,
reject non-positive/malformed IDs before opening graph state, and report an absent database-local
ID or a stale/cross-database generation as `E018_QUERY_NOT_FOUND`. The unversioned `id:<n>` form
remains a same-database compatibility input but cannot prove generation provenance.
`identity_scoped` must describe evidence precision, not merely schema
availability: if an inbound reference still has more than one resolution candidate, emit
`identity_scope_reason: ambiguous_reference_candidates` and keep the candidate rows visible while
marking the bundle non-identity-scoped. C# call resolution may narrow ordinary required-parameter
overloads by positional argument count only. Named arguments, optional/default parameters,
`params`, generic method inference, and incomplete syntax remain ambiguous. Extension receiver
adjustment and dynamic receiver types are outside this arity helper.
C# constructor resolution applies the same conservative contract to direct calls and after an
unqualified `using` alias is normalized: keep the canonical target leaf and qualifier for identity,
but preserve the physical source line and anchor the lexical constructor token with its source
column and span when counting positional arguments. A bounded multiline context may complete the
balanced scan. If any primary or explicit constructor in the target family has optional/default or
`params` binding, do not use argument count to narrow that family. Named arguments, raw strings,
interpolated strings with embedded expressions, relational/generic angle syntax that the bounded
scanner cannot distinguish, malformed persisted spans, and malformed calls likewise remain
ambiguous. Simple conditional expressions and `global::` qualifiers are positional expressions
rather than named-argument syntax.
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

`symbol_references.reference_kind` stores raw extractor labels. Default call-graph surfaces (`callers`, `callees`, inspect/analyze caller and callee bundles, and their JSON/MCP fields) expose the canonical public vocabulary `call`, `instantiate`, and `subscribe`. Non-invoking value reads use the canonical `member_read` label and stay out of default callers, callees, and impact traversal; opt in with CLI `--include-member-reads` or MCP `includeMemberReads`. The primary `reference_kind`, `reference_kinds`, and `reference_kind_counts` keys use that same vocabulary. Legacy indexes remain readable but stored these reads as `call`, so they retain the historical inclusive behavior until re-indexed. Use `--raw-kinds` on `callers` / `callees`, or `references --kind <raw-kind>`, when debugging raw extractor output.

`ReferenceRecord.SpanLength` and `symbol_references.span_length` persist the physical matched-token width rather than deriving it from the resolved symbol name; this matters for constructor-chain tokens such as `base`, `super`, and `this`. `DbReader.GetCallees` preserves that span while aggregating counts: it selects the smallest `(line, column_number)` among rows with a stored column, exposes that 1-based pair as `first_line` / nullable `first_column`, and carries the same row's nullable width as `first_length`; `reference_count` remains the independent aggregate. When every contributing legacy row has `column_number IS NULL`, the reader retains the minimum line and a null column. A migrated row can also retain a column with a null span length. CLI/MCP location adapters degrade either case without fabricating precision.

Reference extraction deduplicates only within the same indexed file and language context. When adding extractor paths, include the file id and language hint in shared `seen` keys so same line/column/name edges from polyglot workspaces do not collapse across Java, Rust, C#, SQL, or other language-specific normalization contexts.

| Raw kind | Logical graph kind | Notes |
|---|---|---|
| `call` | `call` | Direct executable invocation edges. |
| `member_read` | `member_read` | Non-invoking member/value reads; excluded from default invocation graphs and included only by an explicit kind filter or the member-read compatibility option. |
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

Post-index TypeScript declaration merging and reference-identity finalization share one graph pass. CLI full scan, scoped update, and MCP indexing defer their earlier mutual-recursion refresh only when clean readiness will definitely rebuild augmentation references; that rebuild deletes and inserts synthetic `augmentation` edges, then finalizes the graph. An empty batch still finalizes deleted edges or an explicitly inherited deferred pass, while marker-only validation with no inserted or deleted edge stamps readiness without scanning the whole graph. If immutable-input validation later makes the run partial, the orchestrator executes the deferred pass before readiness handling instead. An authoritative fresh or rebuild scan with no TypeScript targets stamps the augmentation contract without scanning augmentation rows. This keeps graph publication and retry semantics unchanged while avoiding two whole-graph refreshes on large TypeScript repositories.

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
The CLI normalizes exact-mode aliases and then rejects raw `--fts` combined with
`--exact`, `--exact-substring`, or `--token-boundary` before database dispatch.
This keeps query context and replay output on one matching model.

Search-result symbol attribution uses the primary match line and column when
the index provides declaration coordinates. C# positional-record properties
store the span from the component's first attribute or type token through its
identifier, so record keywords, record type names, base arguments, and body
members remain attributed to their actual enclosing symbols even when they
share a physical line with a positional component. Only `record`, `record class`,
and `record struct` declarations synthesize these property symbols; ordinary C#
class and struct primary-constructor parameters are not member definitions.
Legacy rows without column
metadata retain the line-based fallback. Primary-match selection mirrors the
displayed focus line. Exact-source names are normalized with each result's
language when the query does not specify one, and raw source-column maps are
created only for lines whose escaped names actually change during normalization.

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
| JSON / JSON Lines | JSON emits `object`, `array`, `property`, and bounded primitive-array `value` symbols with indexed paths. Array indexes attach directly to the parent path (`command_cases[0]`, not `command_cases.command_cases[0]`), and object/array parent kinds preserve the hierarchy used by outline depth. Root arrays start at paths such as `[0]`. `.jsonl` and `.ndjson` parse each non-empty physical line independently, prefix symbols with a stable zero-based record path such as `[0].result.path`, and emit repository-local path references from each valid record without flattening malformed neighbors. |
| TOML / repository metadata | TOML tables and keys, EditorConfig sections and keys, Git/Docker ignore rules, Git attribute rules/attributes, and `.rules` blocks/keys are emitted as bounded structural symbols. References are limited to repository-local paths or globs; remote URLs, absolute filesystem paths, and parent traversal are suppressed. |
| CODEOWNERS | Path-aware detection recognizes only the case-sensitive paths `.github/CODEOWNERS`, repository-root `CODEOWNERS`, and `docs/CODEOWNERS`, relative to the enclosing Git worktree root or to the scan root for non-Git input. A dedicated bounded line parser handles full-line and inline comments and emits ordered ownership `rule` symbols and child owner `property` symbols, including ownerless rules, with limits of 4,096 characters per line, 128 owners per rule, and 256 characters per owner. Invalid input, including unsupported escaped leading `#` patterns and malformed mentions, produces category-deduplicated, persistable extraction-diagnostic annotations. No reference extractor is registered because owner authorization and pattern-to-path resolution are outside the syntax-only contract. |
| Windows application manifests | Manifest element paths, assembly identities, execution levels, and supported-OS values remain structural symbols. Dependent assembly identities emit `dependency` references, while local `file`, `codeBase`, and probing paths emit `project_reference` edges. |
| XML / NuGet.config | Generic XML emits bounded element and attribute paths. NuGet.config additionally promotes package sources, source mappings, signature validation mode, trusted signer names, certificate fingerprints, and `allowUntrustedRoot` values to semantic `property` symbols with `nuget.*` subkinds. |

Before the line-oriented regex loop, built-in case-sensitive patterns may opt into one of two mutually
exclusive Tier A gates: a single `RequiredLiteral`, or `RequiredAnyLiterals` for audited alternatives.
Every literal must contain at least two characters and use Ordinal matching. A single literal must be a
substring of every successful regex path; for an any-of set, every successful path must contain at least
one distinct member. A pattern is skipped only when its literal, or every member of its any-of set, is
absent from normalized file content, without changing the order of the remaining patterns. The any-of
form is currently limited to the proved JavaScript/TypeScript HOC family, both quoted and identifier
TypeScript `namespace` / `module` patterns, Kotlin `class` / `object`, and Kotlin `val` / `var`.
`IgnoreCase` patterns, one-character literals, optional or alternative paths without either proof,
project custom patterns, and plugins are deliberately excluded.

Supplemental scans that consult the pattern list, including C# incomplete-attribute recovery and C++
same-line member recovery, must consume the same ordered applicable set. Immediately before each regex
call, the same single-or-any Ordinal proof is applied to the exact input presented to that call, after
transformations such as C# property-header merging, Fortran continuation joining, Java/Kotlin annotation
stripping, C# wrapped-modifier synthesis, C++ same-line segmentation, or CSS selector-brace
reconstruction. An any-of input is skipped only when none of its members is present. A miss behaves like
a failed regex attempt rather than terminating language-specific recovery: notably, a C#
static-constructor pattern rejected on the bare identifier line must still try each synthesized
`static ...` wrapper. The content-wide check may retain a pattern because a required literal appears in
a comment, string, annotation, or another declaration; the exact-input check then recovers that lost
optimization without changing matches. Patterns without either gate, including custom and plugin
patterns, still run unchanged.

C# adds three narrower, proof-preserving gates on top of that general contract. Property-header
lookahead returns before its prefix regexes for empty inputs and completed `;` / `}` lines; a trailing
`=` is skipped only when no `(` is present, preserving multiline default arguments. The built-in
plain-field regex is attempted only when its exact transformed input contains `=` or `;`, which every
successful path must consume. Wrapped-modifier recovery caches both found and absent prefixes for all
function patterns at one scan offset, rejects predecessor lines with characters outside the regex's
lowercase-ASCII-plus-whitespace alphabet, and materializes a confirmed prefix once in forward order.
These are private built-in optimizations: pattern/plugin APIs, symbol fields, diagnostic output,
cancellation points, and same-line column mapping remain unchanged.

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

Watch-generated batches enter this runner as internal `--files` calls.
`RunPartialUpdate` suppresses output only when such a sub-run returns
`UsageError`, including directory and unsupported event targets: it discards
the captured rejection, including any spooled JSON output, emits no failed
watch event, and invokes exactly one no-`--files` full-workspace rescan with the same
`startup` or `incremental` phase. It records that rescan's exit code and returns
without running later partial batches. Other sub-run exit codes retain their
normal reporting. This fallback prevents valid sibling changes in an event
batch from being stranded by a transient path-shape race.
Polling snapshots resolve immediate and final symlink/reparse targets before
adding a path. Aliases of the configured database, SQLite sidecars, lock/info,
checkpoint, restore/backup, and atomic-temporary artifacts are excluded, as are
ancestor-ignore aliases that resolve to those artifacts. Ordinary file and
directory symlinks allowed by `internal` or `all` remain tracked. Directory
subtrees use full-scanner depth-first lexical alias selection, resolve descendant
paths only while beneath an alias, and deduplicate resolved directory identities
to bound cycles and duplicate targets.

`--commits` uses `git diff-tree --no-commit-id -r --name-only` to resolve changed file paths.
`--changed-between` uses `git diff --name-status -M <old-ref> <new-ref>` and includes both old and new rename paths so stale indexed paths can be purged.

Git-scoped refreshes compare the requested range with the persisted whole-workspace verification baseline (`workspace_verified_head_sha`, with a conservative `indexed_head_commit` fallback for legacy databases). When the baseline is older than or divergent from the supplied old ref and the new ref is the current HEAD, the resolver unions the baseline-to-current paths with the caller range before indexing. Every scoped mutation precommits its affected-path set, and the next baseline-reconciled Git refresh unions those paths even when the net commit diff is empty. If that bounded set is incomplete or a full scan is partial, scoped verification advancement fails closed until a normal full scan succeeds. If the persisted baseline is no longer resolvable, the command stops with fetch/full-workspace-refresh guidance instead of publishing incomplete freshness. A successful full scan or reconciled Git refresh advances `workspace_verified_head_sha`; `indexed_head_sha` still records the latest successful scoped or full write, while `indexed_head_commit` remains the full-scan compatibility baseline. Explicit `--files` updates preserve a current verification stamp but cannot advance an older one. Success metadata is committed together in the final update transaction, so failed, partial, cancelled, or rolled-back runs cannot advance verification provenance. When verification and latest-write HEADs differ, the nested `head_freshness` object omits the latest-write branch, timestamp, and ahead count rather than attaching them to the older verified HEAD; the top-level latest-write fields remain available.

Watch batches reuse this partial-update runner with the top-level cancellation token. Ctrl-C, SIGTERM, or an embedding-host token remains active after the initial scan, so it interrupts idle watch waits, active extraction, FTS recovery/rebuild/optimization, and SQLite planner maintenance instead of waiting for a sub-run to finish. Watch does not install a second console handler, preserving the top-level first-Ctrl-C cooperative / second-Ctrl-C force-exit contract. A cancelled bulk FTS completion restores synchronization triggers and leaves an owner-independent recovery marker when its transaction did not roll back the marker. Long-lived MCP write contexts register each request token to suppress dispose-time planner maintenance after cancellation. Sub-run JSON is routed through `CommandOutputWriter`'s async-local scope into the watch capture writer; the watch loop never replaces `Console.Out`, so other commands and embedding hosts retain their own stdout.

Source membership is shared through `FileIndexer`: full scan, workspace freshness checks, and watch all exclude the `.cdidx` namespace. Watch classifies ignore files plus `.cdidx/patterns/**` and `.cdidx/plugins/**` before applying that source filter, so those non-source inputs remain debounced reconciliation events while ordinary `.cdidx` sidecars stay excluded. Before either an event-driven enqueue or polling snapshot admits a path, watch also rejects the exact resolved database, WAL/SHM/journal sidecars, index lock, lock-info file, and each target's `AtomicFileWriter` sibling temp form. That temp matcher derives its target-based prefix from the writer's shared filename pattern and uses the workspace path comparison; it does not blanket-ignore `.tmp` or `.cdidx-*`, so unrelated user files still follow normal source membership. Subdirectory watches normally add non-recursive, ignore-file-only watchers for each ancestor directory through the repository rule root. On macOS/.NET 8, the project tree remains on FSEvents while those exact ancestor `.gitignore` / `.cdidxignore` paths use bounded polling to avoid the runtime's silent ancestor-event miss; recursive project polling remains the failure-recovery backend. The resulting `--files` sub-run recognizes extractor inputs, refreshes the process registry generation, and falls back to a full scan that disables unchanged-file reuse so every retained source row is extracted with the new generation. Refresh unloads file-discovered workspace plugins and patterns before loading the current generation, while retaining extractors explicitly registered by an embedding host, so edits and deletions cannot leave extension membership or persisted rows stale.

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

For `impact`, the `file_impacts` collection derives its allowed leaves from
`FileDependencyResult`: `result_kind`, `source_path`, `target_path`,
`source_db`, `target_db`, `reference_count`, `ranking_score`, `symbols`, and
`evidence`. Compact file-impact rows retain `source_path`, `target_path`,
`reference_count`, and `result_kind`, so every non-empty row remains
self-identifying. The ambiguous `file_impacts.path` and `file_impacts.file`
aliases are rejected; callers and definitions keep their established path
aliases.

`inspect` keeps a dedicated typed schema in the same registry because its
established JSON bundle is not a shared bounded-response envelope. It accepts
top-level groups and exactly one `collection.field` level for definitions,
nearby symbols, references, callers, and callees. Inspect selectors normalize
case and hyphens, resolve aliases before first-occurrence deduplication, and
preserve canonical request order. A selected parent dominates its children and
keeps complete rows; otherwise the row projector emits only selected leaves.
Projection runs before final serialization and byte budgeting while preserving
root metadata, section totals/cursors/truncation, partial-family metadata, and
definition body paging/recovery fields. The queryless `inspect --fields list`
catalog and unknown-field errors are generated from that same schema.

| Output mode | Contract |
|---|---|
| Human-readable default | Query commands (`search`, `definition`, `references`, `callers`, `callees`, `symbols`, `files`, `excerpt`, `map`, `inspect`, `outline`, `suggestions`) default to **human-readable output**. |
| `--json` | Emits JSON lines output, one JSON object per line, designed for easy parsing by AI agents. |
| Delegated audit command identity | `audit` delegates recipe execution to `search` internally while retaining the public `audit` identity in human usage, recovery hints, and generated replay commands, except that compact results-only replays use the canonical results-only-capable `search --recipe` entry point. Explicit `audit --json` usage errors emit stable versioned command-error objects with `command: "audit"` and no human-readable `usage`; direct `search` errors retain `search` identity. Late response-budget failures use the same invocation context, so `command`, `usage`, `retry.command`, and audit-specific recovery guidance cannot mix `audit` and `search` identities. |
| Recipe issue-draft summary | `search --recipe ... --format issue-drafts --summary-only` and its `audit` alias use a dedicated summary DTO instead of rendering full issue bodies. Each positive recipe query contributes one compact `drafts[]` row with query/title identity, result and file counts, at most five counted evidence paths plus explicit path omission metadata, labels, severity, confidence, optional result cursor, and a full-detail replay command. The root reports `total_count`, its authority/lower bound, `returned_count`, `omitted_count`, `truncated`, and an uncapped summary `recovery_command`. `--max-json-bytes` measures the exact UTF-8 document plus its final newline and removes only complete trailing summary rows; a cap below the zero-row envelope returns typed `E028_RESPONSE_BUDGET_TOO_SMALL` guidance under the invoked `search` or `audit` identity. Full issue-draft mode retains the established recipe metadata, evidence, source, triage, and rendered body shape. |
| `definition --json` miss | A default-format definition lookup that finds no matching symbol emits the shared versioned `E018_QUERY_NOT_FOUND` command-error object and exits `2`, with or without `--body`; it never succeeds with empty stdout. Bounded-envelope controls move the object to `metadata.error` and keep `results` empty instead of projecting it as a location row. The object is preflighted against `--max-json-bytes`; an impossible cap returns a usage error without oversized stdout. `--count` still returns its structured zero-count object, and explicit location formats retain their existing format-specific empty-result output. |
| Raw discovery JSON shape | `symbols` and `files` build each result row through the same DTO path for array, NDJSON, and envelope output. `symbols --json=array` therefore preserves `exact_index_available` just like NDJSON. Every cardinality and `--max-json-bytes` path keeps the selected flat shape: zero-result NDJSON is an empty stream, `--json=array` is always an array, and byte-capped output omits whole trailing rows without changing the top-level type. Bounded projections keep rows in `results`, pagination facts in `metadata`, and exact-query readiness in `metadata.response_context`; they never reuse a result row as response context. Use `--format compact` or `--json-envelope` when truncation and freshness metadata must accompany the results. |
| Generated-code filtering metadata | DB-backed discovery `query_context` always reports `include_generated`, `generated_code_policy`, and `generated_file_filter_available`. The `files --count --json` and every JSON `map` summary (including `issue-drafts`) also report `generated_file_count_excluded` and `generated_file_count_excluded_authoritative`. The excluded count is `0` when generated files are included. For a legacy DB without `files.generated` when filtering is requested, the policy is `unavailable`, the count is `null`, and the authoritative/available flags are `false` rather than claiming that an unavailable filter ran; explicit `--include-generated` remains `include` with an authoritative excluded count of `0`. Byte-capped and uncapped raw discovery arrays retain SQLite trust diagnostics even when the query returns no result rows. |
| Map scope, depth, and freshness | `map --depth <n>` applies path, language, test, generated-code, and exclusion filters before aggregating modules by the requested prefix depth. Scoped map output excludes the workspace-global decomposition plan. Workspace HEAD metadata is read in one query from the same SQLite snapshot as the map and remains explicit under `head_freshness`: `scope=workspace`, `indexed_head_source=latest_index` for the current successful index stamp (or `legacy_full_scan` only when it is the fallback), and `legacy_full_scan_head` for the separately labeled compatibility stamp. `issue-drafts` evaluates every scoped file for its thresholds, so `candidate_source=evaluated_scoped_candidates`, candidate counts, group totals, omitted counts, and `truncation.issue_draft_candidates` are candidate-based even though candidate details remain bounded; `truncation.largest_files` is a labeled compatibility alias only. |
| `test-extractor` JSON | Machine-readable `test-extractor` success uses a versioned `{"api_version":"1","symbols":[...]}` envelope; the nested symbol objects retain their established property names. `--json` failures use the shared versioned command-error contract. |
| Database diff categories | Every `diff` summary reports `data`, `schema`, `readiness_provenance`, and `volatile_telemetry` entries with `evaluated`, `included`, `different`, `reason_count`, and stable `reasons`. Default `semantic` status excludes volatile telemetry; `--data-only` also excludes readiness/provenance, while `--include-telemetry` explicitly includes volatile run metadata. `summary.difference_reasons` is the complete included reason set and must be non-empty whenever the result is non-identical. |
| Bounded database comparison | `diff` normalizes local paths and SQLite file URIs before checking filesystem identity, so the same readable database—including a sidecar-free symlink or URI alias with equivalent SQLite snapshot semantics—returns an identical result after one header read instead of enumerating rows. A plain path and an immutable URI remain distinct when an active WAL can make their snapshots differ, and different hard-link/symlink paths with path-specific sidecars fall back to snapshot comparison. Distinct sidecar-free files use a fixed-buffer byte comparison and short-circuit when they are exact copies; files with SQLite sidecars and all non-identical inputs continue through the bounded streaming row comparison. If that comparison reaches its row or per-row byte budget during `import --check`, JSON mode returns one typed `import` error with `error_code=import_destination_comparison_budget_exceeded` and `root_cause=comparison_budget_exceeded`, while text mode prints the same actionable retry guidance. |
| Compact location envelope | CLI `--format compact` location output uses a versioned envelope with `api_version`, returned `count`, conservative limit-based `truncated` / `truncation` metadata, applied `query_context`, and lightweight `results` rows. |
| Grouped search totals | `search --format grouped` derives `total_matches` / `matched_count`, `total_groups`, and `total_files` from the complete bounded query rather than the displayed page. `grouped_match_count` counts rows supplied to returned groups, `emitted_match_count` counts rows left after per-file grouping limits, and `omitted_match_count`, `truncated`, `has_more`, and `continuation_action` describe incomplete output. |
| Bounded high-volume responses | `search`, `definition`, `find`, `status`, `hotspots`, `references`, `callers`, `callees`, `symbols`, `files`, `languages`, `impact`, and `map` accept shared bounded-response controls where their schema exposes them. Newly emitted opaque `--cursor <response:v2:...>` values bind the offset to the command/query/filter selection and index generation; legacy `response:v1:<offset>:<fingerprint>` cursors remain accepted for transition. Reuse with changed selection or generation fails with restart-required guidance. `search --format compact`, `symbols --format compact`, and `files --format compact` auto-select the bounded contract, while `search --json=array --json-envelope` provides the opt-in array envelope and `languages --json` selects it when paging or `--max-json-bytes` is requested. Existing compact roots and location rows remain compatible while adding shared metadata. Metadata reports `returned_count`, authoritative `total_count` where available, `omitted_count`, `remaining_count`, `cursor_offset`, `page_limit`, `has_more`, `next_cursor`, `result_stable_at`, `pagination_window_limit`, and `pagination_window_exhausted`. The safety window is 10,000 rows; exhaustion suppresses `next_cursor` rather than returning a cursor that the next request would reject. Pageable commands pass the cursor offset into their database/scan layer instead of serializing an `offset + limit` prefix. `find --all` partial scans encode the next path/line in the opaque cursor so replay continues after the last scanned line. `hotspots` and `impact` page their active primary nested collection as `results`, identify it with `metadata.primary_collection`, and retain scalar/container evidence in `metadata.response_context`; dotted fields such as `callers.path,callers.depth` select that collection and project its rows. The final newline is included in `--max-json-bytes`, and trailing whole rows are removed until the complete envelope fits. `definition` remains metadata-only by default; explicit `--body` content is retained for `body`, `body_content`, or `all`, and suppressed when the projection excludes it. `map --sections` remains its section-level projection, while dotted bounded fields page a selected array section with section-specific totals and scalar projections skip unused ranked arrays. |
| Grouped partial-family continuation | A grouped `symbols` row reports authoritative family-member total, returned, omitted, and remaining counts while materializing at most 50 members. Its opaque recovery/next cursors bind the normalized symbol selection, fixed-size family ID, member offset, and index generation; they never embed the potentially unbounded family key. SQL applies the family-ID filter and member offset before JSON aggregation, so continuation never loads the complete family into managed memory. Compact and projected output preserve the recovery metadata even when they omit `family_members`; array, NDJSON, envelope, and byte-bounded paths serialize the same row contract. |
| Bounded outline responses | `outline` opts into the shared bounded-response contract only when `--max-json-bytes` is present. The wrapper extracts complete projected symbol rows, preserves hierarchy and deterministic order, reports authoritative returned / total / omitted counts, includes the final newline in its UTF-8 measurement, and emits a bound `response:v2` continuation cursor. An undersized minimum envelope produces one typed `E028_RESPONSE_BUDGET_TOO_SMALL` object on stdout with empty stderr and actionable byte fields. Uncapped outline JSON retains its existing root shape and outline cursor contract. |
| Bounded unused responses | `unused` opts into the shared bounded-response contract only when `--max-json-bytes` is present. The wrapper extracts the canonical `symbols` rows, applies the cursor offset in the unused query layer, recomputes returned bucket / confidence / contract-domain counts after byte trimming, and includes an optional `by_bucket` view in the same whole-response UTF-8 budget. Compact mode projects smaller audit rows. Continuation cursors bind the effective audit filters, bucket mode, ordering, and index generation; an undersized one-row envelope returns one typed `E028_RESPONSE_BUDGET_TOO_SMALL` object on stdout with empty stderr and actionable byte fields. Uncapped JSON, compact summaries, and the legacy unused cursor remain unchanged. |
| MCP outline pages | MCP `outline` routes `fields`, `sort`, `limit`, and `cursor` through `QueryCommandRunner.BuildOutlinePage`, so its projection aliases, derived sort fields, stable tie-breakers, `page:v1` query fingerprint, and generation validation remain the CLI outline contract rather than a second MCP-specific implementation. The default page is 100 rows and the MCP-wide maximum is 200. `maxBytes` measures the fully enriched serialized `structuredContent`; a binary search rebuilds the page with fewer complete rows and therefore regenerates `next_cursor` from the actual returned count. A budget that cannot hold metadata plus one row fails instead of returning a zero-progress cursor. Default MCP symbol serialization remains backward-compatible, while explicit projection fields use the CLI snake_case names. |
| Bounded response edge cases | `impact` applies the cursor offset only to the selected nested collection so definition pages do not repeat or alter caller/fallback mode. Plain `map --compact` preserves its established section arrays and truncation payload; a collection projection is rejected when `--summary-only` or an excluding `--sections` filter would remove it. Explicit definition body fields override compact defaults. Profile and verbose records are moved into `metadata.stream_control_records`. Parser/capture error envelopes use the normal cap when they fit; otherwise a complete `E028_RESPONSE_BUDGET_TOO_SMALL` diagnostic replaces them so machine output never becomes empty or malformed. |
| `--count --json` envelope | Count-only JSON for `search`, `definition`, `references`, `callers`, `callees`, `symbols`, `files`, `find`, `impact`, and `unused` is a single automation-oriented object. It always includes `count`, applied `query_context`, freshness metadata (`indexed_file_count`, `indexed_at`, `freshness_available`), and trust flags `degraded` / `authoritative_count`; commands with matched-file totals also include `files` and the older `file_count` compatibility alias. `file_count` carries the same value as `files`, remains for compatibility, and is not scheduled for removal before the next major release. `unused --count --json` also includes `returned_bucket_counts`, `returned_contract_domain_counts`, and `summary.by_bucket` / `summary.by_confidence` / `summary.by_contract_domain`. `authoritative_count=false` means a readiness or graph/exact trust signal made the count non-authoritative, while the freshness fields describe the indexed snapshot used for the count. |
| Search row selection | Row-producing plain-search and recipe paths share `ApplySearchOutputSelection`: `--first-per-file` and fixed-seed deterministic `--sample` run before the effective per-query / remaining total limit. Sample fetch envelopes are sized from at least the requested sample target. Aggregate/compact query DTOs, plain compact roots, run summaries, issue-draft source DTOs, NDJSON terminals, and bounded array-envelope stream terminals expose `source_total`, `selected_total`, `returned`, `selector_omitted_count`, and `limit_omitted_count`; `source_total_authoritative` / `source_total_lower_bound` distinguish complete populations from bounded observations. Guard filters, origin/facet post-filters, exhausted candidate windows, and recipe file-reject post-filters force lower-bound authority. Their ordered `selectors` entries preserve each stage's input/output/omission counts plus sample size, mode, and seed, while nullable `selection_reason` / `selection_omitted_count` remain compatibility summaries. Bounded plain-search selection is computed once and its selected page is reused by compact/envelope serialization. Search `query_context.row_selectors` records the applied selector configuration. Selection-only omission updates matched/omitted lower bounds without setting `truncated`, `has_more`, or `next_cursor`. When a later limit truncates selected rows, limit truncation remains visible but `next_cursor` is suppressed because raw database cursors cannot preserve selector state; incoming `--cursor` values are rejected with either selector for the same reason. Generated compact and issue-draft replay commands retain the selector. Compact results-only replays always use the results-only-capable canonical `search --recipe` entry point and render structured argv with current-shell-safe quoting. Count, aggregation, named-query, recipe-list, results-only, metadata-free array, unsupported formatted, and summary-only compact shapes reject `--first-per-file` / `--sample`, while every recipe shape rejects grouped-only `--per-file-limit`. |
| Search selection edge cases | Issue-draft roots independently retain per-query `selection_accounting`, including zero-draft and exhausted-total-limit queries. Byte-bounded compact and array envelopes rewrite `returned` to the emitted row count while preserving logical `limit_omitted_count`; hard-cap omissions remain separate in `metadata.byte_limit_omitted_count`. |
| Ad-hoc search SARIF | `search <query> --format sarif` stores completion metadata on each SARIF run. The run and its single `queries[]` summary report `source_result_count`, `source_result_count_authoritative`, emitted `result_count`, the applied `limit_per_query` / `result_limit`, conservative `minimum_omitted_result_count`, and `truncated` state. Source and emitted counts use the final SARIF result/location unit, including exact-search occurrence expansion. Guarded searches retain their bounded candidate budget instead of failing during a completion recount; their source count is an explicitly non-authoritative lower bound and their truncation state remains conservative. Facet-filtered exact searches use an exhaustive source count rather than the display candidate window. Ad-hoc search does not expose a continuation cursor, so `cursoring_available` is `false` and `next_cursor` is null; a shell-quoted `replay_command` preserves option-like queries and active search controls. The completion vocabulary intentionally matches recipe SARIF, and empty runs carry the same fields with zero counts. |
| Ad-hoc issue-draft selection | `search --format issue-drafts` reads the complete filtered ad-hoc population, then applies `--first-per-file`, deterministic `--sample`, and `min(--limit, --total-limit)` in that order. Guarded searches retain their finite candidate inspection contract: `source_total_count` is omitted, `source_minimum_count` reports the observed lower bound, `source_total_count_authoritative=false`, `source_fetch_limit` reports the bounded fetch, and `truncated=true` preserves incomplete-population state. Existing `result_count`, `result_limit`, `omitted_count`, and `truncated` fields describe the returned selection accurately; additive `source_total_count`, `returned_count`, `limit_per_query`, `total_limit`, `first_per_file`, and `sample` fields make the applied contract auditable. Replay commands are serialized from normalized parsed options, use POSIX-safe single-quote escaping, and retain raw/exact/prefix modes, path/language/facet/guard filters, selection controls, evidence formatting, duplicate preflight, and issue hints. |
| Recipe SARIF | `search --recipe <name> --format sarif` emits one result per bounded recipe result. Rule IDs use `recipe/query`; standard `fingerprints.cdidx/v1` values are derived from the normalized source location; result properties preserve recipe/query identity, severity, confidence, and per-query truncation; run properties preserve scope, applied result limits, aggregate counts, and conservative omitted-result metadata. `--max-json-bytes` preflights the complete schema-valid document and exact UTF-8 byte count, including escaping and the final newline, through a counting writer before materializing only the selected prefix. If the full document does not fit, serialization omits only whole trailing results and adds run/query source, emitted, omitted, byte-strategy, and replay metadata; emitted rules and locations remain intact. This truncation exits `11` unless `--allow-partial` is set. A cap below the zero-result document minimum emits no SARIF and reports the required bytes; explicit `--json` may place a versioned error object on stdout when that object fits the cap. Replay metadata removes the byte cap when a complete report exceeds the parser's maximum supported cap. Bound SARIF with `--limit` / `--total-limit`; row selectors such as `--sample`, `--first-per-file`, and `--per-file-limit` are rejected instead of being silently ignored. Recipe severity maps `critical` / `high` to `error`, `medium` to `warning`, and `low` / `info` to `note`. |
| Recipe classifier output | Recipe run JSON may add `audit_classifications` to individual `CompactSearchResult` rows when a recipe classifier can classify the hit, and query/count payloads may add `classifier_counts` when classified rows are present. These fields are additive; use them to separate triage domains such as DTO/result-wrapper `.Result` properties versus Task/ValueTask blocking waits without changing the raw search query. JSON read/write recipes also classify source-proximate `cdidx-audit: json-trust` annotations by origin, direction, sensitivity, trust, and rationale. Classification reads bounded indexed source rather than the projected snippet, including for guard-projected rows, and lexically verifies a real C# line comment so regular/verbatim/raw string contents and conditional-compilation regions cannot provide trust evidence. It evaluates every retained match site after overlap deduplication, collapses declaration-type facets—including expression-bodied method/local-function return types and generic return types split before the audited type—when the same containing statement has a later constructor facet across the same or following lines, consumes each annotation at the first lexical audited match across all selected JSON child queries, and binds it only to the next operation by parsing C# tokens. Annotation lookup searches the bounded indexed prefix rather than a fixed line gap. Nullable declarations, direct casts, nested-generic first arguments, and declaration-resolved direct receivers remain valid while an earlier statement, evaluated operand, indexer target, unresolved bare receiver, one-hop/chained property-valued assignment or invocation receiver, preprocessor directive, completed expression, control-flow block, or comma-separated operation is rejected as `not_adjacent`. Rows with distinct evidence remain conservatively `mixed_boundaries`; missing, invalid, direction-mismatched, or `review_required` evidence remains `ambiguous_trust`. The classifier groups rows by file and reconstructs the maximum required bounded prefix once; its per-query lexical cache retains only that one file prefix, records the prefix actually reconstructed, and remembers source-budget exhaustion so high-line matches neither repeat reconstruction nor poison a lower-line result. For `json-parse-apis`, every retained structured or compact row receives exactly one `parser_guard_evidence` classification from lexically masked containing-symbol context. A byte/depth/item/file-size bound must relate to the consumed payload and takes precedence over streaming/cancellation for the same operation; otherwise a streaming/cancellation signal or the non-authoritative `unbounded_materialization` fallback is emitted. If one row represents multiple operations, any unbounded operation keeps the row unbounded. Compact rows preserve the same classification evidence used for `classifier_counts`. Classification never removes or reorders the raw result. Source-backed classification runs only for JSON/NDJSON/compact/count-JSON shapes that serialize row classifications or classifier counts; text, scalar count, compact summary, SARIF, issue-draft, and `--search-fields` projection paths skip it. |
| NDJSON terminal records | Default NDJSON for `search`, `symbols`, and `files` appends one final `terminal_record` after result rows; search also emits it for zero-result responses, while raw `symbols` and `files` keep zero-result NDJSON empty. Recipe/audit search row streams share the same writer. Terminals report returned and observed total counts, `total_count_authoritative` / `total_count_lower_bound`, selection or interruption reason, applied limits, omitted rows, and recovery guidance. A raw cursor-capable stream with `has_more: true` and at least one emitted result adds a shared generation- and query-bound `response:v2` `next_cursor` whose offset advances by the actual emitted count. Replaying it with unchanged filters and ordering selects the bounded envelope; its `metadata.stream_terminal` preserves the same continuation. Final and zero-result pages omit continuation fields. A partial terminal that cannot safely advance instead omits the cursor and adds machine-readable `next_cursor_unavailable_reason`: `no_result_row_emitted` covers a terminal-only byte-capped response; `stream_not_cursor_capable` covers recipe/named or row-selector streams; `pagination_window_exhausted` covers the 10,000-row response window; `index_generation_changed_during_query` fails closed when the generation captured before row materialization differs from the generation used to encode the cursor; and `index_generation_unavailable` covers failure to obtain either comparison snapshot. `--max-json-bytes` covers the complete stdout stream, including newlines, cursor, and terminal record; each byte-fit candidate regenerates the cursor from its emitted count, and optional selector accounting is dropped before declaring the terminal impossible. A cap that still cannot fit the terminal fails before stdout. Capped output rejects `--profile`, `--verbose`, and `--json-envelope`. Byte-cap partial output exits with `CommandExitCodes.PartialResult` (`11`) unless `--allow-partial` explicitly opts into exit `0`. `--results-only` is the explicit terminal-record opt-out for these NDJSON row streams and is rejected with array, compact, summary, or count output. |
| C# outline callable display | `DbSymbolReader.Outline` derives `display_name` only at read time and never changes canonical `symbols.name`, qualified paths, folded identity, or exact-query aliases. A complete C# generic method signature normally uses arity placeholders (`<T>` or `<T1, T2, ...>`); if one would collide with a concrete parameter type, it deterministically selects collision-free `TArg` placeholders. Replacement applies only to unqualified method-type-parameter references, preserving qualified concrete types and escaped-keyword distinctions. Literal-aware scanning keeps delimiters in attributes and default values from changing parameter boundaries. The display omits `where` constraints, drops non-identity `this` / `params` / `scoped`, and retains overload-significant `ref` / `out` / `in` (including `ref readonly`). Non-generic and non-C# formatting stays on the existing path. Missing, truncated, or syntactically incomplete persisted signatures retain the legacy `Name@line` fallback for old-index compatibility. |
| `outline` / `unused` cursor binding | `outline --json` accepts `--kind <kind[,kind]>`, `--limit` / `--top`, opaque `--cursor <next_cursor>`, and `--outline-fields <csv>` for bounded machine output. Controlled outline responses keep the normal envelope and add `total_symbol_count`, `returned_symbol_count`, `cursor_offset`, `next_cursor`, `has_more`, and `result_stable_at`, plus `kind_filter` and `selected_fields` when active. Projection parsing canonicalizes aliases and removes duplicates before validation; unknown field names are reported together as one terminal usage error with valid candidates, while the empty-selection error is reserved for deliberately empty CSV input. `outline` and `unused` cursors bind their offset to the normalized path/scope, filters, ordering, and index generation; reuse after changing those inputs or refreshing the index fails with explicit restart-required guidance. Legacy `outline:<offset>` / `unused:<offset>` inputs remain accepted for transition, but every newly emitted cursor is opaque and bound. |
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
| Unknown-extension and runtime diagnostics | `unknown_extension_file_count`, `unknown_extension_files`, `unknown_extension_files_truncated`, `unknown_extension_file_path_limit`, `unknown_extension_extension_counts`, `unknown_extension_category_counts`, `unknown_extension_groups`, `unknown_extension_group_count`, `unknown_extension_groups_truncated`, `unknown_extension_group_limit`, `unknown_extension_group_omitted_count`, `unknown_extension_guidance`, `extractors`, `hooks`, `hook_diagnostics`, `trust_overrides`, `path_case_sensitive`, `data_dir_mode`, `mac_profile`, `mac_profile_diagnostics`, `stale_after_seconds`, `index_age_seconds`, `query_context.check_mode`, `query_context.stale_after_seconds`, `last_index_run.reference_extraction_cap_hits`, `last_failed_or_partial_index_run`, `last_failed_or_partial_index_run.progress_persisted`, `last_failed_or_partial_index_run.recovery_hint`, `last_failed_or_partial_index_run.file_errors`, `status_metadata_diagnostics`. |
| Database maintenance | `db_size_bytes`, `wal_size_bytes`, `db_pragma_settings` (`journal_mode`, `synchronous`, `wal_autocheckpoint`, `busy_timeout_ms`, `page_count`, `freelist_count`, `page_size`, `auto_vacuum`), `prepared_command_cache` (`count`, `capacity`, `hit_count`, `miss_count`, `eviction_count`), `maintenance_guidance`. |
| Database size attribution | `database_size_attribution` (`available`, `measurement`, `unavailable_reason`, physical main/WAL/SHM sizes, logical/object/freelist/residual reconciliation, page-type and payload/overhead subtotals, and bounded `top_objects`). |
| Remediation fields | `degraded_root_cause`, `degraded_reason`, `recommended_action`, `alternative_action`, `readiness_degradations`, `repair_commands`. |
| MCP-only session diagnostics | `mcp_session`, `mcp_session.metrics`, `mcp_session.audit_log`, `mcp.rate_limit.bucket_limit`, and `mcp.rate_limit.bucket_limit_rejection_count`. `mcp_session` is session-scoped diagnostics rather than persisted DB state. It contains `log_level`, bounded `roots`, optional `client_info`, bounded optional `client_capabilities`, an always-present `metrics` object, and `audit_log` when audit emission is enabled. When advertised roots are capped, `roots_truncated`, `root_count`, `root_limit`, and `root_uri_length_limit` describe the truncation. When client capabilities are capped, `client_capabilities_truncated`, `client_capabilities_truncation_reason`, `client_capabilities_serialized_bytes`, `client_capabilities_byte_limit`, and `client_capabilities_depth_limit` describe the retained diagnostic subset. `mcp_session.metrics` is `{"enabled":false}` when unconfigured. An enabled metrics sink contains `enabled`, `path`, `max_bytes`, `bytes_written`, `disposed`, `degraded`, `queue_capacity`, `queue_depth`, `queued_event_count`, `written_event_count`, `dropped_event_count`, `queue_full_drop_count`, `serialization_failure_count`, `write_failure_count`, `rotation_failure_count`, `batch_flush_count`, `consecutive_failure_count`, and `recovery_count`, plus optional `next_retry_at`, `last_recovery_at`, and `last_failure`. MCP ping always mirrors the metrics object as `metrics`; metrics degradation is intentionally excluded from its top-level liveness result. The audit status fields and their health semantics are defined in [MCP audit log emission](#mcp-audit-log-emission). `mcp.rate_limit.bucket_limit` is the configured process-local cap across normalized `(partition, caller)` buckets: every direct call uses one fixed caller-wide coarse partition, canonical known tools additionally use secondary per-tool partitions, and unknown `batch_query` slots share one fixed invalid-slot partition per caller. `mcp.rate_limit.bucket_limit_rejection_count` counts calls denied because creating a new bucket would exceed that cap. |
| Documentation sync | Keep this list synchronized with `README.md` and `AGENT_GUIDE.md`; `DocumentationStatusContractTests` fails when any required field is missing from one of those docs. |

The persisted status subdocuments `last_index_run.reference_extraction_cap_hits`,
`last_index_run.rebuild_reclaim`, and
`last_failed_or_partial_index_run.file_errors` are selected through a bounded
SQLite accessor that returns no managed string when the stored UTF-8 value is
larger than 512 KiB. `BoundedJson` then parses them at maximum depth 16 before
the reader revalidates the persisted semantic contract: at most 50 file errors,
50 cap-hit files, and 16 nested reasons; at most 32,768 characters per path,
128 per category/phase/reason code, 4,096 per detail/rebuild reason, and 262,144
decoded string characters per subdocument. A rejected subdocument is omitted
without failing the rest of status. `status_metadata_diagnostics[]` reports its
field, `max_utf8_bytes`, optional `observed_utf8_bytes`, and one stable reason:
`raw_size_exceeded`, `invalid_json`, or `semantic_validation_failed`. Human
first-failure and recovery-hint fields pass through `ConsoleUi.FormatBoundedValue`
so persisted controls cannot create extra lines and long values use the stable
truncation marker; structured JSON retains accepted values unchanged.

`status --explain` resolves top-level keys through the source-generated
`StatusResult` `JsonTypeInfo` used by `status --json`; ignored properties are
excluded, and a coverage test requires every serialized top-level property to
produce an explanation. Explicit registry metadata supplies useful meaning,
source, dependencies, interpretation, and repair guidance for major readiness,
trust, extension, maintenance, and cap-hit sections. Other serialized scalar
fields receive a bounded contract explanation instead of becoming unknown as
the DTO evolves. Dotted paths resolve against the same source-generated nested
metadata (including collection element DTOs), while unknown paths receive
bounded valid candidates. Explain responses contain static contract metadata
only, cap known fields and dependencies, sanitize unknown input, and never
include runtime field values or paths. Regular JSON retains that full payload.
`--compact`, `--format compact`, and bounded output without explicit `--fields`
project the typed explanation to `api_version`, `field`, `meaning`,
`interpretation`, and `remediation` before applying the global byte bound. The
envelope sets `explanation_schema=compact`, publishes
`explanation_required_fields`, and accounts for removed optional content with
`explanation_omitted_optional_field_count` and
`explanation_omitted_optional_fields`. Explicit `--fields` remains an
operator-selected projection. If the complete envelope plus one compact
explanation cannot fit, the command returns `E028_RESPONSE_BUDGET_TOO_SMALL`
with measured minimum-size and retry guidance instead of an empty success.
Bounded envelopes also omit database paths, timings, indexed HEADs, and
stable-at timestamps.

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

For `references`, `callers`, and `callees`, an explicit `--snippet-lines` is valid only with `--body` and text or JSON result output. Location-only formats and `--count` reject the option before opening the database so a requested snippet length is never silently ignored or recorded in replay/query context without being applied. Explicitness comes from the argument parser, so option-like literals passed through `--query` or after `--` remain queries. Bounded JSON projection also removes the snippet-only control from its internal count replay, preserving clean stderr, total counts, and cursors for visible body excerpts.

Bounded `--fields` output is a generic JSON projection contract and can be combined only with `--format json` or `--format compact`; projection-incompatible formats such as CSV, TSV, quickfix, LSP, and SARIF are rejected with `E010_USAGE_ERROR` before the database is opened. For `references`, `callers`, and `callees`, a collection-bearing zero-result document is unwrapped authoritatively so an empty collection remains empty and its result, returned, and total counts remain zero. Selecting `body` or `body_content` automatically retains the companion start/end, requested/effective range, truncation flag/reasons, next-start, omission, and recovery fields. A byte budget omits the whole row or returns `E028_RESPONSE_BUDGET_TOO_SMALL` rather than stripping those companion fields.

For a semicolon-form C# positional `record`, `record class`, or `record struct`, the indexed body range spans the complete declaration from its record line through the terminating `;`, including declarations longer than the lightweight 64-line signature lookahead. Consequently, `definition --body` returns declaration content with the same bounded line/byte truncation and recovery metadata as other bodies, including under `--fields` projection; when another declaration shares either boundary line, the excerpt is column-clipped from the record declaration start through its terminator instead of exposing the enclosing or following sibling text, even when a multiline string closes earlier on the start line. Recovery hints preserve those boundaries with optional 1-based inclusive `--start-column` / `--end-column` arguments on `excerpt`, so replaying a line- or byte-cap recovery command cannot expose sibling text. Braced records retain their brace-derived body range when generic constraints or `allows ref struct` anti-constraints wrap after `:`, whitespace-qualified base or constraint types such as `N . Base` and `Alias :: Base` remain declaration continuations, and preprocessor-directive lines within those continuations act as lexical whitespace—including when a directive payload itself contains `;` or `{`—while an incomplete record does not consume a following type or member declaration, including an instance constructor after a completed base-list entry, explicit-interface method or indexer, or fixed buffer. A semicolon record's same-line signature and reference scope stop at its terminator, including when another declaration precedes it, when a parameterless or positional record's `;` starts the next line, when directives separate its name or generic arity from the rest of the header, when whitespace separates its name from generic arity, when a source Unicode escape spells its name, and when a type-parameter attribute contains a nested generic type, so later sibling initializers remain attributed to the enclosing or following sibling type and a following same-line multiline method or type owns references inside its opened body. Reference extraction and symbol-container assignment reuse each record owner's resolved boundary so large multiline declarations remain linear rather than rescanning the header for every component. The C# extractor contract is advanced with this persisted-range change, so a normal full scan re-extracts unchanged C# files from existing indexes.

Graph body mode keeps the established `body_*` definition/container excerpt and adds an independent `callsite_*` excerpt for `references`, `callers`, `callees`, and graph-backed `impact` rows. The call-site window is read from indexed source and centered on the deterministic `first_reference`: grouped rows choose the lowest source position, while an individual reference selects itself. `callsite_line`, available persisted `callsite_column` / `callsite_length`, `callsite_selection`, `callsite_reference_count`, and `callsite_omitted_reference_count` make that choice explicit; legacy rows omit a coordinate or span that was not indexed. The content/range fields mirror the body contract through requested/effective ranges, `line_width_cap` and other truncation reasons, redacted recovery metadata, and `callsite_content_unavailable_reason` when the exact focus line cannot be reconstructed from indexed chunks. A bounded projection containing only `callsite_*` fields still materializes body mode; a projection containing neither `body_*` nor `callsite_*` skips it. Existing output without `--body` remains unchanged.

`ReferenceResult` includes `is_self_reference` and `is_mutual_recursion`; `CallerResult` includes `has_self_reference` and `has_mutual_recursion`. These fields identify self-recursive edges and direct two-symbol cycles without removing valid recursive calls from default graph results. Reader APIs that need a non-recursive view can opt into self-reference exclusion.

MCP tool calls return structured JSON in `structuredContent` plus a short summary in `content`, so clients can consume typed data directly. Text content blocks include `mimeType`: `application/json` when a structured payload is present and `text/plain` otherwise. Tool input schemas also carry common JSON Schema constraints (`minimum` / `maximum` for limits and line counts, `maxLength` for free text, `pattern` for workspace-relative path filters, and `enum` for common kind values) so MCP-aware clients can reject invalid requests before dispatch.

Exact-match flag compatibility is documented in [USER_GUIDE.md](USER_GUIDE.md#flag-compatibility-and-migrations). Keep MCP schemas aligned with that table: `search.exact` is the legacy alias for `exactSubstring`, while name-based tools use `exact` as the legacy alias for `exactName`. Do not add new exact-match aliases without updating the compatibility table, CLI help, MCP descriptions, and changelog fragment together.

`search`, `definition`, `references`, `callers`, `callees`, `symbols`, and `files` also share path-aware narrowing via `--path`, repeatable `--exclude-path`, and `--exclude-tests`. The read layer ranks source files ahead of tests and docs, and `search` further boosts exact symbol-name and path matches so AI clients are more likely to land on implementation files first.

`search --json` and MCP `search` project full chunks into compact match-centered snippets with `chunk_start_line`, `chunk_end_line`, `snippet_start_line`, `snippet_end_line`, `snippet`, `match_lines`, `highlights`, `context_before`, `context_after`, `truncated_line_count`, `dropped_match_line_count`, and `truncation_context`. Compact CLI rows and MCP search results also echo effective output options with `snippet_lines` / `snippetLines`, `max_line_width` / `maxLineWidth`, `exact`, `raw_fts` / `rawFts`, `literal_highlights_available` / `literalHighlightsAvailable`, and optional `literal_highlight_warning` / `literalHighlightWarning`. `--snippet-lines` caps the snippet length up front (default: 8, max: 20), and `--max-line-width` (CLI) / `maxLineWidth` (MCP) clamps each individual snippet line around the first match token via the shared `LineWidthFormatter.ClampLine` contract used by `find` / `references` / `excerpt` / `inspect` (default: 512, max: 4096) so a single match inside a minified / transpiled / generated single-line file no longer returns hundreds of KB per hit. Clamped lines surface `...(+N)...` markers inside the snippet and expose `truncation_context.char_counts`, `truncation_context.total_chars`, `highlights[].truncated`, `highlights[].original_line_length`, and `highlights[].truncated_char_counts` so AI clients can detect clamping and quantify omitted characters. `highlights[].terms` remains a distinct term list for compatibility; `highlights[].term_occurrences` records every matched occurrence with `term`, 1-based `line`, 1-based `column`, `length`, plus `visible`, `visible_column`, and `visible_length` for the portion still present in the returned snippet text after line clamping. Exact substring search also adds `highlights[].literal_terms` and `highlights[].literal_term_occurrences` (camelCase in MCP) so clients can render only the requested literal phrase while preserving the broader diagnostic token list; raw FTS rows set `literal_highlight_warning` / `literalHighlightWarning` to `literal_highlights_unavailable_raw_fts` because FTS syntax can no longer be mapped to one literal phrase. Non-exact punctuation-heavy code-phrase searches add `exact_substring_hint` to CLI JSON compact results and `recovery_hint` to MCP `search` responses so clients can retry with exact substring semantics when FTS tokenization is likely to hide punctuation. `focus_mode`, `focus_line`, `focus_column`, and `focus_reason` describe the match window selected for the snippet, while `dropped_match_line_count` and optional `next_match` report match lines omitted because they fell outside that selected snippet window.

Default `quality` snippet focus treats a single query that begins with a letter or underscore and otherwise contains only letters, digits, and underscores as identifier-shaped. When a result mixes matching code with earlier comments or strings, the first `code`-origin occurrence supplies both the preferred snippet line and column, including when a literal and executable occurrence share one long line; automatic occurrence focus scans the complete valid chunk even when its text exceeds the bounded line-only preferred-focus probe. Space-delimited phrase queries and explicit `leftmost` / `proximity` focus modes retain their existing selection. Explicit origin filters refocus on the first retained facet's line and column so focus metadata, visibility, and line clamping describe the filtered result. The selected occurrence remains auditable through the existing focus and origin metadata, `dropped_match_line_count` is computed from the final returned window, and `next_match` continues forward from that window.

When the match line falls inside an indexed symbol range, `search --json` and MCP `search` also include optional `enclosing_symbol_name`, `enclosing_symbol_kind`, `enclosing_symbol_start_line`, `enclosing_symbol_end_line`, and `enclosing_container_name`.

`find --json` remains line-delimited for repeated matches and adds bounded match-span/truncation metadata to each row: `length` reports the 1-based `column` span length, `original_line_length` reports the source line length before any line-width clamp, and `snippet_truncation_context.line_count` / `char_counts` / `total_chars` / optional `reason` describe snippet clamping. `reason` is `line_width` when `--max-line-width` elides one or more snippet lines.

`excerpt --json` includes `semantic_tokens`, a lightweight range list with 1-based source start/end positions, token `type`, and `modifiers`, so IDE and LLM clients can render or post-process excerpt spans without reparsing the raw `content` string. C# excerpts and LSP `textDocument/semanticTokens/full` share the same source classifier for keywords/modifiers, namespaces and types, methods and properties, parameters, variables and fields, and declaration modifiers. Excerpt classification uses indexed-source context, applies the output token budget only after filtering to visible source lines, and falls back to classifying the visible content when a bounded source scan cannot reach it; narrow or late-file excerpts therefore retain context when available without becoming empty because earlier tokens exhausted the output budget. Excerpt range mapping and LSP delta encoding only translate coordinates and do not choose semantic kinds. `semantic_token_coordinate_space` is `source`; when `--max-line-width` clamps returned content, `content_line_spans` maps each returned content line and visible content-column span back to the matching source line and source-column span, while clamp markers remain unmapped and are not emitted as semantic tokens. Excerpt rows also expose `requested_start_line`, `requested_end_line`, `effective_start_line`, `effective_end_line`, `content_truncation_reasons`, and optional `content_recovery` so clients can tell when `--max-line-width` caused `line_width_cap` and replay the omitted text. Body-bearing JSON rows use matching `body_requested_*`, `body_effective_*`, and `body_content_truncation_reasons` fields; body reasons include `body_line_cap` for snippet/body line caps and `body_byte_cap` for definition body byte caps. Call-site evidence mirrors these fields with the `callsite_` prefix and centers line-width clamping on its exact edge span.

`content_recovery`, `body_content_recovery`, and `callsite_content_recovery` use `argv` as their primary machine-readable contract. Shared CLI JSON and MCP responses redact machine-specific absolute apphost, assembly, source, and database paths by default with the structured path sanitizer, before rendering `command`; they never regex-rewrite the rendered shell string. SQLite file-URI query segments are processed independently so safe controls such as `mode=ro` remain visible while path-valued or sensitive query values are sanitized. The database option is located only after the known source-argument position, so an option-like source name such as `--db` cannot bypass DB-path redaction. Default metadata reports `paths_redacted: true`, `command_display_only: true`, and `requires_local_path_substitution: true` when any argument was replaced. Root-level paths beginning with `-` retain the supported `--` end-of-options marker, and `command_shell` (`posix-sh` or `powershell`) identifies the escaping contract. CLI `definition`, `references`, `callers`, `callees`, `excerpt`, `inspect`, and `impact` accept `--redact-paths` as the explicit default and `--show-paths` as the local-only opt-in. `--show-paths` emits the resolved apphost or `dotnet` plus running assembly, source, and database arguments, sets `paths_redacted: false` and `command_display_only: false`, and produces a safely quoted command for the declared shell. MCP remains support-safe and emits the equivalent camelCase metadata.

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
| [`src/CodeIndex/Cli/SuggestionsCommandRunner.Query.cs`](src/CodeIndex/Cli/SuggestionsCommandRunner.Query.cs) | Redacted full-text history matching plus count, summary, compact, and byte-bounded JSON projections |

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

`suggestions list|export --query <text>` matches the NFKC-normalized query as an ordinal, case-insensitive substring against the redacted stable ID, sampled title, description, context, evidence paths, category, and language. Applying `SuggestionStore.RedactSensitiveText` before matching is a confidentiality contract: a caller cannot use zero-result/count differences to probe a value removed by redaction. Status, time, category, language, and agent filters run first; the text query follows; records are then ordered by descending `CreatedAt` and ordinal stable ID before offset/limit pagination.

The structured history projections share one JSON envelope. `--count` and `--summary-only` summarize the complete filtered set rather than the requested page; they report zero pagination omissions and classify non-emitted records as projection omissions. Summary dimensions have fixed distinct-value caps (status 16, category 32, language 20) and expose their own omitted/truncated metadata. `--compact` emits only redacted bounded list fields. `--max-json-bytes` measures the serialized UTF-8 document plus its final platform newline, uses a logarithmic fitting-prefix search, and removes complete trailing result rows until the envelope fits. `total_count` remains authoritative, while `byte_limit_omitted_count`, `next_offset`, and recovery guidance describe byte truncation. Row-producing compact and byte-bounded modes reject `--limit 0` so every advertised continuation can progress. If the metadata-only envelope cannot fit, the runner writes no stdout JSON. These projections are local read-only operations and do not change the streaming store's retention or mutation contracts.

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
| Duplicate preflight | `IssueDuplicatePreflight` loads at most 1000 raw issue nodes or local entries and 1000 repository labels. Live GitHub lookup requests the selected open, closed, or all state through bounded GraphQL cursor pagination (100 raw nodes per page, 1000 raw nodes total), deduplicates issue numbers across pages, accepts an empty terminal cursor only when `hasNextPage=false`, and rejects missing, repeated, or oversized cursors. Each GitHub page body is capped at 8 MiB/depth 32; the GraphQL Issue connection excludes pull requests, issue/title/body/label scalars remain bounded, and recoverable diagnostics remain sanitized. Remote failures fail closed as runtime errors with `github_preflight_authentication`, `github_preflight_permission`, `github_preflight_rate_limit`, `github_preflight_validation`, `github_preflight_transient`, `github_preflight_timeout`, `github_preflight_transport`, `github_preflight_response`, or `github_preflight_pagination` categories instead of being reported as CLI usage errors. |
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

- **Marker-gated reference extraction** — Before running an expensive per-line reference regex, check the cheapest required syntax marker with an ordinal scan. Stateful extractors must advance comment, fence, header, binding, and other continuation state before a markerless line is skipped; marker gates must not reorder or bypass those transitions. Paths whose syntax intentionally lacks the shared marker, including no-parentheses constructors, trailing lambdas, annotations, and markup continuations, keep their own narrowly scoped markers. Changes to these gates require cross-language positive regressions plus a markerless-decoy practical-budget test so indexing speed does not trade away graph edges.
- **Reference contexts materialize only for emitted rows** — Built-in core, functional-language, and Solidity extractors pass the physical source-line instance through their emitters and trim it only after at least one reference survives filtering and deduplication. The deferred normalizer must key the source line by the emitted physical line number and require reference identity with that raw line; value equality would rewrite derived XAML, Razor, plugin, or other specialized contexts. Columns remain based on the untrimmed physical line, and stateful emitters must still advance on reference-free lines.
- **Language capability patterns remain typed at the integration boundary** — CLI/MCP `languages` rows expose suffix-only `extensions`, literal `exact_filenames`, and `<suffix>`-rendered `filename_prefix_patterns`. `legacy_patterns` preserves the former combined list during deprecation, and `pattern_provenance` identifies built-in, plugin/pattern, and language-map override ownership. Round-trip tests feed every advertised typed pattern back through `FileIndexer.DetectLanguage` (#4617).
- **Ambiguous source extensions stay explicit** — `.m` and `.pl` are not assigned to Objective-C and Perl by default. After language-map overrides and built-in exact/prefix filename rules, `FileIndexer` checks an authoritative recognized shebang whose first physical line is bounded to 256 bytes, then a 64 KiB bounded prefix for strong mutually exclusive Objective-C/MATLAB or Perl/Prolog markers, then at most 256 entries per ancestor directory for conservative project markers. A first line that reaches the shebang boundary without a terminator falls through instead of selecting an interpreter. Conflicting or weak evidence is indexed as `ambiguous_m` / `ambiguous_pl`; unresolved `.m` files run the bounded MATLAB and Objective-C symbol/reference paths after a shared position-preserving comment mask, while Prolog and `ambiguous_pl` advertise conservative reference/graph support and the ambiguous `.pl` bucket uses union symbol/reference rules without changing content-based classification. The detector owns the ordered candidate descriptors, filename patterns, exact content patterns, project markers, bounded shebang rules, and reason/confidence vocabulary; CLI/MCP `extension_lookup` diagnostics and dry-run `language_detections` consume that same source so catalog guidance cannot drift from indexing decisions (#4612, #4738, #4746, #4901).
- **Content reads aggregate open-handle metadata** — Authoritative raw loads, raw-chunk probes, the specialized C# prepass, and final unknown-language probes capture one initial and one final `FileHandleSnapshot` for every stable open. Each snapshot obtains length, mtime, and file identity together through one `GetFileInformationByHandle` call on Windows, one `fstat` call on macOS, or one fixed-layout `statx(..., AT_EMPTY_PATH, ...)` call on Linux; older or unsupported runtimes retain the managed multi-call fallback. The initial snapshot supplies both open-binding identity and the read baseline, while the final snapshot supplies both mutation metadata and the opened identity used against a separate current-path identity probe. On attempt zero, full-content and negative scans stop at the initial length without an extra EOF or `ReadByte` growth probe; stability requires the actual byte count, final length/mtime/handle identity, and current-path identity to agree with that baseline. A changed snapshot reopens once and only the retry scans to bounded EOF, while a final handle length over the max-file cap still fails immediately as growth during the current read. Positive raw-chunk matches remain conservatively true. Stable reads therefore use exactly two logical snapshots, and one bounded retry uses four, without changing the distinct raw-load, positive chunk-match, C# prepass, or unknown-language retry contracts.
- **Unknown-language membership uses one bounded file snapshot** — Exact filenames, registered extensions, pattern/plugin mappings, and the `.m` / `.pl` ambiguous detectors keep their existing precedence and I/O. Only the final extensionless or unregistered-extension fallback defers its script-header check: it fills at most 256 bytes with short-read-safe reads from one authorized handle and returns immediately for a recognized shebang or `#compdef`. Otherwise the first attempt continues through a pooled fixed-size buffer only to the initial handle length, retaining the first 4096 bytes for UTF-16 BOM/parity detection while checking every consumed byte for NUL and the strict sub-1024-byte Git LFS pointer shape. Matching final length, actual byte count, mtime, and identity make that snapshot authoritative without an EOF probe; a change discards it and re-resolves, reauthorizes, and reopens once, and only that bounded retry continues to EOF or `max-file-bytes + 1`. Full CLI scans, scoped dry-run probes, freshness scans, and MCP full/dry indexing all consume this `FileIndexer` boundary, so unknown-language diagnostics do not pay a separate full-file allocation or second stable open.
- **Dynamic reference-graph readiness follows extractor contracts** — when indexed Crystal, Groovy, Tcl, Prolog, or `ambiguous_pl` rows have a missing or stale symbol-extractor version stamp, status reports `dynamic_reference_graph_contract_stale` and keeps `reference_graph_complete` / `graph_data_current` false until a normal index refresh rewrites those rows (#4746).
- **Hotspot marker fingerprints share one bounded tree traversal** — full/update CLI and MCP indexing compute C#, VB, F#, and MSBuild marker fingerprints together instead of walking the directory tree once per language. Each distinct marker glob retains the platform filesystem's matching behavior and is enumerated once per visited directory, while child directories are enumerated once; marker sets, budgets, truncation sentinels, and warning order remain isolated per language. The single-language API delegates to the same engine, preserving ignore rules, nested-repository/submodule boundaries, and MCP authorized-read failures.
- **Lock-file dependency graphs model package relationships** — `packages.lock.json`, `package-lock.json`, and `npm-shrinkwrap.json` keep package declarations as symbols, but emit `dependency` references only for explicit parent-package to child-package entries. NuGet lock symbols and references preserve the current file, target/RID, parent package, and exact JSON property span; candidate resolution stays file-local, while file-level `deps` suppresses cross-file package-name inference. Normal index updates invalidate the prior dependency-lock extraction and reference-identity contracts, so `callers` identifies the requiring package without connecting unrelated lock files or collapsing repeated declarations to the first matching line (#4409, #4845).
- **Dependency-cycle audits separate analysis from display** — CLI `deps --cycles` and MCP `deps` with `cycles=true` analyze a deterministic, path-ordered edge set up to the independent `--graph-budget` / `graphBudget` before computing and stably ranking strongly connected components. `--limit` / `limit` only paginates that ranked SCC set, and opaque cursors are bound to the filters, graph budget, and indexed graph that produced them. Machine-readable responses expose `analysis_complete`, graph edge count/budget, stable ranking mode, authoritative total-cycle status, and continuation metadata; exhausting the graph budget is reported as an explicitly incomplete analysis rather than a complete cycle audit (#4731).
- **Large dependency SCCs use bounded presentation, not bounded facts** — SCC analysis and ranking always retain authoritative component size, edge/reference totals, the largest-component summary, and evidence breakdowns independently of node presentation. CLI and MCP materialize 50 path-ordered nodes per returned component by default; human, JSON, DOT, GraphML, and JSON graph output project presentation to that sample, expose omitted-node and display-truncation metadata, support compact summaries, and require explicit `--all-cycle-nodes` / `includeAllCycleNodes` opt-in for raw expansion. The MCP cycle schema gives component node arrays a specialized maximum equal to the graph budget ceiling, so an allowed raw expansion remains schema-valid without weakening ordinary row-array bounds. Machine-readable metadata aggregates total, materialized, and omitted nodes for the current returned SCC page separately from the global largest-component summary, so later cursor pages remain authoritative. Retained-evidence summaries aggregate source language, origin, resolution state, reference kind, target kind, and suppression reason. Noise suppression classifies evidence before candidate budgeting and drops only legacy Markdown heading matches plus qualified C# calls classified by a current reference-identity contract as unresolved, ambiguous, or resolved to a target in another file. Stale or absent identity metadata fails closed to retained evidence with `resolution_state=unavailable`; the confirmed target file takes authoritative precedence even when it contains same-name overloads, and resolved partial-family groups retain only files represented by their persisted candidate set. Grouping metadata is file-level and explicitly reports that partial-type-family collapse was not applied (#5197).
- **Dependency-cycle cursors bind presentation evidence as well as topology** — the graph fingerprint includes each retained evidence row's source language, origin, resolution state, reference kind, target kind, suppression reason, and count in deterministic order. A metadata-only graph refresh therefore rejects an older cursor instead of mixing evidence summaries from different snapshots. MCP `format=json-graph` cycle requests use the same bounded node/edge projection as CLI graph output, and the specialized graph node/edge schema limits match the maximum cycle graph budget (#5197).
- **No ORM** — Raw `Microsoft.Data.Sqlite` with parameterized queries. Keeps dependencies minimal and control explicit.
- **Batch commits** — 500 records per transaction for write performance. Reduces fsync overhead.
- **Fact-backed C# instantiation families** — Graph finalization materializes one TEMP row per eligible C# type declaration and constructor after identity facts are ready. Ranks 0–4 use symbol-primary-key joins and rank 5 uses a reference-driven composite family seek, so no candidate path performs a correlated constructor-family symbol scan. Separate lower-rank and fallback binding-sensitive flags preserve all-partial versus representative-only primary-constructor semantics. Type-declaration-only BINARY uniqueness keeps project/file-local conflicts ambiguous and constructor-only orphans out of rank 5 while retaining overload, implicit-default, optional/default/`params`, value-type, enum, delegate, and unknown-arity behavior.
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
- **Freshness metadata for trust decisions** — `status` exposes whole-workspace freshness and git state, plus trust metadata such as `sql_graph_contract_ready` / `sql_graph_contract_degraded_reason`, `hotspot_family_ready` / `hotspot_family_degraded_reason`, forward-compatibility audit fields (`index_writer_version`, `index_newer_than_reader`, `index_newer_than_reader_reason` — see "Forward-compatibility readiness audit"), and fold remediation fields (`fold_ready_reason`, `degraded_reason`, `recommended_action`, `alternative_action`) so AI clients can tell up front whether SQL graph/dependency/impact answers, duplicate-name hotspot families, and Unicode `--exact` are authoritative. CLI `status --json` and MCP `status` both populate those fold remediation fields when `fold_ready=false`. It also carries `unknown_extension_file_count`, a capped `unknown_extension_files` path sample, `unknown_extension_files_truncated`, and `unknown_extension_file_path_limit` after a current full-repository scan so readable text files excluded for missing filename/extension language support are visible and actionable; binary, oversized, unreadable, ignored, and internal-artifact files are not counted. A diagnostics-version stamp gates all persisted fields, so legacy or unsupported semantics are omitted until a current full scan replaces them; extensionless actionable guidance recommends a recognized shebang or a supported-extension rename. When those fold remediation fields are derived from an explicit read-only `file:` DB URI, they are normalized back to a writable filesystem path for both absolute (`file:///...?...`) and relative (`file:codeindex.db?...`) forms instead of echoing the read-only URI into commands that would fail. `cdidx index` JSON/human readiness output also surfaces the same trust bits, keeping the post-index readiness summary aligned with `status`. `impact` / MCP `impact_analysis` also mirror the SQL graph-contract signal in JSON so stale SQL rows do not masquerade as authoritative zero-impact answers. `inspect` / MCP `analyze_symbol` and `references` / MCP `references` now mirror that same SQL graph-contract signal whenever SQL-backed graph reads contribute to their payloads, so stale SQL rows do not look like authoritative hits or zero-result answers there either. `map` keeps `indexed_at` / `latest_modified` scoped to the filtered result set and also exposes `workspace_indexed_at` / `workspace_latest_modified` for whole-workspace freshness. `inspect` mirrors those whole-workspace timestamps and git fields so symbol-oriented AI flows can make trust decisions without a separate `status` call. `files` exposes per-file checksum plus modified/indexed timestamps. File-column migrations are applied opportunistically for older DBs, and read paths are designed to avoid crashing if in-place migration is unavailable. CLI and MCP zero-result JSON responses for `search`, `files`, `symbols`, `definition`, `references`, `callers`, `callees`, `deps`, `unused`, `hotspots`, and `impact` include `indexed_file_count`, `indexed_at`, and `freshness_available`. `indexed_at:null` with `freshness_available=true` means the index is empty, while `freshness_available=false` means a legacy/read-only DB could not expose freshness timestamps and `freshness_degraded_reason` explains why. **HEAD-aware staleness signal**: every successful `cdidx index` full scan stamps the captured `git HEAD` into `codeindex_meta` so subsequent runs can compare it against the workspace HEAD. A successful full scan reconciles and purges the whole workspace, advances `workspace_verified_head_sha`, and returns `head_changed: false`; a partial scan keeps verification at the prior baseline and exposes `head_changed` / `prior_indexed_head_commit` / `current_head_commit` / `head_change_notice` with normal full-scan recovery guidance. `status --check` compares against the same verified baseline through `workspace_check.head_changed`. Git-scoped refreshes can advance verification only after unioning the prior baseline-to-current paths, while explicit `--files` updates preserve it. Non-git workspaces and legacy DBs with no captured baseline skip the comparison instead of false-positive flagging.
- **Observable bounded workspace-check samples** — `IndexFreshnessChecker` retains at most 20 ordered entries for each changed, missing, outside-sparse-cone, unindexed, unverifiable, and scan-error list while incrementing authoritative counts independently. The serialized `workspace_check` object derives each `*_omitted_count` from that authoritative count and the returned list, exposes the shared `*_path_limit`, and marks `*_truncated` whenever paths are omitted. A list-only `--fields` projection automatically includes these signals and its count; compact status keeps the count and signals without the arrays; and byte-budget trimming removes only ordered suffix paths, recomputes the signals, and records `metadata.byte_limit_omitted_path_count`. Human check diagnostics use `coverage=sample` or `coverage=complete` so a bounded list cannot be mistaken for a complete inventory.
- **Folded-key upgrade without reparse** — `backfill-fold` and MCP `backfill_fold` recompute `name_folded`, explicit-interface `display_name_folded`, and reference `*_folded` values directly from existing DB rows, then stamp `FoldReadyFlag` once verification confirms no required folded values remain NULL. This gives AI clients and users a low-cost upgrade path from pre-#86 DBs without re-reading every source file. It rewrites all folded rows when fold metadata is stale or the C# symbol-name contract changed, and stamps the current C# contract only after verification succeeds. Upgrading the explicit-interface v3 contract also requires persisted signatures for every C# method/property/event row; older databases without that reconstruction evidence must refresh C# files or rebuild rather than receiving a false-ready stamp.
- **Bundled symbol analysis** — `inspect` and MCP `analyze_symbol` return definition, nearby symbols, references, callers, callees, file metadata, workspace trust metadata, and graph-support metadata in one request so AI clients can answer common symbol questions with fewer round-trips.
- **Language-aware reference extraction** — `references`, `callers`, `callees`, and `impact` are backed by an indexed reference table built only for languages where regex-based call/reference extraction is meaningful. Unsupported languages intentionally fall back to text search instead of returning low-confidence pseudo-graph data. When a language is removed from graph support, `PurgeUnsupportedReferences` deletes its stale `symbol_references` rows on the next indexing run, and graph read paths additionally filter by supported languages to prevent stale edges from surviving between index runs. Shell is intentionally excluded because its command-style invocations (`foo arg1 arg2`) cannot be detected by the parenthesized-call regex. **Nested generic call sites**: C#/Java constructor calls like `new Dictionary<string, List<int>>()` and C# generic method calls like `Helper.DoWork<List<int>>()` are recovered by a depth-aware fallback scanner so the outer target still reaches the reference table even though the flat regex fast-path cannot balance `>>`. **JS/TS no-paren constructors**: JavaScript / TypeScript zero-argument constructor calls that legally omit `()` — for example `new Foo;`, `new Date;`, qualified targets like `new Demo.Provider;`, and one-level generic TypeScript forms like `new Box<number>;` — are emitted as `instantiate` edges via a dedicated language-gated path, while next-line `.bar()` / `[0]` continuations are suppressed so a line-ended `new Foo` does not become a phantom standalone instantiation. **Constructor chain calls**: C# `: this(...)` / `: base(...)` initializers and Java `this(...)` / `super(...)` first-statement calls are detected separately from the generic call regex and rewritten so the reference target is the real constructor (enclosing class/record for `this`, the parsed base type from the class signature for `base` / `super`). Cross-line C# initializers are attributed to the owning constructor rather than the enclosing class. Base-type parsing strips generics, record primary-ctor args, `where` constraints, and `global::` / dotted namespace qualifiers; Java `super.method()` stays a normal method call. **Type-position dependency edges**: C#/Java base lists, declaration types, generic constraints, `throws`, `is`/`as`/`instanceof`, and real C# XML-doc `cref` sites are indexed as `type_reference` rows so `references` / `impact` can see compile-time rename dependencies without polluting the default dynamic call graph exposed by `callers` / `callees`. C# XML-doc `cref` extraction accepts declaration-attached XML-doc comments from both `///` lines and delimited `/** ... */` blocks, including declarations that begin later on the same physical line after the closing `*/` only when no unrelated same-line code or declaration intervenes, while ordinary `//` / `////` comments, non-documenting block comments, method-body XML-doc comments that merely precede a later declaration, brace-free field/property initializer continuations, brace-free expression lambdas, intervening top-level executable statements, same-line non-target code after `*/`, other nested executable continuations, and multiline raw/verbatim string content whose line happens to start with `/**` stay excluded. Non-doc code or string content after the closing `*/` on the same physical line is still outside the doc-comment slice. Even though the regex now runs against that narrower slice, the extractor preserves `symbol_references.column` relative to the original physical source line. On the C# read path, `using static` constant-pattern suppression is token-aware around `is` / `case`, reconstructs an anchor-aware indexed multi-line window when the anchor lives on a previous line, and keeps trivia-bearing forms such as `value is/*comment*/Red`, `value is\n    Red or Blue`, `value is\n    // comment\n    Red`, `case\n    // comment\n    Point:`, long `case` / `or` chains, and `case\tRed:` filtered or rescued correctly. Qualified constant/member patterns stay qualifier-driven on that exact-name read path, so an unrelated same-name type such as `class Red {}` no longer cancels suppression for `case Color.Red or Color.Blue:` just because the leaf name matches. The extractor-side pending type-pattern carry now also survives trivia-only separator lines, standalone continuation-line `not`, and multiline `case` heads/logical continuations, so comment-only or `not`-only continuation lines no longer drop the later type head before the real token arrives. Non-type `case` labels such as `case > 0:` and `case not > 0:` do not arm that pending carry, so the next-line call/identifier token stays out of `type_reference`. Same-name type rescue also honors `file` visibility so file-local types only rescue references from the same physical file; inherited protected/public/internal nested types from real base classes rescue derived-class pattern heads only after the base reference is normalized through active type and namespace aliases, and alias-expanded constructed generic bases are canonicalized again before containing-type lookup so `AliasBase = Probe.Base<int>` resolves the same way as `Probe.Base`; implemented interfaces do not contribute inherited nested-type rescue; and same-file `using Namespace;`, project-wide `global using Namespace;`, and active type aliases all participate in the rescue set. The extractor deliberately leaves ambiguous unqualified `using static` heads such as `value is Red` in the DB, because file-local parsing alone cannot know whether another file in the same namespace declares the real `Red` type; the workspace-aware read path is responsible for suppressing the pure constant-only cases. **SQL qualified-name alignment**: SQL definitions still persist their schema-qualified symbol name (`dbo.fn_X`), but graph/`deps`/unused/hotspot readers now resolve each SQL reference row through its stored source-line context, recorded call column, and enclosing container before they compare it to definitions, so qualified `references` / `callers` / `impact` queries stay schema-scoped even when one line contains multiple qualified calls or the lookup is non-exact. Those readers fall back to the bare leaf only when the source site itself is genuinely unqualified, which keeps `deps`, `unused`, and `hotspots` aligned with qualified SQL calls without regressing bare-call support or double-counting `EXEC dbo.fn_Target; EXEC sales.fn_Target;`. Once a row already has a recorded call column, those downstream readers no longer whole-line-upgrade that row to a later qualified token, so trailing comments, string literals, or a second qualified call cannot steal the earlier unqualified edge. Exact SQL graph/dependency readers also preserve the resolved segment count, so a quoted single identifier containing a dot such as `"sales.fn_Target"` stays distinct from the real qualified name `sales.fn_Target` across exact `references` / `callers` / `impact` and aggregate `deps` / `unused` / `hotspots`. SQL CTE body source rows use the raw `cte_body_reference` kind, so `references --kind cte_body_reference` can distinguish anchor/recursive-member internals from outer-query table references. Qualified SQL `callees` queries also keep leaf fallback disabled unless the caller query itself is unqualified, so `callees sales.Caller` no longer widens to `dbo.Caller`. SQL extractors also accept optional whitespace around qualified-name dots, so definitions/calls such as `[sales] . [fn_Target]` and `[dbo] . [fn_Target]` keep their full qualified identity instead of truncating at the first segment. The same SQL no-parens extractor now preserves ANSI / PostgreSQL double-quoted call targets such as `CALL "sales"."proc_name"` and `EXEC "dbo"."fn_Target"` instead of stripping them as string literals, while true single-quoted SQL string literals remain masked. Definition-oriented readers also canonicalize quoted qualified SQL names (`[dbo].[fn_X]` → `dbo.fn_X`) before matching, and they only fall back to the leaf identifier for unqualified queries so exact qualified lookups do not widen to sibling schemas that merely share the same leaf name. Exact SQL definition matching also preserves segment count, so a quoted single identifier that contains a dot (`"sales.fn_Target"`) does not collide with a real qualified name (`sales.fn_Target`). SQL exact graph leaf fallback also stays on the Unicode folded exact path, and both quoted qualified and unqualified Unicode exact definition lookups now use the folded normalized path, so queries such as `dbo.Äpfel` / `dbo.äpfel` and bare `Äpfel` / `äpfel` keep matching leaf call/reference rows such as `äpfel` plus stored definitions such as `[dbo].[Äpfel]` or `dbo.Äpfel` instead of silently degrading to ASCII-only `NOCASE`. Exact multi-name SQL `symbols --count` lookups also bind the folded leaf parameters on that same `_foldReady` path, so Unicode leaf query sets no longer fail with missing-parameter database errors.
- **Transitive impact analysis** — `impact` and MCP `impact_analysis` compute the transitive caller chain of a symbol using BFS. Design constraints refined through adversarial review: caller matching uses case-insensitive exact match (`lower() = lower()`) to avoid both substring expansion and case-sensitivity brittleness; symbol names are pre-resolved through definitions with exact-case preference; the read path filters to graph-supported languages to prevent stale edges from removed languages; the definition set used for heuristic fallback must also respect active `--lang` / `--path` / `--exclude-path` / `--exclude-tests` filters and graph-supported languages so out-of-scope or unsupported duplicates do not suppress in-scope hints; fallback eligibility is keyed off class-like definitions only, so same-name namespace/import siblings do not block a single resolved class / struct / interface target, while pure non-callable `namespace` / `import` queries surface `non_callable_symbol_kind` guidance; heuristic file-level hints still return a successful result and encode their non-authoritative status via `impact_mode`, `heuristic`, `hint_count`, and `truncated`; caller rows include `result_kind: "graph"` and heuristic `file_impacts` rows include `result_kind: "file_heuristic"` so clients can distinguish authoritative hop-depth graph results from boundary fallback hints without inferring from list position or depth values; when `truncated` is `true`, the JSON / MCP payload also exposes `truncated_reason` so callers can distinguish actionable cases from runaway-graph cases — `user_limit` means the caller-supplied `--limit` was reached and raising `--limit` will return more results, while `safety_cap` means an internal per-symbol BFS fetch-iteration cap fired (the graph is likely pathological / cyclic and raising `--limit` alone will not help). `impact` / MCP `impact_analysis` also expose `termination_reason` (`completed`, `max_depth_reached`, `cycle_detected`, `row_limit_truncated`, `safety_cap`, or `cancelled`), `cycle_detected`, and `cycles` so caller cycles are distinguishable from natural traversal completion or limit/depth termination (#1883). `safety_cap` outranks `user_limit` whenever both are encountered, and the heuristic file-level hints path is `user_limit`-only because hint truncation is always driven by the caller's `--limit`. The field is omitted whenever `truncated` is `false`. (#1533) `count` / `file_count` now describe the visible returned set while `confirmed_count` / `confirmed_file_count` preserve symbol-level caller totals for heuristic-success payloads, and `impact --json --count` uses the same `*_count` field names as the full payload; to reduce general-name collisions, a file only qualifies for type fallback if it both references one of the candidate member names and also exposes same-file evidence anchoring the source/target pair — either a `call` / `instantiate` reference to the resolved target name (the call-graph itself authoritatively pins the relationship, so this path runs before the metadata-attribute bypass and does not depend on the looser ambiguity guard) or structured type evidence through indexed symbol metadata such as signatures or return types — rather than raw comment/string text matches. The call/instantiate anchor matches the resolved name exactly with no suffix-strip alias, because callable references already carry the authoritative identifier and applying the C# `[Foo]` → `FooAttribute` alias there would let unrelated `Foo()` method calls falsely anchor `impact FooAttribute` (#1881); the metadata bypass keeps the C# `Attribute` suffix alias because attribute use sites legitimately abbreviate the target name. The signature evidence path is Unicode-aware so fullwidth/accented identifiers are tokenized consistently with exact-name resolution; hint `reference_count` reflects the real number of matching reference rows while the symbol list stays deduplicated; only multiple class-like definitions are treated as fallback ambiguity, even when they share one file; and `PurgeUnsupportedReferences` runs in all three indexing paths (CLI full scan, CLI update mode, MCP index).
- **Impact cycle identity** — On a current reference-identity graph, `impact` carries resolved source/target symbol IDs through every BFS hop and detects cycles only from actual traversed directed edges between those canonical IDs. A repeated or folded display name is never a zero-hop cycle by itself; direct recursion remains a real singleton cycle because its persisted edge has the same source and target ID. Unresolved or ambiguous same-leaf evidence remains available through `references` and unqualified graph discovery but does not enter identity-scoped impact traversal; a `resolved_group` edge can traverse only when its selected target candidates match. Structured caller rows expose `caller_symbol_id` / a uniquely resolved `callee_symbol_id`, `--with-paths` nodes expose `symbol_id` only for unique identities, and cycle rows expose `member_identities` while retaining the display-only `members` list for compatibility. Legacy graphs without the current identity contract keep the name-keyed compatibility path (#4847).
- **Extractor regex backtracking policy** — Built-in symbol and reference extractors must not use unbounded regular expression matching on repository-controlled file content. Backtracking regexes use `BoundedRegex.DefaultMatchTimeout`, while `RegexOptions.NonBacktracking` is allowed for patterns that are compatible with the non-backtracking engine. Patterns that deliberately remain backtracking-only, such as lookaround-heavy or balancing-group extractors, are acceptable only because the shared timeout audit covers them. If a future extractor must use `System.Text.RegularExpressions.Regex` directly, it must pass an explicit timeout and document why `BoundedRegex` or `NonBacktracking` is not suitable.
- **Hybrid symbol extraction** — No AST parsers and no heavyweight language-specific dependencies. Most languages still use compiled regex patterns, while JavaScript/TypeScript add a lightweight lexer/state machine for class-body method extraction, private-scope filtering, synthetic class-expression binding detection, and JS/TS-specific range resolution that regex alone could not handle reliably. The trade-off still favors speed and portability over full parser accuracy, but the index stores richer symbol metadata such as definition ranges, optional body ranges, signatures, enclosing symbols, qualified container paths, authoritative family keys, visibility, and return types when the language patterns or JS/TS state machine can infer them. Visual Basic patterns also treat `Namespace ... End Namespace` as a real container and allow implicit-visibility declarations plus leading modifiers (`Shared`, `Overrides`, `Partial`, etc.), so VB projects expose the same top-level orientation and member coverage that other class-based languages already get. Visual Basic container patterns use case-insensitive `VisualBasicEnd` range tracking so cross-file partial families still get stable body ranges and can participate in hotspot-family grouping. **Pattern externalization**: Language patterns are currently defined inline in `SymbolExtractor.cs` using compiled `Regex` objects. This keeps the extraction pipeline self-contained and allows compile-time validation, but means adding a new language requires a code change and rebuild. A future iteration could externalize patterns to JSON/TOML files (loaded at startup), which would lower the barrier for community contributions and enable hot-reload during development. The trade-off is losing compile-time safety and slightly increasing startup cost. If externalized, patterns should include: language name, kind (function/class/import/namespace), regex string, body style (brace/indent/ruby-end/none), and optional capture group names for visibility and return type.
- **Reusable record-declaration parsing** — C#, Java, and Kotlin record/primary-constructor extraction matches declaration kinds with shared bounded regexes and compares the captured declaration name ordinally. Multiline declaration collection grows one contiguous character buffer, scans its current span without materializing prefixes, and creates only the final declaration string. Keep these languages on the same path so dense type sets do not rebuild regex engines per declaration and wide headers do not regress to quadratic prefix allocation.
- **Nested C# interpolation state** — The C# lexical masker keeps immutable parent frames when an interpolated regular, verbatim, or raw string starts inside another interpolation hole. Closing the nested string restores the complete outer mode, delimiter, dollar-count, and brace-depth state; expression-bodied property calls remain excluded from declaration patterns, and C# extractor contract bumps force existing indexes to refresh affected files.
- **C# static-lambda declaration gating** — The declaration scanner treats a candidate name inside a confirmed `static`, `static async`, or `async static` lambda header as expression context, including when multiline property-header composition prepends call arguments. Real static members, local functions, and assigned-lambda symbols remain eligible. C# extractor contract v6 makes a normal index refresh re-extract stale C# symbols (#4830; regression of #4453).
- **C# switch-pattern/call boundary** — Positional-pattern suppression distinguishes result terminators and expression continuations from nested positional clauses and valid pattern suffixes such as contextual-keyword designations, property clauses, and `when` guards before following an arm arrow onto the next line. Calls inside lambdas returned from switch-expression arms therefore remain call references attributed to the enclosing callable while genuine pattern heads remain excluded. C# extractor contract v10 refreshes stale C# rows under this rule (#5085).
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
- **MCP language-catalog pagination** — MCP `languages` builds rows through the same canonical `LanguageCatalog` used by the CLI, then applies exact normalized language/extension/alias filters and ordinal language sorting. Pages default to 20 rows and use opaque response-v2 cursors bound to every filter, `limit`, `maxBytes`, the fixed sort contract, and a fingerprint of the emitted catalog generation (including indexed-language membership for `indexedOnly`). Query changes return `cursor_query_mismatch`; catalog changes return retry-safe `cursor_stale`. `maxBytes` accepts 4,096 through 1,000,000 bytes and measures the complete UTF-8 JSON-RPC envelope, shrinking the row page before emission while keeping filtered totals separate from catalog and capability counts.
- **MCP bounded discovery and status projection** — A no-argument `tools/list` returns every enabled tool in deterministic order as the agent-safe `compact` catalog, whose complete JSON-RPC envelope is kept within a 64-KiB UTF-8 budget. Compact entries retain authoritative invocation schemas (types, required fields, constraints, and defaults) while removing documentation-only schema keywords; descriptions are shortened without removing safety-critical guidance such as the prohibition on including source code in suggestions, and optional output schemas and examples are omitted. Request `format: "full"` with exact `names` only when complete descriptions, output schemas, examples, or workflow metadata are needed. Standard opaque cursors preserve compact and name-filtered controls; legacy numeric cursors continue the unfiltered full catalog. `_meta.size_telemetry` reports serialized UTF-8 bytes and approximate tokens for tools, descriptions, input schemas, output schemas, examples, annotations/stability, and catalog metadata without recording arguments. `names` accepts at most 24 exact enabled names of 128 characters each. MCP `status.fields` projects exact top-level fields after `format` and optional diagnostic attachments are built, while `api_version` remains part of every structured result. Projection inputs are capped at 32 names, 128 characters each, and 2,048 characters total; unknown names and nested paths fail as `invalid_argument`. New discovery modes must preserve tool reachability, invocation-schema authority, deterministic standard pagination, the default byte budget, and explicit-full compatibility.
- **MCP resource-list cursor stability** — `resources/list` emits a fixed-size opaque keyset cursor that binds the last consumed file id to a persisted indexed-file generation and the canonical discovery filters. The reader resolves that id back to the existing source/test/docs bucket plus path ordering inside the same SQLite snapshot. Any file insertion, deletion, or update changes the generation; a later page then returns `-32011` / `index_stale` with `restart_required: true`. Changing `path`, `lang`, or `includeGenerated` between pages returns `-32602` / `resources_list_filters_changed` with the same restart requirement. In either case, the client must omit `params.cursor` to restart instead of continuing across mixed snapshots or filters. Writable legacy databases install the generation row and triggers through the normal read migration before a cursor is issued. A mutable read-only legacy database that cannot prove generation tracking returns `resources_list_generation_unavailable` with `migration_required: true`; a canonical, unambiguous `immutable=1` legacy URI (optionally paired with `mode=ro`) may safely use connection-local generation zero because it cannot change between pages. Encoded, case-variant, whitespace-padded, duplicated, conflicting, or extra query parameters are not trusted as that immutable guarantee. The legacy decimal zero remains a first-page upgrade input, but nonzero decimal offsets cannot prove their source generation and therefore return the same restart-required error; decimal cursors are never emitted. Version-1 opaque cursors remain valid only with the default unfiltered view.
- **MCP file resource discovery** — `resources/templates/list` advertises `cdidx://file-path/{path}` so a client that already knows an exact repository-relative path can construct a `resources/read` URI without paging the repository inventory. Simple URI-template expansion percent-encodes separators and reserved filename characters such as `?` and `#`; the template-only resolver decodes the value once, rejects absolute paths, traversal, backslashes, empty segments, queries, and fragments, then returns the canonical `cdidx://file/<path>` identity. Canonical resource URIs continue to reject encoded separators. `resources/list` accepts `path` as one string or at most 100 strings of at most 1024 characters and 128 wildcard operators each, using the same anchored directory/glob semantics as file queries, plus an exact normalized `lang` filter and `includeGenerated` (default `false`). Generated files also require `includeGenerated: true` for direct reads.
- **MCP resource-list response budget** — `resources/list.params.maxBytes` accepts 4,096 through 1,000,000 bytes and defaults to 1,000,000, matching the default HTTP response-body cap. The effective budget is the minimum of that request, the server-wide MCP envelope cap, and the active HTTP transport response-body cap (when applicable), so a lower configured HTTP cap shapes a valid page instead of rejecting it with HTTP 500. The server measures the complete JSON-RPC envelope, keeps 200 as the candidate ceiling, and stops before the next resource would cross the effective byte budget. For HTTP JSON-RPC batches, the active transport budget covers the complete response array, including brackets and commas, and is divided fairly among response-bearing items; notifications consume no response slot. Each `resources/list` item honors its current share and preserves its request ID in a canonical budget error if even a bounded page cannot fit. State-changing and other non-resource outcomes are never relabeled as retry-safe; an aggregate overflow after execution reports an unknown completion state and forbids automatic retry. `_meta.response_controls` reports the requested/effective budgets, consumed and returned counts, `omitted_resource_count`, bounded reason counts (`resource_uri_too_long` / `resource_exceeds_max_bytes`), `byte_budget_reached`, and `continuation_reason` (`byte_budget`, `item_limit`, or `completed`). A continuation cursor anchors the last consumed database row; a valid resource that did not fit remains unconsumed for the next page, while a resource that cannot fit even on an empty page is consumed and counted so pagination cannot livelock.
- **Typed MCP resource reads** — `read_resource` is the discoverable tools/call surface for bounded file reads. Its `tools/list` schema owns the typed `uri`, 1-based inclusive line range, 4-byte through 128-KiB UTF-8 text budget, generated-file opt-in, and opaque continuation cursor contract. The handler delegates to `HandleResourcesRead`, so URI validation, generated-file policy, snapshot lookup, response-aware budgeting, UTF-8 boundaries, stale-cursor checks, and legacy index compatibility stay in one reader. The adapter returns file text once in `content[0].text`, puts canonical identity in `structuredContent.resource`, and puts range/truncation/continuation data in `structuredContent._meta`; resource errors become MCP tool-result errors with the original classification and JSON-RPC code preserved. Keep `resources/read` and both URI forms backward compatible. Clients must feature-detect `read_resource` in `tools/list` so older servers continue through the protocol method.
- **MCP array argument bounds** — MCP string-array filters such as `path`, `project`, `excludePaths`, and mixed `names` arrays reject invalid entries instead of silently dropping them. Arrays are capped at 100 entries and each entry is capped at 4096 characters; `batch_query` reports these validation failures per slot with `request_index` and `ok: false`.
- **MCP schema lock-down** — Every tool `inputSchema` includes `additionalProperties: false`, and `tools/call` mirrors that contract by rejecting unknown argument names with `-32602` / `invalid_argument` instead of silently defaulting misspelled fields.
- **MCP stability markers and naming** — Every tool advertises `x-stability` (`stable`, `experimental`, or `deprecated`). MCP structured payload keys use snake_case, matching the CLI JSON contract; do not add camelCase aliases for new fields.
- **MCP language-support clauses** — Every advertised MCP tool description ends with a `Language support:` clause generated through `McpServer.CreateToolDefinition`. Graph tools enumerate `ReferenceExtractor.GetSupportedLanguages()`, symbol tools enumerate `SymbolExtractor.GetSupportedLanguages()`, and file/content tools point at the detected-language catalog used by `cdidx languages`, so `tools/list` stays aligned with the runtime registries instead of carrying hand-maintained prose.
- **MCP tool annotations** — All tools emit `annotations` with `readOnlyHint`, `destructiveHint`, `idempotentHint`, and `openWorldHint` per the MCP spec, so AI clients can auto-approve safe read-only queries.
- **MCP server instructions** — The `initialize` response keeps first-contact guidance within a 2-KiB UTF-8 budget. It explains bounded `tools/list` discovery and on-demand full definitions, advertises only enabled tools, and directs extended workflows to `prompts/list` and `prompts/get` only when the workflow's required tools are enabled instead of duplicating the full catalog.
- **Per-deployment MCP tool enablement** — `cdidx mcp` honors two environment variables so operators can narrow the exposed tool surface without a code change (#1561). `CDIDX_MCP_TOOLS_ALLOW=<csv>` is a strict allowlist; if set, only those tools appear in `tools/list` and are dispatched by `tools/call`. `CDIDX_MCP_TOOLS_DENY=<csv>` removes individual tools from the default-all-enabled set. Allow wins over deny when both are set. The single source of truth for known tool names is `McpToolFilter.KnownToolNames`, which is checked against by both the `tools/list` filter and the `tools/call` gate (and the per-slot guard inside `batch_query`). `BuildInstructions` is also gate-aware: scoped deployments never recommend a disabled tool in the `initialize` instructions, so the guidance stays in sync with the advertised surface. Top-level `tools/call` on a disabled known tool returns `-32601 Tool not enabled: <name>`; `batch_query` envelopes succeed but each disabled-tool slot carries a `code: -32601` field alongside the `error` string so clients can branch on the code without parsing prose. Truly unknown names still fall through to the existing `-32602 Unknown tool` path so operator-disabled tools remain distinguishable from typos. Tool names compare case-insensitively, unknown env-var entries are filtered against the known set (an allowlist of only-unknown names intentionally exposes nothing rather than silently disabling the gate), and the default — no env vars set — keeps every tool enabled so existing deployments are unaffected.
- **Backward-compatible symbol schema** — Opening an older DB with a newer binary auto-adds missing symbol columns when possible, including hotspot-family metadata such as `container_qualified_name` and `family_key`. If a read path cannot migrate the DB in place, symbol queries fall back to the legacy column set instead of crashing.
- **Bounded hotspot aggregation** — `DbWriter` maintains `hotspot_reference_counts` as compact per-file logical-reference totals. Limited hotspot readers use a fixed bounded candidate frontier before the non-SQL, SQL exact, SQL leaf, ambiguity, and target-family joins: current C# callables rank resolved logical target identities, while other results use the persisted name-rank index. Downstream joins therefore stay bounded without letting unresolved high-volume names evict confirmed C# targets. Logical-site identity excludes raw aliases, mutations refresh cross-file context dependents and demote reference-identity trust transactionally, and reader/writer aggregate SQL is cancellation-interruptible. Bulk-eligible refreshes with at least 64 dirty file IDs use primary-key seeks plus a bounded total-row probe to drop the four query indexes only when existing dirty aggregate rows cover at least three-fifths of the table; an empty pre-refresh aggregate also qualifies for fresh and rebuild runs, while skewed or small updates retain every index. Qualifying runs rebuild the indexes once after the set-based insert in the same transaction. Writable legacy databases create and backfill the table transactionally, while immutable legacy readers retain the raw-reference compatibility path.
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

`deps --symbol`, `--symbol-family`, and the generic-symbol part of
`--suppress-noise` are pushed into the logical-reference and target-candidate
SQL scopes before candidate ranking and `--limit`; cycle and cross-workspace
reads apply the same name filters before their candidate limits. Markdown
heading-name matches are classified separately as
`markdown_heading_name_match` evidence. Suppressed queries prioritize retained
evidence before candidate limits, then remove only that evidence in the CLI
layer, so explicit Markdown path links remain visible even when an edge also
contains legacy heading fanout. Machine-readable edges expose
`source_language`, `origin`, `reference_kind`, `target_kind`, and
`reference_count` distributions in `evidence`; `symbol_filter` adds reference
before/after totals and per-reason affected/removed counts. Generic-symbol
counters still describe the SQL-filtered scope rather than the whole pre-filter
workspace. Long SQLite dependency reads also register command cancellation with
the query token.

Practical consequence: `impact <ClassName>` on a class-like symbol returns the heuristic file-dependency-hint fallback (with metadata edges) when no member-level callers exist, whereas default `callers <ClassName>` returns only executable edges. Both are correct under their own contracts; counts will not match. To reconcile, run `references <ClassName> --kind attribute` (or `annotation`), or pass an explicitly supported non-default kind to `callers` / `callees`, to surface edges that the default call graph intentionally drops.

For a single C# logical partial family on a current reference-identity contract, impact resolution precedes the physical-file ambiguity guard. `ResolveImpactDefinitions` retains the representative output plus a separately bounded set of every physical family symbol ID and path. `GetTransitiveCallers` queries that ID set as one SQL union root, so candidate rows, callers, cycle nodes, and converging paths are deduplicated before BFS accounting; file-hint fallback resolves member names across every retained family path and excludes all declaration files from dependents. Physical family expansion uses `DefaultImpactPartialFamilyMemberBudget`, reported through `partial_family_member_*`, and never sets the ordinary result `truncated` / `truncated_reason` fields. A stale reference-identity contract keeps symbol-root scope and preserves physical multi-file ambiguity because legacy traversal cannot guarantee the family ID/path union. When the family expansion is capped, CLI count JSON marks the count degraded and non-authoritative, and MCP count-only returns `total: null`. Only equal logical partial keys may enter this path; multiple logical keys, including unrelated same-name namespace or language symbols, retain the established ambiguity result.

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
next candidate instead of losing the first log write. Repository-configured
`metrics_path` and `global_tool_log_dir` values from `.cdidxrc.json` or
`.cdidx/config.json` use a stricter boundary: every existing component below
the config workspace is rejected when it is a symbolic link, junction, bind or
cross-device mount point, reparse point, device, or dangling link. The boundary
is revalidated before each mutation; on Linux, path and opened-handle mount IDs
from `statx` or the `/proc/self/fdinfo` fallback also reject same-device bind
mounts. On POSIX, directory creation, append,
permission changes, rotation, replacement, and deletion are additionally anchored
to the workspace directory handle with no-follow relative operations. Directory
parents are rebound from that root after mutation-time validation so a
moved ancestor is not reused. Guarded renames fsync their already-open destination
parent before reporting success. On Windows, the guard retains the workspace root's
final physical path so a retargeted root alias is not followed; root-handle-relative
native opens reject reparse points during name resolution, and guarded replacement
and deletion remain handle/root relative. The same
`global_tool_log_dir` guard covers lifecycle logs, file query traces, and the
bounded `last-failure.json` diagnostic. An unsafe value fails config validation
with the bounded `unsafe_output_path` diagnostic and does not create, append,
rotate, replace, delete, or chmod the external target. Explicit CLI and process
environment destinations retain their existing operator-controlled behavior.
The file name is
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
  that directs clients toward the matching cdidx tools. `summarize_file`
  advertises `path` as required and rejects missing, non-string, blank, absolute,
  drive-prefixed, control-character-containing, or `..`-traversing values with JSON-RPC
  `-32602` before constructing the prompt. Accepted workspace-relative paths
  preserve spaces, Unicode, and POSIX backslash filename characters while
  normalizing Windows path separators to `/`. `logging` advertises
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
  audit log (#1562). Successful authentication now follows the exact request
  through dispatch: a concurrent transport may attach its principal to
  `McpTransportFrame`, and that transport principal takes precedence over a
  successful server-side placeholder identity without bypassing a failed
  server authentication check (#5186). The scope is restored on every exit,
  including notifications, cancellation, deadlines, errors, and serialization
  failures, so concurrent HTTP requests cannot exchange audit attribution.

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

- **Field stability.** `timestamp`, `tool`, `auth_source`, `auth_subject`, `arg_keys`, `arg_lengths`, `elapsed_ms`, `error_code` are emitted on every per-tool record. `caller`, `caller_version`, `request_id`, `request_id_type`, `request_id_length`, `request_id_truncated`, `arg_key_lengths`, `arg_keys_truncated`, `arg_key_truncation_reasons`, `arg_values`, `arg_values_redacted`, `arg_values_truncated`, `arg_values_truncation_reasons`, `arg_values_serialized_bytes`, `arg_values_max_bytes`, `result_count`, `checked_root_identity`, `error` are emitted only when non-null or true; renaming or repurposing any published field is a breaking change, the same policy as the CLI `--metrics` schema.
- **Authenticated principal.** `auth_source` and `auth_subject` identify the request authentication or transport provenance separately from MCP client metadata: `stdio` / `local`, `stdio-token` / `token`, `http-bearer` / `token`, or explicit loopback `http` / `anonymous`. A token subject is deliberately generic: no token, hash, reversible fingerprint, or individual-human claim is logged. Both values are sanitized and bounded, include length/truncation metadata when needed, and survive compact and minimal event-size fallbacks.
- **Request-id privacy.** `request_id` is the process-salted fixed-length token described above, never the JSON-RPC wire value. A present token is accompanied by `request_id_type` and the decoded-value `request_id_length`; the legacy truncation guard is normally absent because the token is already bounded.
- **Error code semantics.** `0` = success, `1` = MCP tool error (`isError: true`), negative = the verbatim JSON-RPC error code (e.g. `-32602` for invalid params, `-32603` for internal error). The companion `error` string is one of `jsonrpc_error`, `tool_error`, `missing_tool_name`, or the sanitized exception type name (`McpServer.BuildSanitizedToolErrorMessage` keeps `ex.Message` out of the wire and out of the audit, #1530).
- **Result count.** `ExtractResultCount` prefers `structuredContent.count` over `structuredContent.results.length`; tool errors and JSON-RPC errors omit the field. Tools that return no count-shaped payload (e.g. `ping`) leave `result_count` absent rather than emitting `0`.
- **MCP index root identity.** `index` resolves the requested root canonically, captures its platform filesystem identity, and retains a no-follow directory handle for the run. Directory enumeration is handle-relative on Linux, macOS, and Windows; the authorized filesystem seam compares each directory/file's pre-open identity, opened-handle identity, and post-open canonical containment before consuming content. Language-map and pattern sidecars are confined to the authorized project tree, opened through that seam, and cached in an authorization-scoped snapshot that excludes wider user configuration and executable workspace plugins. A root, ancestor, link, or entry identity change raises a bounded `permission_denied` tool error with `authorization_failure_reason`; successful/dry-run structured output and every post-authorization audit record carry the same fixed-length opaque `checked_root_identity`, even when the response was built by an error path. A containing repository root is used for ignore rules only when it remains authorized; otherwise discovery is confined to the requested project root.
- **Argument privacy.** `arg_keys` and `arg_lengths` are always recorded so query *shape* is recoverable, but argument-key count and displayed key length are capped and marked with `arg_keys_truncated`. `arg_values` is gated behind `--audit-log-include-values` because cdidx queries can carry literal source snippets or secret-shaped strings. The echo is a sanitized, budgeted clone: secret-like keys classified by the shared diagnostic/audit taxonomy and known token patterns are replaced with `[REDACTED]`, and depth, object-property, array-item, total-node, string-length, serialized-byte, and event-byte limits can mark `arg_values_truncated` before values are written.
- **Client-declared metadata.** Compatibility fields `caller` and `caller_version` contain the bounded name/version from the accepted `initialize.clientInfo`. They describe the currently connected MCP client and are not an authenticated identity; arbitrary client metadata cannot change `auth_source` or `auth_subject`. Failed protocol negotiation never overwrites these compatibility fields or other session state (#4540, #5186).
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
`cdidx`.

| Capability | Configuration |
|---|---|
| Extension aliases | `~/.config/cdidx/langmap.yaml` and the nearest workspace-ancestor `.cdidx-langmap.yaml` |
| Regex-backed symbols | Workspace `.cdidx/patterns/*.yaml` and user `~/.config/cdidx/patterns/*.yaml` |
| Standalone verification | `cdidx test-extractor --language <lang> --file <path> --json` |

### Extension alias precedence

- Workspace entries override user entries.
- A trusted suffix override is evaluated before built-in exact-filename,
  filename-prefix, and extension rules.
- If the closest workspace map cannot be probed or read, lookup stops for that
  subtree instead of reusing a parent map.
- `languages --json` and the MCP `languages` tool expose sanitized failures in
  `language_map_diagnostics` and the effective order in
  `detection_policy.precedence`.

### Pattern sidecar safeguards

| Limit | Value |
|---|---|
| Discovery candidates | 128 per pattern directory |
| Sidecar size | 64 KiB per file |
| Rules per sidecar | 128 |
| Configured rules | 128 per immutable workspace snapshot |
| Worker project-root snapshots | 32 per persistent symbol worker |
| Worker pattern-directory snapshots | 4,096 per root and 8,192 per worker; overflow uses live discovery |
| Regex match timeout | 100 ms |
| Timed-out rule cooldown | At most one minute in the owning workspace snapshot |

- Sidecars must be regular files inside non-symlink pattern directories.
- Each sidecar is parsed, compiled, and checked against `SymbolKindCatalog`
  before its path, rules, or budget are committed.
- Rejected content is fingerprinted to suppress duplicate diagnostics. On paths
  that perform another discovery or explicit refresh, content or metadata changes
  and recovery from a transient read failure trigger a retry without restarting
  the process.
- Workspace discovery requires an explicit trust root and never probes above
  it. Nested sidecars inside that boundary are loaded for the current file by
  the bounded extraction worker.
- A persistent symbol-worker command treats pattern discovery as a project-root
  snapshot. Root reload reads user and workspace-root configs once; each nested
  pattern directory's first result, including missing, unsafe, and known discovery
  failures, remains fixed for the run. Sidecars added or repaired afterward become
  visible on the next worker command, while unexpected exceptions remain retryable.
  A saturated directory cache falls back to uncached discovery and never skips a
  config merely to preserve the memory bound.
- Path identity follows the active filesystem's case-sensitivity, so
  case-distinct sidecars remain distinct on case-sensitive volumes.
- `status --json` reports accepted files in `extractors.pattern_configs[]`,
  including sanitized path, workspace/user provenance, normalized language,
  and rule count.
- Reindexing atomically replaces the workspace snapshot. The old rule budget
  and timeout state can then be collected without affecting other workspaces.
  A timed-out rule emits a workspace-scoped diagnostic before entering cooldown.

### Extractor testing

`cdidx test-extractor --language <lang> --file <path> --json` runs extraction
without building an index. Add `--expect-symbols <json>` to compare the result
with a fixture. Source and expectation files are each capped at 4 MiB.

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
| `make mcp-smoke` | 1回ビルドし、そのconfigurationの出力からMCP helpを実行。 |

net9 CI lane に合わせる場合は `FRAMEWORK=net9.0 make test` を使います。`make` がない
環境では、同じタスクを `./dev.sh build`、`./dev.sh test` などで実行します。

開発上の契約:

| 項目 | 契約 |
|---|---|
| formatting と warning | CI は `.editorconfig` による repository formatting を強制し、`Directory.Build.props` により compiler warning を error として扱います。ローカル変更は PR 前に formatting check を通してください。既存の trim 解析警告は、通常の警告エラー化を止めずに修正を進められるよう `WarningsNotAsErrors` に明示列挙されています。ILLink は trimmed publish の smoke test を失敗させずに trim warning を報告し続けます。 |
| CLI help | `cdidx --help` は短い概要、`cdidx --help-all` は全コマンド・flag・例の一覧、`cdidx --help-flags` は共有 flag table のみ、`cdidx <command> --help` は 1 コマンドの usage line を出します。新しいコマンドは主要な user workflow である場合だけ簡易概要に載せ、full help とコマンド固有の usage table には必ず載せてください。 |
| `index --dry-run` の mutation 推定 | dry-run は厳密に非変更のままにし、database の作成・lock や source / index artifact の変更を行いません。read-only な stat / checksum と production の cap issue、symbol filter、extractor version、graph contract、extractor / config の強制 refresh、hotspot marker の trust、C# workspace の再利用条件を組み合わせて未変更 skip を判定し、update、内容に対する policy skip、delete、purge、symbol / reference 上限到達を予測し、update 予定 file のうち最大100件に通常の chunk / symbol / reference / content diagnostic 抽出を適用します。scoped な `--files`、`--commits`、`--changed-between` preview は read-only C# preflight を実行し、production と同じ static-interface / 修飾 member-read 検出を再利用して、件数・上限・sample を確定する前に candidate path を展開します。`projection_authoritative`、`projection_unavailable_reasons`、C# workspace 展開の status / reason は、candidate cap、scan failure、snapshot 不足、利用不能な preflight によって安全な展開ができない場合の lower bound と正確な projection を区別します。`estimated_table_mutations` は最終 table row 数ではなく、delete と insert の予測 row operation 数です。nullable な値は `estimated_table_mutation_details` と常に同期させます。`source` は filesystem plan または parse-only と index snapshot の入力元、`confidence` は `exact` / `estimate` / `unknown`、安定した `unknown_reasons` は candidate の切り詰め、parse 推定の切り詰め・失敗、C# workspace の展開、index snapshot の読み取り不能、partial index の table 不足を表します。数値のゼロは、処理がないと計測できた場合だけに使い、その信頼度も併記します。parse-only 推定では extraction 後の hook mutation と preflight 後の cross-file child-row materialization を意図的に省くため、C# workspace 展開が適用された場合は不完全な parse 合計を残さず child-table metric を明示的な unknown にします。 |
| `cdidx validate` | replacement character、BOM、NUL byte、混在改行、UTF-16 BOM、非 UTF-8 らしい内容など、indexed content の問題を user-facing に検査する integrity scan です。validation issue の種別や filter を追加する場合は、CLI usage、README entry、help summary を同期してください。 |
| `cdidx doctor` | support request 向けにコピーしやすい environment summary です。既定では redacted に保ち、secret 風の `CDIDX_*` 値は出力しないでください。新しい diagnostic field は issue triage に使える程度に安定したものだけにします。full environment inventory の filter（`--env-domain`、`--env-category`、`--env-sensitivity`）は大文字小文字を区別しない完全一致で AND 合成し、filtered JSON summary は global catalog ではなく返却 inventory を表します。`--max-json-bytes` は `--json --env-inventory=full` または `--integrations --json` と組み合わせ、serialize した UTF-8 文書と改行を数え、上限を超える成功文書の代わりに structured usage error を返します。`github` block は `proxy_default_credentials` を `enabled` / `disabled` として出力し、bounded な `max_request_timeout_s` も出します。proxy credential material や raw secret value は出力しないでください。`license --json` は version 付きの `license`、`commercial_use`、`trademark`、controlling `documents` contract を返します。 |
| 例外診断 | user-facing な CLI / JSON / MCP / file issue / local diagnostic output では raw `ex.Message` を直接 echo しないでください。例外の prose は `CommandErrorWriter.FormatSanitizedExceptionMessage`、`DiagnosticSanitizer.ForMessage`、または既存の bounded な `DiagnosticRedactor` helper を通し、回復に message が不要な場合は安定した error code/category を使ってください。意図的に残す broad catch は `risky-code/broad-exception-catch` taxonomy に沿い、bounded diagnostic、private な best-effort suppression、または documented fallback に正規化してください。 |
| shell completion | 生成された shell completion script には、生成元の `cdidx` version comment が含まれます。completion candidate は `CliFlagSchema` を基準にし、`ValueKind` / `CommandValueKinds` が path、project、repository、language、symbol kind の文脈別動作を選び、`ValueDomain` / `CommandValueDomains` は網羅的な有限候補を定義し、`SubcommandValueDomains` は親 command の候補を広げずに nested verb 固有の候補へ絞り込みます。`CompletionSubcommands` は flag を正確な nested verb に限定し、`ParentCompletionCommands` は有効な既定の親操作でもその flag を維持します。`SupplementalCompletionValues` は path または `github` のような混合入力で実在する予約 literal を維持します。command context と nested-command context を解決する前に、schema 定義済みの先頭 global option と、その分離形式または inline 形式の値を読み飛ばしてください。`<name\|path>` のような表示用 placeholder は metavariable であり、候補へ分解してはいけません。専用 parser / help inventory と生成された Bash、zsh、fish、PowerShell の各 context には双方向 test を置き、公開された受理 flag がすべて登録・生成されること、および sibling context が拒否される flag を提示しないことを検証してください。command や flag の schema を変えた場合はそれらの completion test を更新し、upgrade 後に installed completion を再生成する README guidance も保ってください。 |
| target framework | 製品版 CLI と NuGet tool packaging は `net8.0` を対象にしています。test project は `net8.0;net9.0` の multi-target で、CI は Linux、Windows、macOS の各 lane で両方の framework に対して test suite を実行します。CI 相当の full matrix を検証する場合は、両方の target framework を restore / 実行できる .NET SDK を使ってください。 |
| SDK selection | `global.json` は repository SDK を `9.0.301` に固定し、`rollForward` を無効化します。CI は `8.0.413` と `9.0.301` を明示的に install します。`8.0.413` は `net8.0` runtime lane を提供し、`9.0.301` は restore、build、test、publish、changelog 検証で選択される SDK です。SDK を更新する場合は、`global.json`、すべての `actions/setup-dotnet` version list、Docker build image、この guide を同じ変更で更新してください。 |
| GitHub Actions policy | workflow は hosted runner を version 付き label（`ubuntu-24.04`、`windows-2022`、`macos-14`）に固定し、top-level の `contents` permission は既定で read-only に保ちます。`continue-on-error` は failure path の diagnostic artifact upload に限定し、すべての upload artifact に明示的な retention を付け、artifact download は pattern と path で境界を絞ります。NuGet cache key は workflow + runner OS + 到達可能な `packages.lock.json` の厳密な restore graph を使い、`global.json` と無関係な lock を明示的に除外し、広い restore-key fallback を避け、version 固定 tool cache を分離します。`PackagesLockTests` が厳密な restore/cache graph 契約を所有し、`CiWorkflowTests.GitHubActionsWorkflows_FollowRunnerArtifactCacheAndContinueOnErrorPolicy` が一般的な workflow checklist を強制します。 |
| test diagnostics | CI は `tests/CodeIndex.Tests/CodeIndex.Tests.runsettings` と VSTest の crash/hang blame collection、上限付きの 1 回だけの retry を使い、再現性のある失敗と retry で通る flake を区別します。Build and Test workflow は `ubuntu-24.04` / `net8.0` を補完的な coverage shard に分割します。Windows と macOS も同じ補完的な net8 分割を coverage overhead なしで使い、Ubuntu net9 compatibility lane は full suite を実行します。test suite の構成、共有 helper、state-isolation rule、timeout diagnostics、test-writing convention については [TESTING_GUIDE.md#テストガイド](TESTING_GUIDE.md#テストガイド) を参照してください。 |
| mutation testing | weekly の `Mutation testing` workflow は `stryker-config.json` を使い、`src/CodeIndex/Database/DbWriter.cs` に対して Stryker.NET を実行します。runtime budget を意図的に広げる場合を除き、transaction、savepoint、rollback、batch-write behavior に scope を集中させてください。workflow は pinned `dotnet-stryker` 4.14.0 tool と NuGet package を cache し、cache miss のときだけ tool を update します。mutation score gate は high 75、low 70、break 65 で、rollback や savepoint coverage を弱める変更は通常の PR test path の外で失敗します。 |

## CI / アーティファクト配布

| workflow | コマンドまたは設定 | 注意点 |
|---|---|---|
| read-only database query | `cdidx status --db /artifacts/codeindex.db --read-only --json`; `cdidx search AuthService --db /artifacts/codeindex.db --immutable` | query コマンドは `--read-only`（alias: `--immutable`）を受け付け、既存の CodeIndex database を SQLite の immutable read-only URI mode で開けます。CI artifact、mounted cache、`codeindex.db-wal` / `codeindex.db-shm` sidecar を作成・更新できない sandbox で使います。 |
| 変更系コマンド | `index`、`backfill-fold`、`optimize`、`vacuum` | 書き込み可能な storage を必要とし、read-only database open を拒否します。 |
| 再利用可能な index artifact | `cdidx export codeindex.cdidx.zip`; `cdidx import codeindex.cdidx.zip --db <path>`; `cdidx import codeindex.cdidx.zip --dry-run --json` | CI job では index 後に export して archive を upload します。export は `--overwrite` を明示しない限り既存 destination を拒否し、owner-only temporary file から atomic に publish して POSIX mode `0600` を検証します。export 成功時の JSON は従来 field を維持し、最終 archive の byte 数と SHA-256、完全で immutable な manifest を追加します。利用側は query コマンドの前に import でき、`--dry-run` / `--check` で destination DB を置き換えず archive を検証できます。別 checkout 由来の archive を import 先 project root として扱いたい場合は `--prune-paths` を使います。`.../.cdidx/codeindex.db` を import 先にした場合は sibling の project directory を使い、それ以外の DB path では process current directory に fallback します。archive は `manifest.json` と `codeindex.db` だけを含みます。import は ZIP entry 名を `ZipArchiveSafetyPolicy` で検証し、absolute path、parent-directory segment、backslash、NUL、non-canonical name、duplicate entry、extra entry を extraction 前に拒否します。manifest は row count、readiness bit、writer / indexed-head metadata、schema contract stamp、利用可能な unknown-extension summary などの bounded summary/readiness metadata を持ちます。import は manifest format、manifest `user_version`、`database_sha256`、存在する summary count、embedded SQLite file が CodeIndex database であることを検証してから destination DB を置き換えます。archive の `codeindex.db` entry は compressed / uncompressed metadata と extraction stream の双方で 8 GiB を上限に拒否されます。 |
| maintenance checkpoint と managed rollback | `cdidx db checkpoint <name> [--dry-run]`; `cdidx db checkpoints --list|--delete <name>|--prune --keep <n> [--dry-run]`; `cdidx db restore <name> [--dry-run] [--no-backup]`; `cdidx db restore-backups --list|--prune --keep <n>|--restore <id> [--dry-run] [--no-backup]` | 危険な maintenance の前に `codeindex.db` と既存 WAL/SHM sidecar の checkpoint を作成できます。import と2種類の restore は、既存 DB を置き換える前に consistent かつ検証済みの managed SQLite rollback snapshot を既定で作成し、`--no-backup` を明示した場合だけ省略します。managed directory は `<db>.restore-backup-<id>/` で、bounded manifest と standalone database payload 1個を含みます。manifest は SHA-256、byte 数、対応する `user_version`、provenance、任意の source identifier を記録しますが、local absolute source path は記録しません。`restore-backups --list` は従来 directory metadata との互換性を維持しつつ ID と provenance を表示し、既存 prune retention もそのまま利用できます。`restore-backups --restore <id>` は directory 境界、manifest、payload hash、schema、staging と rollback を合わせた free space を再検証してから、失敗時の transient rollback を伴う atomic replacement を実行します。`--dry-run` は変更せず、すべての検証と作成予定 backup を報告します。checkpoint の delete / prune と restore-backup の prune は明示的な変更 action を必要とし、checkpoint prune の bounded scan が truncated の場合は削除をすべて skip します。checkpoint は `<db>.checkpoints/<name>/` に置かれます。`backfill-fold` は index lock 内で folded row、永続化されたfolded値、readiness を事前確認し、mutation が必要な場合だけ automatic checkpoint を作ります。完了済みDBでもsnapshotを明示的に残すには `--checkpoint`、mutation protectionを明示的に省略するには `--no-checkpoint` を使います。JSON は `checkpoint_skipped` と `checkpoint_skipped_reason`（`already_complete`、`dry_run`、`disabled_by_option`）を返し、human output も同じ判断を表示します。 |
| checkpoint operation plan | `cdidx db checkpoint <name> --dry-run [--json]` | dry-run は immutable な plan を作成し、source DB/WAL/SHM の file と byte 数を、version 付き `manifest.txt`、その SHA-256、最終 byte 数の見積もり、destination/conflict の状態と policy、sidecar/compression/metadata policy、残る不確実性を含む全 output から分けて報告します。実行時は現在の source から新しい plan を作成し、copy 前後の source metadata と SHA-256、および作成予定の全 output を検証して、入力が変化した場合は atomic publish を拒否します。従来の JSON `files` / `bytes` field は維持されます。明示的な contract には `source_files` / `source_bytes`、`planned_output_files` / `estimated_output_bytes`、`final_output_bytes` を使用してください。 |
| binary compatibility | [COMPATIBILITY.md](COMPATIBILITY.md) | `cdidx` binary の upgrade / downgrade をまたぐ database compatibility を記載します。readiness bit、`codeindex_meta` contract stamp、rebuild requirement を変える場合は、この policy も更新してください。 |
| Fold backfill の preview / recovery | `backfill-fold --dry-run`; `backfill-fold --checkpoint`; MCP `backfill_fold` の `dry_run: true` または `force: true` | dry-run は DB を変更せず FoldReady stamp も書かずに、rewrite 対象の folded-key row をプレビューします。CLI preflight でmutation不要と判断された場合でもsnapshotを明示的に保存するには `--checkpoint` を使います。既定の完了済みno-opはcheckpoint artifactを作りません。MCP も同じ preview を受け付け、stored version / fingerprint が current に見える場合でも suspicious な fold metadata や row state を復旧するため `force: true` を受け付けます。non-dry-run rewrite は中断後に resume でき、完了済み row update は durable に残り、最終 FoldReady metadata は verification 成功後にだけ stamp されます。MCP response は `progress.rows_done`、`progress.rows_total`、`progress.fraction` を含みます。 |

portable archive の trust は scope を考慮します。filter なし export は `scope.represents_entire_source_database` を設定し、completeness、indexed-HEAD、run、unknown-extension metadata を維持します。filter 済み export は archive snapshot だけを `index_complete=false` / `partial_archive` に正規化し、source 全体に対する HEAD / run provenance を削除して、未計測の unknown-extension summary を省略します。scope metadata がない legacy manifest は import 時に同じ正規化を行い、現行の full-snapshot manifest は trust を維持します。partial archive に対する scoped index は `partial_archive` を解除する前に full workspace scan へ fallback します。

portable archive の path privacy は互換性のため opt-in です。既定 export は
`manifest.project_root`、snapshot の `indexed_project_root`、指定 scope value、解決済み
success path を保持します。`--redact-paths` は private な copy 済み snapshot だけを変更します。
最初に scope を解決・適用し、copy 側の project root を削除して、POSIX / Windows /
file URI 形式の絶対 scope value と永続化済み path sample を `[redacted]` に置換します。
flat / group 別 sample の両方を対象とし、不正または上限超過の path metadata は fail-closed
として copy から削除するか空にします。workspace verification の pending-path identity を失う場合は、
coverage marker も incomplete に stamp します。scope と redaction の後に最終 `VACUUM` を一度だけ実行してから
`database_sha256` を計算します。成功時の manifest と
export JSON では `path_redaction_requested`、`path_redaction_complete`、上限付きで安定した
`path_redaction_omitted_categories` を同期させます。redacted success output は解決済み
archive / database / source-root path を再表示してはいけません。repository-relative な
indexed path、source content、hash、readiness、commit provenance は redaction 対象外です。
import は destination DB path / current directory から destination project root を導出し、
source root がない archive を execution / dry-run の双方で許容する必要があります。
`path_redaction_complete=true` を受け入れる import は、その claim を complete と報告する前に
manifest の root / scope value と既知の embedded path metadata を検証する必要があります。

checkpoint plan の drift 検出は、publish 前の最終検証までに起きる DB/WAL/SHM content の変更と sidecar の出現・消失を対象にします。plan の `uncertainty` 値は最終検証後に残る race を記録し、copy 済み output は atomic publish 前に plan の hash と個別に照合されます。DB/WAL/SHM 候補は hash 読み取り前に native regular-file type validation を通すため、Unix FIFO が plan 作成を停止させることはありません。`metadata_policy` は POSIX では `owner_only_files_and_directories`、Windows では実際に継承される `inherited_windows_acls` policy を報告します。database payload の output 名が filesystem 上で同一の大小文字を含め `manifest.txt` と衝突する場合、plan は not ready となり、変更前に拒否されます。

## ファイルシステム権限

| artifact | POSIX permission / behavior |
|---|---|
| `.cdidx/` | mode `0700` で作成。 |
| `codeindex.db` と WAL/SHM sidecar | ファイルが存在する場合は mode `0600` を適用。既定は best-effort で、`CDIDX_DB_PERMISSION_POLICY=strict` により strict enforcement にできます。 |
| `suggestions-*.json` suggestion store | POSIX では owner-only の mode `0600` で atomic write します。 |
| portable export archive | indexed source text を含むため `Sensitive` profile で atomic write し、POSIX では owner-only の mode `0600` であることを検証します。既存 destination の置換には明示的な `--overwrite` が必要です。 |
| import staging database と WAL/SHM sidecar | non-dry-run import は hidden な同一 filesystem 上の staging DB を `FileMode.CreateNew` と POSIX mode `0600` で作成してから、最初の archive byte を書き込みます。main DB と SQLite WAL/SHM sidecar は、extraction、manifest / SQLite validation、trust metadata rewrite、任意の path pruning、rollback backup 作成、atomic replacement の各段階で owner-only に再 hardening して検証します。dry-run staging は owner-only temp directory と同じ private-file 作成経路を維持します。 |
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
dotnet ./src/CodeIndex/bin/Debug/net8.0/cdidx.dll search File.Delete --path src/ --exclude-tests --exact-substring --count-by file --limit 80 --db .cdidx/codeindex.db
dotnet ./src/CodeIndex/bin/Debug/net8.0/cdidx.dll search Directory.Delete --path src/ --exclude-tests --exact-substring --count-by file --limit 80 --db .cdidx/codeindex.db
dotnet ./src/CodeIndex/bin/Debug/net8.0/cdidx.dll search File.Move --path src/ --exclude-tests --exact-substring --count-by file --limit 80 --db .cdidx/codeindex.db
```

| surface | ownership / boundary policy | cleanup / rollback policy |
|---|---|---|
| `AtomicFileWriter` の file delete / move helper | caller が destination を承認または検証した後の出力 path に使います。temp file は target の sibling として衝突しにくい名前で生成するため、置換は同じ filesystem boundary に残り、別 temp root の policy には依存しません。 | 書き込みは temp file を flush し、target に rename し、Unix では parent directory を flush します。move 前の temp cleanup は best-effort です。置換後の parent-directory flush failure は、target がすでに変わっているため command failure として明示します。 |
| `cdidx import` / `cdidx export` の temp database と sidecar | import temp DB は destination DB directory 内の hidden sibling、または dry-run 用の owner-only `codeindex-import-*` temp directory に置きます。hidden import sibling は extraction 前に衝突を拒否する `CreateNew` と owner-only POSIX create mode を使い、staged DB/WAL/SHM 一式を validation / rewrite 中も再 hardening しつつ、同一 filesystem 上の atomic replacement を維持します。export snapshot は owner-only `codeindex-export-*` temp directory に置きます。destination DB replacement は export output が source DB / sidecar と重なる path を拒否し、backup sidecar 経由で rollback します。 | temp DB、WAL、SHM、空の temp directory の cleanup failure は warning として出し、import/export の primary error を隠しません。replacement failure では destination state を確認できる residual diagnostics を報告します。 |
| `cdidx db` checkpoint restore staging と restore-backup prune | checkpoint、restore staging、restore backup は解決済み DB path から派生します。recursive cleanup は DB parent を safe root とし、`codeindex.db.restore-*` 形式の expected prefix を渡して `FileSystemBoundary.TryValidateDirectoryCleanupTarget` で検証します。checkpoint payload file は regular file である必要があり、symlink、reparse point、device は拒否します。 | restore は置換前に backup を作り、失敗時は rollback を試みます。temp directory cleanup と restore-backup prune の失敗は bounded diagnostic または warning になり、検証済み root の外側は削除しません。 |
| upgrade installer script と temp directory | upgrade download は `Path.GetTempPath()` 配下の owner-only `cdidx-install-*` directory を使います。recursive directory cleanup は削除前に temp root、required prefix、symlink / reparse / device 状態を検証します。install-directory write probe は、root、symlink / reparse point、unsafe POSIX mode を拒否する install-directory validation の後だけ実行します。 | installer script と temp-directory cleanup の失敗は warning です。install operation の結果は、二次的な cleanup failure とは分けて報告します。 |
| `.cdidx` write probe と case-sensitivity probe | write probe は、解決済み install directory、`.cdidx` directory、または `.cdidx/probes` directory 配下に fresh file として生成します。probe directory は owner-only で作成され、workspace data directory 配下にあります。 | probe file は確認後に削除します。case-sensitivity probe directory の cleanup は、作成済みの空 probe directory を削除する前に workspace data / probe root、期待する directory name、symlink / reparse / device 状態を再検証します。拒否した cleanup は bounded diagnostic を記録し、`cdidx` process が動いていないときに stale `.cdidx/probes` entry を削除するよう案内します。 |
| legacy scan checkpoint | full scan は `.cdidx/scan-checkpoint.json` を作成も参照もせず、最初の immutable scan-input barrier 後に success / partial のどちらでも旧fileをparseせず削除します。 | delete failure は human output では warning、JSON output では `CliJsonMessage` entry です。indexing は古いHEAD-only stateに依存せず継続します。 |
| Git hook staging | hook installation はまず current invocation を絶対かつ実行可能な executable path に解決します。`dotnet` launcher は symlink の leaf を解決する前の host 分類を維持し、host の場合は絶対 `cdidx.dll` も固定します。その他の symlink executable leaf は最終 target に解決し、すべての argv / path を POSIX shell 向けに quote します。managed block は上限付きの versioned provenance manifest と目視可能な source / version comment を保持し、生成 script 全体が preview または書き込み前に同じ 64 KiB の management-read 上限内であることを要求します。その後、操作を create、managed replacement、custom-hook chain、exact no-op のいずれかに分類します。`hooks install --dry-run` は directory 作成や staging より前に plan、managed script、executable diagnostics を返し、実際に変更する場合は repository hook directory 内に private staged hook scriptを書き込み、必要に応じて backup path 付きの `File.Replace` で hook file を置き換えます。 | 同一かつ実行可能な UTF-8/no-BOM の managed hook に対する再実行は書き換えず `already_installed` を返します。それ以外の encoding や実行不可の managed hook は Git が実行できるよう置き換えます。`hooks status` は保存済み invocation を parse しますが実行せず、まず source / argv の個数整合性と managed block / manifest の一致を確認してから path availability と POSIX の実効 execute access を検証します。current version は同じ実行中 invocation または上限付きの隣接 `version.json` からだけ取得し、不正形式、tamper、missing、検証不能、version skew を support-safe な diagnostic argv とともに報告します。staged script が配置されなかった場合の cleanup は best-effort で、hook warning として記録します。managed hook の削除失敗は requested mutation の失敗なので command error です。 |
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
graph readiness を報告します。project directory と database の存在有無は sibling field の
`project_exists` / `db_exists` で明示し、従来の `exists` は `project_exists` の互換 alias として
維持します。member ごとの `index_health` は安定した reason と構造化 `repair_action` も返します。
1 回の実行で probe する既存の異なる member database は最大 64 個で、
`single` strategy で database が共有される場合は probe 結果を再利用し、それ以降の member は
`not_checked` として top-level の truncation summary に反映します。

このリポジトリで追跡している manifest は、意図的に `index_strategy: single` を選択します。正規の dogfood topology はリポジトリルートを `.cdidx/codeindex.db` に index し、source、tests、ルートの documentation、scripts、workflows、agent policy を1つの検索可能な database に保ちながら、`workspace status` がすべての宣言済み member に同じ database を再利用します。リポジトリの dogfood では、CLI query、MCP / LSP、maintenance command（`optimize`、`vacuum`、`db integrity` と関連 preview）のすべてに `--db .cdidx/codeindex.db` を明示します。`CDIDX_DATA_DIR` と active workspace の設定は CWD discovery より優先されるため、リポジトリルートで実行するだけでは database を固定できません。リポジトリ検証では明示的に固定した `status --check --json` と manifest 駆動の `workspace status --check --json` の両方を実行し、per-member database は作成しません。

```bash
dotnet ./src/CodeIndex/bin/Debug/net8.0/cdidx.dll status --check --db .cdidx/codeindex.db --json
dotnet ./src/CodeIndex/bin/Debug/net8.0/cdidx.dll workspace status --check --json
```

`member_health_summary` は healthy / degraded / missing member を集約し、enforcement 時の exit code と
重複排除した recommended action code を返します。`cdidx workspace status --check` はすべての
required member が `ready` の場合だけ success `0`、manifest 不在、member 0件、required project /
database が1件でも missing の場合は not-found `2`、それ以外の degraded aggregate（stale、
incomplete、incompatible、invalid、unavailable、probe limit による未確認を含む）では
stale-index `5` を返します。mixed workspace では missing が degraded より優先されます。
`--check` なしの `workspace status` は informational なままで、report 成功時は `0` です。
不正な manifest schema / safety validation は従来どおり usage exit `1` で、JSON mode では
top-level crash handler へ落とさず構造化 `workspace_manifest_invalid` error を返します。
repair command は shell quoting 済み文字列ではなく `name` と `args[]` で返すため、
Windows / POSIX shell 間でも path を安全に扱えます。

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
| worker protocol JSON | isolated worker の stdin frame は `BoundedLineReader` で読みます。symbol-worker client は小さい request metadata を直接 UTF-8 に serialize し、source content を固定長の pooled buffer で JSON escape しながら改行区切り request を process stream へ書き、source 規模の JSON byte array を保持しません。writer は出力 byte 数を数え、合意済み上限を超える前に frame を拒否します。worker は response を stdout stream へ直接 serialize し、client は bounded response frame を UTF-8 byte のまま読み取って直接 deserialize します。これにより source file ごとに両方向で発生する追加の source 規模 JSON string / encoding buffer を避けます。既定の frame 上限は文字数・UTF-8 byte 数ともに 32 MiB です。大きな `--max-file-bytes` によって JSON escape 分の余裕が必要な場合、protocol frame 上限は `WorkerProtocolLineLimits.MaxExtendedLineUtf8Bytes`（384 MiB）まで拡張できますが、`int.MaxValue` までは拡張しません。`WorkerProtocolJsonValidator` は `JsonDocument.Parse` の前に合意済みの文字数 / UTF-8 byte 上限を超える payload を拒否し、`DefaultMaxJsonDepth`（32）で parse し、object property 1,000,000 件超と frame 上限を超える string を拒否します。 |
| user regex find | `find --regex` は lookaround / backreference 互換性のため classic .NET regex engine を維持し、`RegexOptions.CultureInvariant` を付け、`--exact` でない場合は `IgnoreCase` も付け、各 match に `BoundedRegex.DefaultMatchTimeout` を使います。timeout は CLI JSON で `E014_REGEX_MATCH_TIMEOUT` / `regex_timeout` として返り、人間向け出力にも同じ recovery hint が出ます。`find --all` は index 全体を走査する前に candidate file と line scan の上限も適用します。 |
| shared regex construction | production の regex 構築は `BoundedRegex`、`RegexRegistry`、または `RegexTimeoutPolicy` に集約します。extractor pattern と bounded static regex API には `BoundedRegex`、timeout 例外を維持する必要がある raw BCL regex factory（`find --regex`、ignore glob regex、generated-code path pattern）には `RegexRegistry`、diagnostic / redaction surface には `RegexTimeoutPolicy` を使います。`RegexRegistry` は ignore glob timeout（100 ms）、generated-code pattern timeout（50 ms）、および `BoundedRegex.DefaultMatchTimeout` を使う find-regex factory の名前付き policy を所有します。search-audit recipe は `BoundedRegex` alias と `RegexRegistry.cs` だけを集約済みの positive evidence と見なすため、新しい production raw constructor は明示的な factory または generated-regex entry とテストを伴う必要があります。 |
| filesystem traversal helper | `FileSystemTraversalPolicy` は top-directory-only enumeration を明示し（`IgnoreInaccessible=false`、暗黙の再帰なし）、任意指定の `CancellationToken` / entry budget option を公開します。想定内の traversal failure は中央で分類し、command diagnostic が permission、I/O、invalid-path、unsupported-path、path-too-long、budget-exceeded の taxonomy を共有します。既存 child の case probe は `CaseSensitivityProbeDirectory.MaxExistingChildProbeEntries`（4,096）を上限とする1つの exact-name set だけを保持し、truncation 時は unknown を返して caller の isolated-write または cached root-policy fallback に委ね、利用可能な cancellation token を伝播します。 |
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

明示された数値 `index` オプションは、database setup や worker 起動より前に検証します。
許容範囲（両端を含む）は `--parallelism` が 1..16、`--max-file-bytes` が
1..2147483647 byte、`--max-symbols-per-file` が 1..50000、
`--max-references-per-file` が 1..1000000、`--dry-run-path-limit` が
1..1000000、`--watch-pending-path-limit` が 1..262144、`--debounce` が
0..60000 ms です。範囲外のゼロ、負数、overflow、非数値、上限超過を明示すると、
構造化された `E010_USAGE_ERROR` と exit code 1 を返し、後続の重複指定が有効でも先行する
不正値を隠しません。互換性のため、不正な `CDIDX_INDEX_PARALLELISM`、
`CDIDX_MAX_FILE_BYTES`、`CDIDX_INDEX_WATCH_PENDING_PATH_LIMIT` は引き続き警告して
fallback または clamp しますが、JSON warning には環境変数由来であることと実効値を含めます。

読み込んだ text file は UTF-16 上の1回の走査で正規化と解析を行います。得られた
`NormalizedContentFacts` を、正規化後の行数、長すぎる行 / FTS token の診断、conflict
marker、replacement character の件数 / 行、80行・10行 overlap の chunk slice に対する
共通の情報源とします。full scan、scoped update、dry-run、MCP indexing は全言語共通の
loader、validation、extraction、persistence 経路でこの facts を引き回し、consumer ごとの
content 再走査を避けます。normalized facts を持たない caller 用の互換 overload は維持しますが、
通常の indexing をその経路へ戻さないでください。80行以下の file は chunk 境界 array を保持せず、
短い file は発生し得ない FTS token 追跡を省き、高比率の invalid UTF-8 decode replacement は
破損行ごとの番号ではなく `non_utf8_likely` に必要な集約件数だけを保持します。

isolated symbol extraction は全言語で1つの byte-oriented newline protocol を共有します。
parent は各 request を引き続き `JsonSerializer.SerializeToUtf8Bytes` で1回だけ serialize し、
その byte 列と LF を書き込み、上限付き UTF-8 response frame を読みます。child は標準入力を
raw stream として開き、`BoundedLineReader.ReadUtf8LineAsync` で読み取り、byte payload を検証して
その span から直接 deserialize します。`TextReader` overload は診断専用です。本番経路へ
`Console.In` や decoded string の copy を戻さないでください。negotiated frame / byte 上限、
JSON の depth / property / string 上限、buffer 境界をまたぐ場合を含む CRLF の除去、終端改行の
ない最後の frame、安定した EOF、pending read の cancellation を維持します。不正 UTF-8 と
malformed JSON は request content や secret を反射せず、sanitization 済みの exception category
だけを返してください。この契約は built-in / custom pattern configuration を含め、symbol worker
へ routing されるすべての language request で共有されます。

parallel full scan は extraction 本体を共有dynamic claimで配分します。入力末尾の大きなfileが
最後のworker waveまで開始されないことを防ぐため、末尾の
`min(4 * workers, 64)` work itemだけをprobeし、size取得済みかつ上限内のfileを大きい順に
claimします。同一size、metadata取得不能、設定size上限を既に超えるfileは元順を維持します。
workerはこのschedule済みsuffixを未scheduleのprefixより先に消費するため、末尾で最大の対象候補も
最初のworker waveへ入ります。schedule消費後はprefix ordinalを元順で再開し、両segment全体で
exactly-onceのpermutationを作ってから既存のsparseな論理file mappingを適用します。target arrayと
論理file indexは並べ替えず、serialなhook/filter経路はprobeせず、bounded completion queueは
引き続き完了順でpublishします。network/virtual filesystemで全file metadata passへ退行しないよう、
tail probeとschedule stateをrepository規模に依存しない固定上限に保ってください。

parallel full scan の worker は、symbol preparation の状態も single persistence consumer へ
引き渡します。worker が解決した family-scope key と完了済みの C# source observation を再利用し、
file ごとに再解決・再観測しないでください。nullable key と `FamilyScopeApplied` は分離します。
resolver が正当に `null` を返す場合と、symbol cap などで scope 未適用の payload は同じ key 値に
なり得るためです。symbol cap は完了済み C# observation だけを、reference cap は両 stage を、
generated-code suppression はどちらも未完了として引き渡します。serial の filter/hook fallback は
consumer 側で各 stage を1回だけ実行し、hook mutation より前に family key を渡して、再構築後の
family-key parity を維持してください。この handoff は parallel full scan 専用です。scoped update、
MCP、dry-run の既存 preparation boundary へ一般化しないでください。

scoped update でも、immutable な C# prepass が authoritative で static-interface
contract を検出した場合は extraction を並列化できます。この経路は snapshot 済み C# target
が2件以上、`--parallelism > 1`、symbol-kind filter 無効、content-load test seam なし、
post-extraction hook なしの場合だけ有効です。各 window は連続する snapshot 検証済み C#
target だけで構成し、ambiguous language の再利用値は nullable のまま保って content-aware
detection が target を拒否できるようにします。最大
`min(parallelism, authoritative snapshot count)` の固定 worker pool が、1 window あたり
`2 * workers` 以下の payload を処理します。window 内の extraction がすべて完了してから、
single consumer が target 順に永続化します。SQLite write、hook、readiness 変更、byte accounting、
user progress はすべて consumer 側に残します。file stat は schedule 前、load 後、persist 直前の
3箇所で検証し、probe failure または対象外境界では通常の serial per-file error boundary へ戻します。

`2 * workers` は件数上限であり byte 上限ではありません。slow tail があると、ordered write
barrier の完了まで他の抽出済み chunk / symbol / reference payload を保持します。worker は
extraction 後に raw file byte を保持しません。上限を超えた collection は publish 前に破棄し、
symbol cap では downstream payload も破棄して、上限内で残る payload、issue、source-contract
evidence だけを保持します。peak RSS と large-file parity を測定せずに
window を拡大したり payload copy を増やしたりしないでください。cancellation は queued work を
中断し、実行中 worker の収束は非同期に待ちます。global no-progress watchdog は phase と validated
load の進捗を監視し、terminal stall では active phase と必要な readiness / batch marker を保持します。
後続 fatal を terminal にする前に、source-negative workspace では、それ以前の target の
source-contract candidate が positive または未評価なら natural serial suffix を再試行します。
永続 source evidence に反映するのは symbol で確認済みの evidence だけで、lexical candidate は
保守的な serial fallback の契機にだけ使います。

single writer は、複数行の chunk、symbol、reference、reference-line command を
row-count shape ごとに bounded な `PreparedCommandCache` で再利用します。SQL text と
型付き parameter schema は cache miss 時だけ構築し、各実行では ordinal 順に parameter
value を再設定します。大規模 index で同じ SQLite command を file ごとに再構築しないよう、
新しい bulk-write 経路もこの bounded cache に載せてください。

fresh な CLI scan と明示的 rebuild は、raw reference の永続化が完了するまで
`symbol_references` の query / graph 用 secondary index を遅延します。candidate-symbol の
reverse lookup は raw persistence 中は維持し、実際の graph refresh が candidate row を削除・
構築する直前だけ外すため、marker-only / 高 cardinality no-op update はこの index 全体を
再構築しません。candidate 構築は `(reference_id, symbol_id)` primary key を使い、reverse B-tree
を行ごとに保守しません。candidate 構築完了直後かつ独立した resolution command の rent / prepare
前に `idx_symbol_ref_candidates_symbol` を復元し、target fact の materialization は bounded な
`(symbol_id, reference_id)` existence seek を使います。reference scope の materialization /
resolution に使う candidate primary key は維持します。load 中も通常は file と reference-line の
保守用 index を残します。唯一の例外は、
transaction-local な再確認後も fresh-resolution claim を所有する authoritative な空DB初回CLI
full scan です。この経路だけは raw reference の永続化中に
`idx_symbol_refs_reference_line` と `idx_reference_lines_file_line` も遅延し、candidate、
deferred graph、mutual recursion、または完了の最初の境界（したがって fresh planner statistics
より前）で2本とも復元します。rebuild、既存DBのfull scan / update、fresh claimのrace、
recoverableなMCP indexingでは2本を常時維持します。drop / restore DDLはcaller-owned outer
transaction内に置くため、cancellationや失敗時はschemaも原子的にrollbackされます。
identity / resolution finalization 中は query index を遅延したままにします。guard は index を外す前に active な dirty graph scope を full refresh へ
昇格します。この force-full plan が確定している間は dirty file / name / reference の TEMP scope を
投入しないでください。fresh index と rebuild はその scope を参照せず、追跡すると全言語の batch ごとに
不要な set materialization が発生します。mutual-recursion 評価の直前に、その graph transaction 内で unresolved-folded、
legacy NOCASE、resolved reverse-edge の3本だけを復元し、残りの query index は mutual update
後に戻します。TypeScript augmentation rebuild が唯一の graph pass を担当する場合は、readiness
前に通常の graph / query index を復元し、augmentation の candidate 構築直前にだけ
candidate-symbol reverse lookup を外します。candidate 構築直後かつ resolution の prepare 前に
この lookup を復元します。その境界より前の失敗は transactional rollback / recoverable disposal
が修復し、正常な resolution と readiness は canonical な candidate lookup を利用できます。
candidate insert 中だけ reverse B-tree の行ごとの保守を省きます。MCP は既定の dirty-byte policy
が FTS bulk load を選ぶ場合に recoverable lifecycle
を使い、正常完了時と dispose 時の両方で全 index を復元します。schema initialization と read
repair は同じ canonical index catalog を使い、すべての経路が同一の最終 schema に収束する状態を
保ってください。

reference の context text は `reference_lines` へ正規化されるため、legacy な
`symbol_references.context` column は reference ごとの parameter ではなく SQL の `NULL`
literal として書き込みます。reference-line materialization は batch-local な key-to-ordinal
map でuniqueなinput arrayを構築した直後にmapを解放します。新規fileの`RETURNING`とreplacement
lookup SQLはいずれもmaterialized IDとbatch-local input ordinalだけを返し、context textを返したり
managed codeでtuple keyを再構築したりしません。ordinalの範囲外・重複・欠落と非正のIDは
`symbol_references` bind前にfail-closedとします。巨大なmulti-language reference集合でdedupe後の
file / line / context tupleを再copy・再hashしないよう、atomic file windowを含む全経路でこの契約を
維持してください。
atomic window の size は rows-per-statement の最悪ケース境界から算出し、materializer だけを
tuple-hash pass として保ちます。その前段に重複した key-sizing set を戻さないでください。

既存DBの full scan でも、既定の FTS dirty-byte policy が bulk load を選ぶ場合は同じ secondary
index 退避を使います。scoped update には workspace 全体の authoritative な byte estimate が
ないため、64 target 以上かつ indexed file 数の60%以上という保守的な recoverable 境界を
使います。この raw 境界を満たしても cleanup / graph / FTS work がまだ無い場合は、path-filter
済み reusable-stat snapshot を全 target と照合します。filtered deletion や duplicate-hardlink cleanup を
含む、indexed state を変更し得る target を見積もり、その件数が同じ64 target / 60%境界を超えた
場合だけ reference と hotspot の secondary-index staging を開始します。変更を伴わない skip、
unchanged、または sparse mutation の target 集合では全 index を維持します。preflight
に不確実性があれば保守的に staging を維持します。この preflight は cost 判定に限り、snapshot 後の
変更も全 index を維持したまま更新できるよう、file loop は authoritative な live lookup を必ず再実行
してください。identity / resolution 中は query-only 集合を遅延したままにしますが、candidate-symbol
reverse lookup だけは candidate 構築後かつ resolution command の prepare 前に復元します。mutual
recursion の直前に reverse-edge 用3本を復元して、その update 後に残りを戻してください。小規模 scoped
update は固定的な再構築 cost が更新時間を支配しないよう、全 index を維持します。
full mutual-recursion update は、call-like または非canonicalな row ごとに望ましい flag を
1回 materialize してから変更を適用します。相関 reverse-edge 式を `SET` と `WHERE` の
両方で評価すると、巨大な fresh graph で同じランダム B-tree probe が二重になるため、
single-evaluation の契約を維持してください。

C# の reference-graph finalization は、reference arity、invocation arity、member receiver、
definition arity、constructor arity、constructor binding sensitivity、value-type の fact を、対象 row ごとに
TEMP table へ1回だけ materialize し、その symbol fact から project / file-local type identity、constructor-owner
の identity / arity、および対象となる全 type declaration / constructor の primary-keyed instantiation-family fact
も materialize します。property-receiver normalization の前に C# field / property の target identity も
primary-keyed TEMP fact 集合へ materialize します。full / scoped / retained graph rebuild の全経路で fact 集合を
property-receiver normalization、candidate 構築、resolution より前に投入してください。candidate SQL は
join candidate ごとに identity 文字列を再構築したり constructor-owner range を再走査したり managed SQLite
scalar function へ再入したりせず、primary-key の fact lookup を使います。scoped refresh の symbol fact は
lookup-name 集合だけに限定し、identity fact もその限定済み集合から作ります。full / retained rebuild は
C# symbol fact の全対象を使います。property-receiver normalization も flag 済み reference fact と target fact の
primary key から駆動し、scoped target materialization は lookup-name 集合だけに限定してください。
instantiation-family materialization は限定済み type / constructor identity fact を外側にして、永続 symbol を
primary key で seek します。rank 0〜4 は symbol ID でこの fact を join します。lower-rank の binding-sensitive
flag は同一 identity の全 partial type declaration と constructor を含め、rank 5 用 flag は constructor と
決定的な代表 type declaration だけを含めてください。

言語共通の scope rank 1〜4 は、共有する reference / name / language candidate relation を
materialized CTE で1回だけ構築し、reference / symbol pair ごとの最良rankを割り当てたうえで、
reference ごとの最小rankに同順位の全candidateを保持します。source identity が不明でも rank 3 の
same-file fallback を残し、scoped refresh は dirty reference ID から reference primary key へ
駆動する契約を維持してください。

rank 0〜4 の candidate 構築後は、一致した reference ID の distinct 集合を compact な
`WITHOUT ROWID` TEMP table に materialize します。言語共通および C# の rank 5 fallback は
巨大な物理 candidate table ではなくこの集合を参照し、永続化される symbol ごとの candidate 行と
ambiguity 契約は変更しません。scoped refresh は dirty reference ID から candidate primary key を
seek して集合を作り、retry が古い行を参照しないよう graph pass ごとに materialize 前の clear を
維持してください。

qualifier のない C# rank 5 instantiation fallback は、reference ごとに type / constructor family を再構築せず、
共有 family fact を使います。一意性 flag の母集団は type declaration だけで、fold済みname、BINARY exact name、
arity ごとに非NULL件数とBINARY identityのmin/max一致を判定し、row自身のidentityがその一意identityに一致する
場合だけ有効にします。そのため constructor しかない orphan はlower rankでは候補になれてもglobal familyを
作りません。reference側から明示的なcomposite family-fact indexをseekし、constructorと決定的な代表typeだけを
出力します。project / file-local競合、partial row、overload、default / optional / `params`、value type、enum、
delegate、arity不明、ambiguityのsemanticsを保ちつつ、相関した永続symbol scanを行いません。

resolution は nullable な target-family key も candidate を持つ target symbol ごとに1回だけ
primary-keyed TEMP fact table へ materialize します。full / fresh / differential / retained refresh は
indexed な candidate-symbol existence probe で対象を限定し、scoped refresh は dirty-reference
candidate から到達する target symbol ID を先に重複排除します。candidate 構築と resolution は別の
SQLite command とし、reverse lookup の復元後にだけ resolution command を rent / prepare します。
resolution は物理 candidate ごとに language / path / container / name key を再構築せず、symbol IDで
このfactへjoinしてください。singleton family 判定は per-group の DISTINCT set を作らず、非NULL
key が1件以上あり、BINARY の `MIN(...) IS MAX(...)` であることを使って従来の
`COUNT(DISTINCT)` NULL semantics を維持します。all-NULL は family なし、NULL と1つの非NULL
family はその family、重複 key は1 family、BINARY で異なる key は ambiguous です。legacy target
の language が欠ける場合は key を `NULL` のまま保ちつつ、物理 candidate が1件なら ID による
resolved 状態を維持します。

リポジトリ全体の incremental scan は、C# contract prepass と parallel extraction の前に
stat-reuse 候補を 1 回の SQLite statement で読みます。各候補は引き続き最新の filesystem
size と UTC 更新時刻と照合し、language extractor version、extraction cap、古い issue metadata、
generated-code suppression も snapshot eligibility contract に含めます。CLI と MCP のどちらでも、
この snapshot を file ごとの database probe に戻さないでください。旧 DB の欠損または不正な
stat 値を持つ row は除外して通常の checksum reuse / 再 index で修復し、CLI/MCP の cancellation は
後続の extraction pipeline だけでなく snapshot query も中断できる状態を保ってください。
CLI full scan、scoped update、MCP indexing は、この prepass より前に workspace pattern config も
1回だけ読み込みます。C# candidate extraction は candidate ごとに default plugin を refresh せず、
その読込済み snapshot を再利用してください。直接 prepass を呼ぶ側は、snapshot 読込済みを明示的に
保証しない限り、従来どおり discovery 有効の既定経路を維持します。

static-interface prepass の raw built-in C# symbol artifact を再利用できるのは、indexed file が
0件の状態から開始した初回 full index だけです。CLI は rebuild と symbols-only を除外し、MCP は
rebuild mutation より前の空 database 条件を使います。cache は source text / byte を保持せず、main
pass は引き続き authoritative な content read と hook、stat snapshot / TOCTOU 検証、checksum 計算を
実行します。正規化 path の artifact は checksum 一致後に1回だけ取り出します。汎用 cache admission は
direct caller 向けの deep-clone isolation を維持します。fresh-index の production 経路では、先に2種類の
workspace lookup snapshot を materialize し、その後で admit した file ごとの symbol list の所有権を
cache へ移し、重複する workspace-symbol fallback list を解放します。snapshot 構築中は file ごとの
list を non-owning な segmented view で列挙し、一時的な flattened list と workspace 規模の2つ目の
pointer bufferを作りません。これにより main-pass mutation と
lookup snapshot の分離を保ったまま、重複する `SymbolRecord` object を保持しません。artifact を生成する
extraction には main pass と同じ absolute file path / project root を渡し、file-local family identity を
一致させてください。FileId、family scope、source observation、post-extraction hook、kind filter、cap、line 検証、
persistence、reference extraction、bounded-regex issue は通常の main-pass 経路で処理してください。
不完全な prepass、extraction-stall test seam、checksum drift、regex timeout、cache 上限では通常
extraction へ fallback します。timeout した prepass 結果は partial であり、一過性の結果を
authoritative にしてはいけません。admission は独立した production file-count 上限ではなく、131,072 symbol と
推定 32 MiB に制限してください。上限へ達する場合は decode 済み source の大きい順、同じ size では元の
candidate 順で admit し、この順序付けより前に immutable lookup を意味上の順序で構築してください。未消費
artifact は reference graph 開始前にすべて clear してください。

workspace qualified-pattern lookup が enum shadowing 判定に必要とするのは raw な non-enum type
nameだけです。このconflict setは直接構築し、per-file type-name builderを呼んでnormalized / qualified
known-type setを直後に捨てないでください。

source discovery 後も file ごとの処理を続ける full-tree consumer は、単一の
`IndexingFileTargetCollection` を opt-in します。string view は `ScanFilesResult.Files` の backing
store となり、concrete iteration は2つ目の workspace 規模 target array を materialize せずに、
lexical path、native/display/index relative path、再利用可能 language、path-only の generated-code
suppression 判定を公開します。CLI full scan、実 MCP indexing、workspace freshness check、authoritative
な CLI dry-run はこの view を使います。public/default の `ScanFiles` / `ScanFilesDetailed`、scoped
update、MCP dry-run は string-list 経路のままです。target に stat、resolved symlink、authorization、
file identity、checksum、content 由来の generated-code state を保持してはいけません。consumer は
引き続き live authorization、secure open、stat、mutation check を実行します。特に `.h` と
extensionless の content/shebang 判定は reusable language を null のままにし、authoritative content
read で再判定します。freshness の順序付けは target struct をコピーせず、整数 target index を sort
してください。

authoritative な CLI full scan は、共有 source-directory enumeration 中に C#、VB、F#、
MSBuild の project-marker fingerprint を収集します。実 MCP indexing は独立した marker tree walk
を行わず、同じ source scan が生成した fingerprint を再利用します。CLI pass は budget 非依存の
directory marker-count snapshot も構築し、`GetFamilyScopeKey` が file ごとに各 ancestor の
marker glob を再列挙しないようにします。fingerprint budget と scope completeness は独立です。
fingerprint budget を使い切っても scope snapshot は complete になり得ますが、discovery 自体が
不完全なら scope snapshot を破棄し、従来の fail-closed な live lookup へ fallback します。
不完全な MCP scan fingerprint は trust の一致にも再 stamp にも使いません。authoritative な
full-tree snapshot を所有しない scoped update と scan 前の直接 caller は同じ live fallback を
維持してください。

`FileIssue` rows には nullable な `origin` / `severity` metadata が入ることがある。
`replacement_char` では `origin: source_literal` が正規にエンコードされた U+FFFD
literal、`origin: decode_replacement` が不正 byte に対して decoder が挿入した U+FFFD
を意味する。source literal は `severity: info`、エンコーディング破損の可能性は
`severity: warning` として返す。

### 部分更新の path rule

`--files` / `--commits` の部分更新は、full scan と同じ path policy を使います。

| 項目 | 契約 |
|---|---|
| nested project root | `FileIndexer` は解決済み rule root から project root の parent まで、既存 ancestor の ignore file を読みます。project directory 自身の rule は通常 walk 中に読みます。 |
| rule 順序 | 各 directory で `.gitignore`、`.cdidxignore` の順に読み、後続の `!` pattern による再包含を認めます。 |
| 読めない ancestor | rule を黙って落とさず scan error で fail closed します。`ScanFilesResult.AncestorIgnoreDirectories` が解決済み ancestor list を診断用に保持します。 |
| ignore file の変更 | commit-scoped refresh に `.gitignore` または `.cdidxignore` の変更が含まれる場合、newly ignored file を purge するため full scan へ fallback します。 |
| malformed rule | scan error を報告してその行だけを skip し、run は継続します。 |
| symlink mode | 既定は `none` です。`internal` は workspace root 内の target、`all` は解決可能な全 target を追跡します。 |
| target identity | discovery、dry-run、C# preflight、content loading は同じ解決済み identity を使います。安定した許可済み外部 target は index し、preflight 後に retarget された link は source drift として拒否します。 |
| watch の symlink reconciliation | `--watch --follow-symlinks internal` では、workspace 内にある lexical symlink が workspace 外へ解決されるようになっても、その event を reconciliation input として保持します。scoped update は禁止された target を open / index せず、残っている stale indexed row を削除します。polling backend は `internal` / `all` で許可された directory link を full scanner と同じ depth-first 順で辿り、scanner が選択した lexical alias を維持し、解決済み directory identity の重複排除で cycle を bounded に保ち、directory alias 経由で到達する内部 DB artifact を除外します。 |
| 明示 file の preflight | 明示的な `--files` 更新では、直接指定された各 token と正規化 provenance を保持し、DB へ書き込む前にそのリスト全体を分類します。無効な token、directory、または同じ canonical target に解決される重複が 1 件でもあれば、要求全体を atomic に `UsageError` とします。path token のない `--files` は artifact 作成前に拒否し、full scan へ移行しません。`--project` の展開結果は `UpdateFiles` 内の導出済み scope input のままとし、直接指定された `--files` 選択として再分類しません。通常実行と dry-run は、project root 外、symlink escape、選択した symlink policy で許可されない path、存在しない path、filter 対象、未対応、重複について、同じ `rejected_paths` entry（`input_index`、support-safe な `path`、安定した `reason`）と上限・件数・truncation metadata を返します。canonical duplicate 判定には workspace 全体の policy ではなく解決済み target ごとの filesystem casing を使い、`none` mode は native spelling の正規化差を link とみなさず、実在する symlink/reparse segment を検出します。Windows の 8.3 ancestor alias は indexed-path membership に確立済みの spelling へ canonicalize します (#5122)。既に index 済みで現在は missing / filtered / unsupported となった path は cleanup 対象として有効なままであり、indexed control file が directory、FIFO、または policy で禁止された symlink に変わっても tombstone として保持し、その object を open / follow せず分類します。既存の関連 ancestor ignore file は reconciliation control として許可します。workspace 内の extractor/configuration input も、reconciliation signal として使う missing / deleted input を含めて許可し、generated code も正常な明示対象として扱います。TypeScript path alias の configuration read は in-process / worker extraction の両方へ active な `none` / `internal` / `all` policy を渡し、その policy で許可された target だけを解決して regular file のみ secure-open します。indexed path membership を読めない場合は `E008_DB_ERROR` で fail closed します。この検証は、`--files` のない暗黙の full scan が 0 件でも成功する契約を変更しません。 |
| symlink warning | dangling link と directory target の permission failure は scan warning です。dry-run は `warnings_total` / `warnings` に出し、成功終了を維持します。human full-index completion は dangling link の件数をユーザー向けの `skipped` label とともに表示します。 |
| Windows 属性 | Hidden / System path は言語検出前に拒否します。ignore rule では再包含できないため、project 所有 source では先に属性を外してください。 |

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

`goto` が曖昧一致し、`--json` が明示されている場合は
`E029_QUERY_AMBIGUOUS` / `ambiguous_query` を使い、`command: "goto"` と exit code 1
を返します。envelope には authoritative な `match_count` / `total_count`、上限付きの
構造化 `candidates`、`returned_count`、`omitted_count`、truncation 状態、構造化
`narrowing` object を含めます。candidate の実体化は20件かつ16 KiBまでとし、すべての
candidate text field を serialization 前に sanitization して上限を適用します。
`--json` を明示しない曖昧一致では、対応する code 付き human diagnostic を stderr に
維持します。rank 上位を自動選択せず、呼び出し側が query を絞るか `--all` を明示します。

response-budget preflight failure は
`CommandErrorWriter.WriteResponseBudgetError` を通して
`E028_RESPONSE_BUDGET_TOO_SMALL` / `response_budget` を返します。
`requested_bytes`、`effective_bytes`、`minimum_required_bytes`、
`minimum_required_bytes_known`、安定した算出不能 / 不確実性理由 field、
機械可読な `retry` object を追加します。
`--max-json-bytes` が制限するのは通常 payload であり、この diagnostic ではありません。
object と NDJSON の preflight は diagnostic が要求 cap を超えても、解析可能な完全な
error object 1 件を stdout に出し、stderr を空に保つ必要があります。実体化済みで安定した payload は
再試行可能な最小値を返し、runtime 依存 envelope は最小値を不確実と明示して余裕を持つ
推奨値を返します。`retry.action=increase_max_json_bytes` は `recommended_bytes` を返します。
最小値が有効な 16 MiB 上限を超える場合は、`retry.action=reduce_response_size` が
`option` と `recommended_bytes` を null にし、`maximum_effective_bytes` を返します。

未対応の `search --regex` / `search --all` diagnostic は、command 固有で表示専用の
`find` recovery 契約を追加します。正規化した引数の semantics を型付き
`alternative_command.argv` に正確に保持し、その argv から POSIX shell と PowerShell
の両形式を生成し、未実行であることを明示してください。不正確な代替 command を出さず、
対応不能な option または blocker を報告し、未指定の data directory など暗黙の
workspace default を含めてはいけません。

`CdidxConfigFile.Load` を呼ぶ前に project-config 依存性を解決します。process-static な
command（`license`、`--version`、help 形式、completion、subcommand help）は project
config を探索・parse しません。`validate-config` と `config show` は有効な設定を
environment consumer に適用しつつ、不正な file の reporting を command 固有契約で
所有します。それ以外の command は environment consumer の実行前に config を load し、
JSON mode の不正 config は共通の command-error envelope で
`E024_CONFIG_INVALID` / `configuration` を返します。

| failure class | exit code | error code | category |
|---|---:|---|---|
| usage / 不正な引数 | 1 または 7 | `E010_USAGE_ERROR` | `usage` |
| outline path が見つからない | 2 | `E019_FILE_NOT_FOUND` | `not_found` |
| 不正な設定 | 1 | `E024_CONFIG_INVALID` | `configuration` |
| hook の platform / filesystem failure | 9 | `E025_HOOK_OPERATION_FAILED` | `platform` |
| Git repository 外での hooks 実行 | 2 | `E026_NOT_GIT_REPOSITORY` | `not_found` |
| JSON response budget が小さすぎる | 1 または基となる command 固有値 | `E028_RESPONSE_BUDGET_TOO_SMALL` | `response_budget` |
| `goto` query の曖昧一致 | 1 | `E029_QUERY_AMBIGUOUS` | `ambiguous_query` |
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
success / typed-error response builder が追加する root-level `api_version` を持ちます。`JsonObject` / `JsonNode` を mutate する前に値を sanitize /
redact してください。full `tools/list` の各 definition は `McpToolOutputSchemas` が生成する draft 2020-12
`outputSchema` も公開し、再利用可能な definition で version 付き success envelope、row、readiness、
pagination / truncation、warning、型付き tool error を表します。success / typed-error variant は
tool 名の `const` を持つ tool ごとの `tool` discriminator を必須とし、typed error も同じ root
`api_version` を持ちます。互換性のための open な値は有限の nesting と property 数、array item 数、
string 長の上限で bounded に保ちます。field 名と nesting を実際の structured
result に合わせ、新しく登録した structured tool が output contract なしで出荷されないよう、tool-name switch は
網羅的に保ってください。compact catalog entry は
引き続き definition-incomplete とし、client を full catalog へ案内します。LSP、quickfix、SARIF 出力は CLI snake_case contract ではなく外部 schema に
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

`cdidx batch` は、同じ DB に複数の query command を投げる editor integration や script 向けの CLI 側 query loop である。newline-delimited な stdin record は従来の JSON 文字列配列 form、または検証済みの `{"command": "...", "args": [...]}` object form を使用できる。object input は重複/未知 property、欠落または空白 command、array でない `args`、文字列でない値、array input と同じ引数数/長さ違反を拒否する。serial mode は 1 つの `DbContext` / `DbReader` を開く。`--parallel <n>` は `--json-summary` を必須とし、最大 16 workers に制限する。parallel mode は active worker slot ごとに分離した query-only context を最大 1 つまで遅延作成し、同じ batch invocation 内で再利用する。保持した context は一度に 1 command だけへ貸し出すため、SQLite reader を並行利用せず、database open、schema probe、reader setup の反復コストを避ける。各 item の間では、保持中の direct connection と分離 snapshot の両方で source SQLite header / WAL generation と file identity を検証し、freshness を証明できない場合は session を置き換えるため、長時間動作する batch も後から完了した index update を観測できる。親で明示した batch database は、wrapper / option parse より前に `--db` を省略した database-aware child へ単一の `--db=<path>` token として注入されるため、`--` で始まる path も含めて、data read、`metadata.db_path`、project-root 解決、Git provenance、freshness、repair diagnostic は同じ context を使用する。暗黙の default database は暗黙の path-resolution semantics を維持する。child が `--db` を明示した場合はその item だけ child 指定を優先し、child の完了、失敗、cancellation のいずれでも次の record より前に thread-local な継承 context を復元する。すべての form は `CliCommandCatalog` が正本となる副作用なし allowlist の command だけを dispatch する。この schema には `goto` や `audit` などの query / read-only discovery surface が含まれ、top-level command や dispatcher arm を追加しただけでは batch の安全境界を越えられない。

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
command ごとの bounded writer へ route し、active worker slot ごとに分離した read-only SQLite
connection と thread-local batch reader を再利用し、active worker window だけを buffer する。`ScopedConsoleOutput` は nested
JSON-envelope capture を現在の worker の routed stdout に保ち、他 worker の process-wide writer を
置き換えない。完了 record は入力順で共有 output writer へ commit する。通常の item failure は
他 item から隔離する。caller cancellation は、消費済み input item と final summary に
`batch_cancelled` を記録してから後続処理を停止する。serial / parallel の input wait は input
reader ごとの bounded pump を共有するため、stdin が block 中でも cancellation を迅速に検知し、
同時に完成した line は後続の batch invocation 用に buffer したまま保持する。database setup
中の cancellation でも型付き final summary を出力する。

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
すべての inbound message は、まず object であり、その `jsonrpc` member が文字列 `"2.0"` と
完全一致しなければならない。欠落、null、文字列以外、または別 version の envelope 値には、
有効な request ID を維持して `-32600`（`Invalid Request`）を返す。この検証は receive-time の
lifecycle reservation、cancellation fast path、method dispatch、state mutation、database access
より前に行う。`workspace/symbol` では、これとは別に `params.query` を JSON string として必須に
する。query が欠落、null、または文字列以外なら query snapshot の refresh や symbol search より
前に `-32602`（`Invalid params`）を返す一方、正当な空文字列は workspace-wide query として
引き続き対応する。
URI / path 解決、live document へのアクセス、query snapshot の refresh より前に、1つの共通
coordinate validator ですべての対応済み position / range method を検証する。
`definition`、`declaration`、`references`、`hover`、`completion`、
`documentHighlight` は1つの position を必須とし、`inlayHint` は順序どおりの range を必須とする。
また、`didChange` に任意の range が指定された場合は、live text を変更する前に各 range を検証する。
line と character は LSP の `uinteger` 範囲（0〜2,147,483,647）の JSON integer でなければならず、
range の start が end より後であってはならない。request の coordinate が malformed、欠落、負数、
overflow の場合は `-32602`（`Invalid params`）を返す。notification の coordinate が不正な場合は
document state を変更せず無視する。構造検証によって、正当な document の no-match を protocol
error に変えてはならない。provider lookup は UTF-16 coordinate と既存の行末 / EOF policy を維持し、
document が欠落、読み取り不能、または範囲外の場合は、引き続き各 provider の保守的な空 / null
result を返す。
`LspServer` は通常 dispatch、cancellation fast path、queue-overload response の全経路で、
1 つの lock 保護された lifecycle state machine を使う。phase は before-initialize、
initializing、running、shutdown、exited である。最初の `initialize` request だけが初期化へ
遷移できる。transport は各 frame の lifecycle action を受信順で予約する。output gate の下で、
serialize 済み initialize response と running への遷移は 1 つの公開境界を共有し、frame の
書き込み開始直前に state を変更する。shutdown は active dispatch の完了待ちより先に phase を
変更し、その後に所有する query context と reader を正確に 1 回だけ破棄する。shutdown 後の
request は `-32600` を返し、notification は無視し、`exit` notification だけが正常な lifecycle を
完了させる。
順序どおりの `exit` notification を受理した後、stdio transport は次の input read を開始しない。
すでに queue に入った frame を drain し、shutdown response を先に公開してから、stdin EOF を待たずに
return する。正当な shutdown/exit sequence は process status 0 で返し、shutdown 前の exit は
process status 1 で返す。input stream は caller 所有のままとし、完了を強制するために close しては
ならない。
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

LSP の symbol kind と completion kind は、ordinal 比較する1つの internal-kind mapping から導出する。
これにより document symbol、workspace symbol、completion item の分類がずれない。意図的な mapping と
fallback は次のとおりである。

| internal kind | LSP `SymbolKind` | LSP `CompletionItemKind` |
|---|---|---|
| `base_image`, `class`, `service`, `specialization`, `type` | `Class` | `Class` |
| `record`, `struct`, `union` | `Struct` | `Struct` |
| `annotation`, `implements`, `interface`, `protocol`, `trait` | `Interface` | `Interface` |
| `enum` | `Enum` | `Enum` |
| enum container 配下で `enum`、`function`、`property` として保存される enum-entry 形状 | `EnumMember` | `EnumMember` |
| constructor subkind、declaration keyword、専用 initializer 名、または言語上有効な型名 declaration で識別される `function` / `method` | `Constructor` | `Constructor` |
| `method`, `test.method`, `accessor`, `class_hook` | `Method` | `Method` |
| `add`, `async_function`, `async_generator`, `copy`, `delegate`, `function`, `generator`, `hook`, `lambda`, `procedure`, `route`, `run`, `shell`, `subroutine` | `Function` | `Function` |
| `attribute`, `expose`, `property`, `stopsignal` | `Property` | `Property` |
| `field`, `volume` | `Field` | `Field` |
| `event` | `Event` | `Event` |
| `constant` | `Constant` | `Constant` |
| `operator` | `Operator` | `Operator` |
| `associatedtype`, `type_parameter`, `typealias` | `TypeParameter` | `TypeParameter` |
| `array` | `Array` | `Value` |
| `assembly`, `file_module`, `import`, `module`, `program`, `project`, `stage`, `submodule` | `Module` | `Module` |
| `namespace` | `Namespace` | `Module` |
| `package` | `Package` | `Module` |
| `block data`, `layout`, `object`, `protocol_impl`, `rule` | `Object` | `Class` |
| `reference` | `Variable` | `Reference` |
| `build_arg`, `environment`, `user`, `variable` | `Variable` | `Variable` |
| `value` | `Variable` | `Value` |
| `code` | `String` | `Text` |
| `anchor` | `Key` | `Reference` |
| `heading`, `label` | `Key` | `Text` |
| `workdir` | `Module` | `Folder` |
| catalog 外の `parameter` および未知または plugin 定義の kind | `Variable` | `Variable` |

constructor と enum member は extractor が広い永続化 kind を再利用するため、metadata に基づいて
意味を詳細化する。constructor は明示 subkind / keyword、専用 initializer 名、およびその declaration
形状が有効な言語だけで型名 constructor を認識する。他言語の同名 method と finalizer は function の
ままにする。戻り値型付きの同名 method、JavaScript object / static の `constructor` member、shell
function も同様である。これには Dart の named constructor、Java の compact record constructor、
C# の逐語識別子を使う constructor、大文字小文字を区別しない Pascal の constructor keyword、
Visual Basic の `New` も含む。enum entry は C#、Java、Kotlin、PHP、Swift における annotation 付き、
C# の逐語識別子を含む escape 済み、indirect、comma-grouped の保存形状を認識し、enum 内の nested
enum は enum declaration のままにする。現在の永続化 catalog には独立した
`parameter` kind がないため、legacy または plugin が提供する parameter 相当の symbol は保守的な
fallback で互換性を維持する。

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

C# declaration recovery でも、重複する pattern probe は candidate-local な work として扱う。
outer pattern loop と recoverable-pattern helper が共有してよいのは、同じ candidate start の
厳密に同一な `PreparedLine.MatchLine` に対する deterministic negative result の連続 prefix
だけである。この prefix を multiline property / function の結合 input、別 offset、別 line へ
持ち越してはならない。regex timeout は deterministic miss ではないため prefix に入れず、
pattern order、成功 probe、timeout diagnostic、cancellation boundary を変えない。

同じ規則を extracted symbol の membership にも適用する。後続の per-line / per-match 判定が
class、property、import alias などの存在を繰り返し確認する場合は、dictionary / set を一度だけ
構築して再利用する。candidate loop の中で `symbols.Any(...)`、LINQ 列挙、signature parse を
隠す helper も反復走査である。まれな language / feature 用 lookup は lazy に構築し、
`Distinct` ベースの走査を置換するときは source order や first-match semantics を維持する。

container ownership にも同じ契約を適用する。reference が extracted declaration を name と
source range で繰り返し解決する場合は、candidate を name ごとに一度だけ索引化し、その name の
ordered range list だけを走査する。duplicate name の first-candidate behavior を維持し、index は
1 回の extraction call 内だけに保持する。
core reference loop は extraction ごとに1つの `CoreReferenceLineContainerResolver` を所有し、
空でない各行の直前に line 座標、fallback container、言語固有 state を reset する。bound 済みの
container delegate は1回だけ生成し、その prepared line 内で同期的に消費し、後続行へ保持しては
ならない。例外または cancellation では extraction-local resolver を破棄し、pool 化や static 化を
しない。C# declaration の generic parameter 検出が空の `IReadOnlySet` を共有できるのは、行に
`<` marker がないことを確認した場合だけである。consumer はこの集合を read-only のまま扱い、
`<` を含む行は従来どおり callable / type declaration の完全な parser を通す。
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
`BoundedRegex.EnumerateMatches` の BCL regex overload は、value type の concrete
`MatchEnumerable` / `MatchEnumerator` を返す。direct `foreach`、cap-aware な reference
wrapper、SQL match helper はこの static type を維持し、空 scan または既に cap 到達済みの
scan で iterator object を割り当ててはならない。`IEnumerable<Match>` への変換と LINQ は
互換経路として維持するが、value enumerable / enumerator を box するため hot loop では
使わない。enumerable / enumerator の作成時には regex engine を呼び出さず、最初の
`Regex.Match` は最初の `MoveNext` まで遅延する。後続処理は timeout、zero-length の進行、
`\G` continuation、明示的 `startAt`、right-to-left の順序を保つため必ず
`Match.NextMatch` を使う。cap-aware wrapper は下位の `MoveNext` より先に capacity を
確認し、list が満杯になった後は先読みしない。
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
JavaScript / TypeScript の pattern scan も、module、supplemental symbol、private-scope 解析で
列位置を保つ遅延生成済み sanitized-line snapshot を1つ共有する。flat file が lexical pass を
負担しないよう snapshot の実体化より前に軽量な raw `{` / `=>` gate を維持し、consumer ごとに
copy や再 sanitization をせず、cached line を immutable として扱う。

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

### C# top-level synthetic scope 契約

C# extractor contract version 13 は、実行可能な top-level statement を持つ
compilation unit に、操作可能な file-scoped symbol を永続化します。この symbol は
`kind=function`、`sub_kind=top_level_scope`、`name=<top-level>` を使い、公開結果は
`is_synthetic=true`、`<indexed-path>::<top-level>` 形式の qualified identity、
`id:<symbol-id>@g:<generation>` 形式の selector を派生させます。selector は active な
index generation だけで有効で、symbol id により解決するため、異なる file にある同一内容の
top-level program が callee identity を共有することはありません。

検出は container assignment の後に実行します。declaration が覆う range、C# keyword 間に
任意の正当な whitespace を持つ import、comment、directive、assembly/module metadata を除外し、`using var` と明示型の
`using Type value = ...` declaration の両方を import directive ではなく実行可能コードとして認識し、
残った最初と最後の実行可能行を source/body 両方の境界にします。top-level local function は container を持たない source-declared symbol
のままで、その function 内の reference はより狭い自身の span が所有します。両側に実行
statement がある場合、synthetic range は local function をまたぐことがあります。local function と外側の statement が
同じ行にある場合、宣言 column は local function が所有し、外側の statement は synthetic scope が所有します。reference
extraction は、この body 内で従来 container を持たなかった call に synthetic symbol の永続化
id を使います。synthetic scope は documented declaration ではないため、top-level statement
直前の XML-doc comment を自身へ結び付けません。そのため `outline`、座標指定 `inspect`、identity-scoped `callees` は同じ row を
navigate しなければならず、CLI / MCP の両 `callees` が selector を永続化済み symbol id で解決します。
unused-symbol の list / count query は、この synthetic entry point が削除可能な dead code ではなく実行基盤なので除外します。
読み取り可能な legacy schema に永続 source-identity column がない場合、selector-scoped callee query は fail-closed します。
保存済み C# extractor version がこの契約より古いか欠落しており、
synthetic row も無い場合、outline は対応済みと見せず、`top_level_symbol_support=reindex_required` と型付きの
limitation を返します。通常の full index はこの contract を stamp する前に未変更 C# file も再抽出するため、
文書化された reindex remediation は `--rebuild` なしで stamp のない legacy database を修復します。

### シンボル種別分類

`symbols.kind`、`symbols.container_kind`、`symbol_references.container_kind` は
以下の公開 symbol kind taxonomy に従います。新しい extractor が kind 値を追加する場合は、
書き込み前に `SymbolKindCatalog` へ登録し、schema check、writer validation、CLI
filter、downstream JSON consumer が同じ値を理解できるようにしてください。

順序付きの `SymbolKinds` / `ReferenceKinds` array は公開互換 snapshot として維持し、caller は
型初期化後の要素を immutable として扱います。private な canonical 順序付き taxonomy だけを、
immutable な Ordinal writer lookup、SQLite schema check / migration、ctags filter の source にします。
そのため legacy consumer が公開 array の要素を誤って置換しても、内部契約は同期したままです。
値を追加する場合は catalog source を変更し、catalog 全件、schema parity、公開 mutation 隔離の
test も更新してください。公開 array を実行時に変更してはいけません。

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
| `field` | property と区別される field declaration。C# の通常 / const / static readonly field は tuple-aware な型文法を共有し、巨大 initializer の signature は declaration metadata を維持しつつ top-level initializer 本体を決定的 marker に置換する | Search/filter symbol |
| `file_module` | file-scoped module / package declaration | Namespace-like context symbol |
| `function` | 関数、method、constructor、delegate、task、およびより狭い kind がない callable binding | Primary callable definition。reference row 経由で callers/callees に参加 |
| `generator` | JavaScript / TypeScript generator declaration | Callable definition。reference row 経由で callers/callees に参加 |
| `heading` | Markdown heading、C# region、Python module docstring、JavaScript / TypeScript `@module` docblock などの language section marker | Outline symbol。Markdown heading は path に限定した fragment reference の定義対象。1始まりの包含 range は末尾の LF / CRLF の有無にかかわらず実在する最終 source 行で止まり、heading body が空なら body range を省略する |
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

C# の `test.method` 所有権は、直前の物理行だけではなく、先頭に連続する attribute
block 全体から判定します。上限付き scanner は C# の字句 masking pass を再利用するため、
複数行の attribute 引数と連続する attribute list を扱い、generic attribute の型引数を
同じ item 内に保ちつつ、comment、通常文字列、verbatim 文字列、raw 文字列、interpolated
文字列、文字 literal 内の bracket や test 風の名前を無視します。明示的な `return:`、
`assembly:`、`module:` target は method を `test.method` に分類しません。declaration
context gate は expression initializer の brace を追跡して行頭 bracket の collection
expression を拒否します。return type を持つ shaped な通常 method declaration だけが test
taxonomy を受け取り、field、indexer、constructor（暗黙 return の partial constructor を含む）
は attribute 所有権を消費しますが、再分類しません。

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
| `generated_column_dependency` | SQL generated / computed column 式の dependency |
| `generic_type_argument` | explicit invocation に付随する generic type argument |
| `implement` | interface implementation relationship |
| `implicit_implementation` | C# implicit interface implementation relationship |
| `import` | module system 経由の import / include / reference |
| `instantiate` | constructor または object creation |
| `join_condition_reference` | SQL join / merge condition column reference |
| `lifetime_reference` | Rust / C# 風 lifetime または lifetime-like type reference |
| `member_read` | C# の修飾付き enum 定数、定数、static readonly field、static property など、呼び出しを伴わない member / value 読み取り |
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
reference safety cap の永続化済み省略証拠、または active な永続 symbol-kind policy がある場合は
`index_complete=false` となり、安定した `index_incomplete_reasons` を返します。
`reference_graph_complete` はさらに利用可能かつ current な graph generation を要求し、
graph 固有の安定した理由を返します。正規化済み `index_symbol_kind_filter` metadata、成功世代の
`index_symbol_kind_filter_audit_version` marker、file ごとの
`files.symbols_dropped_by_kind_filter` fact は CLI index/status、workspace health、
MCP indexing/status で共有します。active policy は除外数0でも必ず
`symbol_kind_filter_coverage_limited` を追加します。policy provenance を持たない legacy DB は
読み取り可能なまま `symbol_kind_filter_provenance_unavailable` を追加します。この保守的な
fallback は従来の completeness metadata とは別で、後者は永続 row が処理の省略を証明しない限り
compatibility default を維持します。audit marker の無い active な legacy generation は、
全 workspace refresh が file ごとの fact をすべて再 stamp するまで aggregate を省略します。

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
provenance 失敗時に実行を拒否します。verifier は検証済みの既知 install path、または
`CDIDX_GH_EXECUTABLE` override からだけ選択されます。macOS の既知配置探索では version 固定の
Homebrew Cellar binary を直接列挙するため、`bin/gh` symlink を起動しません。override は絶対 `gh` path
（Windows では `gh.exe`）でなければならず、canonical な target に解決したうえで regular file / image、owner、write mode または ACL、
ancestor、executable、上限付き `gh --version` の全検証を通過する必要があります。PATH 順序や
current directory が verifier を選ぶことはなく、無効な明示 override は fallback せず fail closed
します。`CDIDX_VERIFY_POLICY=compat` だけが監査対象の明示的 opt-in で、verified provenance と
表示せず未検証 bypass を報告します。upgrade JSON は `verification_policy`、`manifest_provenance_verified`、
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
| `index_complete=false` | 永続化済み省略証拠、active symbol-kind policy、または legacy policy provenance 不在により generation 全体の不在 authority が利用できない | `index_incomplete_reasons` に対処して再 index。full coverage が必要なら filter なしで rebuild |
| `symbol_kind_filter_coverage_limited` | 永続 include/exclude policy が、除外数0の場合も含めて symbol coverage を意図的に制限 | symbol-kind filter なしで `cdidx index <projectPath> --rebuild` |
| `symbol_kind_filter_provenance_unavailable` | legacy DB に永続 policy stamp が無い | 現行 binary で rebuild。完了まで symbol/graph の否定結果は non-authoritative |
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
| caller-owned write batch | full-scan などの atomic file write は既に1つの caller-owned transaction 内で実行されるため、言語共通の chunk、symbol、issue、reference-line、reference insert は statement を32 parameter以下に制限します。すべてのbatchはrow / column順にcompactな1-origin SQLite numeric slot（`?1`〜`?N`）を使い、既存のstatement-size、cancellation、checkpoint契約を保ったままparameter name解決の処理を抑えます。500 rowを超えるoperationでは、永続 `db_writer_batch_checkpoint` を500 row境界をまたいだ時点と完了時だけ出力することで、小さなstatementごとの同期log flushを避けます。public writer API は SQLite variable limit までの batch 形状と既存の batch ごとの transaction / SAVEPOINT 契約を維持します。 |
| prepared savepoint control | `DbWriter` は connection の prepared-command cache から固定・有界な control statement だけを借ります。対象は file 単位 full-index scope が使う最初の nested `sp_1` の SAVEPOINT / RELEASE / ROLLBACK、atomic metadata savepoint、FTS bulk-load owner savepoint です。各 lease は現在の outer `SqliteTransaction` へ再 bind し、cache なし writer は従来どおり呼び出しごとに command を作成・破棄します。depth 2 以深の savepoint 名は動的なまま cache を迂回し、cancellation、rollback、terminal state、transaction gate の契約は変更しません。 |
| authoritative-fresh raw insert scope | empty-database CLI経路がcaller-owned transaction内でauthoritative-fresh claimを再検証した後に限り、extraction pipelineのnew-file、chunk、symbol、new-file issue、fresh reference-line、atomic fresh-reference INSERTをprovider所有connection handle上のSQLitePCLRawでbind / executeします。native positional bindingは専用の512 parameter上限を使い、provider経由のcaller-owned writeは32 parameter上限を維持します。fresh file / reference-line writeもDONE-onlyです。file insertは同じconnectionの正の`last_insert_rowid`を取得し、reference-line batchは毎回`MAX(id)`と`sqlite_sequence.seq`の大きい方を読み、Int64範囲全体を検証して`?1 + input_ordinal`の明示的な連続IDを挿入します。batchごとのfloor読取により、rollback依存のallocator stateを保持せずAUTOINCREMENT履歴とbatch間insertを反映します。不正floorとidentity range overflowはINSERT実行前に失敗します。実行済みfresh identity writeはID公開前に`sqlite3_changes()`を検証し、row count不一致、constraint、cleanup failure、cancellationでは対象prepared statementを破棄し、data rollbackはcallerのfile単位SAVEPOINTが所有します。scopeは正確なtail / write-count形状、batch hook、row-skip replay、outer transaction atomicityを維持し、32-entry LRUで繰り返すfull / tail statement形状を保持します。同期的なfull-scan persistence consumerと`DbWriter`のtransaction owner検査により、非thread-safe cacheはsingle-ownerのままです。各leaseはresetとbinding clearを行い、error時は元のstep結果を保持し、SQLite interruptを`OperationCanceledException`へ変換し、graph / index / FTS処理より前に全cached statementをfinalizeします。同じtransaction内では3本の`files_resource_generation_*` triggerを停止し、native statementのfinalize後だけ再作成します。fileを1件以上永続化した場合はresource-list generationを厳密に1回進め、空repositoryでは変更しません。rollback時はschemaとgenerationを一括で元へ戻します。replacement、incremental、rebuild、symbols-only、fresh-claim race fallback、MCP、public writer経路はMicrosoft.Data.Sqlite、既存の`RETURNING`挙動、mutationごとのgeneration invalidationを維持します。 |
| authoritative-fresh source-symbol lookup | native statementをprepareする前に、raw scopeはfold済みname、fold済みdisplay name、legacy ASCII `NOCASE` fallback用partial indexを備えたconnection-local TEMP `WITHOUT ROWID` snapshotを作成します。atomic reference collectionごとにsnapshotをclearし、異なるsource file IDのsymbolを`idx_symbols_file`経由で各1回copyします。containerを持たないreferenceではmaterializationを省きます。3本のindexed probeは、同じsymbolが複数branchに一致する場合も既存のname-or-display semanticsを保つため`UNION`で重複を除き、その後もcontaining rangeとinnermost span / start / idの同じrankingを維持します。TEMP schema作成はcallerのouter transactionに属し、各file savepointがsnapshot populationとreference writeを一緒に所有するため、cancel、failure、rollbackでは両方を復元します。provider、rebuild、incremental、fresh-claim fallback、MCP、public-writer経路はTEMP tableを作成も参照もしません。 |
| authoritative-fresh core secondary index | 同じempty-database CLI transactionがauthoritative-fresh claimを再検証した後、`files`、`chunks`、`file_issues`、`symbols`の言語共通secondary index 22本をpersistence前に停止し、全native INSERT statementのfinalize後かつgraph / readiness queryの開始前に各B-treeを1回だけ構築します。UNIQUE autoindexは維持するため、pathとtable constraintは通常どおり適用されます。fresh-reference insertが関連する各fileのsymbolをindexed TEMP snapshotへ1回copyするため、`idx_symbols_file`も維持し、file単位materializationがsymbol table全体のscanへ退行しないようにします。cancel / failure時はouter rollbackがload前のschemaをatomicに復元し、rebuild、incremental、fresh-claim race fallback、MCP経路はwrite中もindexを維持します。canonical DDLをschema initialization、opportunistic read migration、bulk-load guardで共有し、deferred setと完了DBの契約がずれないようにします。 |
| checkpoint | `DbWriter` は outer transaction commit 後に `PRAGMA wal_checkpoint(PASSIVE)` を実行し、SQLite も設定済みの 1000 page threshold を超えると自動 checkpoint する場合があります。どちらの checkpoint path も opportunistic で、active reader は block されず、未 checkpoint の WAL は corruption ではなく期待される状態です。 |
| checkpoint result contract | 明示的な `PRAGMA wal_checkpoint(TRUNCATE)` path は reader を実行し、SQLite の `(busy, log, checkpointed)` を含む構造化結果を返します。`busy` が 0 以外、または remaining page が正の場合は、上限付き machine reason を伴う unsuccessful result です。`(0, -1, -1)` は SQLite の非 WAL database に対する成功 no-op です。instance checkpoint、read-only fallback 前の static preflight、query diagnostics、top-level status、nested connection-policy status は同じ結果と count を保持します。raw exception text や path を diagnostics に含めてはいけません。 |
| crash recovery | SQLite が transaction を commit した後、checkpoint 前に process が kill された場合、次の通常 open が WAL を roll forward するため手動 recovery は不要です。commit 前に process が終了した transaction は SQLite により rollback されます。 |
| migration transaction と foreign key の所有権 | `TryMigrateForRead` は transaction を開始する前に SQLite の autocommit state を確認します。active transaction は明示的に caller-owned として扱い、cdidx は commit / rollback せず、無関係な `BEGIN` failure も nested transaction と誤認せず伝播します。foreign key を無効にする必要がある rebuild migration は、所有する transaction の開始前に `PRAGMA foreign_keys=OFF` を設定して read-back し、nested rebuild helper 内では無効状態が実効的であることを assert します。成功・失敗どちらでも transaction dispose 後に caller の元の mode を復元し、再度 read-back して検証します。 |
| schema discovery cache | `DbReader` の schema discovery は正規化済み DB path を key にした process-level cache を使います。column / index 結果は immutable な `FrozenSet` snapshot として保存・返却されるため、caller が他の reader と共有する schema 判定を変更することはできません。path state は有効な `DbSchemaCache` owner により参照カウントされ、最後の owner `DbContext` が dispose されると削除され、owner が active な間は退避されません。lookup 前に `PRAGMA schema_version` を確認するため、cdidx や外部 `sqlite3` session による SQLite DDL は stale snapshot を invalidate します。cdidx 外での手動 schema edit は運用上 unsupported であり、その後は query output を信頼する前に `cdidx validate` を実行してください。 |
| batch trust marker | index write batch は mutation transaction を始める前に `codeindex_meta.batch_in_progress=true` を stamp し、対応する row と readiness metadata を commit する transaction 内で clear します。marker が書かれた後、clear される前に indexer が crash した場合、その後のすべての open は readiness metadata を変更せずに `Last batch did not complete; run cdidx index --rebuild to re-index from a known clean state.` と警告します。readiness を degrade するのは、明示的な `index --rebuild` repair path だけです。file ごとの error が graceful に処理された場合は rollback 後に marker を clear するため、orphaned marker は interrupted / crashed batch の trust metadata を clean と扱わないための signal です。 |
| read-only open / fallback | query-only command は最初の試行から SQLite `Mode=ReadOnly` で開き、WAL の可視性を保ちながら writable setup と opportunistic migration を実行しません。write-capable intent は journal/WAL setup に失敗した場合に read-only へ fallback することがあります。明示的な `immutable=1` URI は stale snapshot を許容する opt-in escape hatch です。sidecar を公開できない storage 上の WAL を観測する必要がある場合は、`.db` / `.db-wal` / `.db-shm` をまとめて readable location に copy するか、full WAL set を open できる環境で SQLite backup を使います。 |
| status pragma diagnostics | `status --json` は選択された read-only connection を `sqlite_connection_policy` (`active_mode=read_only`, `open_mode=read_only`) で、解決済みの接続値を `db_pragma_settings` (`journal_mode`, `synchronous`, `wal_autocheckpoint`, `busy_timeout_ms`, `page_count`, `freelist_count`, `page_size`, `auto_vacuum`) で公開します。また、prepared command cache counter を `prepared_command_cache` (`count`, `capacity`, `hit_count`, `miss_count`, `eviction_count`) で公開します。`maintenance_guidance` は raw 値を変えずに `wal_state`、`freelist_ratio`、`freelist_state`、`estimated_*_reclaimable`、`auto_vacuum_mode(_name)`、`recommended_command`、`post_maintenance_follow_up` を派生します。nested な `fts_optimization` は optimize preview / execution と同じ純粋 evaluator を使い、database に書き込まず `recommended`、`action`、`reason`、`threshold_writes`、`observed_writes`、`state` を公開します。`status --check --json` は `repair_commands[]` に `name`、`action`、`args`、`mutation_class`、`safety_class`、`safety_notes`、互換用の `reason`、順序付きの `reasons` を返します。完全に同一の構造化 identity は deduplicate して check の優先順に reason を集約し、target、option、action、mutation class、安全性 semantics が異なる場合は merge しません。human check output も同じ command set を使い、platform-aware な shell quote を維持し、control character を可視 escape して各 `[repair]` action を1行に保ちます。構造化 JSON の `args` は変更しません。`last_failed_or_partial_index_run` は bounded な failed / partial index context (`status`、`mode`、timing、count、stable error code、reason、`progress_persisted`、bounded な `recovery_hint`) のみを公開し、raw exception text や file path を含めてはいけません。 |
| maintenance threshold | WAL guidance は `CDIDX_MAINTENANCE_WAL_WARN_BYTES` (既定 64 MiB) 以上で `checkpoint_recommended` になります。freelist guidance は `CDIDX_MAINTENANCE_FREELIST_WARN_RATIO` (既定 `0.20`) 以上で `vacuum_recommended` になります。不正・範囲外の環境変数値は既定値へ戻します。 |
| rebuild 後の reclaim | CLI / MCP rebuild が正常に commit された後、cdidx は共通の freelist threshold を評価し、reclaim が推奨され、かつ `auto_vacuum=INCREMENTAL` の場合だけ `PRAGMA incremental_vacuum` を実行します。自動の full `VACUUM` は実行せず、legacy database は `skipped/auto_vacuum_not_incremental` を報告して明示的な `cdidx vacuum` conversion path を維持します。reclaim は index transaction の後に実行し、上限付き progress / log phase を出力して、stable な state / reason、duration、before / after の logical database size と物理 main-file sample、page / free-page count と ratio、回収 page / byte 数を `last_index_run.rebuild_reclaim` に保存します。WAL-backed page が checkpoint 待ちの間は物理 sample が遅れて変化する場合があり、即時 attribution の contract は logical size と page metrics です。cancellation、busy / read-only I/O、その他の reclaim failure は commit 済み index generation を rollback したり failure に再分類したりしません。 |
| maintenance command の優先順位 | `maintenance_guidance.recommended_command` は既存の vacuum、checkpoint の順序を維持します。WAL と freelist の state が両方とも厳密に `ok` で、信頼できる FTS write snapshot が threshold に達した場合だけ `cdidx optimize --db <db>` を返します。上位 state が `unknown` の場合や FTS snapshot が stale / unavailable の場合は optimize command を選択しません。 |
| page attribution | `status --json` は source を変更せずに SQLite page ownership を読み取ります。`dbstat` page byte を優先し、利用できない場合は件数上限付きの b-tree / WAL snapshot traversal（最大1,000,000 page、100,000 schema object）へ fallback します。live WAL connection が安定した detached file set に基づいていない場合、fallback は先にその connection の active read snapshot を cancellation 対応の private backup に固定し、並行 commit による世代混在を防ぎます。`allocated_object_bytes + freelist_bytes + unexplained_residual_bytes` は `logical_database_bytes` と一致し、table/index と internal/leaf/overflow/other page の小計はそれぞれ `allocated_object_bytes` と一致します。payload、unused space、structural overhead も別に再照合されます。物理 main/WAL/SHM byte は分離して報告します。出力する object 名は最大20件で、各名称は support-safe sanitizer により最大128文字になります。probe の失敗・不整合時は `available=false`、安定した `unavailable_reason`、null / 省略された attribution 値を返し、ゼロとして偽装しません。 |
| vacuum | `cdidx vacuum` は incremental-auto-vacuum DB では `PRAGMA incremental_vacuum` を実行し、legacy no-autovacuum DB では初回のみ `PRAGMA auto_vacuum=INCREMENTAL` と full `VACUUM` で変換します。`cdidx vacuum --dry-run --json` は vacuum pragma を実行せずに回収可能 page/byte を推定します。論理 metrics は artifact-preserving query snapshot から取得します。物理 `*_before` alias は `command_entry`、物理 `*_after` alias は後続の source-generation 検証済み `post_command` observation を表し、その tuple を安定して採用できない場合は省略します。したがって両 alias が1つの observation を共有するとは扱わず、明示的な境界には `file_set_observations` を使います。実行系 `cdidx vacuum --json` は、論理 database byte（`page_count * page_size`）、maintenance 前後の指定元 source main/WAL/SHM file length、およびその物理 file-set 合計を返します。論理 before / after の各 PRAGMA 群は、それぞれ1つの SQLite read snapshot から取得します。実行後の field と maintenance guidance は、cancellation 対応の best-effort truncate checkpoint と command 所有 vacuum connection の close が完了した後に確定します。logical-after snapshot、command 所有 checkpoint、安定した source-state capture の間で `data_version` が変わらない場合に、close 前の witness を確立します。post-close の query-only observer は、その論理 snapshot を指定元 source の1つの安定 file set に結合します。raw source 世代が不変な場合、または witness の WAL が non-empty、observer の WAL が zero-length で、両者の logical size signature（`page_count`、`freelist_count`、`page_size`、`auto_vacuum`）が一致する checkpoint 表現の場合だけ採用します。後者は公開 size metrics の一貫性を保ちますが、application data が同一世代に留まったことまでは保証しません。外部 writer がその signature を変更した場合、または別の raw 世代がこの hot-WAL から zero-WAL への checkpoint 形状に当てはまらない場合は、すべての物理 after field と `db_size_bytes_after` / `wal_size_bytes_after` alias を unavailable（result DTO では null、通常の CLI JSON では省略）にし、WAL guidance を `unknown` にします。物理 observation は、file identity 一式の2回の取得結果が一致し、かつ同じ DB/WAL 世代を挟んでいる場合だけ採用します。取得は最大3回まで再試行し、継続的な不安定状態または metadata read failure では物理 tuple と互換 alias をすべて unavailable にし、WAL guidance を `unknown` にします。sidecar が存在しないことを安定して確認できた場合だけゼロを返します。`db_size_bytes_*` と `wal_size_bytes_*` はそれぞれ `main_file_bytes_*` と `wal_file_bytes_*` の互換 alias として維持し、`pages_reclaimed` / `bytes_reclaimed` は物理 file-size 差ではなく論理 page 回収量を表します。別の active connection が WAL/SHM を保持することは正当なため、main-file byte や physical-file-set byte は logical database byte と一致するとは限りません。`physical_file_set_bytes_*` は filesystem allocated-block usage ではなく論理 file length の合計であり、`wal_checkpoint_timing_note` はこの安定した post-close observation point を説明します。 |
| vacuum の file-set observation point | `vacuum --json` は物理 snapshot を `file_set_observations.command_entry`、`post_open_pre_vacuum`、`post_command` に分離して公開します。command entry は validation が SQLite を open し、pragma、checkpoint、sidecar permission hardening を行うより前に取得します。post-open snapshot は vacuum 前の SQLite metrics と共に使う source set を記録し、post-command snapshot は command 所有 connection の close 後に dry-run source generation または実行系 post-close witness 規則で再検証します。各 snapshot は `state=captured` と main/WAL/SHM の存在 flag・byte length、または `state=unavailable` と上限付き stable reason `unstable_or_inaccessible` を返します。存在しないことを確認した sidecar は `exists=false` かつ 0 byte、空 sidecar は `exists=true` かつ 0 byte です。互換 `*_before` file field と `db_size_bytes_before` / `wal_size_bytes_before` は command entry の alias となり、`*_after` は post-command の alias です。論理 `page_count_*`、`freelist_count_*`、`logical_database_bytes_*` は引き続き SQLite の vacuum 前後 snapshot なので、command が作成した sidecar の消失を回収 page/byte として扱いません。 |
| FTS optimize preview | `cdidx optimize --dry-run` とその alias である `cdidx index <project> --optimize --dry-run` は `QueryOnly` snapshot を開き、既存 lockfile を作成も取得もせずに probe し、source DB/WAL/SHM set に対する write PRAGMA、schema setup、FTS control insert、metadata write を一切実行しません。どちらの entry point も query connection では明示的に指定された `file:` URI と `immutable=1` を保持し、filesystem probe だけが正規化済み local path を使うため、hot WAL がある場合も status と両方の preview は同じ stale-snapshot semantics を適用します。human / JSON の path 表示は共有 maintenance policy を使用し、既定では呼び出し元の相対表記を維持しつつ absolute input を `<redacted>` にし、`--show-paths` 指定時だけ解決済み absolute database path を表示します。内部の snapshot / filesystem 操作は常に実 path を保持します。JSON は size/freelist/readiness 指標、planned operation、および正確な threshold と observed write を含む status と同じ `fts_optimization` recommendation object を返します。stale batch または legacy counter / page snapshot が利用できない場合は、安定した reason と state で recommendation を抑止します。実行系は optimize 前後で同じ object と reset 後の counter を返し、repair mode の schema 初期化または migration 確認を実行します。object size は利用可能なら `dbstat` page byte を使い、利用できない場合は明示した logical-payload fallback を使います。実際の optimize は所要 millisecond を記録し、後続 preview が `estimated_duration_ms` として返せるようにします。 |
| size / process diagnostics | `status --json` は `db_size_bytes`、`wal_size_bytes`、上限付きの `symbol_kinds` / `symbols_by_language` kind map と、上限適用時の `symbol_kind_*` / `symbols_by_language_kind_*` overflow metadata、現在の `process` heap / GC / working-set metrics、成功した CLI / MCP index 実行由来の `last_index_run` metadata、最新の成功 index/update 時刻を示す `last_workspace_freshened_at` も公開します。`last_index_run.bytes_read_skipped_file_count` と `bytes_read_incomplete` は、読み取り不能な file が `bytes_read` 合計から除外されたかどうかを報告します。`last_index_run.diagnostics`、`diagnostic_count`、`diagnostics_truncated` は、index data 自体の書き込みが成功した後に best-effort index metadata write が失敗した場合の上限付き warning を保持します。`indexed_at` は引き続き indexed file row 由来なので、partial / no-op update は `indexed_at` を動かさずに workspace 鮮度だけを更新することがあります。 |
| memory tracing | `index --json --memory-trace` は CLI index 結果に `memory_timeline` block を追加し、peak working-set MB を `last_index_run` に保存します。dry-run 結果も live な `start`、`snapshot`、`scan`、`finalize` sample を返しますが、run metadata は保存しません。`index --dry-run --rebuild` は index を削除も rewrite もしないため destructive confirmation を bypass します。`CDIDX_MEM_WARN_MB=<mb>` は sampled working set がしきい値を超えたときに warning を出します。 |
| newer schema protection | writable open は、`PRAGMA user_version` に current binary の `CurrentSchemaVersion` mask 外の readiness bit が含まれる database も拒否します。read-only status/query path は degraded audit signal として `index_newer_than_reader=true` を表示できますが、write-capable path は古い cdidx が新しい binary で stamp された DB を黙って rewrite しないよう `E003_SCHEMA_TOO_NEW` で失敗しなければなりません。 |

status freshness summary は1つの共有 evaluator で分類します。authoritative な
`status --check` は file-level の結果を提供します。check がない場合、
`last_workspace_freshened_at >= latest_modified` が checksum 再利用 no-op update の
freshness を証明できるのは、worktree が clean で、runtime、workspace 検証済み、直近
index の HEAD SHA がすべて一致し、後続変更を隠せる `skip-worktree` / `assume-unchanged`
entry が Git index に無い場合だけです。Git の dirtiness probe は未追跡 file を明示的に
すべて要求し、repository の `status.showUntrackedFiles=no` 設定を上書きします。provenance
不足、隠れた index state、未来 timestamp、通常 status の dirty worktree は `unknown`、
後続の変更は `stale` とし、HEAD 変更は保守的な判定を維持します。authoritative file check
は全 workspace path が一致する場合、dirty repository でも freshness を証明できるため、
index 済み未追跡 path や subdirectory index の scope 外にある dirtiness は check mode を
失敗させません。status-level の HEAD / branch drift は引き続き file check より先に評価し、
checked failure、終了 status、workspace member health に伝播します。通常 status
の `head_freshness=head_current` は、authoritative check 済みの `fresh` と引き続き区別します。

`vacuum --dry-run` は、`file:/absolute/path/codeindex.db`、Windows の `file:/C:/absolute/path/codeindex.db`、canonical な `file:///...` 形式を受け付けます。single-slash の path を canonicalize しつつ元の query string を維持して URI fragment を無視し、validation と metric 収集に同じ query-only URI を使うため、明示的な `immutable=1` の stale-snapshot semantics も維持されます。

### データディレクトリ解決

`--db <path>` が省略された場合、cdidx は data directory を解決し、その下に `codeindex.db` を置く。優先順位は次のとおりです。

1. `--data-dir <dir>`
2. `CDIDX_DATA_DIR`
3. `XDG_DATA_HOME` が設定されている場合の `XDG_DATA_HOME/cdidx/<workspace-hash>`
4. `<workspace>/.cdidx`

`--db <path>` は最も明示的な override であり、data-directory resolution を bypass する。`status --json` は effective directory を `data_dir`、選択元を `data_dir_source` (`flag`, `env`, `xdg`, `workspace`) として報告し、automation が index の配置先を audit できるようにする。

### SQLite パフォーマンス調整

すべての `DbContext` connection は `PRAGMA cache_size=-65536` (64 MiB)、`PRAGMA temp_store=MEMORY`、64-bit process では `PRAGMA mmap_size=268435456` (256 MiB) を設定する。これらは connection-scoped な query-performance knob であり、on-disk schema は変更せず、SQLite が適用できない場合だけ skip される。

bulk-load 経路に入る高 churn な index run は、input-snapshot validation barrier の通過後に一時的に `mmap_size=0` を設定する。これにより SQLite mapping と最大の managed reference-graph working set が重ならないようにする。設定済みの mapping は failure / cancellation を含め、すべての write scope が unwind した後に復元される。通常 query、no-op indexing、低 churn の incremental run は設定値を維持する。

operator は environment variable で既定値を上書きできる。

| Variable | Default | Meaning |
|---|---:|---|
| `CDIDX_SQLITE_CACHE_KB` | `65536` | KiB 単位の正の cache size。上限は `1048576`。cdidx は SQLite が KiB として解釈するよう負の `cache_size` 値として適用する。invalid / oversized value は既定値に戻る。 |
| `CDIDX_SQLITE_MMAP_BYTES` | `268435456` | 64-bit process で使う memory-map window の byte 数。`0` 以上、上限 `1073741824`。`0` で mmap を無効化する。invalid / oversized value は既定値に戻る。 |
| `CDIDX_SQLITE_BUSY_TIMEOUT_MS` | `5000` | SQLite busy timeout の millisecond 値。`0` 以上、上限 `3600000`。低速 disk や concurrent MCP/index workflow では大きい値を使える。invalid / oversized value は既定値に戻る。 |
| `CDIDX_PREPARED_COMMAND_CACHE_CAPACITY` | `64` | connection ごとの prepared SQLite command cache capacity。正の整数、上限 `512`。invalid / oversized value は既定値に戻る。 |

`cdidx index` が成功すると、writer は SQLite planner statistics を更新し、大規模 repository で `search`、`references`、`callers` などの join が default selectivity estimate に依存しないようにする。新規 index database は初回 population 後に full `ANALYZE` を一度実行し、それ以降の成功した index run では軽量な `PRAGMA optimize` を使う。この maintenance は best-effort であり、schema contract は変更しない。

真に空の database からの bulk load では、reference candidate の構築直前にも独立した対象限定の planner-statistics refresh を実行する。有効な reference-secondary-index bulk-load guard は candidate reverse index の drop 後、identity-resolution SQL の prepare 前に `ANALYZE main.files`、`ANALYZE main.symbols`、`ANALYZE main.symbol_references` を正確に1回実行する。TypeScript に委譲する経路ではこの時点までに通常の graph / query index が復元済みであり、direct graph 経路では追加の復元 phase を挟まずに進む。CLI は indexed file が0件の状態から開始し、rebuild でも symbols-only でもない場合だけ有効化する。MCP も rebuild 前の空状態を明示的に保持して同じ条件を適用する。既存 database、update、rebuild、symbols-only、guard 無効時は従来どおりである。cancellation は indexing 全体へ伝播し、cancellation 以外の SQLite failure は nested statistics savepoint だけを rollback して、従来の planner state で graph 構築を続ける。この pre-graph phase は専用 testing hook を持ち、最終 planner maintenance とは独立している。

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
idx_symbol_refs_file      ON symbol_references(file_id)
idx_symbol_refs_container_kind ON symbol_references(container_name, reference_kind)
idx_symbol_refs_name_kind ON symbol_references(symbol_name, reference_kind)
idx_symbol_refs_name_file ON symbol_references(symbol_name, file_id)
idx_symbol_refs_name_nocase_kind ON symbol_references(symbol_name COLLATE NOCASE, reference_kind)
idx_symbol_refs_name_nocase_file ON symbol_references(symbol_name COLLATE NOCASE, file_id)
idx_symbol_refs_container_nocase_kind ON symbol_references(container_name COLLATE NOCASE, reference_kind)
idx_symbol_refs_symbol_name_folded_kind ON symbol_references(symbol_name_folded, reference_kind)
idx_symbol_refs_symbol_name_folded_file ON symbol_references(symbol_name_folded, file_id)
idx_symbol_refs_container_name_folded_kind ON symbol_references(container_name_folded, reference_kind)
idx_symbol_refs_unresolved_mutual_folded ON symbol_references(container_name_folded, symbol_name_folded)
  WHERE source_symbol_id IS NULL AND target_symbol_id IS NULL AND is_self_reference = 0
    AND container_name_folded IS NOT NULL AND container_name_folded <> ''
    AND symbol_name_folded IS NOT NULL AND symbol_name_folded <> ''
    AND reference_kind IN ('call', 'instantiate', 'subscribe', 'unsubscribe', 'razor_event_binding')
idx_symbol_refs_source_symbol ON symbol_references(source_symbol_id)
idx_symbol_refs_target_symbol ON symbol_references(target_symbol_id)
idx_symbol_refs_resolved_source_target_kind ON symbol_references(source_symbol_id, target_symbol_id, reference_kind)
  WHERE source_symbol_id IS NOT NULL AND target_symbol_id IS NOT NULL
idx_symbol_ref_candidates_symbol ON symbol_reference_candidates(symbol_id, reference_id)
```

reference の BINARY / NOCASE / folded name・container 用単一カラム index は
canonical schema に含めません。保持する `*_kind` / `*_file` composite の左端 prefix が
同じ equality seek を提供するためです。read migration はこの legacy 6本と、全rowを保持していた
旧 `idx_symbol_refs_mutual_folded` を削除します。未解決 reciprocal lookup は上記 partial indexを
使い、resolved rowやcall graph外のreferenceをgraph index rebuildへ含めません。exact queryの
readiness diagnosticも、folded / NOCASE経路を実際に支える保持済みcomposite名を報告します。

### クエリプランナー期待値

`symbol_references.symbol_name` と小さな `reference_kind IN (...)` 集合で絞る
hot graph aggregation は、`idx_symbol_refs_name_kind` で indexable な状態を
保つ必要があります。回帰テストは `ANALYZE` 前後の `EXPLAIN QUERY PLAN` を使い、
`GROUP_CONCAT(DISTINCT r.reference_kind)` の要約がname-onlyのcomposite-prefix
probeと行ごとのkind filteringに戻らず、このcompound indexを期待計画として
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

C# の無修飾 call と method group が local function 宣言に一致する場合、enclosing callable 内の
最も狭い完全な字句 block に対して解決します。宣言順は visibility gate とせず、選択された block の
overload 群を候補に残したうえで通常の call arity filter で絞り込みます。parameter、local value、
delegate value binding は graph 解決より先に local function を shadow し、兄弟 block や無関係な
enclosing callable の local function は候補になりません。callable または block range の evidence が
不完全な場合は file 全体の同名 edge へ fallback せず `unresolved` のままにします。永続化した target
identity は references、callers、inspect、impact、LSP definition/reference read で共有します。

C# の type-reference resolution は、有効な論理 partial-family identity を持つ declaration に
`LogicalPartialSymbolGrouper` を使用します。full / scoped refresh は grouped symbol discovery と同じ
安定した `family:` target key を永続化するため、language、kind、namespace / container、generic arity が
同じ 1 family 内の複数物理 declaration は semantic ambiguity ではなく `resolved_group` になります。
`resolution_candidate_count` は意図的に物理 declaration 数のままとし、
`symbol_reference_candidates` はすべての物理 symbol row を保持します。search ranking、grouped hotspot、
inspect、dependency、impact は論理 identity を使用し、definition 一覧 API は決定的な物理定義を維持します。
この key を変更するときは reference-identity contract version を進める必要があります。version 9 は
identity-aware read を ready にする前に、full、scoped、no-op、削除のみの index path で旧来の
物理 path target key を置き換えます。

C# の一般的な member 名は extraction 時に破棄しません。writer は receiver / 型の evidence を
`target_qualifier` に永続化し、reference finalization は `resolution_state` を記録します。bare-name の
`references` と無修飾 graph discovery は広い検索結果と保存済み resolution label を維持します。一方、
現行の C# reference-identity contract では、exact C# `callers` query と confirmed C#
`impact` traversal は、選択した definition ID または解決済み polymorphic-dispatch family の
いずれかに一致する `resolved` または `resolved_group` candidate を必須とします。C# の同名 definition
が複数ある場合は解決済み ID の和集合を対象にしますが、未解決または
曖昧な same-leaf evidence は引き続き確認できても confirmed caller edge や推移 hop にはなりません。
一致する definition ID が無い場合、exact callers と impact は name fallback を走査せず confirmed 0 件を
返します。CLI/MCP payload は `identity_root_available`、`identity_root_unavailable_reason`、
`graph_evidence_confidence`、`identity_root_resolution_truncated` を公開し、root 不在または identity 集合の
cap 到達では `authoritative_count: false` を設定します。impact は root 不在を heuristic とし、strict mode は
安定した `no_identity_backed_root` failure code で失敗します。legacy または stale な identity contract が
名前ベースの互換経路を維持する場合も、`name_fallback`、degraded、non-authoritative と明示しなければなりません。
他の graph 言語は、同等の永続 target-identity contract を公開するまで既存の言語固有 exact matching を維持します。
`--lang` を省略した場合、impact は同じ traversal step を分割し、C# row には解決済み candidate ID の
一致を必須とする一方、非 C# row では言語固有の exact-name graph を維持します。非 C# definition だけから
推論した root は `language_graph` と明示し、永続 reference graph が incomplete なら、局所 traversal と
identity resolution が完了していてもすべての impact count を non-authoritative とします。
C# callable hotspot も
leaf-name aggregate ではなく logical target identity を
集計し、candidate group は logical target、name、kind が 1 つに収束するときだけ加算し、unresolved
row は confirmed count を増やしません。CLI の `--include-qualified-common-calls` と MCP の
`includeQualifiedCommonCalls` は、引き続き query-time common-member noise filter を無効化します。
dependency edge は identity scope のままにし、completeness option で未解決 evidence を公開しても、
同名の file dependency へ変換してはいけません。

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

既存index、rebuild、retained graph の reference finalization は、candidate count、最小 symbol ID、
非NULL count が正で BINARY の `MIN(...) IS MAX(...)` となる single-target-family flag、安定 target key
を reference ごとに1回の correlated aggregate で
計算します。この4つの resolution field は row-value assignment のまま維持してください。
scalar subquery を分けると、大規模 graph で candidate index と symbol/file lookup が重複します。
global に一意な language/name family は
connection-local な `temp.reference_unique_symbol_families` table へ1回だけ集約し、non-C#、C#、
C# attribute fallback で共有します。この temp table は refresh command を prepare する前に別の
prepared command で作成してください。SQLite は command batch の全statementをprepareする時点で
参照tableを解決します。

真に空のdatabaseから始める通常のCLI full scan（`--rebuild` と `--symbols-only` を除く）だけは、
fresh resolution専用の契約をopt-inします。reference insertはbind parameterを増やさず、
`unresolved`、candidate count 0、self/mutual flag 0というcanonicalな暫定値を永続化します。
fresh CTEは各fileのreferenceより先に永続化済みの同一file symbolから`source_symbol_id`も設定し、
通常経路と同じ最小包含rangeのtie-breakを使い、literalのinput ordinalでbatch順序を維持します。
このauthoritative経路のfinalizationはreference全件のsource-identity UPDATEを省略します。通常full、
differential、scoped、rebuild、retained、MCP経路は従来のsource refreshを維持します。
早期のempty確認はadvisoryです。authoritativeなouter write transaction開始直後に、CLIは同じ
transaction内で`files`、`symbols`、`symbol_references`を再確認します。write前のgapで別connectionが
1行でもcommitしていた場合は、最初のrowを永続化する前にgraph scopeのfresh insert defaultを無効化し、
candidateを持たないreferenceも含めて通常のfull-resolution SQLでfinalizeします。
finalizationは`symbol_reference_candidates`をreferenceごとのmaterialized factsへ1回走査し、
candidateを持つreferenceだけをprimary keyで更新します。candidateを持たないreferenceは暫定値を
維持し、self flagも同じsparse update内で導出します。このopt-inはgraph transaction失敗後も
pendingのまま残り、graph commit後にだけ解除します。既存indexのupdate、rebuild、retained graph
rebuild、MCP indexingは従来経路を維持します。特にMCPはgraph finalization前にfile batchをdurable
commitできるため、その状態を扱う独立したrecovery契約が設計されるまでは既存の再試行semanticsを
変更してはいけません。

fold readiness には、通常の CLI / MCP full indexing が共有する、より限定的な authoritative-fresh
最適化があります。最初の書き込み前に、`DbWriter` は `files`、`symbols`、
`symbol_references` がすべて空である同一の `BEGIN IMMEDIATE` snapshot からのみ、opaque で
一回限りの claim を発行できます。claim は writer / connection と取得時の
`PRAGMA data_version` に束縛され、同じ writer が一度だけ consume できます。stamp 決定前に
別 connection が commit した場合は無効になります。built-in extractor pipeline が正常完了した
場合、finalization は claim を使って、永続化済みの全 symbol / reference value を読み出して
再 fold する allocation-heavy な処理を省けますが、FoldReady を stamp する前の SQL による
NULL completeness check は引き続き実行します。
raw `BEGIN IMMEDIATE` helperは、成功後のcancellation checkを、callerがrollback ownershipを記録した
後にだけ行います。その境界でcancelされてもwriter gateを解放する前にraw transactionをrollbackし、
warmなCLI / MCP connectionを次のrequestで引き続き利用できます。
run は registry の accepted-producer mutation generation も取得します。一時的な custom producer
が後で削除され、最終 registry が再び built-in-only になっていても、generation が変化していれば
claim を無効化します。staged workspace replacement の commit もこの履歴に含めますが、
diagnostic-only publication と状態が変わらない missing-directory discovery は generation を進めません。

この shortcut は意図的に fail closed です。rebuild、update、legacy または既存 index、public な
`DbWriter` readiness API、custom plugin / pattern config、post-extraction hook、owner が異なるか
再利用された claim、外部 connection が commit した database では、従来の full value validation
を使います。shortcut は insert 時に生成する folded value も、readiness transaction / rollback の
semantics も変更しません。

C# の明示的 interface member では、`symbols.name` は短い表示用 / discovery alias のままにし、
`symbols.name_folded` に正規化した interface qualifier と末尾 method の generic arity を
保存します。identity が異なる場合は `symbols.display_name_folded` に短い Unicode-folded
discovery alias を保存し、`idx_symbols_display_name_folded` によって非修飾の完全一致 query
も index 対応に保ちます。kind と正規化済み signature は canonical symbol row の独立した列に
保持するため、outline や LSP の表示名を変えずに qualifier、member kind、arity、signature を
identity 比較へ残せます。修飾した完全一致 query は generic parameter 名を arity に正規化し、
indexer の `this` と `Item` を同じ表記として扱います。非修飾の完全一致 query は短い名前の
discovery alias を使うため、明示的実装と public member の両方を返す場合があります。fold の
検証 / backfill は永続化済み signature から両方の fold を復元します。
`CSharpSymbolNameContractVersion` の変更時は、indexing が未変更の C# file を refresh するか、
`backfill-fold` / MCP `backfill_fold` が v3 stamp 前に full rewrite mode へ切り替わります。

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
CLI `inspect --selector 'id:<n>@g:<fingerprint>'` は出力時の index generation を検証し、active
database の永続 ID を直接解決してから同じ candidate-bundle 経路へ入ります。selector parser は型付きで拡張可能な model に保ち、正でない
ID や不正形式は graph state を開く前に拒否し、database-local ID が存在しない場合は
もちろん、stale / cross-database generation の場合も `E018_QUERY_NOT_FOUND` を返してください。
generation なしの `id:<n>` は same-database 互換入力として維持しますが、generation provenance
は証明できません。`identity_scoped` は schema の利用可否だけでなく
evidence の精度を表します。inbound reference に複数の resolution candidate が残る場合は
candidate row を維持しつつ bundle を non-identity-scoped とし、
`identity_scope_reason: ambiguous_reference_candidates` を出力します。C# call は通常の required
parameter overload を位置引数個数だけで絞り込めます。named argument、optional/default parameter、
`params`、generic method inference、不完全な構文は曖昧なままにします。extension の receiver
調整と dynamic receiver の型は、この arity helper の対象外です。
C# constructor resolution では direct call と未修飾の `using` alias を正規化した後の call の
両方に同じ保守的な契約を適用します。identity には canonical な target leaf と qualifier を保持し、
位置引数を数える際は物理 source line を維持して source column と span で字句上の constructor
token を特定します。上限付きの multiline context で balanced scan を完結できます。target family
内の primary / explicit constructor に optional/default または `params` binding を持つものが1つでも
あれば、引数個数でその family を絞り込まないでください。named argument、raw string、embedded
expression を含む interpolated string、上限付き scanner では区別できない relational / generic angle syntax、
不正な persisted span、不正な call も同様に曖昧なままにします。単純な conditional expression と
`global::` qualifier は named-argument syntax ではなく位置引数 expression として扱います。
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

`symbol_references.reference_kind` には extractor が出力した raw label を保存する。既定の call-graph 表示（`callers`、`callees`、inspect/analyze の caller / callee bundle、および JSON/MCP フィールド）は、公開 canonical 語彙 `call`、`instantiate`、`subscribe` を返す。呼び出しを伴わない value read は canonical `member_read` を使い、既定の callers / callees / impact traversal から除外する。含める場合は CLI の `--include-member-reads` または MCP の `includeMemberReads` を明示する。primary `reference_kind`、`reference_kinds`、`reference_kind_counts` の key はすべて同じ語彙を使う。legacy index は引き続き読み取れるが、これらの read を `call` として保存しているため、再 index するまでは従来の inclusive な挙動を維持する。raw extractor 出力を調べる場合は、`callers` / `callees` の `--raw-kinds`、または `references --kind <raw-kind>` を使う。

`ReferenceRecord.SpanLength` と `symbol_references.span_length` は、解決後の symbol 名から導出せず、物理的に一致した token 幅を永続化する。これは `base`、`super`、`this` のような constructor-chain token で重要になる。`DbReader.GetCallees` は count を集約しながらその span を保持し、列が保存された row のうち最小の `(line, column_number)` を選び、その 1-based 座標を `first_line` / nullable な `first_column`、同じ row の nullable な幅を `first_length` として公開し、`reference_count` は独立した集約値のままにする。寄与する legacy row がすべて `column_number IS NULL` の場合は最小行と null 列を保持する。移行済み row では列があっても span 長が null の場合がある。CLI/MCP の location adapter はどちらの場合も精度を捏造せず劣化させる。

| Raw kind | Logical graph kind | 備考 |
|---|---|---|
| `call` | `call` | 直接実行される呼び出しエッジ。 |
| `member_read` | `member_read` | 呼び出しを伴わない member / value read。既定の invocation graph から除外し、明示 kind filter または member-read compatibility option のときだけ含める。 |
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

index 後の TypeScript declaration merge と reference identity の確定は、1回の graph pass を共有する。CLI full scan、scoped update、MCP indexing は、clean readiness で augmentation reference rebuild が確実に実行される場合だけ先行 mutual-recursion refresh を遅延する。rebuild は合成 `augmentation` edge を削除・挿入して graph を確定する。空batchでもedgeを削除した場合または遅延passを明示的に引き継いだ場合は確定を行うが、edgeの挿入・削除がないmarker検証だけなら全graph走査をせずreadinessだけをstampする。immutable-input validation により後から partial になった場合は、readiness 処理の前に orchestrator が遅延 pass を補完する。TypeScript target のない authoritative な fresh / rebuild scan は augmentation row を走査せず contract だけを stamp する。これにより graph 公開と retry semantics を維持しながら、大規模 TypeScript repository での全 graph refresh 2回を避ける。

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

CLI は exact-mode alias を正規化してから、raw `--fts` と `--exact`、
`--exact-substring`、`--token-boundary` の組み合わせを database dispatch 前に
拒否します。これにより query context と replay output は 1 つの一致モデルだけを
保持します。

検索結果の囲みシンボルは、index が宣言座標を持つ場合、主要一致の行と列を使って
判定します。C# の位置 record property は、component の最初の attribute または型 token から
識別子までの範囲を保持します。これにより、同じ物理行に位置 component があっても、
record keyword、record 型名、base 引数、body member は実際の囲みシンボルに帰属します。
これらの property symbol を合成するのは `record`、`record class`、`record struct` 宣言だけであり、
通常の C# class / struct の primary constructor parameter は member definition ではありません。
列 metadata がない旧 row では行ベースの fallback を維持します。主要一致の選択は表示される
focus 行と一致します。query が言語を指定しない場合、exact-source 名は各結果の言語で正規化し、
raw source の列 map は escaped name が正規化で実際に変わる行にだけ作成します。

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
| JSON / JSON Lines | JSON は `object`、`array`、`property` と、上限付きの primitive-array `value` symbol を index 付き path で出力します。配列 index は親 path に直接連結し（`command_cases.command_cases[0]` ではなく `command_cases[0]`）、object / array の親 kind を保持して outline の depth に階層を反映します。root array は `[0]` のような path から始まります。`.jsonl` と `.ndjson` は空でない物理行を個別に parse し、`[0].result.path` のような安定した 0 始まり record path を付けます。各有効 record から repository-local path reference を出し、不正な隣接 record の内容は平坦化しません。 |
| TOML / repository metadata | TOML の table / key、EditorConfig の section / key、Git / Docker ignore rule、Git attribute の rule / attribute、`.rules` の block / key を上限付き structural symbol として出力します。reference は repository-local な path / glob に限定し、remote URL、絶対 filesystem path、親 directory traversal は抑止します。 |
| CODEOWNERS | path-aware detection は enclosing Git worktree root（非 Git input では scan root）からの相対位置が case-sensitive な `.github/CODEOWNERS`、repository root の `CODEOWNERS`、`docs/CODEOWNERS` である場合だけ認識します。専用の上限付き line parser が full-line / inline comment を処理し、順序付き ownership `rule` symbol と child owner `property` symbol を出力して ownerless rule も保持します。上限は1行4,096文字、1 rule 128 owner、1 owner 256文字です。GitHub が対応しない先頭 `#` の escaped pattern や malformed mention を含む不正 input は、category ごとに重複を除いた永続化可能な extraction-diagnostic annotation にします。owner authorization と pattern から path への解決は syntax-only contract の範囲外なので、reference extractor は登録しません。 |
| Windows application manifest | manifest element path、assembly identity、execution level、supported OS value を structural symbol として維持します。依存 assembly identity は `dependency` reference、local な `file` / `codeBase` / probing path は `project_reference` edge を出力します。 |
| XML / NuGet.config | 汎用 XML は上限付きの element / attribute path を出力します。NuGet.config ではさらに package source、source mapping、署名検証モード、trusted signer 名、証明書 fingerprint、`allowUntrustedRoot` の値を `nuget.*` subkind 付きの semantic `property` symbol にします。 |

行指向の正規表現 loop に入る前に、built-in の case-sensitive pattern は、単一の
`RequiredLiteral` または監査済み alternative 用の `RequiredAnyLiterals` という、相互排他的な2種類の
Tier A gate のどちらかへ opt-in できます。すべての literal は2文字以上で Ordinal matching を使います。
単一 literal は正規表現の全成功経路に substring として必ず現れ、any-of set では全成功経路に distinct な
member の少なくとも1つが必ず現れなければなりません。正規化済み file content に単一 literal がない場合、
または any-of set の全 member がない場合だけその pattern を skip し、残る pattern の順序は変えません。
any-of 形式は、証明済みの JavaScript / TypeScript HOC family、TypeScript の quoted / identifier 両方の
`namespace` / `module` pattern、Kotlin の `class` / `object` と `val` / `var` に限定しています。
`IgnoreCase` pattern、1文字の literal、いずれの証明もない optional / alternative path、project の custom
pattern、plugin は意図的に対象外です。

C# の不完全 attribute recovery や C++ の same-line member recovery を含め、pattern list を参照する補助
scan は同じ順序の applicable set を使わなければなりません。各 regex call の直前には、C# property
header の結合、Fortran continuation の連結、Java / Kotlin annotation の除去、C# wrapped modifier の
合成、C++ same-line segment、CSS selector の brace 再構成などを反映した、実際に regex へ渡す input
そのものに同じ single-or-any の Ordinal 判定を適用します。any-of input は member が1つもない場合だけ
skip します。miss は言語固有 recovery を終了せず、regex failure と同様に扱います。特に C# static
constructor の bare identifier 行が gate miss しても、合成した各 `static ...` wrapper は引き続き試さなければ
なりません。comment、string、annotation、別 declaration にだけ required literal がある場合は
exact-input 判定が失われた最適化を回収し、match は変えません。custom / plugin pattern を含む、どちらの
gate も持たない pattern は従来どおり実行します。

C# では、この一般契約に加えて、成功可能性を変えない3つの狭い gate を使います。property-header
lookahead は、空 input と `;` / `}` で完結した行で prefix regex より前に戻ります。末尾 `=` は
`(` が無い場合だけ skip し、複数行 default argument を保持します。built-in plain-field regex は、全成功経路が
必ず消費する `=` または `;` が、変換後の実 input にある場合だけ実行します。wrapped-modifier recovery は、
同一 scan offset の function pattern 間で、見つかった prefix と見つからなかった null の両方を cache します。
また、確認 regex の lowercase ASCII + whitespace の形に合わない直前行を先に除外し、確定した prefix は前向きに
1回だけ materialize します。これらは private な built-in 最適化であり、pattern / plugin API、symbol field、diagnostic output、
cancellation point、same-line column mapping は変えません。

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

watch が生成した batch は内部 `--files` 呼び出しとしてこの runner に入る。
`RunPartialUpdate` は、その sub-run が directory や未対応 event target などにより
`UsageError` を返した場合だけ出力を抑止する。捕捉済み rejection を破棄して JSON
spool の内容も出力せず、失敗 watch event を出さない。同じ `startup` または `incremental`
phase で `--files` を付けない workspace 全体 rescan をちょうど 1 回呼び出す。
その rescan の exit code を記録して後続 partial batch は実行せず return する。
他の sub-run exit code は通常どおり報告する。この fallback により、event batch
内の有効な sibling 変更が一時的な path-shape race のために取り残されない。
polling snapshot は path を追加する前に symlink / reparse の immediate target と final
target を解決する。configured DB、SQLite sidecar、lock/info、checkpoint、restore/backup、
atomic temporary artifact の alias と、それら artifact に解決される ancestor ignore
alias は除外する。`internal` / `all` で許可される通常の file / directory symlink は
追跡を維持する。directory subtree は full scanner と同じ depth-first の lexical alias
選択を使い、alias 配下だけ descendant path を解決し、解決済み directory identity を
重複排除して cycle と重複 target を bounded に保つ。

`--commits` は `git diff-tree --no-commit-id -r --name-only` で変更ファイルパスを解決します。
`--changed-between` は `git diff --name-status -M <old-ref> <new-ref>` を使い、rename の旧パスと新パスを両方含めるため、古い indexed path も purge できます。

Git scoped refresh は、要求された範囲を永続化済みの workspace 全体検証基準（`workspace_verified_head_sha`、旧 database では `indexed_head_commit` へ保守的に fallback）と比較します。基準が指定 old ref より古い、または分岐しており、new ref が現在 HEAD の場合、index 前に基準から現在 HEAD までの path を caller range と合流します。scoped mutation ごとに対象 path set を先に commit し、次回の baseline 補完済み Git refresh では commit 間の net diff が空でもその path を合流します。bounded set が不完全な場合や full scan が partial な場合は、通常の full scan が成功するまで scoped refresh による検証値更新を fail-closed で拒否します。永続基準を git で解決できない場合は、不完全な freshness を公開せず fetch または full-workspace refresh の案内を返して停止します。成功した full scan または補完済み Git refresh だけが `workspace_verified_head_sha` を進めます。`indexed_head_sha` は最新の成功 scoped/full write、`indexed_head_commit` は full-scan 互換基準を引き続き表します。明示的な `--files` は現在 HEAD の検証 stamp を維持できますが、古い検証値を進めることはできません。成功 metadata は update 終端の transaction でまとめて commit するため、failure、partial、cancel、rollback では検証 provenance が進みません。検証 HEAD と最新 write HEAD が異なる場合、nested `head_freshness` は古い検証 HEAD に最新 write の branch、timestamp、ahead count を結び付けず省略します。top-level の最新 write field は引き続き利用できます。

watch batch は top-level の cancellation token を使ってこの部分更新 runner を再利用する。Ctrl-C、SIGTERM、埋め込み host token は初回 scan 後も有効なので、sub-run の完了を待たず、idle watch wait、実行中 extraction、FTS recovery / rebuild / optimization、SQLite planner maintenance を中断できる。watch は第2の console handler を登録せず、top-level の「最初の Ctrl-C は協調的 cancellation、2 回目は強制終了」という契約を維持する。cancel された bulk FTS completion は同期 trigger を復元し、transaction rollback で marker が戻らなかった場合は owner 非依存の recovery marker を残す。長寿命 MCP write context は request ごとの token を登録し、cancel 後の dispose-time planner maintenance を抑止する。sub-run JSON は `CommandOutputWriter` の async-local scope から watch capture writer へ送られ、watch loop は `Console.Out` を置き換えないため、他の command や埋め込み host は自身の stdout を維持できる。

source membership は `FileIndexer` で共有し、full scan、workspace freshness check、watch のすべてで `.cdidx` namespace を除外する。watch は source filter の適用前に ignore file と `.cdidx/patterns/**` / `.cdidx/plugins/**` を分類するため、これらの非 source 入力は debounce 付き reconciliation event として保持し、通常の `.cdidx` sidecar は除外したままにする。event-driven enqueue と polling snapshot のどちらでも path を受理する前に、解決済み database、WAL/SHM/journal sidecar、index lock、lock-info file、および各 target に対する `AtomicFileWriter` の sibling temp form と正確に一致する path も除外する。temp matcher は writer と共有する filename pattern から target に基づく prefix を導出し、workspace の path comparison を使う。`.tmp` や `.cdidx-*` を一律除外しないため、無関係な user file は通常の source membership に従う。subdirectory watch は通常、repository rule root までの各 ancestor directory に non-recursive かつ ignore-file-only の watcher を追加する。macOS/.NET 8 では project tree の FSEvents を維持しつつ、runtime が祖先 event を黙って見落とす問題を避けるため、ancestor `.gitignore` / `.cdidxignore` の exact path だけを bounded polling する。project の再帰 polling は failure-recovery backend に限定したままにする。生成された `--files` sub-run は extractor 入力を認識し、process registry の generation を refresh してから unchanged-file reuse を無効にした full scan へ fallback し、保持対象の全 source row を新しい generation で抽出し直す。refresh は埋め込み host が明示的に登録した extractor を維持しつつ、file から発見した以前の workspace plugin / pattern を unload して現在の generation を読み込むため、編集や削除後に extension membership や persisted row が stale のまま残らない。

watcher は startup reconciliation scan より先に有効化する。`FileChangeBatcher.TryDrainImmediately` は通常の debounce interval を待たずに buffer 済み startup generation を閉じ、その path を `watching` event より前に適用する一方、snapshot 後に到着した event は通常の live update として queue に残す。すべての startup reconciliation sub-run が成功した場合だけ `watching` を出力し、失敗した generation は batch を捨てて ready を宣言せず non-zero exit を返す。この generation boundary により、初回 scan と subscribe の間の gap と、変更が連続する workspace で ready が無期限に遅れる問題の両方を防ぐ。

### Index 中の FTS maintenance

| 状況 | FTS policy |
|---|---|
| 差分 write ごと | `fts_incremental_writes_since_merge` と `fts_incremental_writes_since_optimize` の両方を増やします。 |
| merge counter が 25 に到達 | `INSERT INTO fts_chunks(fts_chunks, rank) VALUES('merge', -1000)` を実行します。1,000 page は最小 work target で、SQLite は完全な segment 単位でさらに処理する場合があります。merge counter だけを reset し、optimize counter は `cdidx optimize --dry-run` の推奨判定用に累積を続けます。 |
| dirty byte が既知 workspace byte の 3/5 以上 | CLI full scan と MCP refresh は trigger-free bulk rewrite、FTS rebuild、full optimize を使います。 |
| fresh index / 明示的 rebuild | 常に bulk path を使います。 |
| scoped `--files` / `--commits` refresh | trigger 同期と incremental merge maintenance を維持します。 |
| 明示的 optimize | `cdidx optimize --db <path>` と `cdidx index <projectPath> --optimize` は full optimize を実行し、両 counter を reset して `fts_last_optimized_at` を記録します。大きな index では短時間 writer lock を保持する場合があります。 |

各 FTS5 rebuild は、その table の effective な `automerge` 値を一時的に zero へ
変更します。設定変更、rebuild、以前の値の復元は同じ transaction または nested
SAVEPOINT を共有するため、cancellation、failure、process 終了時は再構築した index と
設定の両方が rollback されます。standard / trigram table は別々の scope を使い、その後
bulk guard が既存の最終 optimize を1回実行します。再構築中の bounded な安全弁として
FTS5 crisis merge は維持します。

bulk path の見積もりと purge の安全性は次の規則に従います。

- dirty byte は書き換える各 file の current / persisted size の大きい方に、rename の
  旧 path を含む削除予定 row の persisted size を加えます。
- 比較対象の total は、読み取り可能な current workspace byte、削除予定 byte、縮小した
  file の persisted-minus-current の正の差分を含みます。両辺を同じ更新前 footprint で
  比較するためです。
- scan error、persisted size の不正値、byte 加算 overflow がある場合は estimate を
  incomplete とし、保守的に trigger 同期を維持します。
- stale file ID は policy 選択前に mutation なしで plan し、選択した bulk guard 内だけで
  削除します。昇順 ID により C# prepass は二分探索で purge 予定 row を除外し、
  reusable-stat eligibility は代わりに index 付き一時 SQL filter を使います。
- MCP の plan 後から scan までに path が再出現しても、同じ run が purge 予定の row は
  reuse しません。
- purge 前の contract 存在 query は plan が非空の場合だけ実行します。削除で implicit
  implementation reference が失われる可能性があれば C# を再抽出し、MCP は最初の
  mutation で symbol-name contract を invalid にします。purge 後の scan error では、次の
  clean run が stat reuse を無効化して reference を修復し、その後に contract を stamp します。
- batch delete の commit 前に cancellation された場合は全削除を rollback します。bulk
  purge の commit 後なら、guard の abandon 処理が残存 chunk から FTS を rebuild し、
  trigger を復元してから終了します。
- full scan と MCP refresh は、current target に使われない非 purge row が current target
  より多い場合だけ current-target path filter を使います。それ以外は全 current path の
  複製を避け、昇順 ID 除外を使います。

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

`impact` の `file_impacts` collection では、許可する leaf を
`FileDependencyResult` から導出します。対象は `result_kind`、`source_path`、
`target_path`、`source_db`、`target_db`、`reference_count`、`ranking_score`、
`symbols`、`evidence` です。compact な file-impact row は `source_path`、
`target_path`、`reference_count`、`result_kind` を保持するため、空でない各 row を
一意に識別できます。曖昧な `file_impacts.path` と `file_impacts.file` alias は拒否し、
callers と definitions では既存の path alias を維持します。

`inspect` は従来の JSON bundle を shared bounded-response envelope に変更しないため、
同じレジストリ内に専用の typed schema を持ちます。top-level group に加え、definitions、
nearby symbols、references、callers、callees では 1 階層だけの `collection.field` を
受け付けます。inspect selector は大小文字と hyphen を正規化し、alias 解決後に最初の出現を
残して重複を除き、canonical な指定順を維持します。parent が選択されていれば child より
優先して完全な row を保持し、それ以外は選択した leaf だけを row projector が出力します。
projection は最終 serialization と byte budget 適用より前に行い、root metadata、section の
total / cursor / truncation、partial-family metadata、definition body の paging / recovery field を
維持します。query 不要の `inspect --fields list` catalog と未知 field error も同じ schema から
生成します。

| output mode | 契約 |
|---|---|
| human-readable default | query command（`search`、`definition`、`references`、`callers`、`callees`、`symbols`、`files`、`excerpt`、`map`、`inspect`、`outline`、`suggestions`）は既定で**人間向け出力**です。 |
| `--json` | JSON lines output（1 行 1 JSON object）に切り替えます。AI agent が容易に parse できるよう設計されています。 |
| 委譲された audit command identity | `audit` は内部で recipe 実行を `search` へ委譲しますが、人間向け usage、復旧 hint、生成する replay command では公開された `audit` identity を維持します。ただし compact results-only replay は、results-only に対応する正規の `search --recipe` entry point を使います。明示的な `audit --json` の usage error は `command: "audit"` を持つ安定した version 付き command-error object を出力し、人間向けの `usage` を含めません。直接の `search` error は `search` identity を維持します。遅い段階で発生する response-budget failure も同じ invocation context を使うため、`command`、`usage`、`retry.command`、audit 固有の復旧 guidance に `audit` と `search` の identity が混在することはありません。 |
| recipe issue-draft summary | `search --recipe ... --format issue-drafts --summary-only` と `audit` alias は、完全な issue body を描画せず専用 summary DTO を使います。結果がある各 recipe query は、query / title identity、result / file 件数、最大 5 件の count 付き evidence path と明示的な path 省略 metadata、label、severity、confidence、任意の result cursor、full-detail replay command を持つ compact な `drafts[]` row を 1 件生成します。root は `total_count` とその authority / lower bound、`returned_count`、`omitted_count`、`truncated`、上限なし summary 用の `recovery_command` を返します。`--max-json-bytes` は最後の改行を含む正確な UTF-8 document を計測し、末尾の完全な summary row だけを省略します。0 row envelope も収まらない上限では、呼び出された `search` または `audit` identity の型付き `E028_RESPONSE_BUDGET_TOO_SMALL` guidance を返します。full issue-draft mode は既存の recipe metadata、evidence、source、triage、描画済み body shape を維持します。 |
| `definition --json` の未検出 | 既定 format の definition lookup で一致する symbol がない場合、`--body` の有無にかかわらず、共通の versioned `E018_QUERY_NOT_FOUND` command-error object を出力して終了コード `2` を返します。空の stdout のまま成功することはありません。bounded-envelope control の使用時は object を location row として projection せず `metadata.error` に移し、`results` は空のままにします。この object は `--max-json-bytes` に対して事前検査され、収まらない上限では oversized stdout を出さず usage error を返します。`--count` は引き続き構造化された 0 件 object を返し、明示的な location format も既存の format 固有の empty-result output を維持します。 |
| raw discovery JSON shape | `symbols` と `files` は、array、NDJSON、envelope の各出力で同じ DTO 経路から result row を構築します。そのため `symbols --json=array` も NDJSON と同様に `exact_index_available` を保持します。結果件数や `--max-json-bytes` の有無にかかわらず選択した flat shape を維持し、0 件の NDJSON は空 stream、`--json=array` は常に array となり、byte cap 到達時は top-level type を変えずに末尾の完全な row を省略します。bounded projection は row を `results`、pagination fact を `metadata`、exact-query readiness を `metadata.response_context` に保持し、result row を response context として再利用しません。truncation / freshness metadata も結果と一緒に必要な場合は `--format compact` または `--json-envelope` を使用します。 |
| generated-code filtering metadata | DB-backed discovery の `query_context` は常に `include_generated`、`generated_code_policy`、`generated_file_filter_available` を返します。`files --count --json` と `issue-drafts` を含むすべての JSON `map` summary は、`generated_file_count_excluded` と `generated_file_count_excluded_authoritative` も返します。generated file を含める場合、除外数は `0` です。`files.generated` が無い legacy DB で filter が要求された場合、未実行の filter を実行済みと誤認させないよう、policy は `unavailable`、count は `null`、authoritative / available flag は `false` になります。明示的な `--include-generated` は `include` のままで、authoritative な除外数 `0` を返します。byte cap の有無にかかわらず、raw discovery array は query result row が 0 件でも SQLite trust diagnostics を維持します。 |
| map の scope、depth、freshness | `map --depth <n>` は path、language、test、generated-code、除外条件を適用してから、指定した prefix depth で module を集計します。scope を絞った map output からは workspace 全体向けの decomposition plan を除外します。workspace HEAD metadata は map と同じ SQLite snapshot から 1 query で読み、`head_freshness` に `scope=workspace`、現在の成功 index stamp なら `indexed_head_source=latest_index`（fallback の場合だけ `legacy_full_scan`）、互換用 stamp は別名の `legacy_full_scan_head` として明示します。`issue-drafts` は scope 内の全 file を閾値評価するため、`candidate_source=evaluated_scoped_candidates`、candidate 件数、group 合計、省略数、`truncation.issue_draft_candidates` は candidate 基準になります。`truncation.largest_files` は明示的な互換 alias としてのみ残します。 |
| `test-extractor` JSON | 機械可読な `test-extractor` success は versioned `{"api_version":"1","symbols":[...]}` envelope を使い、内側の symbol object は既存の property 名を維持します。`--json` failure は共通の versioned command-error 契約を使います。 |
| database diff category | すべての `diff` summary は `data`、`schema`、`readiness_provenance`、`volatile_telemetry` の entry を返し、各 entry は `evaluated`、`included`、`different`、`reason_count`、stable な `reasons` を持ちます。既定の `semantic` status は volatile telemetry を除外し、`--data-only` は readiness/provenance も除外します。`--include-telemetry` は volatile な実行 metadata を明示的に含めます。`summary.difference_reasons` は判定対象に含まれる完全な reason set であり、non-identical result では必ず空でないことを維持します。 |
| 上限付き database 比較 | `diff` は filesystem identity を確認する前に local path と SQLite file URI を正規化するため、sidecar の無い symlink や同等の SQLite snapshot semantics を持つ URI alias を含む同じ読み取り可能な database は、row を列挙せず header を 1 回読むだけで identical result を返します。active WAL によって snapshot が異なり得る通常 path と immutable URI は別の input として扱い、path 固有の sidecar を持つ異なる hard-link / symlink path は snapshot 比較へ戻します。別 file で SQLite sidecar が無い場合は固定 buffer の byte 比較を行い、完全な copy なら short-circuit します。sidecar がある file と non-identical な全 input は、上限付き streaming row 比較を継続します。`import --check` 中に row または row 単位 byte budget へ到達した場合、JSON mode は `error_code=import_destination_comparison_budget_exceeded` と `root_cause=comparison_budget_exceeded` を持つ型付き `import` error 1 件を返し、text mode は同じ実行可能な retry 案内を表示します。 |
| compact location envelope | CLI の `--format compact` location output は、`api_version`、返却 `count`、limit 到達を基準にした保守的な `truncated` / `truncation` metadata、適用済み `query_context`、軽量な `results` row を持つ versioned envelope です。 |
| grouped search の総数 | `search --format grouped` の `total_matches` / `matched_count`、`total_groups`、`total_files` は、表示 page ではなく上限適用前の query 全体から算出します。`grouped_match_count` は返却 group に渡した row 数、`emitted_match_count` は file ごとの上限適用後に残った row 数を表し、`omitted_match_count`、`truncated`、`has_more`、`continuation_action` が未完了出力を示します。 |
| 高ボリューム応答の bounded 契約 | `search`、`definition`、`find`、`status`、`hotspots`、`references`、`callers`、`callees`、`symbols`、`files`、`languages`、`impact`、`map` は、それぞれの schema が公開する共通 bounded-response control に対応します。新しく発行する opaque な `--cursor <response:v2:...>` は offset を command / query / filter と index generation に束縛し、移行用に legacy の `response:v1:<offset>:<fingerprint>` も受理します。選択条件または generation を変えて再利用すると restart-required の案内付きで失敗します。`search --format compact`、`symbols --format compact`、`files --format compact` は bounded 契約を自動選択し、`search --json=array --json-envelope` は opt-in の array envelope、`languages --json` は paging または `--max-json-bytes` 指定時に同じ契約を使います。既存 compact の root と location row は維持したまま共通 metadata を追加します。metadata は `returned_count`、取得可能な場合は authoritative な `total_count`、`omitted_count`、`remaining_count`、`cursor_offset`、`page_limit`、`has_more`、`next_cursor`、`result_stable_at`、`pagination_window_limit`、`pagination_window_exhausted` を返します。safety window は 10,000 row で、上限到達時は次の request が拒否する cursor を返さず `next_cursor` を抑止します。pageable command は `offset + limit` 件を serialize せず、cursor offset を database / scan layer へ渡します。`find --all` の partial scan cursor は次の path / line を保持し、再利用時は最後に scan した line の次から継続します。`hotspots` と `impact` は active な主要 nested collection を `results` としてページングし、`metadata.primary_collection` でその名前を示し、scalar / container evidence は `metadata.response_context` に保持します。`callers.path,callers.depth` のような dotted field で collection と row field を同時に選べます。`--max-json-bytes` は最後の改行を含み、完全な envelope が収まるまで末尾の完全な row を省略します。`definition` は既定で metadata-only のままで、明示的な `--body` は `body`、`body_content`、`all` で保持し、それ以外の projection では materialize 前に抑止します。`map --sections` は section-level projection として残り、dotted な bounded field は選択した array section を section 固有の総件数付きでページングし、scalar projection は不要な ranking array を構築しません。 |
| grouped partial-family の継続取得 | grouped `symbols` row は authoritative な family-member の総数、返却数、省略数、残数を報告し、materialize する member を最大 50 件に保ちます。opaque な recovery / next cursor は正規化済み symbol 選択、固定長の family ID、member offset、index generation に束縛され、長さが無制限になり得る family key 自体は埋め込みません。SQL が JSON aggregation 前に family-ID filter と member offset を適用するため、継続取得時も family 全体を managed memory へ読み込みません。compact / projected 出力は `family_members` を省略しても recovery metadata を維持し、array、NDJSON、envelope、byte-bounded の各経路は同じ row 契約を serialize します。 |
| bounded outline 応答 | `outline` は `--max-json-bytes` がある場合だけ共通 bounded-response 契約を選択します。wrapper は projection 済みの完全な symbol row を抽出し、階層と決定的な順序を維持し、authoritative な返却 / 総 / 省略件数を報告します。UTF-8 計測には最後の改行を含め、束縛済みの `response:v2` continuation cursor を発行します。最小 envelope が収まらない場合は、実行可能な byte field を持つ型付きの `E028_RESPONSE_BUDGET_TOO_SMALL` object 1 件を stdout に出し、stderr を空に保ちます。上限なしの outline JSON は既存の root shape と outline cursor 契約を維持します。 |
| bounded unused 応答 | `unused` は `--max-json-bytes` がある場合だけ共通 bounded-response 契約を選択します。wrapper は canonical な `symbols` row を抽出し、unused query layer で cursor offset を適用し、byte trimming 後の返却 bucket / confidence / contract-domain 件数を再計算します。任意の `by_bucket` view も同じ応答全体の UTF-8 budget に含め、compact mode はより小さな audit row へ projection します。continuation cursor は有効な audit filter、bucket mode、ordering、index generation に束縛されます。1 row を含む最小 envelope が収まらない場合は、実行可能な byte field を持つ型付きの `E028_RESPONSE_BUDGET_TOO_SMALL` object 1 件を stdout に出し、stderr を空に保ちます。上限なしの JSON、compact summary、legacy unused cursor は既存契約を維持します。 |
| MCP outline page | MCP `outline` は `fields`、`sort`、`limit`、`cursor` を `QueryCommandRunner.BuildOutlinePage` へ渡すため、projection alias、派生 sort field、安定した tie-breaker、`page:v1` query fingerprint、generation validation は第 2 の MCP 固有実装ではなく CLI outline 契約のままです。既定 page は 100 row、MCP 共通の上限は 200 row です。`maxBytes` は enrichment 済みの `structuredContent` 全体を serialize した byte 数で計測し、binary search で完全な row 数を減らして page を再構築するため、`next_cursor` も実際の返却件数から再生成されます。metadata と 1 row が収まらない budget は、進捗しない cursor を返さず失敗します。既定の MCP symbol serialization は後方互換を維持し、明示的な projection field は CLI の snake_case 名を使います。 |
| bounded 応答の edge case | `impact` は選択された nested collection だけに cursor offset を適用するため、definition page の重複や caller / fallback mode の変化を防ぎます。通常の `map --compact` は既存の section array と truncation payload を維持し、collection projection が `--summary-only` または除外する `--sections` filter で失われる組み合わせは拒否します。明示的な definition body field は compact default より優先します。profile / verbose record は `metadata.stream_control_records` へ移します。parser / capture failure の error envelope が通常 cap に収まらない場合は完全な `E028_RESPONSE_BUDGET_TOO_SMALL` diagnostic で置き換え、machine output が空または不正 JSON にならないようにします。 |
| `--count --json` envelope | `search`、`definition`、`references`、`callers`、`callees`、`symbols`、`files`、`find`、`impact`、`unused` の count-only JSON は単一の自動化向け object です。常に `count`、適用済み `query_context`、freshness metadata（`indexed_file_count`、`indexed_at`、`freshness_available`）、trust flag の `degraded` / `authoritative_count` を含みます。matched-file total を持つ command は `files` と古い互換 alias の `file_count` も含みます。`file_count` は `files` と同じ値を持つ互換用 field として残り、少なくとも次の major release までは削除予定はありません。`unused --count --json` は `returned_bucket_counts`、`returned_contract_domain_counts`、`summary.by_bucket` / `summary.by_confidence` / `summary.by_contract_domain` も含みます。`authoritative_count=false` は readiness または graph/exact trust signal により count が authoritative ではないことを示し、freshness field は count に使った index snapshot を説明します。 |
| search row selection | row を返す plain search / recipe path は `ApplySearchOutputSelection` を共有し、`--first-per-file` と固定 seed の決定的な `--sample` を、有効な query ごとの limit / 残り total limit より先に適用します。sample 用 fetch envelope は少なくとも要求 sample 数を基準に sizing します。aggregate / compact の query DTO、plain compact root、run summary、issue-draft の source DTO、NDJSON terminal、bounded array envelope の stream terminal は `source_total`、`selected_total`、`returned`、`selector_omitted_count`、`limit_omitted_count` を公開し、`source_total_authoritative` / `source_total_lower_bound` で完全な population と bounded な観測を区別します。guard filter、origin / facet の後段 filter、candidate window の枯渇、recipe の file-reject 後段 filter は lower-bound authority にします。適用順の `selectors` entry は各段階の input / output / omission count と sample の size / mode / seed を保持し、nullable な `selection_reason` / `selection_omitted_count` は互換用 summary として維持します。bounded plain-search selection は一度だけ計算し、その selected page を compact / envelope serialize で再利用します。search の `query_context.row_selectors` は適用済み selector 設定を記録します。selection だけによる省略は matched / omitted の lower bound を更新しますが、`truncated`、`has_more`、`next_cursor` は設定しません。後続の limit が選択済み row を truncate する場合、limit truncation は表示しますが raw database cursor は selector state を保持できないため `next_cursor` を抑止し、同じ理由で selector と受け取った `--cursor` の併用も拒否します。compact / issue-draft の生成 replay command は selector を保持します。compact の results-only replay は常に results-only 対応の canonical `search --recipe` entry point を使用し、構造化 argv を current shell 向けに安全に quote します。count、aggregation、named-query、recipe-list、results-only、metadata を持たない array、非対応 formatted、summary-only compact の shape は `--first-per-file` / `--sample` を拒否し、すべての recipe shape は grouped 専用の `--per-file-limit` を拒否します。 |
| search selection の edge case | issue-draft の root は query ごとの `selection_accounting` を独立して保持するため、draft が 0 件の場合や total limit を使い切った query も accounting を失いません。byte 上限付き compact / array envelope は `returned` を実際の出力 row 数へ更新し、論理的な `limit_omitted_count` を保持します。hard cap による省略は `metadata.byte_limit_omitted_count` で別に報告します。 |
| ad-hoc search SARIF | `search <query> --format sarif` は completion metadata を SARIF の各 run に格納します。run と単一の `queries[]` summary は `source_result_count`、`source_result_count_authoritative`、出力済み `result_count`、適用された `limit_per_query` / `result_limit`、保守的な `minimum_omitted_result_count`、`truncated` state を返します。source / emitted count は exact search の occurrence 展開を含む最終的な SARIF result / location 単位を使用します。guard 付き search は completion の再計数で失敗せず bounded candidate budget を維持し、source count を明示的に non-authoritative な lower bound として返して truncation state を保守的に保ちます。facet filter 付き exact search は表示用 candidate window ではなく exhaustive な source count を使います。ad-hoc search は継続 cursor を公開しないため、`cursoring_available` は `false`、`next_cursor` は null となり、shell quote 済みの `replay_command` が option のような query と有効な search control を保持します。completion vocabulary は意図的に recipe SARIF と共通化し、空 run も count が 0 の同じ field を保持します。 |
| ad-hoc issue-draft selection | `search --format issue-drafts` は filter 済みの ad-hoc 母集団全体を読み、`--first-per-file`、決定的な `--sample`、`min(--limit, --total-limit)` の順に適用します。guard 付き検索は有限の candidate inspection 契約を維持し、`source_total_count` を省略し、観測下限を `source_minimum_count`、非 authoritative 状態を `source_total_count_authoritative=false`、bounded fetch を `source_fetch_limit` で報告し、母集団が未完了であることを `truncated=true` で保持します。既存の `result_count`、`result_limit`、`omitted_count`、`truncated` field は返却 selection を正確に表し、additive な `source_total_count`、`returned_count`、`limit_per_query`、`total_limit`、`first_per_file`、`sample` field により適用済み契約を監査できます。replay command は正規化済み parse option から serialize し、POSIX-safe な単一引用符 escape を使い、raw / exact / prefix mode、path / language / facet / guard filter、selection control、evidence formatting、duplicate preflight、issue hint を維持します。 |
| Recipe SARIF | `search --recipe <name> --format sarif` は、上限付き recipe result ごとに result を1件出力します。rule ID は `recipe/query` を使い、標準の `fingerprints.cdidx/v1` は正規化済み source location から導出します。result properties は recipe/query identity、severity、confidence、query ごとの truncation を保持し、run properties は scope、適用済み result limit、集計 count、保守的な omitted-result metadata を保持します。`--max-json-bytes` は escape と末尾改行を含む schema-valid な完全 document と正確な UTF-8 byte 数を counting writer で検査し、選択した prefix だけを materialize します。完全な document が収まらない場合、末尾の result だけを1件単位で省略し、run / query に source、emitted、omitted、byte strategy、replay metadata を追加します。出力済み result の rule と location は維持し、この truncation は `--allow-partial` がなければ `11` を返します。result 0件の document の最小値より小さい cap では SARIF を出力せず必要 byte 数を報告し、明示的な `--json` では error object 自体が cap に収まる場合に version 付き error を stdout へ出力することがあります。完全な report が parser の対応可能な最大 cap を超える場合、replay metadata は byte cap を外します。SARIF の上限には `--limit` / `--total-limit` も使い、`--sample`、`--first-per-file`、`--per-file-limit` のような row selector は黙って無視せず拒否します。recipe severity は `critical` / `high` を `error`、`medium` を `warning`、`low` / `info` を `note` に対応付けます。 |
| Recipe classifier output | recipe classifier が hit を分類できる場合、recipe run JSON は個別の `CompactSearchResult` row に `audit_classifications` を追加することがあり、分類済み row がある query / count payload は `classifier_counts` を追加することがあります。これらは additive field です。raw search query を変えずに、DTO / result-wrapper の `.Result` property と Task / ValueTask の blocking wait などの triage domain を分離するために使います。JSON の read / write recipe は source-proximate な `cdidx-audit: json-trust` 注釈も origin、direction、sensitivity、trust、rationale で分類します。分類は guard により1行へ投影された row を含め、投影済み snippet ではなく上限付きの indexed source を読み、実際の C# line comment であることを lexical に検証するため regular / verbatim / raw string の内容や条件コンパイル領域を trust evidence にしません。overlap dedup 後に残った各 match site を評価し、expression-bodied method / local function の戻り型、および audit 対象型より前で改行された generic 戻り型を含め、同じ containing statement の同一行または後続行に constructor facet がある宣言型 facet は畳み込み、選択されたすべての JSON child query を横断した最初の lexical な audit 対象 match で各注釈を消費します。注釈探索は固定の行差ではなく上限付き indexed prefix を検索します。C# token 解析では nullable 宣言、直接 cast、nested-generic の first argument、宣言から解決できる直接 receiver は有効なまま維持する一方、途中の実行コード、先行 statement、評価済み operand、indexer 代入先、解決不能な単純名 receiver、1段 / 連鎖した property-valued な代入 / 呼び出し receiver、preprocessor directive、完了済み expression、control-flow block、カンマ区切りの操作がある注釈は `not_adjacent` として次の操作へ流用しません。異なる evidence を持つ row は保守的に `mixed_boundaries` とし、注釈の欠落、不正、direction 不一致、`review_required` を `ambiguous_trust` とします。分類は row を file ごとにまとめて最大必要行までの上限付き prefix を1回復元します。query ごとの lexical cache はその file prefix 1件だけを保持し、実際に復元できた行数と source budget の枯渇を記録するため、高い行で再構築を繰り返さず、低い行の結果も汚染しません。`json-parse-apis` では、保持された structured / compact row ごとに、lexical mask 済みの containing-symbol context から `parser_guard_evidence` を必ず1つ付与します。byte / depth / item / file-size bound は消費される payload と関係する必要があり、同じ operation では streaming / cancellation より優先されます。それ以外では streaming / cancellation signal または非 authoritative な `unbounded_materialization` fallback を出力します。1つの row が複数 operation を表す場合は、未bounded operation が1つでもあれば row を unbounded のままにします。compact row は `classifier_counts` と同じ classification evidence を保持します。分類は raw result を削除も並べ替えもしません。source-backed 分類は row classification または classifier count を serialize する JSON / NDJSON / compact / count JSON shape だけで実行し、text、scalar count、compact summary、SARIF、issue-draft、`--search-fields` projection path では省略します。 |
| NDJSON terminal record | `search`、`symbols`、`files` の既定 NDJSON は result row の後に最後の `terminal_record` を 1 件追加します。`search` は 0 件応答にも終端を出力しますが、raw `symbols` / `files` の 0 件 NDJSON は空のままです。recipe / audit search の row stream も同じ writer を使います。終端は返却件数と観測済み総件数、`total_count_authoritative` / `total_count_lower_bound`、selection または中断理由、適用上限、省略行数、復旧案内を報告します。raw の cursor 対応 stream で `has_more: true` かつ result を 1 件以上出力した場合、実際の出力件数だけ offset を進めた、generation と query に束縛済みの共有 `response:v2` `next_cursor` を追加します。filter と ordering を変えず再利用すると bounded envelope を選択し、`metadata.stream_terminal` にも同じ continuation を保持します。最終 page と 0 件 page は continuation field を省略します。安全に進めない partial terminal は cursor を省略し、機械可読な `next_cursor_unavailable_reason` を追加します。terminal だけの byte-cap 応答は `no_result_row_emitted`、recipe / named / row-selector stream は `stream_not_cursor_capable`、10,000 row の response window 枯渇は `pagination_window_exhausted`、row materialize 前に取得した generation と cursor encode 時の generation が異なる場合は fail-closed な `index_generation_changed_during_query`、比較用snapshotの取得失敗は `index_generation_unavailable` を使います。`--max-json-bytes` は改行、cursor、終端レコードを含む stdout stream 全体を対象とし、各 byte-fit candidate の実出力件数から cursor を再生成します。追加 selector-accounting field が原因で終端が収まらない場合は、終端自体を不可能と判定する前にそれらの任意 field を省略します。それでも終端が収まらない cap は stdout 出力前に失敗します。上限付き出力は `--profile`、`--verbose`、`--json-envelope` を拒否します。byte cap による部分出力は、`--allow-partial` で終了コード `0` を明示許可しない限り `CommandExitCodes.PartialResult`（`11`）を返します。`--results-only` はこれらの NDJSON row stream から終端レコードを明示的に除外するための option であり、array / compact / summary / count 出力との組み合わせは拒否されます。 |
| C# outline callable 表示 | `DbSymbolReader.Outline` は read 時にだけ `display_name` を導出し、canonical な `symbols.name`、qualified path、folded identity、完全一致 query alias は変更しません。完全な C# generic method signature は通常 arity placeholder（`<T>` または `<T1, T2, ...>`）で表示し、具体的な parameter type と衝突する場合は決定的で衝突しない `TArg` placeholder を選びます。置換対象は修飾されていない method type parameter の参照だけであり、修飾された具体型と escaped keyword の区別は保持します。literal-aware な走査により、attribute と既定値内の区切り文字は parameter 境界を変えません。`where` constraint と identity に影響しない `this` / `params` / `scoped` は省略し、overload を区別する `ref` / `out` / `in`（`ref readonly` を含む）は保持します。非 generic および C# 以外の formatting は既存経路のままです。永続 signature が欠落、切り詰め、または構文的に不完全な場合は、旧 index 互換のため legacy `Name@line` fallback を維持します。 |
| `outline` / `unused` cursor の束縛 | `outline --json` は bounded な機械向け出力として `--kind <kind[,kind]>`、`--limit` / `--top`、opaque な `--cursor <next_cursor>`、`--outline-fields <csv>` を受け付けます。制御付き outline 応答は通常の envelope を維持し、`total_symbol_count`、`returned_symbol_count`、`cursor_offset`、`next_cursor`、`has_more`、`result_stable_at` を追加し、該当時は `kind_filter` と `selected_fields` も返します。projection parser は検証前に alias を canonicalize して重複を除きます。未知field名はvalid候補を伴う1つの終端usage errorにまとめ、empty-selection errorは意図的に空のCSV入力にだけ使います。`outline` と `unused` の cursor は offset を正規化済み path/scope、filter、ordering、index generation に束縛するため、条件変更後または index 更新後の再利用は restart-required の明示案内付きで失敗します。移行用に legacy の `outline:<offset>` / `unused:<offset>` 入力は受理しますが、新しく出力する cursor はすべて opaque かつ束縛済みです。 |
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
| unknown-extension / runtime diagnostics | `unknown_extension_file_count`, `unknown_extension_files`, `unknown_extension_files_truncated`, `unknown_extension_file_path_limit`, `unknown_extension_extension_counts`, `unknown_extension_category_counts`, `unknown_extension_groups`, `unknown_extension_group_count`, `unknown_extension_groups_truncated`, `unknown_extension_group_limit`, `unknown_extension_group_omitted_count`, `unknown_extension_guidance`, `extractors`, `hooks`, `hook_diagnostics`, `trust_overrides`, `path_case_sensitive`, `data_dir_mode`, `mac_profile`, `mac_profile_diagnostics`, `stale_after_seconds`, `index_age_seconds`, `query_context.check_mode`, `query_context.stale_after_seconds`, `last_index_run.reference_extraction_cap_hits`, `last_failed_or_partial_index_run`, `last_failed_or_partial_index_run.progress_persisted`, `last_failed_or_partial_index_run.recovery_hint`, `last_failed_or_partial_index_run.file_errors`, `status_metadata_diagnostics`。 |
| database maintenance | `db_size_bytes`, `wal_size_bytes`, `db_pragma_settings` (`journal_mode`, `synchronous`, `wal_autocheckpoint`, `busy_timeout_ms`, `page_count`, `freelist_count`, `page_size`, `auto_vacuum`), `prepared_command_cache` (`count`, `capacity`, `hit_count`, `miss_count`, `eviction_count`), `maintenance_guidance`。 |
| database size attribution | `database_size_attribution`（`available`、`measurement`、`unavailable_reason`、物理 main/WAL/SHM size、論理/object/freelist/residual の再照合、page type と payload/overhead の小計、上限付き `top_objects`）。 |
| remediation fields | `degraded_root_cause`, `degraded_reason`, `recommended_action`, `alternative_action`, `readiness_degradations`, `repair_commands`。 |
| MCP-only session diagnostics | `mcp_session`、`mcp_session.metrics`、`mcp_session.audit_log`、`mcp.rate_limit.bucket_limit`、`mcp.rate_limit.bucket_limit_rejection_count`。`mcp_session` は persisted DB state ではなく session-scoped diagnostics で、`log_level`、上限付きの `roots`、任意の `client_info`、上限付きの任意の `client_capabilities`、常設の `metrics` object、audit 出力が有効な場合の `audit_log` を含みます。advertised root が切り詰められた場合は `roots_truncated`、`root_count`、`root_limit`、`root_uri_length_limit` が切り詰め内容を示します。client capabilities が切り詰められた場合は `client_capabilities_truncated`、`client_capabilities_truncation_reason`、`client_capabilities_serialized_bytes`、`client_capabilities_byte_limit`、`client_capabilities_depth_limit` が保持された診断 subset を示します。未設定時の `mcp_session.metrics` は `{"enabled":false}` です。有効な metrics sink は `enabled`、`path`、`max_bytes`、`bytes_written`、`disposed`、`degraded`、`queue_capacity`、`queue_depth`、`queued_event_count`、`written_event_count`、`dropped_event_count`、`queue_full_drop_count`、`serialization_failure_count`、`write_failure_count`、`rotation_failure_count`、`batch_flush_count`、`consecutive_failure_count`、`recovery_count` に加え、任意の `next_retry_at`、`last_recovery_at`、`last_failure` を追加します。MCP ping は常に metrics object を `metrics` として返し、metrics の degradation は意図的に top-level liveness result へ反映しません。audit status field と health semantics は [MCP 監査ログの出力](#mcp-監査ログの出力) に定義します。`mcp.rate_limit.bucket_limit` は normalized な `(partition, caller)` bucket 全体に対する process-local 上限で、direct call はすべて caller-wide の固定 coarse partition、canonical な既知 tool は追加の secondary per-tool partition、unknown な `batch_query` slot は caller ごとの 1 つの固定 invalid-slot partition を使います。`mcp.rate_limit.bucket_limit_rejection_count` は新規 bucket 作成がその上限を超えるため拒否された呼び出し数です。 |
| documentation sync | この一覧は `README.md` と `AGENT_GUIDE.md` と同期してください。必須 field がそれらの docs から欠けると `DocumentationStatusContractTests` が失敗します。 |

永続化された status subdocument の
`last_index_run.reference_extraction_cap_hits`、`last_index_run.rebuild_reclaim`、
`last_failed_or_partial_index_run.file_errors` は bounded SQLite accessor で取得し、
保存済み UTF-8 value が 512 KiB を超える場合は managed string を返しません。
続いて `BoundedJson` が最大 depth 16 で parse し、reader が永続化 semantic 契約を
再検証します。file error は最大 50 件、cap-hit file は最大 50 件、入れ子の reason は
最大 16 件、path は 1 件 32,768 文字、category / phase / reason code は 128 文字、
detail / rebuild reason は 4,096 文字、subdocument 内の decoded string 合計は
262,144 文字です。拒否した subdocument は status の残りを失敗させずに省略します。
`status_metadata_diagnostics[]` は対象 field、`max_utf8_bytes`、任意の
`observed_utf8_bytes` と、安定した reason `raw_size_exceeded`、`invalid_json`、
`semantic_validation_failed` のいずれかを返します。human の first failure と
recovery hint は `ConsoleUi.FormatBoundedValue` を通すため、永続化された control で
追加行を作れず、長い値には安定した truncation marker を使います。structured JSON は
受理した値を変更せずに維持します。

`status --explain` の top-level key は `status --json` と同じ source-generated
`StatusResult` `JsonTypeInfo` で解決します。ignored property は除外し、coverage test で
serialized top-level property がすべて説明を返すことを固定します。主要な readiness、
trust、extension、maintenance、cap-hit section には、明示的な registry metadata として
meaning、source、dependencies、interpretation、repair guidance を付けます。それ以外の
serialized scalar field も、DTO 拡張時に unknown へ戻らず上限付き contract explanation を返します。
dot 区切り path は collection element DTO を含む同じ source-generated nested metadata で解決し、
unknown path には上限付きの有効な candidate を返します。explain response は static contract
metadata だけを含み、known field と dependency の件数を制限し、unknown input を sanitize し、
runtime field value や path を含めません。通常の JSON はこの完全な payload を維持します。
`--compact`、`--format compact`、および明示的な `--fields` を伴わない bounded output は、
typed explanation を `api_version`、`field`、`meaning`、`interpretation`、
`remediation` へ投影してから global byte 上限を適用します。envelope は
`explanation_schema=compact` と `explanation_required_fields` を返し、省略した任意内容を
`explanation_omitted_optional_field_count` と `explanation_omitted_optional_fields` で
集計します。明示的な `--fields` は operator が選択した投影のままです。完全な envelope と
compact explanation 1件を収められない場合、空の成功ではなく、計測済み必要最小 size と
retry guidance を含む `E028_RESPONSE_BUDGET_TOO_SMALL` を返します。bounded envelope は
database path、timing、indexed HEAD、stable-at timestamp も省略します。

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

`references`、`callers`、`callees` で明示した `--snippet-lines` は、`--body` と text または JSON の結果出力を併用する場合だけ有効です。location-only format と `--count` は database を開く前にこの option を拒否するため、要求した snippet 長が黙って無視されたり、適用されないまま replay / query context に記録されたりすることはありません。明示指定かどうかは引数 parser の状態で判定するため、`--query` の値または `--` 以降に渡した option 風の literal は query のままです。bounded JSON projection の内部 count replay からは snippet 専用 control も除去し、表示可能な body excerpt で stderr、total count、cursor を正しく維持します。

bounded `--fields` 出力は汎用 JSON projection 契約であり、`--format json` または `--format compact` とだけ併用できます。CSV、TSV、quickfix、LSP、SARIF など projection と互換性のない形式は、database を開く前に `E010_USAGE_ERROR` で拒否されます。`references`、`callers`、`callees` では、collection を持つ 0 件 response を authoritative に展開するため、空 collection は空のまま維持され、result / returned / total count はすべて 0 になります。`body` または `body_content` を選択すると、開始/終了位置、requested/effective range、truncation flag/reason、next-start、omission、recovery の付随 field も自動的に保持されます。byte budget に収まらない場合は、付随 field だけを削らずに row 全体を省略するか `E028_RESPONSE_BUDGET_TOO_SMALL` を返します。

セミコロン形式の C# 位置 `record`、`record class`、`record struct` では、index 上の body range は record の宣言行から終端 `;` までの宣言全体を含み、軽量な64行の signature lookahead を超える宣言も対象です。このため `definition --body` は、`--fields` 投影時も含め、ほかの body と同じ bounded な行数 / byte truncation と recovery metadata を伴って宣言内容を返します。別の宣言が開始行または終端行を共有する場合は、同じ開始行の前方で複数行文字列が閉じていても、外側または後続 sibling の文字列を公開せず、record 宣言の開始列から終端までを列単位で切り出します。recovery hint は、`excerpt` の任意の1-based inclusive `--start-column` / `--end-column` 引数でこの境界を維持するため、行数または byte 上限からの復旧 command を実行しても sibling の文字列を公開しません。brace 形式の record は generic constraint または `allows ref struct` anti-constraint が `:` の後で改行されても brace に基づく body range を維持し、`N . Base` や `Alias :: Base` のように空白を含む修飾 base / constraint 型も宣言の継続として扱い、その継続内の preprocessor directive 行は payload 自体に `;` や `{` が含まれる場合も字句上の空白として扱います。一方、不完全な record が、完了した base-list entry 後の instance constructor、明示的 interface の method / indexer、fixed buffer を含む後続の型宣言または member 宣言を取り込むことはありません。セミコロン形式の record では、同一行の前方に別の宣言がある場合、引数なし / 位置 record の `;` が次の行の先頭にある場合、directive が名前または generic arity と後続 header を分離する場合、名前と generic arity の間に空白がある場合、source Unicode escape で名前が記述される場合、型パラメーター属性内に nested generic 型がある場合も、同一行の signature と reference scope は終端で止まり、後続 sibling の initializer は外側または後続 sibling 型に帰属します。また、同じ行で後続する複数行 method または型は、その body 内の参照を自身に帰属させます。reference extraction と symbol-container assignment は record owner ごとに解決済み boundary を再利用するため、大きな複数行宣言でも component ごとに header を再走査せず、線形性を維持します。この永続 range 変更では C# extractor contract も更新するため、通常 full scan が既存 index の未変更 C# file を再抽出します。

graph body mode は既存の定義 / container 抜粋である `body_*` を維持し、`references`、`callers`、`callees`、graph-backed な `impact` row に独立した `callsite_*` 抜粋を追加します。call-site window は indexed source から取得し、決定的な `first_reference` を中心にします。group row は source position が最小の参照を選び、個別 reference は自分自身を選びます。`callsite_line`、永続化済みの場合の `callsite_column` / `callsite_length`、`callsite_selection`、`callsite_reference_count`、`callsite_omitted_reference_count` でその選択を明示し、座標または span を index していない legacy row では該当 field を省略します。content / range field は requested / effective range、`line_width_cap` などの truncation reason、伏字化した recovery metadata、indexed chunk から正確な focus 行を復元できない場合の `callsite_content_unavailable_reason` まで body 契約を反映します。`callsite_*` field だけを含む bounded projection でも body mode を materialize し、`body_*` と `callsite_*` のどちらも含まない投影では省略します。`--body` なしの既存出力は変わりません。

`ReferenceResult` は `is_self_reference` と `is_mutual_recursion` を含み、`CallerResult` は `has_self_reference` と `has_mutual_recursion` を含む。これらのフィールドは、正当な再帰呼び出しを既定の graph 結果から削除せずに、自己再帰エッジと直接の2シンボル循環を識別する。非再帰 view が必要な reader API は自己参照除外を opt-in で使える。

MCPツール呼び出しは `structuredContent` に構造化JSON、`content` に短い要約を返すため、クライアントは型付きデータを直接利用できます。

exact-match flag の互換性は [USER_GUIDE.md](USER_GUIDE.md#フラグ互換性と移行) に記載しています。MCP schema はこの表と同期してください。`search.exact` は `exactSubstring` の legacy alias、name-based tools の `exact` は `exactName` の legacy alias です。新しい exact-match alias を追加する場合は、compatibility table、CLI help、MCP description、changelog fragment を同じ変更で更新してください。

`search`、`definition`、`references`、`callers`、`callees`、`symbols`、`files` は `--path`、繰り返し指定できる `--exclude-path`、`--exclude-tests` による絞り込みを共有します。読み取り層は tests や docs より source を優先し、`search` はシンボル名やパスがクエリと正確に一致する候補をさらに上位に出して、AIクライアントが実装ファイルへ早く到達できるようにします。

literal-safe な `search` query は reader 層で FTS5 sanitization 前に 1000 文字、128 whitespace term へ制限します。CLI、MCP、直接 reader caller の failure mode を揃えるため、この guard は `DbReader` に置きます。raw `--fts` query は別途 raw FTS complexity limit を使います。

`search --json` と MCP の `search` は、フルチャンクを `chunk_start_line`、`chunk_end_line`、`snippet_start_line`、`snippet_end_line`、`snippet`、`match_lines`、`highlights`、`context_before`、`context_after`、`truncated_line_count`、`dropped_match_line_count`、`truncation_context` を持つ軽量スニペットへ投影します。compact CLI row と MCP search result は有効な出力オプションも `snippet_lines` / `snippetLines`、`max_line_width` / `maxLineWidth`、`exact`、`raw_fts` / `rawFts`、`literal_highlights_available` / `literalHighlightsAvailable`、任意の `literal_highlight_warning` / `literalHighlightWarning` として返します。`--snippet-lines` で抜粋長を先に制限でき（デフォルト: 8、最大: 20）、`--max-line-width`（CLI）/ `maxLineWidth`（MCP）は `find` / `references` / `excerpt` / `inspect` と同じ共有 `LineWidthFormatter.ClampLine` 契約（デフォルト: 512、最大: 4096、`0` で切り詰め解除）で各スニペット行を最初のマッチトークン周辺にクランプするため、minified / transpiled / 生成された 1 行ファイル内の 1 ヒットで数百 KB を返さなくなります。クランプされた行はスニペットに `...(+N)...` マーカーが入り、`truncation_context.char_counts`、`truncation_context.total_chars`、`highlights[].truncated`、`highlights[].original_line_length`、`highlights[].truncated_char_counts` で AI クライアントがクランプの有無と省略文字数を検出できます。`highlights[].terms` は互換性のため distinct な term list のまま残し、`highlights[].term_occurrences` は一致ごとの `term`、1-based の `line` / `column`、`length` に加えて、行クランプ後に返却 snippet text 内へ残っている部分を示す `visible`、`visible_column`、`visible_length` を記録します。exact substring search では `highlights[].literal_terms` と `highlights[].literal_term_occurrences`（MCP では camelCase）も追加され、広めの診断 token list を残したまま、要求された literal phrase だけを render できます。raw FTS row は FTS 構文を単一の literal phrase へ対応付けられないため、`literal_highlight_warning` / `literalHighlightWarning` に `literal_highlights_unavailable_raw_fts` を設定します。exact ではない記号の多い code phrase 検索では、FTS tokenization が記号を失いやすい場合に exact substring semantics で再検索できるよう、CLI JSON compact result に `exact_substring_hint`、MCP `search` に `recovery_hint` を追加します。`focus_mode`、`focus_line`、`focus_column`、`focus_reason` は snippet に選ばれた match window を説明し、`dropped_match_line_count` と任意の `next_match` は選択された snippet window 外に落ちた一致行を示します。

既定の `quality` snippet focus は、先頭が文字または underscore で、残りが文字、数字、underscore だけで構成される単一 query を identifier 形状として扱います。一つの result に一致する code と、それより前の comment / string が混在する場合は、最初の `code` origin occurrence の行と列を snippet の優先位置にします。同じ長い行に literal と実行 code の occurrence がある場合も code 側を選び、自動 occurrence focus は text が行だけを指定する preferred-focus probe の上限を超える有効な chunk でも chunk 全体を走査します。空白区切りの phrase query と、明示的な `leftmost` / `proximity` focus mode は従来の選択を維持します。明示的な origin filter がある場合は、最初に残る facet の行と列へ再 focus し、focus metadata、visibility、行 clamping を filter 後の result と一致させます。選択位置は既存の focus / origin metadata で監査でき、`dropped_match_line_count` は最終的に返す window から計算し、`next_match` はその window の先を引き続き示します。

マッチ行がインデックス済みシンボル範囲内にある場合、`search --json` と MCP の `search` は任意フィールドの `enclosing_symbol_name`、`enclosing_symbol_kind`、`enclosing_symbol_start_line`、`enclosing_symbol_end_line`、`enclosing_container_name` も返します。

`find --json` は繰り返し一致でも line-delimited のまま維持し、各 row に bounded な match span / truncation metadata を追加します。`length` は 1-based の `column` から始まる一致長、`original_line_length` は行幅クランプ前のソース行長、`snippet_truncation_context.line_count` / `char_counts` / `total_chars` / 任意の `reason` は snippet クランプを表します。`--max-line-width` によって snippet 行が省略された場合、`reason` は `line_width` になります。

`excerpt --json` は 1-based の source 開始/終了位置、token `type`、`modifiers` を持つ軽量 range list の `semantic_tokens` を返すため、IDE や LLM クライアントは生の `content` 文字列を再パースせずに抜粋範囲を描画・後処理できます。C# の excerpt と LSP `textDocument/semanticTokens/full` は、keyword/modifier、namespace と type、method と property、parameter、variable と field、declaration modifier を判定する同じ source classifier を共有します。excerpt の分類は indexed source の context を利用し、出力 token budget を可視 source 行へ絞った後に適用します。bounded source scan が可視範囲まで到達できない場合は可視 content の分類へ fallback するため、狭い範囲では利用可能な context を維持し、file 後半の excerpt が手前の token に出力 budget を消費されて空になることも防ぎます。excerpt の range mapping と LSP の delta encoding は座標変換だけを担当し、semantic kind を選びません。`semantic_token_coordinate_space` は `source` です。`--max-line-width` で返却内容がクランプされた場合、`content_line_spans` は返却 content 行と可視 content column span を、対応する source 行と source column span に対応付けます。clamp marker は未対応領域として扱い、semantic token には含めません。excerpt row は `requested_start_line`、`requested_end_line`、`effective_start_line`、`effective_end_line`、`content_truncation_reasons`、任意の `content_recovery` も返すため、`--max-line-width` による `line_width_cap` を検出して省略部分を再取得できます。body を持つ JSON row は対応する `body_requested_*`、`body_effective_*`、`body_content_truncation_reasons` も返します。body reason には snippet/body 行数上限の `body_line_cap` と definition body byte 上限の `body_byte_cap` があります。call-site evidence も `callsite_` prefix で同じ field 群を返し、行幅クランプは正確な edge span を中心にします。

`content_recovery`、`body_content_recovery`、`callsite_content_recovery` では `argv` が一次的な機械可読契約です。共有用の CLI JSON と MCP response は既定で、機械固有の apphost、assembly、source、database の絶対パスを構造化 path sanitizer で伏せてから `command` を生成し、render 済み shell 文字列を regex で置換しません。SQLite file URI の query segment は個別に処理するため、`mode=ro` などの安全な control は維持しつつ、path 値や機密値を持つ query は sanitization されます。database option は既知の source 引数位置より後だけで探索するため、`--db` のように option と紛らわしい source 名でも DB path redaction を迂回できません。既定の metadata は `paths_redacted: true`、`command_display_only: true` を返し、いずれかの引数を置換した場合は `requires_local_path_substitution: true` も返します。先頭が `-` の root-level path には対応済みの `--` end-of-options marker を維持し、`command_shell`（`posix-sh` または `powershell`）で escape 契約を示します。CLI の `definition`、`references`、`callers`、`callees`、`excerpt`、`inspect`、`impact` は、既定を明示する `--redact-paths` と、ローカル用途だけの opt-in である `--show-paths` を受け付けます。`--show-paths` は解決済みの apphost、または `dotnet` と実行中 assembly、source、database の各引数を出力し、`paths_redacted: false` と `command_display_only: false` を設定し、宣言した shell 向けに安全に quote した command を生成します。MCP は常にサポート共有向けの安全な既定を使い、同等の camelCase metadata を返します。

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
| [`src/CodeIndex/Cli/SuggestionsCommandRunner.Query.cs`](src/CodeIndex/Cli/SuggestionsCommandRunner.Query.cs) | redaction 済みの履歴全文検索、および count、summary、compact、byte 上限付き JSON projection |

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

`suggestions list|export --query <text>` は、NFKC 正規化した query を、redaction 済みの stable ID、sampled title、description、context、evidence path、category、language に対して ordinal・大文字小文字を区別しない部分一致で照合します。照合前に `SuggestionStore.RedactSensitiveText` を適用することは confidentiality contract です。caller は 0 件結果や count の差を使って redaction により除去された値を探索できません。status、時刻、category、language、agent の filter を最初に適用し、次に text query、続いて `CreatedAt` 降順と ordinal stable ID の順で並べてから offset/limit pagination を行います。

履歴の structured projection は共通 JSON envelope を使います。`--count` と `--summary-only` は要求された page ではなく filter 後の全集合を要約し、pagination omission を 0、出力しない record を projection omission として報告します。summary dimension は distinct 値に固定上限（status 16、category 32、language 20）を持ち、それぞれ omitted/truncated metadata を公開します。`--compact` は redaction・上限付きの list field だけを出力します。`--max-json-bytes` は serialized UTF-8 document と末尾の platform newline を計測し、対数回の fitting-prefix search を使って envelope が収まるまで末尾の完全な result row だけを取り除きます。`total_count` は authoritative なまま、`byte_limit_omitted_count`、`next_offset`、recovery guidance が byte truncation を表します。row を返す compact / byte 上限付き mode は `--limit 0` を拒否し、公開する continuation が必ず進捗できるようにします。metadata-only envelope が収まらない場合、runner は stdout JSON を一切書きません。これらの projection は local read-only 操作で、streaming store の retention や mutation contract は変更しません。

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
| duplicate preflight | `IssueDuplicatePreflight` は最大 1000 件の raw Issue node または local entry と 1000 件の repository label を読み取ります。live GitHub lookup は選択した open / closed / all state を上限付き GraphQL cursor pagination（1 page 100 raw node、合計 1000 raw node）で取得し、page 間の Issue 番号を重複排除し、`hasNextPage=false` の場合だけ空の最終 cursor を許容して、欠落・反復・上限超過 cursor を拒否します。GitHub の各 page body は 8 MiB / 深度 32 に制限し、GraphQL の Issue connection で pull request を除外し、issue/title/body/label scalar の上限と recoverable diagnostic の sanitize を維持します。remote failure は fail closed とし、CLI usage error ではなく runtime error として `github_preflight_authentication`、`github_preflight_permission`、`github_preflight_rate_limit`、`github_preflight_validation`、`github_preflight_transient`、`github_preflight_timeout`、`github_preflight_transport`、`github_preflight_response`、`github_preflight_pagination` の category で区別します。 |
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

- **marker gate 付き reference extraction** — 高コストな行単位 reference regex を実行する前に、必要な構文 marker のうち最も安価なものを ordinal scan で確認する。stateful extractor は marker のない行を skip する前に comment、fence、header、binding などの継続 state を更新し、marker gate がその遷移を並べ替えたり迂回したりしてはならない。括弧なし constructor、trailing lambda、annotation、markup continuation のように共有 marker を意図的に持たない構文経路は、それぞれ narrowly scoped な marker を維持する。gate を変更するときは、indexing 速度と graph edge の維持を同時に確認できるよう、言語横断の positive regression と markerless-decoy practical-budget test を追加する。
- **reference context はrowを発行した行だけmaterialize** — built-inのcore、functional language、Solidity extractorは物理source lineのinstanceをemitterへ渡し、filterとdedupを通過したreferenceが1件以上ある場合だけ後段でtrimします。遅延normalizerは発行された物理line numberからsource lineを引き、そのraw lineとの参照同一性を必須にします。値一致にするとXAML、Razor、pluginなどの派生contextまで書き換えるためです。columnはtrim前の物理行を基準に保ち、stateful emitterはreferenceがない行でもstate更新を続けます。
- **integration boundary では language capability pattern の型を維持** — CLI/MCP の `languages` 行は suffix のみの `extensions`、literal な `exact_filenames`、`<suffix>` 表記の `filename_prefix_patterns` を公開します。`legacy_patterns` は deprecation 中に従来の combined list を保持し、`pattern_provenance` は built-in、plugin/pattern、language-map override の所有元を示します。round-trip test は広告した全 typed pattern を `FileIndexer.DetectLanguage` に戻して検証します（#4617）。
- **曖昧な source extension は曖昧なまま明示** — `.m` と `.pl` を既定で Objective-C / Perl に割り当てません。language-map override と built-in の完全一致/prefix filename rule の後で、`FileIndexer` は先頭物理行を 256 byte に制限した authoritative な認識済み shebang、64 KiB 上限 prefix 内の相互排他的で強い Objective-C/MATLAB または Perl/Prolog marker、各 ancestor directory 最大 256 entry の保守的な project marker の順に確認します。行終端なしで shebang 境界に達した先頭行は interpreter を選択せず、後続判定へ進みます。競合または弱い証拠は `ambiguous_m` / `ambiguous_pl` として index し、未確定の `.m` は位置を保つ共通コメントマスクの後で上限付きの MATLAB / Objective-C symbol・reference 経路を実行します。一方、Prolog と `ambiguous_pl` は保守的な reference / graph 対応を広告し、曖昧な `.pl` bucket は content-based classification を変えずに symbol / reference rule の和集合を使います。順序付き candidate descriptor、filename pattern、正確な content pattern、project marker、上限付き shebang rule、reason/confidence 語彙は detector 自身が所有し、CLI/MCP の `extension_lookup` diagnostic と dry-run の `language_detections` は同じ source を使うため、catalog guidance と indexing 判定が乖離しません（#4612、#4738、#4746、#4901）。
- **content read はopen済みhandleのmetadataを集約する** — authoritative raw load、raw-chunk probe、C#専用prepass、最終unknown-language probeは、stableなopenごとに最初と最後の`FileHandleSnapshot`を1つずつ取得します。各snapshotはWindowsの`GetFileInformationByHandle` 1回、macOSの`fstat` 1回、Linuxの固定layout `statx(..., AT_EMPTY_PATH, ...)` 1回によりlength・mtime・file identityをまとめて取得し、古いruntimeまたは未対応platformでは従来のmanaged multi-call fallbackを維持します。最初のsnapshotはopen bindingのidentityとread baselineを兼ね、最後のsnapshotはmutation metadataと、別途取得するcurrent-path identityとの比較に使うopened identityを兼ねます。attempt 0のfull-content readとnegative scanはinitial lengthで停止し、余分なEOFまたは`ReadByte` growth probeを行いません。実読byte数、final length/mtime/handle identity、current-path identityがbaselineと一致した場合だけstableと判定します。変化時は1回だけ再openし、bounded EOF scanはretryだけが行います。一方、final handle lengthがmax-file上限を超えた場合は、従来どおりそのread中のgrowthとして即時失敗します。positive raw-chunk matchは保守的なtrueを維持します。これによりstable readは論理snapshotを厳密に2回、上限付きretry 1回では4回に保ちつつ、raw load、positive chunk match、C# prepass、unknown-languageで異なるretry契約を変更しません。
- **未知言語の membership は1つの上限付き file snapshotを使う** — 完全一致 filename、登録済み extension、pattern/plugin mapping、`.m` / `.pl` の曖昧判定は従来の優先順位と I/O を維持します。最後の拡張子なし・未登録拡張子 fallback だけ script header 判定を遅延し、同じ認可済み handle から short read に耐える loop で最大256 byteを満たします。認識済み shebang / `#compdef` はそこで即時返却し、それ以外の初回attemptは同じhandleをpooled fixed-size bufferでinitial handle lengthまでだけ読み進めます。UTF-16 BOM/parity判定用の先頭4096 byteを保持し、その範囲を含む読取済み全byteでNULと1024 byte未満の厳密なGit LFS pointer形を確認します。final length、実読byte数、mtime、identityが一致すればEOF probeなしでauthoritativeとし、変化時はsnapshotを破棄してresolve・authorize・openを1回だけやり直します。EOFまたは`max-file-bytes + 1`まで進むのはこのbounded retryだけです。CLI full scan、scoped dry-run、freshness scan、MCP full/dry indexingはいずれもこの`FileIndexer`境界を共有するため、未知言語diagnosticのための別のfull-file allocationやstable fileの2回目openは発生しません。
- **動的言語の reference-graph readiness は extractor contract に従う** — index 済みの Crystal、Groovy、Tcl、Prolog、`ambiguous_pl` row で symbol-extractor version stamp が欠落または古い場合、status は `dynamic_reference_graph_contract_stale` を報告し、通常の index refresh が対象 row を更新するまで `reference_graph_complete` / `graph_data_current` を false に保ちます（#4746）。
- **hotspot marker fingerprint は上限付きtree traversalを1回共有** — full/update CLIとMCP indexingは、directory treeを言語ごとに歩かず、C#、VB、F#、MSBuildのmarker fingerprintをまとめて計算します。各directoryでは固有marker globごとにplatform filesystemのmatching挙動を保って1回ずつ列挙し、child directoryも1回だけ列挙する一方、marker集合、budget、truncation sentinel、warning順は言語別に分離します。single-language APIも同じengineへ委譲し、ignore rule、nested repository/submodule境界、MCP authorized read failureを維持します。
- **lock file の依存グラフは package 間の関係をモデル化** — `packages.lock.json`、`package-lock.json`、`npm-shrinkwrap.json` は package 宣言を symbol として保持しますが、`dependency` reference は明示された親 package → 子 package の項目だけに出力します。NuGet lock の symbol / reference は現在の file、target/RID、親 package、正確な JSON property span を保持し、candidate 解決を file 内に限定します。file 単位の `deps` は package 名による file 間推論を抑止し、通常の index update は以前の dependency-lock 抽出 contract と reference-identity contract を無効化します。そのため、`callers` は無関係な lock file を接続したり、反復宣言を最初の一致行へ畳み込んだりせず、要求元 package を特定できます（#4409、#4845）。
- **依存サイクル監査では解析と表示を分離** — CLI の `deps --cycles` と MCP `deps` の `cycles=true` は、独立した `--graph-budget` / `graphBudget` まで path 順で決定的な edge 集合を解析してから、強連結成分を安定順位付けします。`--limit` / `limit` はその SCC 順位集合をページ分割するだけで、不透明 cursor は生成時の filter、graph budget、indexed graph に結び付けます。machine-readable 応答は `analysis_complete`、graph edge 件数/予算、安定 ranking mode、authoritative な総 cycle 件数かどうか、continuation metadata を公開し、graph budget 枯渇時は完全な cycle 監査を装わず明示的な未完了解析として報告します（#4731）。
- **大規模な依存 SCC は事実ではなく表示だけを制限する** — SCC の解析と順位付けは、node 表示とは独立して authoritative な component size、edge / reference 合計、最大 component summary、evidence breakdown を保持します。CLI / MCP は各返却 component について path 順 node を既定で 50 件だけ materialize し、human、JSON、DOT、GraphML、JSON graph の各出力をその sample へ投影して、省略 node 数と表示 truncation metadata を公開し、compact summary を提供します。raw 展開には `--all-cycle-nodes` / `includeAllCycleNodes` の明示 opt-in が必要です。MCP cycle schema は component node array 専用の最大値を graph budget 上限と同じ値にするため、通常の row array 上限を緩めずに許可済み raw 展開を schema-valid に保ちます。machine-readable metadata は current な返却 SCC page の node 合計、materialize 済み件数、省略件数を global な最大 component summary とは別に集計するため、後続 cursor page でも authoritative な件数を維持します。retained-evidence summary は source language、origin、resolution state、reference kind、target kind、suppression reason を集計します。noise 抑制は candidate budget より前に evidence を分類し、旧 Markdown 見出し一致に加え、current な reference-identity contract によって未解決、曖昧、または別 file の target へ解決済みと分類された C# 修飾 call だけを除外します。identity metadata が stale または absent の場合は `resolution_state=unavailable` の retained evidence として fail closed します。同名 overload がある場合も確認済み target の file を authoritative に優先し、解決済みpartial-family groupも保持します。grouping metadata は file 単位で、partial type family の collapse を適用していないことを明示します（#5197）。
- **依存 cycle cursor は topology に加えて表示 evidence にも束縛する** — graph fingerprint は retained evidence 各行の source language、origin、resolution state、reference kind、target kind、suppression reason、件数を決定的な順序で含めます。そのため metadata だけが更新された graph でも古い cursor を拒否し、異なる snapshot の evidence summary を混在させません。MCP の `format=json-graph` cycle request は CLI graph 出力と同じ上限付き node / edge 投影を使い、専用 graph node / edge schema の上限も cycle graph budget の最大値と一致させます（#5197）。
- **ORMなし** — `Microsoft.Data.Sqlite`でパラメータ化クエリを直接使用。依存関係を最小限に、制御を明確に。
- **バッチコミット** — 書き込み性能のため1トランザクション500レコード。fsyncオーバーヘッドを削減。
- **fact-backed C# instantiation family** — graph finalization はidentity fact完成後、対象となるC# type declaration / constructorごとにTEMP rowを1件materializeします。rank 0〜4はsymbol primary key join、rank 5はreference側からcomposite family indexをseekするため、candidate経路で相関constructor-family symbol scanを行いません。lower-rankとfallbackのbinding-sensitive flagを分離し、全partialと代表typeだけのprimary-constructor semanticsを保ちます。type declarationだけを母集団にしたBINARY一意性によりproject / file-local競合はambiguousのまま、constructor-only orphanはrank 5から除外し、overload、implicit default、optional / default / `params`、value type、enum、delegate、arity不明の挙動を維持します。
- **部分的なバッチ失敗** — `DbWriter` は通常の chunk / symbol batch では高速な multi-row `INSERT` 経路を保ちます。SQLite が batch を拒否した場合、その batch を rollback し、各 row を per-row `SAVEPOINT` の下で再試行し、有効な row だけを commit し、失敗 row だけを skip して `BatchRowsSkipped` を増やし、row identifier と SQLite error を含む warning を出します。これにより、抽出された 1 行の破損で大きな indexing batch 全体が捨てられることを防ぎます（#1754）。
- **WALモード + busy_timeout** — Write-Ahead Loggingで読み書き同時アクセスとクラッシュ安全性を確保。5秒のbusy_timeoutで即座のSQLITE_BUSYエラーを回避。
- **複数 SELECT をまたぐ reader の snapshot 隔離** — 1 回の呼び出しで複数 SQL を発行する read エントリポイント（`DbReader.GetStatus`、`DbReader.AnalyzeSymbol`（CLI `inspect` / MCP `analyze_symbol`）、`RepoMapBuilder.Build`（CLI `map` / MCP `repo_map`））は、本体を 1 つの `BEGIN DEFERRED` transaction で囲み、すべての sub-query が同じ WAL snapshot を参照するようにする。これが無いと、2 つの `COUNT(*)` の間に writer が commit した結果として並行 reader が `files=836, refs=0` のような不整合状態を観測しうる（issue #180 で露見）。`DEFERRED` は最初の SELECT で `SHARED` lock を取るだけで writer を阻害せず、末尾で明示 Commit して `SHARED` lock を早期解放する。独自に `SqliteDataReader` を開く sub-query は内側ブロックに閉じ込めて `Commit()` より前に handle を解放すること — `SqliteTransaction.Commit()` は同じ connection 上で開いている reader があると失敗する。新しい多段 read エントリポイントは同じパターンに従うこと。単一 SQL のクエリは SQLite の auto-commit が文単位の snapshot を与えるため不要。
- **デフォルトはリテラル安全検索** — 検索は既定でトークンごとに引用してFTS構文エラーを避ける一方、`search "\"new Regex\""` のようなダブルクォート範囲は独立トークン一致に広げず、単一の FTS5 phrase token として扱う。生のFTS5構文は `--fts` またはMCPの `rawQuery` で明示 opt-in。prefix 拡張も opt-in：トークン末尾に `*` を付ける（`search auth*`）とそのトークンだけが FTS5 prefix phrase に昇格し、`--prefix` フラグ（MCP の `prefix`）はクエリの全トークンを昇格させる。opt-in がなければ `search 計算` は indexed token `計算` のみにマッチし `計算する` には広がらない（issue #1519）— unicode61 は連続 CJK コードポイントを 1 トークンとして扱うため、広く拾いたい場合は `--prefix` か末尾 `*` を明示する。
- **`find` の正確な正規表現 span** — `find --regex` の JSON / MCP 結果は正規表現エンジンの一致長をそのまま保持し、`^` や `$` のような挿入位置アンカーは `length: 0` を返す。可視範囲が必要な表示向け形式では、machine-readable な一致長を変えずに1文字の span として表示する場合がある（#4473）。
- **Git 風 ignore ルール対応** — `FileIndexer` は non-repo ディレクトリ向けに常時有効な `SkipDirs` / `SkipFiles`（および macOS の `._*` AppleDouble resource fork を indexability/言語プローブに到達させないための接頭辞除外 — #1583）を維持しつつ、走査時にはユーザーの `.gitignore` と任意の `.cdidxignore` をディレクトリごとに積み上げて適用する。Git 管理下のワークスペースでは大小文字の扱いを OS 名ではなく `core.ignorecase` から解決し、repo 配下の subdirectory を project root にした場合でもその設定を引き継ぐ。さらに repo-root やその途中階層にある ancestor `.gitignore` を preload してから走査し、`--commits` でも Git が返す repo-root 基準の changed path を project root 基準へ正規化してから update filter に通す。Git でないツリーではベストエフォートの filesystem probe にフォールバックする。`**` も Git の path-form globstar の場合だけ特別扱いし、それ以外は通常の single-segment wildcard として扱う。ignore ファイルが読めない場合は、そのディレクトリ範囲を fail-closed で扱い、full scan では subtree を飛ばし、scoped refresh では不完全なルールのまま index を更新しない。後勝ちの negation により、秘密情報、生成コード、fixture、ビルド成果物を index から外しつつ、Git でないツリーに対する cdidx 既定の挙動も崩さない。
- **パス考慮の絞り込みとランキング** — `search`、`definition`、`references`、`callers`、`callees`、`symbols`、`files` はパス include/exclude フィルタと `--exclude-tests` を共有する。読み取りクエリは tests や docs より source を優先し、全文検索はシンボル名やパスの exact match を追加ブーストして、実装ファイルを先に返しやすくする。
- **AI向けの軽量検索スニペット** — `search --json` と MCP の `search` は、チャンク全文ではなく snippet range、match line、highlight、context count、`truncated_line_count`、`truncation_context` を持つ一致中心スニペットを返す。`truncation_context.char_counts` と `truncation_context.total_chars` はクランプされた各スニペット行の省略文字数を公開し、truncated な highlight も `truncated_char_counts` を持つ。`--snippet-lines` でペイロード量と文脈量のバランスを取れ、`--max-line-width`（CLI）/ `maxLineWidth`（MCP）は `find` / `references` / `excerpt` / `inspect` と同じ共有 `LineWidthFormatter.ClampLine` 契約で各スニペット行を最初のマッチトークン周辺にクランプするため、minified / transpiled / 生成された 1 行ファイル内の 1 ヒットで数百 KB を返さなくなる。クランプされた行はスニペットに `...(+N)...` マーカーが入り、`highlights[].truncated` と `highlights[].original_line_length` で AI クライアントがクランプを検出できる。
- **初動向けの repo map** — `map` は、インデックス済みデータから言語、モジュール、主要ファイル、ホットスポット、推定エントリポイントを集約し、AIクライアントが精密検索前に見るべき場所を決めやすくする。シンボル抽出が `Main` 系シンボルを出さない場合でも、既知のトップレベル実行ファイルへフォールバックして入口候補を補う。
- **信用判断のための鮮度メタデータ** — `status` はワークスペース全体の鮮度と git 状態を返す。`map` は `indexed_at` / `latest_modified` を絞り込み結果の鮮度として維持しつつ、`workspace_indexed_at` / `workspace_latest_modified` でワークスペース全体の鮮度も返す。`inspect` も同じワークスペース鮮度と git フィールドを返すため、シンボル中心の AI フローで `status` を別途呼ばずに済む。さらに `status` は `sql_graph_contract_ready` / `sql_graph_contract_degraded_reason`、`hotspot_family_ready` / `hotspot_family_degraded_reason` に加えて、forward-compatibility 監査 (`index_writer_version`、`index_newer_than_reader`、`index_newer_than_reader_reason`、詳細は「リーダー側の forward-compatibility 監査」を参照)、および fold-only remediation 用の `fold_ready_reason`、`degraded_reason`、`recommended_action`、`alternative_action` も返すため、AI クライアントは SQL graph/dependency/impact、duplicate-name hotspot family、Unicode `--exact` のどれが authoritative か、また DB が現在の binary より新しい `cdidx` で書かれていないかを最初に判断できる。現行の全体 scan 後は `unknown_extension_file_count` も返すため、未知拡張子で index 対象外になった件数を `status` から確認できる。これらの fold-only remediation field は、明示的な read-only `file:///...?...` DB URI から導出された場合でも、失敗する read-only URI をそのままコマンドへ埋め込まず、writable な filesystem path に正規化して返す。さらに `impact` / MCP `impact_analysis` に加えて、`inspect` / MCP `analyze_symbol`、`references` / `callers` / `callees`、`deps` / `unused` / `hotspots` 系も、SQL ベースの graph/dependency read が実際に結果へ関与したときだけ `sql_graph_contract_ready` / `sql_graph_contract_degraded_reason` を反映するため、stale な SQL 行が authoritative なヒットや 0 件応答に見えてしまうのを防ぎつつ、mixed-language index 内の純粋な非SQL結果を誤って degraded 扱いしない。`files` はファイルごとの checksum・modified・indexed timestamp を返す。古いDBに対する file 列の移行は可能なら自動で行い、その場移行できない場合でも読み取り経路がクラッシュしないようにする。CLI と MCP の 0 件 JSON レスポンスは `indexed_file_count`、`indexed_at`、`freshness_available` を含む。`freshness_available=true` で `indexed_at:null` なら空インデックス、`freshness_available=false` なら legacy/read-only DB で鮮度 timestamp を取得できず、理由は `freshness_degraded_reason` に入る。**HEAD 起点の stale 検知**: `cdidx index` の full scan が成功するたびに、現時点の `git HEAD` を `codeindex_meta` に stamp し、後続実行で workspace HEAD と比較できるようにする。full scan 成功時は workspace 全体を照合・purge して `workspace_verified_head_sha` を進め、`head_changed: false` を返す。partial scan は検証値を従来基準に残し、通常の full scan を再実行する recovery guidance とともに `head_changed` / `prior_indexed_head_commit` / `current_head_commit` / `head_change_notice` を公開する。`status --check` も同じ検証基準を `workspace_check.head_changed` で比較する。Git scoped refresh は従来基準から現在 HEAD までの path を union した場合だけ検証値を進め、明示的な `--files` update は検証値を維持する。非 Git workspace と基準を記録していない legacy DB は比較自体をスキップし、false-positive な警告を出さない。
**観測可能な上限付き workspace-check sample**: `IndexFreshnessChecker` は authoritative な件数を独立して加算しながら、changed、missing、outside-sparse-cone、unindexed、unverifiable、scan-error の各一覧を決定的順序の最大20件に制限します。serialized `workspace_check` は authoritative count と返却一覧から各 `*_omitted_count` を導出し、共有の `*_path_limit` を公開し、path が省略された場合に `*_truncated` を設定します。一覧だけを選ぶ `--fields` projection は count とこれらの signal を自動的に含め、compact status は配列を省いて count と signal を維持します。byte budget による短縮は順序付き末尾 path だけを除去して signal を再計算し、`metadata.byte_limit_omitted_path_count` を記録します。human check 診断は `coverage=sample` または `coverage=complete` を表示します。

`unknown_extension_files` は `unknown_extension_file_path_limit` 件と decoded-character budget の両方で上限付けされた未対応言語 path sample で、`unknown_extension_files_truncated` は件数上限または decoded-character budget により未出力の path が残ったことを示します。対象は読み取り可能な text 系ファイルであり、binary、上限超過、読み取り不能、ignore 済み、内部 artifact は件数に含めません。拡張子なしの actionable file は `<none>` として分類し、guidance は認識可能な shebang の追加または対応済み拡張子への rename を案内します。diagnostics-version stamp が無い、または未対応の legacy DB では永続化 field を省略し、現行全体 scan の成功後にだけ公開します。`unknown_extension_file_path_limit` は item 上限であり、常にその件数まで返す保証ではありません。

`extractors` は extractor plugin と pattern config の runtime health で、読み込み済み plugin assembly / pattern 件数、symbol/reference extractor 件数、parent load context が 0 であることと isolated-worker lifecycle、skip されたファイル数、上限付き diagnostics list を返します。

`hooks[]` は `callback_budget_ms` を含み、`CDIDX_HOOK_CALLBACK_BUDGET_MS`（既定値: 5000 ms）で強制される post-extraction callback 予算を反映します。hook は結果反映前の scratch copy 上で実行されるため、timeout した callback の変更は破棄されます。

文書化された `status --json` trust contract は `fold_ready`、`fold_ready_reason`、`graph_table_available`、`issues_table_available`、`file_issues_data_current`、`migration_in_progress`、`sql_graph_contract_ready`、`sql_graph_contract_degraded_reason`、`hotspot_family_ready`、`hotspot_family_degraded_reason`、`csharp_symbol_name_ready`、`csharp_metadata_target_ready`、`csharp_metadata_target_degraded_reason`、`indexed_head_commit`、`worktree_head_changed`、`indexed_head_sha`、`indexed_head_branch`、`indexed_head_timestamp`、`commits_ahead_of_indexed_head`、`index_writer_version`、`index_newer_than_reader`、`index_newer_than_reader_reason`、`unknown_extension_file_count`、`unknown_extension_files`、`unknown_extension_files_truncated`、`unknown_extension_file_path_limit`、`extractors`、`path_case_sensitive`、`stale_after_seconds`、`index_age_seconds`、remediation field の `degraded_root_cause`、`degraded_reason`、`recommended_action`、`alternative_action`、`readiness_degradations`、および MCP 専用の `mcp_session` を対象にします。MCP `mcp_session` は永続化された DB 状態ではなく、セッション単位の診断情報で、`log_level`、`roots`、任意の `client_info`、任意の `client_capabilities` を含みます。この一覧は `README.md` と `AGENT_GUIDE.md` に同期してください。いずれかの必須 field がこれらの docs から漏れると `DocumentationStatusContractTests` が失敗します。
- **再解析不要の folded-key アップグレード** — `backfill-fold` と MCP `backfill_fold` は、既存 DB 行から `name_folded`、明示的 interface 用 `display_name_folded`、reference の `*_folded` を直接再計算し、必要な folded 値に NULL が残っていないことを検証してから `FoldReadyFlag` を stamp する。これにより、pre-#86 DB から AI クライアントやユーザーが低コストで Unicode `--exact` へ上がれる。fold metadata が stale、または C# symbol-name contract が変わった場合は全 folded 行を再生成し、検証成功後にだけ現在の C# contract を stamp する。明示的 interface の v3 contract 更新では、C# の method/property/event 全行に永続 signature が必要であり、復元根拠がない古い DB は false-ready stamp を付けず C# file refresh または rebuild を要求する。
- **まとめて取るシンボル分析** — `inspect` と MCP の `analyze_symbol` は、定義、近傍シンボル、参照、caller、callee、ファイルメタデータ、ワークスペース信頼メタデータ、graph 対応メタデータを1回で返し、AIクライアントが一般的なシンボル調査を少ない往復で終えやすくする。
- **言語考慮の参照抽出** — `references`、`callers`、`callees` は、正規表現ベースの call/reference 抽出が意味を持つ言語だけに対してインデックス化された参照テーブルで支える。未対応言語では、低信頼な疑似グラフ結果を返す代わりにテキスト検索へ戻る前提で設計する。**nested generic 呼び出し**: `new Dictionary<string, List<int>>()` のような C#/Java のコンストラクタ呼び出しと、`Helper.DoWork<List<int>>()` のような C# generic method call は、平坦な regex fast-path で `>>` を釣り合わせられなくても depth-aware fallback scanner で拾い直し、外側 target を参照テーブルへ残す。**コンストラクタ連鎖呼び出し**: C# の `: this(...)` / `: base(...)` イニシャライザと、Java のコンストラクタ本体冒頭文 `this(...)` / `super(...)` は、汎用 call regex とは別に検出し、呼び先が実際のコンストラクタとなるように書き換える（`this` は外側の class/record、`base` / `super` は外側クラスのシグネチャから解析した基底型）。C# のクロス行イニシャライザは外側クラスではなく、そのコンストラクタに紐付ける。基底型の解析は generic 引数、record のプライマリコンストラクタ引数、`where` 制約、`global::` やドット付きの namespace 修飾を剥がす。Java の `super.method()` は通常のメソッド呼び出しのまま扱う。**型位置の依存エッジ**: C#/Java の継承リスト、宣言型、generic 制約、`throws`、`is` / `as` / `instanceof`、および実際の C# XML doc `///` `cref` は `type_reference` 行として索引し、既定の `callers` / `callees` が見せる動的 call graph を汚さずに、`references` / `impact` から compile-time rename 依存を辿れるようにする。**SQL qualified-name alignment**: SQL の graph/dependency reader は、各 reference 行の source-line context、記録済み call 列位置、enclosing container から SQL 参照名を復元して定義と照合するため、qualified な `references` / `callers` / `impact` query は exact / non-exact を問わず sibling schema へ widen しない。source 側が genuinely unqualified な場合にだけ bare leaf fallback を許可するので、qualified call を含む `deps` / `unused` / `hotspots` も schema 単位で整合し、`EXEC dbo.fn_Target; EXEC sales.fn_Target;` のような同一行 multi-call も二重計上しない。列位置が記録されている row は、その列に qualified token が見つからなければ whole-line の別 qualified token へ昇格させないため、行末コメント・文字列リテラル・後続の別 call が先頭の unqualified edge を横取りすることもない。qualified な `callees` query でも caller query 自体が unqualified なとき以外は leaf fallback を無効化したため、`callees sales.Caller` が `dbo.Caller` へ広がらない。SQL extractor は qualified-name の `.` 前後空白も許容し、definition 系 reader は quoted qualified SQL name (`[dbo].[fn_X]` → `dbo.fn_X`) を正規化してから照合する。さらに exact SQL 定義照合は segment 数を保持し、SQL の exact graph leaf fallback は Unicode folded exact path を維持する。SQL CTE 本体内の source 行は raw `cte_body_reference` kind を使うため、`references --kind cte_body_reference` で anchor/recursive member 内部を outer query の table reference と区別できる。そのため、quoted single identifier の衝突や Unicode exact lookup の ASCII-only `NOCASE` 退行も防ぐ。
  exact な SQL の graph/dependency reader は解決済み segment 数も保持するため、`"sales.fn_Target"` のようなドット入り quoted single identifier が、本物の qualified name `sales.fn_Target` と exact `references` / `callers` / `impact` や集計系の `deps` / `unused` / `hotspots` で衝突しない。
- **言語考慮の参照抽出** — `references`、`callers`、`callees` は、正規表現ベースの call/reference 抽出が意味を持つ言語だけに対してインデックス化された参照テーブルで支える。未対応言語では、低信頼な疑似グラフ結果を返す代わりにテキスト検索へ戻る前提で設計する。**nested generic 呼び出し**: `new Dictionary<string, List<int>>()` のような C#/Java のコンストラクタ呼び出しと、`Helper.DoWork<List<int>>()` のような C# generic method call は、平坦な regex fast-path で `>>` を釣り合わせられなくても depth-aware fallback scanner で拾い直し、外側 target を参照テーブルへ残す。**JS/TS の no-paren constructor**: JavaScript / TypeScript の zero-argument constructor call で `()` を合法的に省略できる `new Foo;`、`new Date;`、`new Demo.Provider;`、`new Box<number>;` も、専用の言語別経路で `instantiate` edge として出す。行末 `new Foo` に対する次行 `.bar()` / `[0]` continuation は suppress し、phantom な単独 instantiation にしない。**コンストラクタ連鎖呼び出し**: C# の `: this(...)` / `: base(...)` イニシャライザと、Java のコンストラクタ本体冒頭文 `this(...)` / `super(...)` は、汎用 call regex とは別に検出し、呼び先が実際のコンストラクタとなるように書き換える（`this` は外側の class/record、`base` / `super` は外側クラスのシグネチャから解析した基底型）。C# のクロス行イニシャライザは外側クラスではなく、そのコンストラクタに紐付ける。基底型の解析は generic 引数、record のプライマリコンストラクタ引数、`where` 制約、`global::` やドット付きの namespace 修飾を剥がす。Java の `super.method()` は通常のメソッド呼び出しのまま扱う。**型位置の依存エッジ**: C#/Java の継承リスト、宣言型、generic 制約、`throws`、`is` / `as` / `instanceof`、および C# XML doc の `cref` は `type_reference` 行として索引し、既定の `callers` / `callees` が見せる動的 call graph を汚さずに、`references` / `impact` から compile-time rename 依存を辿れるようにする。C# XML doc の `cref` 抽出は、実際に後続宣言へ結び付く XML-doc comment である `///` 行と delimited `/** ... */` block の両方を対象にしつつ、通常の `//` / `////` コメントや通常の block comment は phantom 依存として扱わない。また、同じ物理行でも closing `*/` より後ろに続く code / string の内容、doc comment と後続宣言の間へ割り込むトップレベル実行文、brace-free field/property initializer continuation、brace-free expression lambda、nested executable continuation、複数行 raw/verbatim string のうち行頭がたまたま `/**` で始まる内容は doc-comment slice の外として扱う。regex 自体は narrowed した doc-comment slice に対して走らせるが、`symbol_references.column` は元の物理ソース行位置に固定したまま保持する。C# の read path では、`using static` による constant-pattern suppress が `is` / `case` の前後の trivia を考慮してトークン単位で判定され、anchor が前行にある場合は anchor-aware な複数行コンテキストをインデックス済み行から再構成するため、`value is/*comment*/Red`、`value is\n    Red or Blue`、`value is\n    // comment\n    Red`、`case\n    // comment\n    Point:`、長い `case` / `or` 連鎖、`case\tRed:` のような形でも phantom `type_reference` を漏らさない。qualified constant/member pattern は exact-name read path でも qualifier 起点で suppress するため、`case Color.Red or Color.Blue:` に対して無関係な `class Red {}` が suppress を打ち消さない。extractor 側の pending type-pattern carry も trivia-only 区切り行、standalone な continuation-line `not`、複数行 `case` head / logical continuation をまたいで維持されるため、comment-only 行や `not` だけの継続行で後続の本物の type head を落とさない。`case > 0:` や `case not > 0:` のような非型 `case` ラベルではその pending carry を armed にしないため、次行の call/identifier token が `type_reference` に混入しない。同名型の rescue も `file` 可視性を尊重し、file-local な型は同じ物理ファイル内の参照だけを救済する。基底クラスから見える protected/public/internal nested type は、基底型参照を active な型 alias / namespace alias 経由まで正規化し、さらに alias 展開後に constructed generic な基底型を再 canonicalize したうえで derived class の pattern head を救済する一方、implemented interface は inherited nested-type rescue に参加しない。さらに same-file `using Namespace;`、project-wide `global using Namespace;`、型 alias も同じ rescue 集合に入る。一方で extractor は file-local な情報だけでは同一 namespace の別ファイルにある実型を判定できないため、`value is Red` のような曖昧な unqualified `using static` head は DB に残し、pure constant-only case の抑止は workspace-aware な read path 側で行う。**SQL qualified-name alignment**: SQL の graph/dependency reader は、各 reference 行の source-line context、記録済み call 列位置、enclosing container から SQL 参照名を復元して定義と照合するため、qualified な `references` / `callers` / `impact` query は exact / non-exact を問わず sibling schema へ widen しない。source 側が genuinely unqualified な場合にだけ bare leaf fallback を許可するので、qualified call を含む `deps` / `unused` / `hotspots` も schema 単位で整合し、`EXEC dbo.fn_Target; EXEC sales.fn_Target;` のような同一行 multi-call も二重計上しない。列位置が記録されている row は、その列に qualified token が見つからなければ whole-line の別 qualified token へ昇格させないため、行末コメント・文字列リテラル・後続の別 call が先頭の unqualified edge を横取りすることもない。qualified な `callees` query でも caller query 自体が unqualified なとき以外は leaf fallback を無効化したため、`callees sales.Caller` が `dbo.Caller` へ広がらない。SQL extractor は qualified-name の `.` 前後空白も許容し、definition 系 reader は quoted qualified SQL name (`[dbo].[fn_X]` → `dbo.fn_X`) を正規化してから照合する。さらに exact SQL 定義照合は segment 数を保持し、SQL の exact graph leaf fallback は Unicode folded exact path を維持する。SQL CTE 本体内の source 行は raw `cte_body_reference` kind を使うため、`references --kind cte_body_reference` で anchor/recursive member 内部を outer query の table reference と区別できる。そのため、quoted single identifier の衝突や Unicode exact lookup の ASCII-only `NOCASE` 退行も防ぐ。exact な SQL の graph/dependency reader は解決済み segment 数も保持するため、`"sales.fn_Target"` のようなドット入り quoted single identifier が、本物の qualified name `sales.fn_Target` と exact `references` / `callers` / `impact` や集計系の `deps` / `unused` / `hotspots` で衝突しない。
- **推移的 impact analysis** — `impact` と MCP `impact_analysis` は、シンボルの推移的 caller chain を BFS で計算する。caller matching は substring expansion と大小文字差の脆さを避けるため `lower() = lower()` の大小文字非依存 exact match を使い、symbol 名は exact-case を優先して definition から事前解決し、read path は graph-supported language に限定して削除済み言語の stale edge を防ぐ。heuristic fallback が使う definition set も `--lang` / `--path` / `--exclude-path` / `--exclude-tests` と graph-supported language を尊重し、class-like definition だけを fallback 候補にするため、同名 namespace/import sibling は単一の class / struct / interface target を妨げず、純粋な non-callable `namespace` / `import` query は `non_callable_symbol_kind` guidance を返す。heuristic file-level hints は成功応答だが non-authoritative status を `impact_mode`、`heuristic`、`hint_count`、`truncated` で示し、caller rows は `result_kind: "graph"`、heuristic `file_impacts` rows は `result_kind: "file_heuristic"` を持つため、クライアントは list 位置や depth 値から推測せずに authoritative hop-depth graph 結果と境界 fallback hint を区別できる。`truncated` が `true` のときは JSON / MCP payload に `truncated_reason` も出し、`user_limit` は caller 指定の `--limit` 到達、`safety_cap` は内部の per-symbol BFS fetch-iteration cap 到達を意味する。`impact` / MCP `impact_analysis` は `termination_reason`（`completed`、`max_depth_reached`、`cycle_detected`、`row_limit_truncated`、`safety_cap`、`cancelled`）、`cycle_detected`、`cycles` も出すため、caller cycle と通常完了や limit/depth termination を区別できる（#1883）。`safety_cap` は `user_limit` より優先し、heuristic file-level hints path は caller の `--limit` だけで切り詰められるため `user_limit` のみを使う。`truncated` が `false` のとき `truncated_reason` は省略される。（#1533）`count` / `file_count` は返却された可視集合、`confirmed_count` / `confirmed_file_count` は heuristic-success payload の symbol-level caller totals を保持し、`impact --json --count` も full payload と同じ `*_count` field 名を使う。一般名の衝突を減らすため、type fallback では候補 member 名への参照に加え、同一ファイル内に source/target pair を anchor する証拠が必要になる。証拠は解決済み target 名への `call` / `instantiate` reference（この経路は call graph 自体が関係を pin するため metadata-attribute bypass より先に走り、緩い ambiguity guard に依存しない）、または signature / return type など indexed symbol metadata からの structured type evidence に限り、comment/string の raw text match は使わない。call/instantiate anchor は解決済み名を exact に照合し、suffix-strip alias は使わない。callable reference はすでに authoritative identifier を持つため、C# の `[Foo]` → `FooAttribute` alias をここへ適用すると無関係な `Foo()` method call が `impact FooAttribute` を偽 anchor できてしまうためである（#1881）。metadata bypass は attribute use site が正当に target 名を省略するため C# `Attribute` suffix alias を維持する。signature evidence path は Unicode-aware で、hint `reference_count` は実際に一致した reference row 数を表し、symbol list は deduplicate される。fallback ambiguity は同じファイル内であっても複数の class-like definition がある場合だけ扱い、`PurgeUnsupportedReferences` は CLI full scan、CLI update mode、MCP index のすべての indexing path で走る。
- **impact cycle の identity** — 現行の reference-identity graph では、`impact` は解決済み source/target symbol ID を BFS の全 hop へ引き継ぎ、実際に走査した正規 ID 間の有向辺だけから cycle を判定する。表示名の重複や fold 一致だけではゼロホップ cycle にせず、永続化された辺の source/target ID が同一になる直接再帰は実在する singleton cycle として残す。未解決または曖昧な same-leaf evidence は `references` と無修飾 graph discovery から引き続き確認できるが、identity scope の impact traversal には入れない。`resolved_group` edge は選択済み target candidate が一致するときだけ走査できる。構造化 caller row は `caller_symbol_id` / 一意に解決できた `callee_symbol_id`、`--with-paths` の node は一意な identity の場合だけ `symbol_id`、cycle row は互換用の表示名 `members` に加えて `member_identities` を公開する。現行 identity 契約を持たない legacy graph は名前キーの互換経路を維持する（#4847）。
- **構造化MCPレスポンス** — MCPツール呼び出しは `structuredContent` に型付きJSONを返し、`content` は互換性のため簡潔に保つ。
- **MCP の pre-validation rate limiting と bucket eviction** — direct な `tools/call` はすべて tool 名、enablement、argument の詳細検証前に caller-wide の固定 coarse bucket を 1 つ消費する。canonical な既知 tool 名は secondary `(tool, caller)` bucket も維持し、missing、malformed、empty、oversized、case-variant、unknown な名前は名前由来 bucket を作成しない。unknown な `batch_query` inner-slot 名は caller ごとの 1 つの固定 invalid-slot partition を共有する。`CDIDX_MCP_RATE_LIMIT_BUCKET_IDLE_SECONDS` は既定 900 秒。process-local 上限到達時は期限切れ bucket を直ちに prune する。layered acquisition は両 partition を 1 lock 内で評価し、secondary 拒否前に coarse token を消費した場合は、`retry_after_ms` が必要なすべての token refill と capacity 境界を含む。これにより malformed call の既知名ローテーションによる burst 増幅と未信頼名による cardinality 増加を防ぎ、正規の bucket 作成は通知された時刻に回復でき、過去の caller ID もプロセス寿命いっぱい保持しない（#2824 / #4547）。
- **MCP envelope レスポンス上限** — `CDIDX_MCP_RESPONSE_MAX_BYTES` は既定 10 MiB、最大 64 MiB。invalid 値は既定値へ戻し、最大超過値は stderr 警告付きでクランプするため、operator が誤って JSON-RPC response guard を実質無効化できない。
- **MCP `batch_query` レスポンス上限** — `batch_query` は集約した slot 結果の UTF-8 JSON サイズを見積もり、`CDIDX_MCP_BATCH_RESPONSE_MAX_BYTES`（既定: 1 MiB / 1,048,576 bytes、最大: 10 MiB）を超える場合は追加を止める。切り詰めたレスポンスには `truncated: true`、`truncated_queries`、byte limit メタデータを含めるため、クライアントは prose を parsing せず batch 分割や slot limit 縮小を判断できる (#1416)。invalid 値は既定値へ戻し、最大超過値は stderr 警告付きでクランプし、有効値は MCP `status` の `mcp.limits.batch_response_bytes` で確認できる。
- **HTTP MCP response / stream 上限** — `HttpMcpTransport` は通常の JSON response body を `CDIDX_MCP_HTTP_MAX_RESPONSE_BYTES`（既定 1,000,000 bytes、最大 16,777,216 bytes）で制限する。response / SSE write timeout は stable diagnostic を使い、期限超過時は HTTP response を abort する。
- **HTTP MCP aggregate request-body budget** — `CDIDX_MCP_HTTP_MAX_IN_FLIGHT_REQUEST_BYTES` は read、queued frame、MCP 実行、response 完了にまたがる process-wide の request-body reservation を制限する。既定値は 64 MiB、最大値は 1 GiB で、request 単位上限以上でなければならない。飽和時は分類済み HTTP 429 で拒否し、queue depth と request 単位 size の積が memory 上限になることを防ぐ (#4548)。
- **MCP の有界 queue / concurrency gate** — HTTP request queue は `TryWrite` 前に slot を取り、満杯時は handler を block せず HTTP 429、`Retry-After: 1`、`X-Cdidx-Mcp-Rejection: request_queue_limit` で拒否する。POST handler と長寿命 event stream は独立した admission semaphore を使い、HTTP health は両方の有効 capacity と `http_separate_event_stream_handlers` を返す。limit 環境変数は未設定の場合だけ既定値を使い、設定済みの malformed 値または範囲外値は listener 起動前に失敗する。transport 所有 gate は bounded shutdown で全取得 slot の返却を確認できた場合だけ dispose する。
- **MCP pagination offset 上限** — `references`、`callers`、`callees` は SQL query 実行前に `offset` を 10,000 へクランプする。`tools/list` は各 offset schema に最大値を広告し、MCP `status` も `mcp.limits.max_pagination_offset` に同じ値を返す。
- **MCP 言語 catalog の pagination** — MCP `languages` は CLI と共通の canonical `LanguageCatalog` から row を構築し、正規化した言語・拡張子・alias の完全一致 filter と言語名の ordinal sort を適用する。page は既定 20 行で、opaque な response-v2 cursor を全 filter、`limit`、`maxBytes`、固定 sort contract、出力 catalog generation の fingerprint（`indexedOnly` では indexed-language membership も含む）へ拘束する。query 変更は `cursor_query_mismatch`、catalog 変更は retry-safe な `cursor_stale` を返す。`maxBytes` は 4,096〜1,000,000 bytes を受け付け、UTF-8 JSON-RPC envelope 全体を計測して送信前に row page を縮小し、filtered total を catalog 件数および capability 件数から分離したまま返す。
- **MCP の bounded discovery と status projection** — 引数なしの `tools/list` は、有効な全 tool を決定的順序で agent-safe な `compact` catalog として返し、JSON-RPC envelope 全体を UTF-8 で 64 KiB 以内に保つ。compact entry は呼び出しに必要な authoritative schema（型、必須 field、制約、既定値）を維持し、schema の説明専用 keyword だけを除去する。description は、提案に source code を含めないという安全上重要な案内を残して短縮し、任意の output schema と example は省略する。完全な description、output schema、example、workflow metadata が必要な場合だけ、正確な `names` と `format: "full"` を指定する。標準の opaque cursor は compact / name-filtered control を維持し、legacy の数値 cursor は filter なし full catalog を継続する。`_meta.size_telemetry` は引数を記録せず、tools、description、input schema、output schema、example、annotation/stability、catalog metadata ごとの serialized UTF-8 bytes と概算 token 数を返す。`names` は 128 文字以内の有効 tool 完全名を最大 24 件受け付ける。MCP `status.fields` は `format` と任意 diagnostic attachment の構築後に exact な top-level field を project し、`api_version` はすべての structured result に残す。projection 入力は最大 32 件、各 128 文字、合計 2,048 文字で、未知名と nested path は `invalid_argument` とする。新しい discovery mode は tool 到達性、呼び出し schema の authority、決定的な標準 pagination、既定 byte budget、明示的 full の互換性を維持すること。
- **MCP ファイル resource discovery** — `resources/templates/list` は `cdidx://file-path/{path}` を公開し、正確なリポジトリ相対 path が既知の client は全 inventory をページングせず `resources/read` URI を構築できる。simple URI-template expansion は separator と `?` / `#` などの予約 filename 文字を percent encode する。template 専用 resolver は値を一度だけ decode し、absolute path、traversal、backslash、空 segment、query、fragment を拒否して canonical な `cdidx://file/<path>` identity を返す。canonical resource URI は encoded separator を引き続き拒否する。`resources/list` は `path` に 1 文字列または各 1024 文字・wildcard operator 128 個以内かつ最大 100 件の文字列を受け付け、file query と同じ anchored directory / glob semantics を使うほか、正規化した完全一致の `lang` と `includeGenerated`（既定 `false`）も受け付ける。cursor は generation と canonical filter の両方に結び付き、ページ間の filter 変更は `-32602` / `resources_list_filters_changed` と `restart_required: true` を返す。generated file は discovery と direct read のどちらでも `includeGenerated: true` が必要である。
- **MCP resource list カーソルの安定性** — `resources/list` は、最後に消費した file id と永続化されたインデックス済みファイル世代を結び付ける固定長の不透明 keyset cursor を返す。reader は同じ SQLite snapshot 内でその id を既存の source/test/docs bucket と path の並び順へ解決する。ファイルの追加・削除・更新で世代が変わると、後続ページは `restart_required: true` 付きの `-32011` / `index_stale` を返し、混在 snapshot を続行せず `params.cursor` を省略して再開する必要がある。書き込み可能な legacy DB は cursor 発行前に通常の read migration で世代 row と trigger を導入する。世代追跡を証明できない変更可能な read-only legacy DB は `migration_required: true` 付きの `resources_list_generation_unavailable` を返すが、canonical かつ曖昧性のない `immutable=1` legacy URI（任意で `mode=ro` を併記）はページ間で変化しないため connection-local な世代 0 を安全に利用できる。encoded、case variant、空白付き、重複、競合、または余分な query parameter はこの immutable 保証として信頼しない。旧 decimal の 0 は先頭ページ用の移行入力として残すが、0 以外の decimal offset は発行時世代を検証できないため同じ再開必須 error を返し、decimal cursor は出力に使わない。
- **MCP resource list の response budget** — `resources/list.params.maxBytes` は 4,096〜1,000,000 bytes を受け付け、既定値は HTTP response body の既定上限と同じ 1,000,000。有効 budget はこの要求値、server-wide MCP envelope 上限、および該当時の active HTTP transport response-body 上限の最小値とし、HTTP の設定上限が低い場合も HTTP 500 で拒否せず有効なページへ整形する。server は JSON-RPC envelope 全体を計測し、candidate 上限を 200 件に保ったまま、次の resource で有効 byte budget を超える直前に停止する。HTTP JSON-RPC batch では active transport budget を bracket と comma を含む response 配列全体へ適用し、応答対象 item へ公平に分配する。notification は response slot を消費しない。各 `resources/list` item は現在の割当を守り、有界なページさえ収まらない場合も canonical な budget error に request ID を保持する。state-changing item とその他の non-resource outcome を retry-safe として付け替えることはなく、実行後の aggregate overflow は completion state が unknown で自動再試行不可であることを報告する。`_meta.response_controls` は要求／有効 budget、消費／返却件数、`omitted_resource_count`、有界な理由別件数（`resource_uri_too_long` / `resource_exceeds_max_bytes`）、`byte_budget_reached`、`continuation_reason`（`byte_budget`、`item_limit`、`completed`）を返す。継続 cursor は最後に消費した DB row を anchor とし、収まらなかった有効 resource は次ページ用に未消費のまま残すが、空ページにも収まらない resource は消費して件数へ計上し、pagination の livelock を防ぐ。
- **型付き MCP resource read** — `read_resource` は bounded file read を発見可能にする tools/call surface である。`tools/list` schema が型付き `uri`、1-based inclusive の行範囲、4 byte〜128 KiB の UTF-8 text budget、generated file の opt-in、opaque な continuation cursor 契約を持つ。handler は `HandleResourcesRead` に委譲するため、URI validation、generated-file policy、snapshot lookup、response-aware budgeting、UTF-8 boundary、stale cursor check、legacy index 互換性は 1 つの reader に保たれる。adapter は file text を `content[0].text` に一度だけ返し、canonical identity を `structuredContent.resource`、範囲・切り詰め・継続 metadata を `structuredContent._meta` に置く。resource error は元の分類と JSON-RPC code を維持した MCP tool-result error へ変換する。`resources/read` と両 URI 形式の後方互換性を維持し、client は `tools/list` で `read_resource` を feature-detect して、古い server では protocol method を使い続けること。
- **MCP 配列引数の上限** — `path` / `project` / `excludePaths` / mixed `names` などの string-array filter は、不正要素を暗黙に落とさず拒否する。配列は 100 件、各要素は 4096 文字を上限とし、`batch_query` では `request_index` と `ok: false` 付きの slot 失敗として報告する。
- **MCP schema のロックダウン** — すべての tool `inputSchema` は `additionalProperties: false` を含み、`tools/call` も同じ契約として未知の引数名を黙って既定値にせず `-32602` / `invalid_argument` で拒否する。
- **MCP stability marker と命名** — すべての tool は `x-stability`（`stable`、`experimental`、`deprecated`）を公開する。MCP の構造化 payload key は CLI JSON 契約に合わせて snake_case を使う。新規 field に camelCase alias を追加しないこと。
- **MCP の言語サポート句** — 公開されるすべての MCP ツール説明は、`McpServer.CreateToolDefinition` で生成される `Language support:` 句で終わる。Graph 系ツールは `ReferenceExtractor.GetSupportedLanguages()`、symbol 系ツールは `SymbolExtractor.GetSupportedLanguages()`、file/content 系ツールは `cdidx languages` と同じ検出言語カタログを参照するため、`tools/list` は手書き説明ではなく実行時レジストリと同期する。
- **MCPツールアノテーション** — 全ツールが MCP 仕様に沿った `annotations`（`readOnlyHint`、`destructiveHint`、`idempotentHint`、`openWorldHint`）を返し、AIクライアントが安全な読み取り専用クエリを自動承認できるようにする。
- **MCP サーバー instructions** — `initialize` response の初回案内は UTF-8 で 2 KiB 以内に保つ。bounded な `tools/list` discovery と完全定義の on-demand 取得を説明し、有効な tool だけを案内し、workflow に必要な tool が有効な場合だけ、完全 catalog の説明を重複させず詳細 workflow を `prompts/list` と `prompts/get` へ誘導する。
- **デプロイ単位での MCP ツール有効化** — `cdidx mcp` が 2 つの環境変数を尊重し、コード変更なしに公開ツールを絞れるようにする (#1561)。`CDIDX_MCP_TOOLS_ALLOW=<csv>` は厳格な allowlist で、指定された場合はそのツールだけが `tools/list` に現れ `tools/call` で dispatch される。`CDIDX_MCP_TOOLS_DENY=<csv>` は既定の全有効集合から個別ツールを除外する。両方指定された場合は allow を優先。既知ツール名の真実の源は `McpToolFilter.KnownToolNames` に集約し、`tools/list` 側の filter、`tools/call` 側のゲート、`batch_query` の slot ガードのいずれもここを参照する。`BuildInstructions` もゲート対応で、scoped デプロイの `initialize` instructions では無効化されたツールを推奨しなくなり、案内と公開面が一致する。トップレベル `tools/call` で無効化された既知ツールを呼ぶと `-32601 Tool not enabled: <name>` を返し、`batch_query` 自体はエンベロープとして成功するが各無効化スロットに `code: -32601` が `error` 文字列と並んで載るため、クライアントは prose を parsing せず code で分岐できる。サーバーに無い名前は既存の `-32602 Unknown tool` に流すことでオペレータ無効化と typo を区別できる。ツール名比較は大小文字無視、env var 内の未知名は既知集合で filter（typo で未知名のみの allowlist は意図的に何も公開しないため、ゲートが silent に外れない）。env var を一切設定しない既定挙動は全ツール有効なので、既存デプロイへの影響はない。
- **トリガー付きコンテンツ外部参照FTS5** — `chunks`テーブルを参照しコピーを保存しないことでストレージ倍増を回避。データベーストリガーでFTSインデックスを自動同期。
- **extractor regex の backtracking policy** — built-in symbol/reference extractor は repository-controlled な file content に対して unbounded regex match を使わない。backtracking regex は `BoundedRegex.DefaultMatchTimeout` を使い、`RegexOptions.NonBacktracking` は non-backtracking engine と互換な pattern で使ってよい。lookaround-heavy な extractor や balancing-group を使う extractor など、意図的に backtracking-only のまま残す pattern は、共有 timeout audit の対象になる場合だけ許容する。将来の extractor が `System.Text.RegularExpressions.Regex` を直接使う必要がある場合は、明示 timeout を渡し、`BoundedRegex` や `NonBacktracking` が適さない理由を文書化すること。
- **ハイブリッドなシンボル抽出** — ASTパーサーも重量級の言語固有依存も追加しない方針。大半の言語はコンパイル済み正規表現で処理し、JavaScript / TypeScript だけは class body の method 抽出、private-scope filtering、synthetic class expression の binding 判定、JS/TS 固有の range 解決など、正規表現だけでは壊れやすい箇所を軽量 lexer / state machine で補う。引き続き精度より速度とポータビリティを優先しつつ、言語パターンや JS/TS state machine から推論できる範囲で定義範囲、本体範囲、シグネチャ、親シンボル、修飾付きコンテナ経路、正式なグループキー、可視性、戻り値型も保存する。Visual Basic では `Namespace ... End Namespace` も実コンテナとして扱い、implicit visibility の宣言や `Shared` / `Overrides` / `Partial` など visibility 以外の先行修飾子も受理するようにしたため、他のクラス系言語と同じようにトップレベル構造とメンバーを取りこぼしにくくなった。Visual Basic のコンテナパターンは `VisualBasicEnd` ベースの範囲追跡を大文字小文字非依存で扱うため、partial 型ファミリーでも安定した本体範囲と `hotspots` 集計用メタデータを維持できる。**パターン外部化**: 言語パターンは現在 `SymbolExtractor.cs` 内にコンパイル済み `Regex` として定義。抽出パイプラインが自己完結し、コンパイル時検証が効くが、言語追加にはコード変更と再ビルドが必要。将来的にはJSON/TOMLファイルに外部化し（起動時読み込み）、コミュニティ貢献の敷居を下げ、開発時のホットリロードも可能にできる。トレードオフはコンパイル時安全性の喪失と起動コストの微増。外部化時のスキーマ: 言語名、種別（function/class/import/namespace）、正規表現文字列、本体スタイル（brace/indent/ruby-end/none）、可視性・戻り値型のキャプチャグループ名。
- **再利用する record 宣言解析** — C#、Java、Kotlin の record / primary constructor 抽出は共有の上限付き regex で宣言種別を照合し、capture した宣言名を ordinal 比較します。複数行宣言の収集は1つの連続 char bufferを拡張し、途中prefixを文字列化せず現在のspanを走査して、最後の宣言文字列だけを生成します。3言語を同じ経路に保ち、型が密なfileで宣言ごとにregex engineを再構築したり、広いheaderでprefix allocationが二乗化したりしないようにしてください。
- **C# の nested interpolation state** — C# lexical masker は、別の interpolation hole 内で interpolated regular / verbatim / raw string が始まると immutable な親 frame を保持する。nested string を閉じると外側の mode、delimiter、dollar count、brace depth を完全に復元し、expression-bodied property 内の call を declaration pattern から除外する。C# extractor contract の更新により、既存 index の対象ファイルも再抽出される。
- **C# static lambda の宣言ゲート** — 宣言 scanner は、確認済みの `static`、`static async`、`async static` lambda header 内にある候補名を式コンテキストとして扱います。複数行 property-header の結合によって呼び出し引数が前置された場合も同様です。本物の static member、local function、代入済み lambda symbol は引き続き抽出対象です。C# extractor contract v6 により、通常の index 更新で古い C# symbol が再抽出されます（#4830、#4453 の回帰）。
- **C# switch pattern と call の境界** — positional pattern の抑止処理は、arm arrow を次行まで探す前に、result の終端や expression continuation と、nested positional clause、contextual keyword の designation、property clause、`when` guard など有効な pattern 構文を区別します。これにより switch expression の arm が返す lambda 内の call は enclosing callable に属する call reference として保持し、本物の pattern head だけを除外します。C# extractor contract v10 はこの規則で古い C# row を更新します（#5085）。
- **`hotspots` の正式な family trust** — `hotspots` が重名グループをコードベース全体の件数へ昇格させるのは、永続化済み `symbols.container_qualified_name` / `symbols.family_key` が現行の言語別 `hotspot_family_version_*` 契約で生成されたときだけ。readiness stamp と marker fingerprint は `codeindex_meta` に置き、旧形式・混在・部分更新直後の DB は古いファイル横断グループ識別子を黙って再利用せず、明示的に縮退する。
- **C# metadata-target の正式な trust** — `deps` / `impact` の metadata attribute edge（`[Foo]` 使用と定義側 `FooAttribute` クラスの紐付け）は、永続化済み `is_metadata_target` が現行の `metadata_target_version_csharp` 契約で stamp されている DB ではシグネチャ形状ヒューリスティックではなく authoritative resolver の判定結果を使う。resolver は C# クラスの base list を同 DB 内の class 行で fixed-point 展開して解決し、未解決の外部基底のみ BCL 規約（`Attribute` サフィックス）へフォールバックする。readiness は `codeindex_meta` に置き、reader は (1) ready → `is_metadata_target = 1`、(2) 列はあるが stamp 未完（legacy 行）→ `signature LIKE '%: %'`、(3) 列すらない → 命名のみ、の 3 way 分岐で縮退する。これにより、`class FooAttribute : BaseService` のような非 attribute 同名 impostor が真の `FooAttribute : Attribute` と同居したときにエッジを黙ってドロップする挙動を修正した（#435）。
- **人間向けがデフォルト** — 全コマンドのデフォルト出力は人間向け。`--json`でAI/機械向け出力。
- **手動引数解析** — `System.CommandLine`は依存削減のため削除。シンプルなswitch文での解析。
- **後方互換なシンボルスキーマ** — 新しいバイナリで古いDBを開いたときは、可能なら不足するシンボル列を自動追加する。対象には `container_qualified_name` や `family_key` のような `hotspots` 用グループメタデータも含む。読み取り経路でその場移行ができない場合も、シンボル検索は旧カラム構成へフォールバックしてクラッシュを避ける。
- **bounded な hotspot 集約** — `DbWriter` は `hotspot_reference_counts` を file 単位の compact な logical-reference totals として維持する。limit 付き hotspot reader は固定上限の candidate frontier を先に選び、current C# callable では resolved logical target identity、その他の結果では永続化した name-rank index で順位付けする。そのため unresolved な高頻度名で確認済み C# target を追い出さず、後続の non-SQL、SQL exact、SQL leaf、曖昧性、target-family join を有界に保つ。logical-site identity から raw alias を除外し、mutation 時は cross-file context 依存を再集計して reference-identity trust を transaction 内で降格し、reader/writer の aggregate SQL は cancellation で中断できる。bulk候補のrefreshはdirty file IDが64件以上の場合にprimary-key seekとbounded total-row probeを行い、既存dirty aggregate rowがtableの5分の3以上を占める場合だけquery index 4本を外す。fresh / rebuild のrefresh前aggregateが空の場合も条件を満たすものとし、偏りの大きいupdateや小規模updateは全indexを維持する。条件を満たすrunだけ同じtransactionのset-based insert後に1回再構築する。writable な legacy database では table の作成と backfill を transaction 内で行い、immutable な legacy reader は raw-reference compatibility path を維持する。
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

`deps --symbol`、`--symbol-family`、`--suppress-noise` の汎用 symbol 部分は、候補の
ranking と `--limit` より前に logical-reference と target-candidate の SQL scope へ
push down される。cycle と cross-workspace の read も、各候補上限より前に同じ名前
filter を適用する。Markdown の見出し名一致は
`markdown_heading_name_match` evidence として別に分類する。抑制時は候補上限より前に
保持対象 evidence を優先し、CLI 層でこの evidence だけを除くため、旧 index 由来の
見出し fanout と同じ edge に含まれる明示的な Markdown path link も残る。
machine-readable edge の `evidence` は `source_language`、`origin`、`reference_kind`、
`target_kind`、`reference_count` の分布を公開し、`symbol_filter` は reference の
before/after 合計と理由別の affected / removed 件数を加える。汎用 symbol の counter は
引き続き、絞り込み前の workspace 全体ではなく SQL で絞り込まれた scope を表す。
長時間の SQLite dependency read では query token による command cancellation も登録する。

実運用上の帰結: クラスのようなシンボルに対する `impact <ClassName>` は、member-level の caller が存在しない場合 heuristic file-dependency-hint fallback (metadata エッジを含む) を返し、一方の既定 `callers <ClassName>` は実行可能 edge だけを返す。両方とも個々の契約上は正しいが、件数は一致しない。差分を埋めるには `references <ClassName> --kind attribute`（または `annotation`）を使うか、`callers` / `callees` に明示的に対応する非既定 kind を渡し、既定 call graph が意図的に落としている edge を確認する。

reference-identity contract が current な単一の C# 論理 partial family では、impact resolution を物理ファイルの ambiguity guard より先に行います。`ResolveImpactDefinitions` は代表出力に加え、全物理 family の symbol ID / path を独立した上限付き集合として保持します。`GetTransitiveCallers` はその ID 集合を単一 SQL union root として問い合わせるため、candidate row、caller、cycle node、収束 path を BFS の計数前に重複排除します。file-hint fallback は保持した全 family path から member 名を解決し、全 declaration file を依存先候補から除外します。物理 family 展開には `DefaultImpactPartialFamilyMemberBudget` を使い、`partial_family_member_*` で報告し、通常の result 用 `truncated` / `truncated_reason` fields は立てません。reference-identity contract が stale の場合は legacy traversal のため symbol-root metadata を維持します。family 展開が上限に達した count は CLI JSON で degraded / non-authoritative とし、MCP count-only では `total: null` を返します。この経路へ入るのは論理 partial key が同一の場合だけで、無関係な同名 namespace / language symbol を含む複数 logical key は従来どおり ambiguity result を維持します。

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
次の candidate へ fall through します。`.cdidxrc.json` または
`.cdidx/config.json` の repository config に由来する `metrics_path` と
`global_tool_log_dir` には、より厳格な境界を適用します。config workspace
配下の既存 component が symbolic link、junction、bind mount / cross-device mount point、
reparse point、device、dangling link のいずれかであれば拒否し、各 mutation の
直前にも境界を再検証します。Linux では `statx` または
`/proc/self/fdinfo` fallback から得た path と open 済み handle の mount ID も
比較して同一 device の bind mount を拒否します。POSIX ではさらに directory 作成、
append、permission 変更、rotation、置換、delete を workspace directory handle 起点の
no-follow relative operation へ固定し、mutation 時の検証後に directory parent を root
から再取得するため、外部へ移動済みの ancestor handle を再利用しません。guarded rename は
成功を返す前に open 済みの destination parent を fsync します。Windows では workspace
root の最終的な物理 path を guard が保持するため、差し替えられた root alias を再追跡しません。
root handle 相対の native open は name resolution 中の reparse point を拒否し、guarded
replacement / delete も handle / root 相対で実行します。同じ `global_tool_log_dir` guard を lifecycle log、file
query trace、上限付きの `last-failure.json` 診断にも適用します。安全でない値は
上限付きの `unsafe_output_path` 診断で config validation に失敗し、外部 target の
作成、追記、rotation、置換、削除、chmod は行いません。明示的な CLI と process
environment の保存先は、operator が制御する従来の挙動を維持します。
ファイル名は
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
- `initialize` の `instructions` はこれらの resource template / list control を直接案内し、各 `resources/list` response は accepted extension parameter と上限を `_meta.discovery_contract` に公開する。これにより AI client は標準外の protocol extension を推測する必要がない。`prompts/list` の `summarize_file` は `path` を必須として公開し、`prompts/get` は prompt 構築前に、欠落、文字列以外、空文字・空白のみ、絶対 path、drive prefix、制御文字、`..` traversal を JSON-RPC `-32602` で拒否する。受理した workspace-relative path は空白、Unicode、POSIX でファイル名文字となる backslash を保持し、Windows の path separator だけを `/` に正規化する。
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
- **認証ミドルウェア**（#1559）。`McpServer` はパース済み JSON-RPC リクエストごとに、メソッド抽出 *後*・dispatch *前* で `IMcpAuthenticator` を呼ぶ。既定の `LocalStdioAuthenticator` は permissive で（従来の stdio 動作を維持し、呼び出し元を `stdio` / `local` でタグ付けする）、stdio では `CDIDX_MCP_AUTH_TOKEN` を設定すると `TokenMcpAuthenticator` に切り替わる。未設定または空文字の token だけが permissive で、空白のみ・空白文字入り・制御文字入り・4096 文字超の token は設定値として拒否する。`TokenMcpAuthenticator` は応答が必要な全リクエストに対し、`params.auth.token` が一致することを要求し、比較は `CryptographicOperations.FixedTimeEquals` による定数時間比較で行う。HTTP はこの body token ゲートを重ねず、`ProgramRunner` が `CDIDX_MCP_HTTP_TOKEN` を優先し、未設定なら `CDIDX_MCP_AUTH_TOKEN` を fallback として bearer secret に解決して、`Authorization: Bearer ...` の transport check に一本化する（#3156）。HTTP bearer 値は `Bearer ` の後ろを trim せず完全一致で扱い、空白文字・制御文字・4096 文字超は hash 前に拒否する。JSON-RPC body token ゲートの失敗は統一された JSON-RPC `-32001 "Unauthorized"` を返し（#1530 の sanitization 方針に従い、ワイヤでは未提示と不一致を区別しない）、`BuildAuthFailureLog` が詳細を stderr に書き出す。handshake 制御の `notifications/initialized` は認証せず short-circuit できるが、それによって送る roots request は認証済み initialize で commit した capability snapshot に制限される。一方、state-changing notification（`$/cancelRequest`、`notifications/cancelled`、`notifications/roots/list_changed`、`notifications/shutdown`、`notifications/exit`）は cancellation / roots / lifecycle state を変更する前に認証する。認証失敗時も notification は応答を返さず、bounded な stderr 診断だけを残す（#4537）。このミドルウェアが将来 transport の差し替え seam になる — ネットワーク listener は別の `IMcpAuthenticator` を提供しつつ、`McpCallerIdentity`（`Source` + `Subject`）の形を保ち、監査ログ（#1562）から再利用できる。成功した認証主体は exact request に紐付けて dispatch まで運び、並行 transport は `McpTransportFrame` に主体を付与する。その主体は server-side placeholder 認証の成功値より優先されるが、server 認証失敗を迂回しない（#5186）。scope は通知、取消、deadline、error、serialization failure を含むすべての終了経路で復元し、並行 HTTP request 間で audit attribution を交換しない。

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

- **フィールドの安定性。** `timestamp`、`tool`、`auth_source`、`auth_subject`、`arg_keys`、`arg_lengths`、`elapsed_ms`、`error_code` は per-tool の全レコードで出力する。`caller`、`caller_version`、`request_id`、`request_id_type`、`request_id_length`、`request_id_truncated`、`arg_key_lengths`、`arg_keys_truncated`、`arg_key_truncation_reasons`、`arg_values`、`arg_values_redacted`、`arg_values_truncated`、`arg_values_truncation_reasons`、`arg_values_serialized_bytes`、`arg_values_max_bytes`、`result_count`、`checked_root_identity`、`error` は値が non-null または true のときだけ含める。既存フィールドの改名や流用は破壊的変更扱い（CLI `--metrics` と同じ運用）。
- **認証主体。** `auth_source` と `auth_subject` は MCP client metadata と分離した request 認証または transport provenance であり、`stdio` / `local`、`stdio-token` / `token`、`http-bearer` / `token`、明示的な loopback の `http` / `anonymous` のいずれかを記録する。token subject は意図的に generic で、token、hash、可逆 fingerprint、個人を表す claim は記録しない。両値は sanitize と bound を適用し、必要なら length / truncation metadata を伴い、compact / minimal な event-size fallback でも保持する。
- **request id のプライバシー。** `request_id` は前述の process-salted な固定長 token で、JSON-RPC wire の値ではない。token がある場合は `request_id_type` と decode 後の値長を示す `request_id_length` を同伴する。token 自体がすでに bounded なため、legacy の truncation guard は通常付かない。
- **エラーコード意味論。** `0`=成功、`1`=MCP ツールエラー (`isError: true`)、負値=JSON-RPC エラーコードそのまま（例: invalid params なら `-32602`、internal error なら `-32603`）。同伴する `error` 文字列は `jsonrpc_error` / `tool_error` / `missing_tool_name` / サニタイズ済み例外型名のいずれか。`McpServer.BuildSanitizedToolErrorMessage` が `ex.Message` をワイヤーと audit から除外している（#1530）。
- **result count。** `ExtractResultCount` は `structuredContent.count` を優先し、無ければ `structuredContent.results.length`、いずれも無ければ省略する。ツールエラー / JSON-RPC エラー時も省略する（`0` ではなく欠落）。
- **MCP index の root identity。** `index` は要求された root を canonical に解決して platform filesystem identity を取得し、run 中は no-follow の directory handle を保持します。directory enumeration は Linux / macOS / Windows のすべてで handle-relative に行い、認可済み filesystem seam は各 directory / file の open 前 identity、実際に開いた handle identity、open 後の canonical containment を内容の利用前に照合します。language-map / pattern sidecar は認可済み project tree 内に限定して同じ seam から開き、より広い user 設定と executable workspace plugin を含まない authorization-scoped snapshot に cache します。root、ancestor、link、entry identity の変化時は上限付きの `permission_denied` tool error と `authorization_failure_reason` を返し、成功時/dry-run の structured output と、response が error path で生成された場合を含む認可後の全 audit record は同じ固定長 opaque `checked_root_identity` を保持します。包含 repository root は認可範囲内の場合だけ ignore rule に使い、範囲外なら discovery を要求 project root 内に限定します。
- **引数のプライバシー。** `arg_keys` / `arg_lengths` は常に記録するので呼び出しの *形状* は復元できるが、引数キー数と表示キー長は capped され `arg_keys_truncated` で明示される。`arg_values` は `--audit-log-include-values` に gated（cdidx クエリにはソース片や secret 風文字列が混入しうる）。echo は sanitize と budget を適用した clone として作り、diagnostic / audit 共有 taxonomy で分類された secret 風キーや既知 token pattern は `[REDACTED]` に置換し、depth / object property / array item / total node / string length / serialized byte / event byte の上限に達した場合は値を書き出す前に `arg_values_truncated` を記録する。
- **client 自己申告 metadata。** 互換フィールド `caller` / `caller_version` は、受理済み `initialize.clientInfo` の bounded な name/version を保持する。現在接続中の MCP client を説明する値であって認証済み identity ではなく、任意の client metadata から `auth_source` / `auth_subject` を変更できない。protocol 交渉に失敗した initialize は、これらの互換フィールドや他の session state を上書きしない（#4540、#5186）。
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

| 機能 | 設定 |
|---|---|
| 拡張子 alias | `~/.config/cdidx/langmap.yaml` と、最も近い workspace ancestor の `.cdidx-langmap.yaml` |
| regex ベースの symbol | workspace の `.cdidx/patterns/*.yaml` と user の `~/.config/cdidx/patterns/*.yaml` |
| 単独検証 | `cdidx test-extractor --language <lang> --file <path> --json` |

### 拡張子 alias の優先順位

- workspace entry が user entry を上書きします。
- 信頼済み suffix override は built-in の完全一致 filename、filename-prefix、
  extension rule より先に評価されます。
- 最も近い workspace map を probe または read できない場合、その subtree では
  parent map を再利用せず探索を停止します。
- `languages --json` と MCP の `languages` tool は sanitization 済み失敗を
  `language_map_diagnostics`、実効順序を `detection_policy.precedence` に公開します。

### Pattern sidecar の安全策

| 上限 | 値 |
|---|---|
| 探索候補 | pattern directory ごとに 128 件 |
| sidecar size | 1 file あたり 64 KiB |
| sidecar 内の rule | 128 件 |
| configured rule | immutable workspace snapshot ごとに 128 件 |
| worker の project-root snapshot | persistent symbol worker ごとに 32 件 |
| worker の pattern-directory snapshot | root ごとに 4,096 件、worker ごとに 8,192 件。超過分は live discovery |
| regex match timeout | 100 ms |
| timeout rule の cooldown | 所有する workspace snapshot 内で最大 1 分 |

- sidecar は symlink ではない pattern directory 配下の通常 file に限定します。
- 各 sidecar は path・rule・budget を commit する前に parse / compile し、
  `SymbolKindCatalog` に対して kind を検証します。
- 拒否された内容は fingerprint で重複診断を抑制します。再度 discovery または明示的
  refresh を行う経路では、内容や metadata の変更、一時的な read failure からの回復後に
  process を再起動せず再試行します。
- workspace 探索には明示的な trust root が必要で、それより上は探索しません。
  境界内の nested sidecar は対象 file の上限付き extraction worker で読み込みます。
- persistent symbol-worker command は pattern discovery を project-root snapshot として扱います。
  root reload は user / workspace-root config を1回だけ読み込み、missing、unsafe、既知の
  discovery failure を含む各 nested pattern directory の初回結果を run 中は固定します。
  その後に追加または修復された sidecar は次の worker command で可視になり、想定外例外は
  引き続き再試行します。directory cache が飽和した場合は uncached discovery に fallback し、
  memory 上限を守るために config を skip することはありません。
- path identity は実際の filesystem の case-sensitivity に従うため、case-sensitive
  volume では大小文字だけが異なる sidecar も別々に扱います。
- `status --json` の `extractors.pattern_configs[]` は、受理済み file の
  sanitization 済み path、workspace/user provenance、正規化済み language、rule count を
  報告します。
- reindex は workspace snapshot を atomically に置換します。以前の rule budget と
  timeout state は他 workspace に影響せず回収でき、timeout rule は
  workspace-scoped diagnostic を出してから cooldown に入ります。

### Extractor のテスト

`cdidx test-extractor --language <lang> --file <path> --json` は index を作らずに
extraction を実行します。`--expect-symbols <json>` を加えると fixture と比較できます。
source と expectation file はそれぞれ 4 MiB が上限です。

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
