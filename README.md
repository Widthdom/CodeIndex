# cdidx

> **[日本語版はこちら / Japanese version](#cdidx日本語)**

[![Build and Test](https://github.com/Widthdom/CodeIndex/actions/workflows/dotnet.yml/badge.svg)](https://github.com/Widthdom/CodeIndex/actions/workflows/dotnet.yml)
[![CodeQL](https://github.com/Widthdom/CodeIndex/actions/workflows/codeql.yml/badge.svg)](https://github.com/Widthdom/CodeIndex/actions/workflows/codeql.yml)
[![Release](https://github.com/Widthdom/CodeIndex/actions/workflows/release.yml/badge.svg)](https://github.com/Widthdom/CodeIndex/actions/workflows/release.yml)

![.NET 8.x / 9.x tests](https://img.shields.io/badge/.NET-8.x%20%2F%209.x%20tests-512BD4?logo=dotnet&logoColor=white)
![C#](https://img.shields.io/badge/C%23-12-239120?logo=csharp&logoColor=white)
![Platform](https://img.shields.io/badge/Platform-Windows%20%7C%20macOS%20%7C%20Linux-lightgrey)
![License](https://img.shields.io/badge/License-FSL--1.1--ALv2-orange)
![SQLite](https://img.shields.io/badge/SQLite-FTS5-003B57?logo=sqlite&logoColor=white)

**CLI code indexing, MCP search, and LSP editor lookup for local repositories.**

`cdidx` is a command-line code indexer, MCP server, and read-only LSP shim that
builds a local SQLite index of your repository so humans, AI agents, and
LSP-native editors can run fast full-text, symbol, dependency, and inspection
queries without repeatedly rescanning the same tree.

## Why cdidx

> **Index once. Ask many times.** `cdidx` turns a repository into a local
> retrieval runtime, so humans and AI agents can pull scoped code context
> without rescanning or resending broad files every turn.

| If your workflow is... | Best fit | Why |
|---|---|---|
| One-off string hunting | `rg` | zero setup, direct file scan |
| Repeated repository investigation | `cdidx` | local SQLite FTS5 index, structured results, incremental refresh |
| VS Code-only chat context | VS Code workspace index | editor-managed context inside the Copilot/VS Code UX |
| Terminal, CI, scripts, or MCP clients | `cdidx` | explicit CLI + MCP boundary that works outside an IDE |

Details: [why cdidx](USER_GUIDE.md#why-cdidx), [cdidx vs rg](USER_GUIDE.md#cdidx-vs-rg),
and [cdidx vs VS Code workspace index](USER_GUIDE.md#cdidx-vs-vs-code-workspace-index).

## Design boundaries

CodeIndex is a local-first code index and retrieval backend for human users and AI agents. Its responsibility is to index a codebase in a lightweight, deterministic, and local way, then return the code context requested by callers through read-only retrieval surfaces such as full-text search, symbol search, reference search, dependencies, excerpts, inspect, map, and MCP.

CodeIndex is not an AI editor, coding agent, or chat application. Conversation, editing, commits, pull requests, and autonomous change decisions are the responsibility of the external agent or editor that calls cdidx. Symbol extraction and reference extraction are lightweight indexing hints designed to prioritize speed, local operation, explainability, and retrieval usefulness. They are not intended to provide compiler-accurate semantic analysis, full type resolution, or an exact call graph.

Embeddings, vector search, and LLM-based semantic ranking are not assumptions of CodeIndex core. If supported in the future, they should be positioned as optional extensions or separate layers built on top of the local index, rather than as part of the core retrieval model.

## Quick Start

```bash
# Install is usually seconds. Homebrew is available on macOS/Linux:
brew install widthdom/tap/codeindex

# Or install as a .NET global tool from NuGet:
dotnet tool install -g cdidx

# Or install directly from the signed GitHub release assets:
curl -fsSL https://raw.githubusercontent.com/Widthdom/CodeIndex/main/install.sh | bash

# First index: ~30-60s on small repos; minutes or longer on 100k-file trees.
# Add --verbose to see each file status while it runs.
cdidx .
cdidx status --check --json
cdidx search "handleRequest"
cdidx definition UserService
cdidx search "Handle" --project MyApp
cdidx search "File.ReadAllText" --exact-substring --reject-before "Length" --guard-window 8
cdidx validate
cdidx mcp
cdidx lsp --db .cdidx/codeindex.db
```

Custom language loops can stay out of tree: put extension aliases in
`.cdidx-langmap.yaml`, put regex symbol patterns in `.cdidx/patterns/*.yaml`,
and run `cdidx test-extractor --language <lang> --file <path> --json` to test
an extractor fixture without building a full index. `test-extractor` source and
`--expect-symbols` files are capped at 4 MiB each. Pattern sidecars are
limited to regular files under non-symlink pattern directories, size/count
bounded per file and per process, and regex matches are time-limited. See
[Custom Language Extraction](DEVELOPER_GUIDE.md#custom-language-extraction).

After the first command, use these cues and follow-up commands:

| Situation | What to expect or run |
|---|---|
| Index progress | The terminal shows `Scanning...`, `Indexing...`, a `67.0% [28/42]`-style progress line, and an elapsed time such as `2.4s` or `5m 42s`. |
| Edits or branch switches | Refresh incrementally with `--files`, `--commits`, or `--changed-between <old-ref> <new-ref>` instead of rebuilding. See [Quick Start](USER_GUIDE.md#quick-start) and [Incremental update reliability](USER_GUIDE.md#incremental-update-reliability). |
| Intentional rebuilds | Interactive terminals ask before deleting the DB. Scripts and CI must pass `--yes` or `--force`. |
| Long-lived DB compaction | Run `cdidx optimize` or `cdidx index <projectPath> --optimize` to compact FTS5 segments immediately. Incremental refreshes also optimize opportunistically. |
| Pathological generated files | `--max-symbols-per-file <n>` skips indexing file content, symbols, and references when one file emits too many symbols, leaving a `symbol_count_exceeded` issue for audit. |
| Maintenance rollback | Run `cdidx db checkpoint <name>` before risky DB maintenance and `cdidx db restore <name>` to roll back. `backfill-fold` creates an automatic checkpoint unless `--no-checkpoint` is passed, and interrupted folded-key rewrites resume from remaining rows. |
| Permission or I/O scan errors | `cdidx` records the scan error, continues other directories, and writes `.cdidx/scan-checkpoint.json` so same-HEAD retries can skip completed directories. |

Output controls:

| Need | Option |
|---|---|
| Owner-only persistent stderr logs on POSIX | Global tool stderr logs are forced to `0600` permissions on every open, including existing date/process-stamped log files. Use `--log-format text|json`, `--log-retain-count <N>`, `--log-max-size-mb <N>`, `CDIDX_LOG_*`, or `CDIDX_GLOBAL_TOOL_LOG_MAX_BYTES` to make lifecycle logs JSONL-friendly and rotate them for aggregation. Log size caps are limited to 1024 MiB / 1 GiB. |
| Checked-in configuration | Use `.cdidx/config.json` or `.cdidxrc.json` for repository defaults such as `search.limit`, `search.snippet_lines`, and `search.max_line_width`; config JSON is size/depth bounded before schema validation, string arrays are count/length bounded before environment expansion, and config-sourced output paths must stay inside the workspace. Run `cdidx validate-config` to validate the discovered file and `cdidx config show` to inspect precedence. |
| Workspaces | Use `cdidx.workspace.json` or `.cdidx-workspace.json` to declare monorepo members, `cdidx workspace list` to inspect them, and `cdidx workspace use <name>` / `cdidx workspace current` for a persisted active workspace. |
| ASCII-only terminal output | Use `--ascii`, `CDIDX_ASCII=1`, `NO_UNICODE`, `TERM=dumb`, accessibility env hints, or a non-UTF-8 locale. Spinners use pipe, slash, dash, and backslash frames; progress bars use `#` / `-`; very narrow terminals fall back to percentage-only progress. Use `--no-progress`, `CDIDX_DISABLE_PROGRESS=1`, or `PREFERS_REDUCED_MOTION` to keep static progress text without animation. |
| Color and terminal capability | `--color auto` emits ANSI only for capable interactive terminals; `TERM=dumb`, `CI=true`, missing Unix terminal hints, `NO_COLOR`, or `CLICOLOR=0` disable ANSI/progress control sequences. `--palette basic|256|truecolor` can override the `COLORTERM` / `TERM` color-depth detection. |
| UTF-8 JSON pipelines | CLI `--json` output is written as UTF-8 without a BOM and never includes ANSI escape sequences, even when color is forced for human output. |
| Script-friendly query pipelines | Use `--quiet`, `-q`, `--silent`, or `CDIDX_QUIET=1` to suppress informational stderr text while preserving errors. `--quiet` takes precedence over `--verbose`. Read commands that support `--format` can emit `count`, `compact`, `csv`, or `tsv` output when callers need smaller or table-shaped payloads instead of full excerpts. |

Use `cdidx` when a repository will be searched repeatedly from terminals,
scripts, CI, or AI tools. Use `rg` when you only need a one-off text scan.

Help discovery:

| Need | Command |
|---|---|
| Concise command overview | `cdidx --help` |
| Full command, flag, and example reference | `cdidx --help-all` or `cdidx --help-extended` |
| Shared flag reference only | `cdidx --help-flags` |
| One command's usage line | `cdidx <command> --help` |

Install choice and network notes:

| Need | Use |
|---|---|
| Self-contained binary with no .NET runtime | `install.sh` |
| .NET global tool workflow | `dotnet tool install -g cdidx` with .NET 8 installed |
| ARM64 host without a preinstalled .NET 8 runtime | `install.sh` |
| Proxy or mirrored GitHub access | `install.sh --doctor` and `CDIDX_GITHUB_BASE_URL` / `CDIDX_GITHUB_API_BASE_URL` |

Installation security: release tarballs are still checked against
`sha256sums.txt`, and the installer also downloads `sha256sums.txt.asc` and
runs `gpg --verify` when GnuPG is available. Set `CDIDX_STRICT_VERIFY=1` or
pass `--strict-verify` to fail closed when signature verification cannot be
performed. Set `CDIDX_RELEASE_GPG_FINGERPRINT=<fingerprint>` to pin the
expected release signing key; strict mode requires this fingerprint once GPG
verification succeeds.

See [DISTRIBUTION.md](DISTRIBUTION.md) for the full channel matrix and
[isolated network install notes](USER_GUIDE.md#isolated-networks-and-proxies).
For database compatibility across `cdidx` upgrades and downgrades, see
[COMPATIBILITY.md](COMPATIBILITY.md).

### Validate

Run `cdidx validate [--db <path>] [--json] [--format <text|json|count|compact|csv|tsv|lsp|qf|sarif>] [--verbose] [--kind <kind>] [--path <glob>]`
to report indexed file issues such as replacement characters (`U+FFFD`), BOMs,
NUL bytes, mixed line endings, UTF-16 BOMs, and likely non-UTF8 content.
Validation findings are reported in the output and do not by themselves make
the command fail; the command exits non-zero when the DB cannot be read or the
command arguments are invalid. Use `--json` for machine-readable issue rows.

### Shell Completion

Generate completion scripts with `cdidx --completions <bash|zsh|fish|powershell>`.
The generated scripts complete subcommands, flags, and common flag values:
`--lang` suggests supported languages, `--kind` suggests symbol/reference
kinds, and path-like options such as `--db`, `--path`, and `--output` use shell
file completion. Each generated script includes the `cdidx` version that
produced it; regenerate installed completion scripts after upgrading or
downgrading `cdidx`.

## Highlights

| Area | What cdidx provides |
|---|---|
| Search surfaces | CLI-first output for humans and machines; full-text, symbol, reference, caller/callee, dependency, map, inspect, and excerpt commands. `search`, `definition`, `references`, `callers`, `callees`, `find`, and `validate` support `--format count|compact|csv|tsv|lsp|qf|sarif` for token-budgeted agents, scripts, editors, and CI reports. `cdidx lsp --db .cdidx/codeindex.db` starts a read-only stdio Language Server Protocol shim for LSP-native editors. |
| Validation diagnostics | `validate --json` and MCP `validate` annotate `replacement_char` rows with `origin` (`source_literal` or `decode_replacement`) and `severity` so agents can separate intentional U+FFFD literals from likely encoding damage. |
| Definition and impact diagnostics | `definition --json` includes C# `disambiguator` hints for overloads, partial types, and extension receivers when indexed metadata can distinguish them. `impact --json` and MCP `impact_analysis` include `impact_failure_chain` and `suggestion_type` for zero-result routing; `impact --strict` exits non-zero when resolution or graph preconditions are unmet. |
| Ranking and filters | Public/exported symbol matches rank ahead of protected, internal, and private matches. Use `--no-visibility-rank` for legacy order, and `--visibility` / `--exclude-visibility` with `symbols`, `definition`, `unused`, and `hotspots`. Query defaults can be adjusted with `CDIDX_DEFAULT_LIMIT`, `CDIDX_DEFAULT_SNIPPET_LINES`, and `CDIDX_DEFAULT_MAX_LINE_WIDTH`; explicit CLI flags still win. |
| Project scoping | `.sln` / `.csproj`-aware <code>--project &lt;name&#124;path&gt;</code> filters for indexing and queries, plus `--solution <path>` when a workspace has multiple solution files. |
| MCP/LSP integration | MCP server support for AI clients such as Claude Code, Cursor, and Windsurf, including tools, indexed-file resources, starter prompts, schema constraints for local argument validation, `mimeType` on text content blocks, logging, a structured `ping` health result, HTTP `GET /healthz`, opt-in HTTP `/events` keep-alive notifications, a compatibility server-side `notifications/initialized` ready signal on stdio or HTTP `/events` streams, and `Language support:` descriptions sourced from the same registries as `cdidx languages`. Tool schemas reject unknown arguments with `-32602`, advertise `x-stability`, and use snake_case structured JSON keys to match the CLI JSON contract. LSP mode exposes `initialize`, `workspace/symbol`, `textDocument/documentSymbol`, `textDocument/definition`, and `textDocument/references` over stdio for editors that do not speak MCP. |
| Freshness | Parallel full-scan extraction with `--parallelism`, incremental refreshes with `--files` and `--commits`, continuous `--watch`, exact `status --check`, and configurable stale thresholds via `--stale-after` / `CDIDX_STALE_AFTER`. |
| Storage | Local-first `.cdidx/codeindex.db` storage. Query commands run from nested directories prefer the outermost ancestor `.cdidx/codeindex.db` before falling back to the current directory. `--data-dir <dir>`, `CDIDX_DATA_DIR`, or `XDG_DATA_HOME` can move default SQLite storage outside the workspace; explicit `--db <path>` still wins. |
| DB maintenance | New indexes use SQLite incremental auto-vacuum. Successful writer runs truncate-checkpoint the WAL. `cdidx vacuum` reclaims free pages from existing DBs, including a one-time full `VACUUM` conversion for legacy no-autovacuum DBs. `cdidx db schema` reports the on-disk schema, `cdidx db prune --dry-run\|--apply` audits or removes orphaned DB rows, and `status --json` reports metrics under `db_pragma_settings`. |
| Security defaults | On POSIX systems, `.cdidx` is created with `0700` permissions, lifecycle, metrics, MCP audit, and query trace logs are created owner-read/write from the start, metrics and audit logs rotate to bounded slots, query trace logs are pruned to a bounded retained set, and `status --json` reports the effective `data_dir_mode` when available. |
| Diagnostics | `doctor` prints a redacted environment summary for bug reports. `status --config` prints effective configuration with source attribution, and `status --explain <field>` describes readiness fields and remediation. Read commands support `--profile`, `--slow-query-ms <n>`, and <code>--trace=stderr&#124;file&#124;none</code>; file traces write daily `query-trace-YYYYMMDD.jsonl` files next to the lifecycle log and retain the newest 30 trace files. |
| Query exit codes | Valid zero-result query commands exit `0` by default. Pass `--strict-not-found` when scripts should treat zero rows as exit code `2`. |
| Drift checks | `cdidx diff <db1> <db2>` compares schema, file, symbol, and reference deltas with stable exit codes: `0` identical, `1` drift, `2` schema mismatch, `3` unreadable DB. |
| Extensibility and feedback | Post-extraction hooks from `~/.config/cdidx/hooks/*.dll` or `CDIDX_HOOKS_DIR` can enrich symbols and references. `cdidx suggestions` lists, inspects, and exports local suggestion history, including issue-draft JSON with optional open-issue duplicate preflight, with fuzzy MCP suggestion deduplication controlled by CLI, env, or `.cdidxrc.json`. |
| Language coverage | 78 detected languages, with symbol and graph support where available. |
| Updates | `cdidx --version` checks GitHub releases at most once per day and appends a newer-release hint when one is available. Use `cdidx --check-updates` or `cdidx status --check-updates` for an explicit freshness check, and `cdidx upgrade` to reinstall the latest GitHub release via `install.sh`. Set `CDIDX_DISABLE_UPDATE_CHECK=1` to suppress checks. |

### Upgrade and uninstall

`cdidx upgrade --check-only` reports whether a newer GitHub release is available. `cdidx upgrade` downloads `install.sh` from the resolved release tag, refuses unwritable install directories, and reruns the installer with `CDIDX_INSTALL_DIR` pointed at the current binary directory.

Direct `install.sh` installs can be removed with:

```bash
bash ./install.sh --uninstall
bash ./install.sh --uninstall --purge-cache
```

The uninstaller removes files placed next to the `cdidx` binary and can optionally remove `~/.cache/cdidx`. It does not remove project `.cdidx/` directories, shell profile PATH edits, shell completion scripts, Homebrew installs, or .NET global-tool installs.

The documented `status --json` trust contract covers these fields:

<table>
<tbody>
<tr><td><code>fold_ready</code></td><td><code>fold_ready_reason</code></td><td><code>graph_table_available</code></td><td><code>issues_table_available</code></td></tr>
<tr><td><code>file_issues_data_current</code></td><td><code>migration_in_progress</code></td><td><code>degraded_root_cause</code></td><td><code>readiness_degradations</code></td></tr>
<tr><td><code>sql_graph_contract_ready</code></td><td><code>sql_graph_contract_degraded_reason</code></td><td><code>hotspot_family_ready</code></td><td><code>hotspot_family_degraded_reason</code></td></tr>
<tr><td><code>language_readiness</code></td><td><code>csharp_symbol_name_ready</code></td><td><code>csharp_metadata_target_ready</code></td><td><code>csharp_metadata_target_degraded_reason</code></td></tr>
<tr><td><code>indexed_head_commit</code></td><td><code>worktree_head_changed</code></td><td><code>indexed_head_sha</code></td><td><code>indexed_head_branch</code></td></tr>
<tr><td><code>indexed_head_timestamp</code></td><td><code>commits_ahead_of_indexed_head</code></td><td><code>index_writer_version</code></td><td><code>index_newer_than_reader</code></td></tr>
<tr><td><code>index_newer_than_reader_reason</code></td><td><code>unknown_extension_file_count</code></td><td><code>unknown_extension_files</code></td><td><code>unknown_extension_files_truncated</code></td></tr>
<tr><td><code>unknown_extension_file_path_limit</code></td><td><code>path_case_sensitive</code></td><td><code>data_dir</code></td><td><code>data_dir_source</code></td></tr>
<tr><td><code>data_dir_mode</code></td><td><code>mac_profile</code></td><td><code>db_size_bytes</code></td><td><code>wal_size_bytes</code></td></tr>
<tr><td><code>db_pragma_settings</code></td><td><code>symbols_by_language</code></td><td><code>process</code></td><td><code>last_index_run</code></td></tr>
<tr><td><code>hooks</code></td><td><code>stale_after_seconds</code></td><td><code>index_age_seconds</code></td><td><code>degraded_reason</code></td></tr>
<tr><td><code>recommended_action</code></td><td><code>alternative_action</code></td><td><code>mcp_session</code></td><td><code>extractors</code></td></tr>
</tbody>
</table>

When any readiness field is degraded, `degraded_root_cause` identifies the primary stable code and `readiness_degradations[]` lists every degraded field with `root_cause`, human `degraded_reason`, `recommended_action`, and `alternative_action`. `issues_table_available` reports physical table presence; use `file_issues_data_current` to decide whether `file_issues` rows are current for the index generation.

After a current full-repository scan, `unknown_extension_file_count` reports how many skipped files had unmapped non-empty extensions, while `unknown_extension_files` lists up to `unknown_extension_file_path_limit` paths and `unknown_extension_files_truncated` marks when more paths exist.

`extractors` reports runtime extractor plugin and pattern-config diagnostics, including loaded counts, skipped file counts, and a bounded diagnostics list for load failures.

`hooks[]` includes `callback_budget_ms`. `CDIDX_HOOK_CALLBACK_BUDGET_MS` bounds each post-extraction hook callback in milliseconds (default: 5000); callbacks that exceed the budget emit index warnings, drop timed-out mutations, and disable that hook for the current index run.

For MCP `status`, `mcp_session` is session-scoped diagnostic data rather than persisted index state. It includes `log_level`, `roots`, optional `client_info`, and optional `client_capabilities`.

`process` is captured at status-call time and includes heap, GC collection, and working-set counters. `last_index_run` is persisted by successful CLI and MCP index runs with the run mode, duration, file counts, byte count, row-change counts, and optional peak-memory summary from CLI `--memory-trace`.

`hotspot_family_degraded_reason` uses these values:

| Value | Meaning |
|---|---|
| `hotspot_family_support_not_indexed` | Legacy DB without hotspot-family support. |
| `hotspot_family_metadata_stale` | Hotspot-family metadata is stale. |
| `hotspot_family_disabled_at_index_time` | The index was written while marker fingerprints were unavailable. |
| `partial_family_key_population` | Some indexed symbols still lack hotspot-family keys. |
| `hotspot_family_marker_fingerprint_incomplete` | Marker fingerprint traversal hit safety caps during the last index run, so hotspot-family metadata is intentionally incomplete. |

Hotspot-family readiness is tracked by per-language
`hotspot_family_version_<lang>` metadata introduced with hotspot-family contract
version 2. When this readiness is degraded, use
`cdidx index <projectPath> --rebuild` so unchanged rows are restamped too. For
`hotspot_family_marker_fingerprint_incomplete`, first narrow or ignore generated
or vendor trees that contain excessive project markers, then run the rebuild.

## Documentation

| Document | Contents |
|---|---|
| [User Guide](USER_GUIDE.md) | Detailed installation, command examples, options, supported languages, MCP setup, and troubleshooting. |
| [Distribution Channels](DISTRIBUTION.md) | Install channel comparison, update paths, platform support, and package maintainer policy. |
| [Cloud Bootstrap](CLOUD_BOOTSTRAP_PROMPT.md) | Install guidance for restricted cloud agent sessions. |
| [Platform Support](docs/platform-support.md) | Official release asset RIDs, unsupported platforms, and source-build alternatives. |
| [Developer Guide](DEVELOPER_GUIDE.md) | Architecture, implementation notes, release workflow, and the [`reference_kind` filtering matrix](DEVELOPER_GUIDE.md#reference-kind-filtering-matrix) for `callers` / `impact` / `deps` count reconciliation. |
| [Testing Guide](TESTING_GUIDE.md) | Test conventions and validation commands. |
| [Agent Guide](AGENT_GUIDE.md) | Shared agent entry point, workflow index, search policy, and status contract maintenance rules. |
| [Claude Code Entry Point](CLAUDE.md) | Thin Claude Code entry point that redirects to the shared agent guide. |
| [Self-Improvement Contract](SELF_IMPROVEMENT.md) | Rules for agents improving CodeIndex itself. |
| [Integration Policy](INTEGRATION_POLICY.md) | Permitted CLI, JSON, MCP, and integration use. |
| [Security Policy](SECURITY.md) | Private vulnerability reporting and coordinated disclosure policy. |
| [Code of Conduct](CODE_OF_CONDUCT.md) | Community standards and reporting expectations. |

## Supported Surfaces

`cdidx` is a **CLI and MCP server** only. The supported, versioned surfaces are
the `cdidx` CLI (including its `--json` output) and the `cdidx mcp` JSON-RPC
interface. There is no public library / SDK API: the `cdidx` NuGet package is
published as a .NET global tool (`PackAsTool=true`), and `public` types on the
assembly are implementation details that may change without notice. See
[INTEGRATION_POLICY.md — API Surface and Library Use](INTEGRATION_POLICY.md#api-surface-and-library-use).

## Verifying releases

Every GitHub release ships the following supply-chain artifacts next to
the per-platform `CodeIndex-<rid>.tar.gz` / `.zip` binaries:

| Asset | Purpose |
|---|---|
| `sha256sums.txt` | SHA-256 of every release asset (including the SBOM). `install.sh` uses it to verify the downloaded tarball before placing anything under `$HOME/.local/bin/`. |
| `cdidx.sbom.cdx.json` | CycloneDX 1.x JSON Software Bill of Materials covering every NuGet dependency (including the bundled `SQLitePCLRaw` native asset) so compliance reviewers (SOC2, FedRAMP-style) and scanners (Snyk, Trivy, Grype) can audit transitive dependencies without re-deriving them from `.deps.json`. |

Windows ZIP releases contain an Authenticode-signed `cdidx.exe`. After
extracting the archive on Windows, verify the signature and timestamp before
trusting the executable:

```powershell
Get-AuthenticodeSignature .\cdidx.exe | Format-List Status,SignerCertificate,TimeStamperCertificate
```

Quick check after downloading both files from the release page:

```bash
sha256sum --check sha256sums.txt --ignore-missing
jq '.metadata.component.name, .components | length' cdidx.sbom.cdx.json
```

## License and Fair Source Use

CodeIndex and official `cdidx` binaries are source-available / Fair Source-style software
under [FSL-1.1-ALv2](LICENSE), unless a specific file or directory says
otherwise. Integration materials may be [Apache-2.0](LICENSES/Apache-2.0.txt)
where marked.

See [COMMERCIAL_LICENSE.md](COMMERCIAL_LICENSE.md), [INTEGRATION_POLICY.md](INTEGRATION_POLICY.md),
and [TRADEMARKS.md](TRADEMARKS.md) for commercial, integration, and naming
details.

# cdidx（日本語）

[![Build and Test](https://github.com/Widthdom/CodeIndex/actions/workflows/dotnet.yml/badge.svg)](https://github.com/Widthdom/CodeIndex/actions/workflows/dotnet.yml)
[![CodeQL](https://github.com/Widthdom/CodeIndex/actions/workflows/codeql.yml/badge.svg)](https://github.com/Widthdom/CodeIndex/actions/workflows/codeql.yml)
[![Release](https://github.com/Widthdom/CodeIndex/actions/workflows/release.yml/badge.svg)](https://github.com/Widthdom/CodeIndex/actions/workflows/release.yml)

![.NET 8.x / 9.x tests](https://img.shields.io/badge/.NET-8.x%20%2F%209.x%20tests-512BD4?logo=dotnet&logoColor=white)
![C#](https://img.shields.io/badge/C%23-12-239120?logo=csharp&logoColor=white)
![Platform](https://img.shields.io/badge/Platform-Windows%20%7C%20macOS%20%7C%20Linux-lightgrey)
![License](https://img.shields.io/badge/License-FSL--1.1--ALv2-orange)
![SQLite](https://img.shields.io/badge/SQLite-FTS5-003B57?logo=sqlite&logoColor=white)

**ローカルリポジトリ向けの CLI コードインデックス、MCP 検索、LSP editor lookup です。**

`cdidx` はコマンドラインのコードインデクサー、MCP サーバー、read-only LSP shim で、
リポジトリのローカル SQLite index を作成します。人間、AI エージェント、
LSP-native editor は、同じツリーを何度も読み直さずに、高速な全文検索、
シンボル、依存関係、inspect クエリを実行できます。

## なぜ cdidx なのか

> **一度インデックスして、何度も聞く。** `cdidx` はリポジトリをローカルな
> retrieval runtime に変え、人間と AI エージェントが毎回広いファイルを
> 読み直さずに、必要なコード文脈だけを取り出せるようにします。

| ワークフロー | 向いているもの | 理由 |
|---|---|---|
| 1回限りの文字列探し | `rg` | セットアップ不要で直接ファイルを読む |
| 同じリポジトリの反復調査 | `cdidx` | SQLite FTS5 のローカル index、構造化結果、差分更新 |
| VS Code 内だけの chat 文脈 | VS Code workspace index | Copilot / VS Code UX 内で editor が管理 |
| ターミナル、CI、スクリプト、MCP client | `cdidx` | IDE 外でも使える明示的な CLI + MCP 境界 |

詳細: [なぜ cdidx なのか](USER_GUIDE.md#なぜ-cdidx-なのか)、[rg との違い](USER_GUIDE.md#rg-との違い)、
[VS Code workspace index との違い](USER_GUIDE.md#vs-code-workspace-index-との違い)。

## 設計上の境界

CodeIndex は、人間のユーザーと AI エージェント向けの local-first code index and retrieval backend です。責務は、コードベースを軽量・決定的・ローカルに索引化し、全文検索、シンボル検索、参照検索、依存関係、excerpt、inspect、map、MCP などの read-only な retrieval surface を通じて、呼び出し側が必要とするコード文脈を返すことです。

CodeIndex は AI editor、coding agent、chat application ではありません。会話、編集、コミット、pull request、自律的な変更判断は、cdidx を呼び出す外部 agent または editor の責務です。Symbol extraction / reference extraction は、速度、ローカル完結、説明可能性、retrieval の有用性に集中するための lightweight indexing hints です。compiler-accurate semantic analysis、full type resolution、exact call graph を目的とするものではありません。

Embedding、vector search、LLM-based semantic ranking は CodeIndex core の前提機能ではありません。将来的に扱う場合も、core に組み込むのではなく、local index の上に置く optional extension または separate layer として位置づけます。

## すぐに試す

```bash
# インストールは通常数秒で終わります。Homebrew は macOS/Linux で利用できます。
brew install widthdom/tap/codeindex

# または NuGet から .NET global tool としてインストールできます。
dotnet tool install -g cdidx

# または署名付き GitHub release asset から直接インストールできます。
curl -fsSL https://raw.githubusercontent.com/Widthdom/CodeIndex/main/install.sh | bash

# 初回 index は小規模 repo で約30-60秒、100kファイル級では数分以上かかることがあります。
# 実行中のファイル別ステータスを見たい場合は --verbose を付けてください。
cdidx .
cdidx status --check --json
cdidx search "handleRequest"
cdidx definition UserService
cdidx search "Handle" --project MyApp
cdidx search "File.ReadAllText" --exact-substring --reject-before "Length" --guard-window 8
cdidx validate
cdidx mcp
cdidx lsp --db .cdidx/codeindex.db
```

カスタム言語の開発ループは out-of-tree で回せます。拡張子 alias は
`.cdidx-langmap.yaml`、regex シンボルパターンは `.cdidx/patterns/*.yaml` に置き、
`cdidx test-extractor --language <lang> --file <path> --json` で full index を作らずに
extractor fixture を確認できます。`test-extractor` の source と `--expect-symbols`
ファイルはそれぞれ 4 MiB に制限されます。pattern sidecar は symlink ではない pattern directory
配下の通常ファイルだけが対象で、size / count は file 単位と process 単位で制限され、
regex match には timeout が付きます。詳細は
[Custom Language Extraction](DEVELOPER_GUIDE.md#custom-language-extraction) を参照してください。

初回実行後は、次の見方と追加コマンドをよく使います。

| 状況 | 見るもの / 使うもの |
|---|---|
| index の進捗 | `Scanning...`、`Indexing...`、`67.0% [28/42]` のような進捗行と、`2.4s` や `5m 42s` のような単位付き経過時間が表示されます。 |
| 編集後やブランチ切り替え後 | 再構築ではなく `--files`、`--commits`、`--changed-between <old-ref> <new-ref>` で差分更新します。詳細は [クイックスタート](USER_GUIDE.md#クイックスタート) と [インクリメンタル更新の信頼性](USER_GUIDE.md#インクリメンタル更新の信頼性) を参照してください。 |
| 意図的な再構築 | interactive terminal では既存 DB 削除前に確認を求めます。script / CI では `--yes` または `--force` が必要です。 |
| 長期間使っている DB の compact | `cdidx optimize` または `cdidx index <projectPath> --optimize` で FTS5 segment をすぐに compact できます。差分更新中も必要に応じて自動 optimize します。 |
| 病的な generated file | 1 ファイルが過剰な symbol を出す場合、`--max-symbols-per-file <n>` は file content / symbols / references を保存せず、監査用の `symbol_count_exceeded` issue を残します。 |
| 保守作業の rollback | risky な DB 保守の前に `cdidx db checkpoint <name>`、戻す場合は `cdidx db restore <name>` を使います。`backfill-fold` は `--no-checkpoint` を渡さない限り自動 checkpoint を作成し、中断された folded-key rewrite は残り行から再開します。 |
| 権限や I/O の scan error | `cdidx` は scan error を記録し、他のディレクトリの走査を続けます。同じ HEAD の再実行では `.cdidx/scan-checkpoint.json` により成功済みディレクトリを読み飛ばせます。 |

出力を整える option:

| 目的 | option / 動作 |
|---|---|
| POSIX の persistent stderr log を owner-only にする | global tool stderr log は開くたびに `0600` 権限へ補正され、既存の日付付き log file も同じ扱いになります。`--log-format text|json`、`--log-retain-count <N>`、`--log-max-size-mb <N>`、`CDIDX_LOG_*`、`CDIDX_GLOBAL_TOOL_LOG_MAX_BYTES` で JSONL-friendly な lifecycle log と rotation を設定できます。log size cap は 1024 MiB / 1 GiB までです。 |
| checked-in configuration | repository 既定値には `.cdidx/config.json` または `.cdidxrc.json` を使えます。例: `search.limit`、`search.snippet_lines`、`search.max_line_width`。設定 JSON はスキーマ検証前にサイズとネスト深度の上限で検査され、string array は環境変数展開前に件数と要素長の上限で検査され、config 由来の出力 path は workspace 内に制限されます。検出された file は `cdidx validate-config` で検証でき、`cdidx config show` で優先順位を確認できます。 |
| workspaces | monorepo member は `cdidx.workspace.json` または `.cdidx-workspace.json` で宣言し、`cdidx workspace list` で確認できます。`cdidx workspace use <name>` / `cdidx workspace current` は永続 active workspace を扱います。 |
| ASCII-only 端末で崩れない表示にする | `--ascii`、`CDIDX_ASCII=1`、`NO_UNICODE`、`TERM=dumb`、accessibility 系の環境変数、非 UTF-8 locale を使います。スピナーは pipe、slash、dash、backslash の frame、進捗バーは `#` / `-` になり、幅が非常に狭い端末では percentage-only になります。`--no-progress`、`CDIDX_DISABLE_PROGRESS=1`、`PREFERS_REDUCED_MOTION` を使うと animation なしの静的な進捗表示になります。 |
| color と端末 capability | `--color auto` は対応する interactive terminal でだけ ANSI を出力します。`TERM=dumb`、`CI=true`、Unix で端末 hint が無い場合、`NO_COLOR`、`CLICOLOR=0` では ANSI / progress 制御シーケンスを抑止します。`--palette basic|256|truecolor` で `COLORTERM` / `TERM` による color-depth 判定を上書きできます。 |
| UTF-8 JSON pipeline | CLI の `--json` 出力は BOM なし UTF-8 で書き出され、human output 向けに色を強制していても ANSI escape sequence を含みません。 |
| script 向け query pipeline の stderr を静かにする | `--quiet`、`-q`、`--silent`、`CDIDX_QUIET=1` で informational stderr を抑制し、error 行だけを残します。`--quiet` は `--verbose` より優先されます。 |

`cdidx` の upgrade / downgrade をまたぐ database 互換性については
[COMPATIBILITY.md](COMPATIBILITY.md) を参照してください。

ターミナル、スクリプト、CI、AI ツールから同じリポジトリを繰り返し検索する
場合は `cdidx` が向いています。1回限りのテキスト検索には `rg` が向いています。

help の探し方:

| 目的 | コマンド |
|---|---|
| 短いコマンド概要 | `cdidx --help` |
| 全コマンド、flag、例の完全版 | `cdidx --help-all` または `cdidx --help-extended` |
| 共有 flag だけの一覧 | `cdidx --help-flags` |
| 1 コマンドの usage 行 | `cdidx <command> --help` |

install security: release tarball は引き続き `sha256sums.txt` と照合され、
installer は `sha256sums.txt.asc` も取得して GnuPG がある場合は
`gpg --verify` を実行します。署名検証できない場合に fail closed したい
ときは `CDIDX_STRICT_VERIFY=1` または `--strict-verify` を使ってください。
期待する release signing key を固定するには
`CDIDX_RELEASE_GPG_FINGERPRINT=<fingerprint>` を設定します。strict mode では、
GPG 検証が成功した後にこの fingerprint の設定も必須です。

### Validate

`cdidx validate [--db <path>] [--json] [--verbose] [--kind <kind>] [--path <glob>]`
は、index 済みファイルの replacement character (`U+FFFD`)、BOM、NUL byte、
混在改行、UTF-16 BOM、非 UTF-8 らしい内容などを報告します。validation finding は
出力で報告され、それ自体では command failure になりません。DB を読めない場合や
引数が不正な場合は non-zero で終了します。機械処理には `--json` を使えます。

### シェル補完

`cdidx --completions <bash|zsh|fish|powershell>` で補完スクリプトを生成できます。
生成されたスクリプトは subcommand、flag、よく使う flag 値を補完します。
`--lang` は対応言語、`--kind` は symbol / reference kind を提示し、`--db`、
`--path`、`--output` など path 系 option は shell の file completion を使います。
生成された script には生成元の `cdidx` version が含まれるため、`cdidx` の
upgrade / downgrade 後はインストール済み補完 script を再生成してください。

## 特長

| 分野 | 内容 |
|---|---|
| 検索面 | CLI-first の人間向け / 機械処理向け出力。全文検索、シンボル、参照、caller/callee、依存関係、map、inspect、excerpt コマンドを提供します。`search`、`definition`、`references`、`callers`、`callees`、`find`、`validate` は `--format count|compact|csv|tsv|lsp|qf|sarif` をサポートし、token-budgeted agent、script、editor、CI report でも使いやすい出力にできます。`cdidx lsp --db .cdidx/codeindex.db` は LSP-native editor 向けの read-only stdio Language Server Protocol shim を起動します。 |
| validation 診断 | `validate --json` と MCP `validate` は `replacement_char` 行に `origin` (`source_literal` / `decode_replacement`) と `severity` を付け、意図的な U+FFFD literal とエンコーディング破損の可能性を agent が分離できるようにします。 |
| definition / impact 診断 | `definition --json` は C# overload、partial type、extension receiver を区別できる場合に `disambiguator` を返します。`impact --json` と MCP `impact_analysis` は 0 件時の経路判断用に `impact_failure_chain` と `suggestion_type` を返し、`impact --strict` は解決または graph の前提条件が満たされない場合に非 0 で終了します。 |
| 順位と filter | public/exported なシンボル一致を protected、internal、private より優先します。従来順は `--no-visibility-rank`、可視性の include / exclude は `symbols`、`definition`、`unused`、`hotspots` の `--visibility` / `--exclude-visibility` で指定できます。query 既定値は `CDIDX_DEFAULT_LIMIT`、`CDIDX_DEFAULT_SNIPPET_LINES`、`CDIDX_DEFAULT_MAX_LINE_WIDTH` で調整でき、明示 CLI flag が常に優先されます。 |
| project scope | `.sln` / `.csproj` を使った <code>--project &lt;name&#124;path&gt;</code> filter で index と query を .NET project 配下へ絞り込めます。workspace に solution が複数ある場合は `--solution <path>` を指定します。 |
| MCP/LSP 連携 | Claude Code、Cursor、Windsurf などの AI クライアント向け MCP server。tools、インデックス済みファイル resources、starter prompts、ローカル引数検証用の schema constraints、text content block の `mimeType`、logging、構造化された `ping` health result、HTTP `GET /healthz`、opt-in の HTTP `/events` keep-alive notification、stdio または HTTP `/events` stream 上の互換性用 server-side `notifications/initialized` ready signal、`cdidx languages` と同じ言語レジストリ由来の `Language support:` 説明を提供します。Tool schema は未知の引数を `-32602` で拒否し、`x-stability` を公開し、CLI JSON contract と一致する snake_case の structured JSON key を使います。LSP mode は MCP 非対応 editor 向けに `initialize`、`workspace/symbol`、`textDocument/documentSymbol`、`textDocument/definition`、`textDocument/references` を stdio で公開します。 |
| freshness | `--parallelism` による parallel full-scan、`--files` / `--commits` による差分更新、`--watch` による継続更新、`status --check` による完全一致確認、`--stale-after` / `CDIDX_STALE_AFTER` による age threshold 上書きに対応します。 |
| storage | `.cdidx/codeindex.db` に保存する local-first 設計。ネストしたディレクトリからの query コマンドは、current directory にフォールバックする前に最上位祖先の `.cdidx/codeindex.db` を優先します。既定の SQLite 保存先は `--data-dir <dir>`、`CDIDX_DATA_DIR`、`XDG_DATA_HOME` で workspace 外へ移せます。明示的な `--db <path>` は引き続き最優先です。 |
| DB maintenance | 新規 index DB は SQLite incremental auto-vacuum を使います。成功した writer 実行は WAL を `TRUNCATE` checkpoint します。既存 DB は `cdidx vacuum` で free page を回収でき、legacy no-autovacuum DB は初回だけ full `VACUUM` で変換します。`cdidx db schema` は on-disk schema を出力し、`cdidx db prune --dry-run\|--apply` は orphaned DB rows を検査・削除します。`status --json` は `db_pragma_settings` 配下に metrics を出力します。 |
| security defaults | POSIX では `.cdidx` を `0700` 権限で作成し、lifecycle log、metrics log、MCP audit log、query trace log は作成時点から owner read/write のみで作成します。metrics log と audit log は bounded slot へ rotation し、query trace log は bounded な保持件数へ pruning します。`status --json` は利用可能な場合に実効 POSIX mode を `data_dir_mode` として報告します。 |
| diagnostics | `doctor` はバグ報告向けの redacted environment summary を出力します。`status --config` は source attribution 付きの effective configuration を出力し、`status --explain <field>` は readiness field の意味と対処を説明します。read 系コマンドは `--profile`、`--slow-query-ms <n>`、<code>--trace=stderr&#124;file&#124;none</code> に対応し、file trace は lifecycle log と同じ場所に日次 `query-trace-YYYYMMDD.jsonl` を書き、最新30件の trace file を保持します。 |
| query exit codes | 有効な 0 件 query は既定で exit code `0` です。script で 0 件を exit code `2` として扱いたい場合は `--strict-not-found` を使います。 |
| drift checks | `cdidx diff <db1> <db2>` は schema、file、symbol、reference の差分を比較します。exit code は `0` identical、`1` drift、`2` schema mismatch、`3` unreadable DB です。 |
| extensibility / feedback | `~/.config/cdidx/hooks/*.dll` または `CDIDX_HOOKS_DIR` の post-extraction hook で永続化前のシンボルと参照を拡張できます。`cdidx suggestions` はローカル提案履歴の一覧表示、詳細表示、open issue 重複 preflight 付き issue draft JSON エクスポートに対応し、MCP 提案の近似重複排除しきい値は CLI、env、`.cdidxrc.json` で調整できます。 |
| language coverage | 78 言語を検出し、対応言語ではシンボルとグラフも利用可能です。 |
| updates | `cdidx --version` は GitHub releases を 1 日 1 回まで確認し、新しいリリースがある場合にヒントを追記します。明示的に確認するには `cdidx --check-updates` または `cdidx status --check-updates` を使い、最新 GitHub release を `install.sh` 経由で再インストールするには `cdidx upgrade` を使います。確認を抑止するには `CDIDX_DISABLE_UPDATE_CHECK=1` を設定します。 |

### アップグレードとアンインストール

`cdidx upgrade --check-only` は新しい GitHub release の有無だけを報告します。
`cdidx upgrade` は解決済み release tag から `install.sh` を取得し、現在の binary
directory が書き込み可能か確認したうえで、`CDIDX_INSTALL_DIR` をその directory に
向けて installer を再実行します。

直接 `install.sh` で入れたものは次のコマンドで削除できます。

```bash
bash ./install.sh --uninstall
bash ./install.sh --uninstall --purge-cache
```

uninstaller は `cdidx` binary の隣に置いたファイルを削除し、任意で `~/.cache/cdidx`
も削除できます。project の `.cdidx/` directory、shell profile の PATH 追記、
shell completion script、Homebrew install、.NET global-tool install は削除しません。

文書化された `status --json` trust contract は次の field を対象にします。

<table>
<tbody>
<tr><td><code>fold_ready</code></td><td><code>fold_ready_reason</code></td><td><code>graph_table_available</code></td><td><code>issues_table_available</code></td></tr>
<tr><td><code>file_issues_data_current</code></td><td><code>migration_in_progress</code></td><td><code>degraded_root_cause</code></td><td><code>readiness_degradations</code></td></tr>
<tr><td><code>sql_graph_contract_ready</code></td><td><code>sql_graph_contract_degraded_reason</code></td><td><code>hotspot_family_ready</code></td><td><code>hotspot_family_degraded_reason</code></td></tr>
<tr><td><code>language_readiness</code></td><td><code>csharp_symbol_name_ready</code></td><td><code>csharp_metadata_target_ready</code></td><td><code>csharp_metadata_target_degraded_reason</code></td></tr>
<tr><td><code>indexed_head_commit</code></td><td><code>worktree_head_changed</code></td><td><code>indexed_head_sha</code></td><td><code>indexed_head_branch</code></td></tr>
<tr><td><code>indexed_head_timestamp</code></td><td><code>commits_ahead_of_indexed_head</code></td><td><code>index_writer_version</code></td><td><code>index_newer_than_reader</code></td></tr>
<tr><td><code>index_newer_than_reader_reason</code></td><td><code>unknown_extension_file_count</code></td><td><code>unknown_extension_files</code></td><td><code>unknown_extension_files_truncated</code></td></tr>
<tr><td><code>unknown_extension_file_path_limit</code></td><td><code>path_case_sensitive</code></td><td><code>data_dir</code></td><td><code>data_dir_source</code></td></tr>
<tr><td><code>data_dir_mode</code></td><td><code>mac_profile</code></td><td><code>db_size_bytes</code></td><td><code>wal_size_bytes</code></td></tr>
<tr><td><code>db_pragma_settings</code></td><td><code>symbols_by_language</code></td><td><code>process</code></td><td><code>last_index_run</code></td></tr>
<tr><td><code>hooks</code></td><td><code>stale_after_seconds</code></td><td><code>index_age_seconds</code></td><td><code>degraded_reason</code></td></tr>
<tr><td><code>recommended_action</code></td><td><code>alternative_action</code></td><td><code>mcp_session</code></td><td><code>extractors</code></td></tr>
</tbody>
</table>

readiness field のいずれかが degraded の場合、`degraded_root_cause` は primary の安定コードを示し、`readiness_degradations[]` は degraded な各 field と `root_cause`、人間向け `degraded_reason`、`recommended_action`、`alternative_action` を列挙します。`issues_table_available` は物理 table の有無を表し、`file_issues` 行が現在の index generation に対して current かどうかは `file_issues_data_current` を使って判定します。

現行の全体 scan 後、`unknown_extension_file_count` は未知の非空拡張子で skip された件数を返し、`unknown_extension_files` は `unknown_extension_file_path_limit` 件までの path sample、`unknown_extension_files_truncated` は sample より多くの path があることを示します。

`extractors` は extractor plugin と pattern config の runtime 診断で、読み込み済み件数、skip されたファイル数、読み込み失敗の上限付き diagnostics list を含みます。

`hooks[]` は `callback_budget_ms` を含みます。`CDIDX_HOOK_CALLBACK_BUDGET_MS` は post-extraction hook callback ごとの上限ミリ秒を指定します（既定値: 5000）。上限を超えた callback は index warning を出し、timeout した変更を捨て、その index run 中は該当 hook を無効化します。

MCP `status` の `mcp_session` は永続化された index 状態ではなく、セッション単位の診断情報です。`log_level`、`roots`、任意の `client_info`、任意の `client_capabilities` を含みます。

`process` は status 呼び出し時点の heap、GC collection、working-set counters です。`last_index_run` は成功した CLI / MCP index 実行が永続化し、run mode、duration、file counts、byte count、row-change counts、CLI `--memory-trace` 由来の任意の peak-memory summary を含みます。

`hotspot_family_degraded_reason` は次の値を使います。

| 値 | 意味 |
|---|---|
| `hotspot_family_support_not_indexed` | hotspot-family 未対応の legacy DB。 |
| `hotspot_family_metadata_stale` | hotspot-family metadata が古い状態。 |
| `hotspot_family_disabled_at_index_time` | marker fingerprint が利用できない状態で書かれた index。 |
| `partial_family_key_population` | 一部の indexed symbol に hotspot-family key が未設定の状態。 |
| `hotspot_family_marker_fingerprint_incomplete` | 前回の index で marker fingerprint traversal が安全上限に到達したため、hotspot-family metadata が意図的に incomplete として扱われています。 |

hotspot-family readiness は、hotspot-family contract version 2 で導入された
言語別 `hotspot_family_version_<lang>` metadata で追跡されます。degraded の場合は
unchanged row も restamp するため `cdidx index <projectPath> --rebuild` を使います。
`hotspot_family_marker_fingerprint_incomplete` の場合は、過剰な project marker を含む
generated/vendor tree を先に絞り込むか ignore してから rebuild してください。

## ドキュメント

| ドキュメント | 内容 |
|---|---|
| [ユーザーガイド](USER_GUIDE.md#cdidx日本語) | 詳細なインストール、コマンド例、オプション、対応言語、MCP 設定、トラブルシュート。 |
| [配布チャネル](DISTRIBUTION.md) | install channel の比較、update path、platform support、package maintainer policy。 |
| [クラウドブートストラップ](CLOUD_BOOTSTRAP_PROMPT.md#日本語) | 制限されたクラウドエージェント環境でのインストール手順。 |
| [プラットフォームサポート](docs/platform-support.md#プラットフォームサポート) | 公式リリースアセットの RID、未対応 platform、source build の代替手段。 |
| [開発者ガイド](DEVELOPER_GUIDE.md#開発者ガイド) | アーキテクチャ、実装メモ、リリース手順、`callers` / `impact` / `deps` の件数差を照合する [`reference_kind` フィルタの対応表](DEVELOPER_GUIDE.md#reference-kind-filtering-matrix)。 |
| [テストガイド](TESTING_GUIDE.md#テストガイド) | テスト規約と検証コマンド。 |
| [エージェントガイド](AGENT_GUIDE.md) | 共有エージェント入口、workflow index、検索ポリシー、status contract の保守ルール。 |
| [Claude Code 入口](CLAUDE.md) | 共有エージェントガイドへリダイレクトする薄い Claude Code entry point。 |
| [自己改善コントラクト](SELF_IMPROVEMENT.md#自己改善ループ) | CodeIndex 自身を改善するエージェント向けルール。 |
| [統合ポリシー](INTEGRATION_POLICY.md) | CLI、JSON、MCP、各種統合で許可される利用。 |
| [セキュリティポリシー](SECURITY.md) | 非公開の脆弱性報告と協調的開示の方針。 |
| [行動規範](CODE_OF_CONDUCT.md) | コミュニティ標準と報告時の期待事項。 |

## サポート対象の利用面

`cdidx` は **CLI と MCP サーバーとしてのみ** 提供します。バージョニング契約の
対象となるのは、`cdidx` CLI（`--json` 出力を含む）と `cdidx mcp` の JSON-RPC
インターフェースだけです。公開ライブラリ/SDK API は提供していません。`cdidx`
NuGet パッケージは .NET グローバルツールとして公開されており
（`PackAsTool=true`）、アセンブリ上の `public` 型は CLI/MCP の実装上の事情で
公開されているだけの実装詳細で、予告なく変更される可能性があります。詳細は
[INTEGRATION_POLICY.md — API Surface and Library Use](INTEGRATION_POLICY.md#api-surface-and-library-use)
を参照してください。

## リリース成果物の検証

各 GitHub release では、プラットフォーム別の
`CodeIndex-<rid>.tar.gz` / `.zip` バイナリと並んで、サプライチェーン関連の
成果物を同梱しています。

| アセット | 用途 |
|---|---|
| `sha256sums.txt` | 各リリースアセット（SBOM を含む）の SHA-256。`install.sh` は `$HOME/.local/bin/` に何も書き込む前に tarball をこのファイルで検証します。 |
| `cdidx.sbom.cdx.json` | CycloneDX 1.x JSON 形式の Software Bill of Materials。同梱の `SQLitePCLRaw` ネイティブアセットを含む全 NuGet 依存を列挙するため、SOC2 / FedRAMP 系のコンプライアンスレビューや Snyk / Trivy / Grype などのスキャナーが `.deps.json` から再構築せずに推移的依存を監査できます。 |

Windows ZIP release に含まれる `cdidx.exe` は Authenticode 署名済みです。
Windows で archive を展開したあと、実行ファイルを信頼する前に署名と
timestamp を確認してください。

```powershell
Get-AuthenticodeSignature .\cdidx.exe | Format-List Status,SignerCertificate,TimeStamperCertificate
```

リリースページから両ファイルをダウンロードしたあとの簡易チェック例:

```bash
sha256sum --check sha256sums.txt --ignore-missing
jq '.metadata.component.name, .components | length' cdidx.sbom.cdx.json
```

## ライセンスと Fair Source の扱い

CodeIndex と公式 `cdidx` バイナリは、ファイルやディレクトリで別途明記されない
限り [FSL-1.1-ALv2](LICENSE) の source-available / Fair Source-style software
です。統合用の素材は、明記されている場合 [Apache-2.0](LICENSES/Apache-2.0.txt)
で利用できます。

商用利用、統合、名称の扱いについては [COMMERCIAL_LICENSE.md](COMMERCIAL_LICENSE.md)、
[INTEGRATION_POLICY.md](INTEGRATION_POLICY.md)、[TRADEMARKS.md](TRADEMARKS.md) を
参照してください。
