# Self-Improvement Loop

> **[日本語版はこちら / Japanese version](#自己改善ループ)**
>
> **Maintainers / forkers only** — see [MAINTAINERS.md](MAINTAINERS.md). End users don't need this document.

This document is a ready-to-use operating contract for AI agents improving `cdidx` from inside this repository.

Use it when the task is:
- improve `cdidx` itself
- identify missing AI-facing features or branding gaps
- implement non-breaking improvements immediately
- repeat the loop commit by commit while keeping the local index fresh

## Goal

Keep making `cdidx` more useful to:
- AI agents
- human developers using AI tools
- terminal-first users
- MCP-based coding workflows

Treat the loop itself as part implementation loop, part regression test, and part monkey test for the freshly built local binary.

The loop is not just "suggest ideas". It is:
1. inspect the current product with `cdidx`
2. identify the next high-value gap
3. implement it if it is non-breaking
4. verify it
5. commit it
6. rebuild `cdidx`
7. refresh `.cdidx/codeindex.db` with the newly built binary, usually via delta updates first
8. use the refreshed index to guide the next improvement

## Hard Rules

- Create a work branch first. Use a descriptive branch name.
- Keep **one task per commit**.
- Before every commit, explicitly work through the `CLAUDE.md` per-commit checklist.
- Before every commit, review README `# Code Search Rules` and `# コードベース検索ルール`; strengthen them if AI behavior should change.
- After every commit, rebuild `cdidx` from the latest local source and refresh `.cdidx/codeindex.db` using that freshly built binary.
- Prefer the lightest truthful refresh mode: use `--files` only for known in-place edits or new files, use `--commits HEAD` after a normal commit because it tracks renames/deletes from git history, and reserve a full `cdidx . --json` scan for cases where the checkout itself changed broadly or stale files must be purged repo-wide.
- Prefer the **locally built latest binary** (`dotnet ./src/CodeIndex/bin/Debug/net8.0/cdidx.dll`) over an older globally installed `cdidx` whenever the repository code has changed. **Never fall back to a global `cdidx`** — the global version may have an older DB schema, missing query features, or stale extraction logic that silently produces wrong results. This is enforced at the Claude Code harness level via the repo-tracked `.claude/settings.json`, which denies the full set of shell code-search and file-discovery commands: `rg`, `grep`, `egrep`, `fgrep`, `zgrep`, `rgrep`, `ripgrep`, `ag`, `ack`, `ack-grep`, `git grep`, `find`, `locate`, `mlocate`, `mdfind`, and `cdidx`. Use the built-in Grep / Glob tools or the locally built binary instead.
- Treat repo-tracked `.claude/settings.json` and `.claude/hooks/bash-guard.py` as policy files. Do not edit them during ordinary self-improvement work unless the task is explicitly about Claude Code guard behavior. The current guard is deny-oriented: it is intentionally permissive for routine `dotnet`, `git`, `gh`, `codex exec`, `/tmp` work, and read-only shell inspection, while still blocking dangerous shell patterns and sensitive local read paths.
- Respect the current local-privacy boundary in Claude Code: home-directory personal areas and credential-like files are intentionally read-denied by policy. Do not weaken those restrictions as a convenience workaround for self-improvement tasks; if the task genuinely requires changing the guard, treat that as the task itself and update docs accordingly.
- After `git reset`, `git rebase`, `git commit --amend`, `git switch`, or `git merge`, re-index with the **locally built binary** using `dotnet ./src/CodeIndex/bin/Debug/net8.0/cdidx.dll .` (full scan, not `--commits HEAD`) so stale files are purged against the current checkout.
- When searching and navigating code to investigate bugs, plan fixes, or verify changes, always use the **locally built binary** — not the globally installed version. This ensures query results reflect the latest extraction rules and DB schema from this branch.
- If the **locally built binary** crashes, aborts unexpectedly, or exposes a new defect during this loop, do not silently work around it or fall back to an older/global binary. Notify the user with the concrete failure, explain that the self-improvement loop is now blocked or tainted by that defect, and propose fixing it as a separate task or as the next approved priority.
- Treat the loop itself as ongoing regression coverage and light monkey testing. Do not limit yourself to only the safest or most standard workflows; actively exercise recent features, edge features, and less-traveled commands/options so the loop can indirectly surface crashes, bad assumptions, stale help text, and integration defects.
- For long-running human-facing CLI workflows, preserve visible liveness. Interactive terminals should continue to show progress or spinner activity during expensive work, and warnings/progress redraws must not leave the user staring at a stale `Indexing...` line or a silent screen that looks frozen.
- If a change may be breaking, migration-heavy, destructive, or likely to impose manual work on users, **stop and ask for approval before implementing**.
- Respect language differences. Do not pretend every query type is meaningful for every language.
- Respect platform differences. Do not assume Windows, macOS, and Linux behave the same for paths, file locking, process invocation, or cleanup.
- Favor implementation over brainstorming when the next improvement is clear and non-breaking.
- **Dogfooding-driven feature insertion** — During the loop, you may realize "if cdidx had feature X, this search/navigation would be much faster and more accurate." When that happens, insert the feature into your current plan and implement it immediately — as long as it is non-breaking. If the feature risks being a breaking change, note it down and propose it to the user after the current task is complete rather than implementing it silently.
- Keep docs and tests in sync with behavior.
- If test code, shared test helpers, test execution flow, or testing conventions change, update `TESTING_GUIDE.md` in the same commit.
- Do not push tags or branches unless explicitly asked.

## Breaking-Change Gate

You must ask the user before proceeding if the next change would do any of the following:
- change DB layout in a way that could break older indexes or require forced rebuilds
- remove or rename CLI/MCP behavior in a user-visible way
- lower compatibility with older databases without a safe fallback
- require users to change their workflow, config, or prompts manually
- introduce risky migrations or destructive cleanup

If a DB/schema change is necessary, design the read path so newer binaries do not crash on older layouts. Prefer:
- additive columns
- additive tables
- opportunistic migration on open
- safe fallback reads when in-place migration is not available

## Standard Loop

### 1. Create a branch

Create and switch to a descriptive work branch before making changes.

Example:

```bash
git switch -c codex/ai-snippets
```

### 2. Build the current source first

Always compile the current repository version before relying on searches for self-improvement work.

```bash
dotnet build
```

### 3. Refresh the index with the freshly built binary

Use the binary produced from the current commit, not an older global tool, so the database shape and query features match the code you are editing.

Prefer delta refreshes during normal iteration, and use a full scan only when you need a whole-checkout resync.

Choose the one refresh mode that matches your situation:

```bash
# First sync on a checkout, or after history-moving git operations
dotnet ./src/CodeIndex/bin/Debug/net8.0/cdidx.dll . --json

# During local in-place edits or new-file additions, refresh only the files you touched
# Old paths from renames/deletes are NOT purged unless you also pass them explicitly
dotnet ./src/CodeIndex/bin/Debug/net8.0/cdidx.dll index . --files src/CodeIndex/Cli/QueryCommandRunner.cs tests/CodeIndex.Tests/QueryCommandRunnerTests.cs --json

# After a normal commit, prefer git-aware diff refresh because it sees renames/deletes
dotnet ./src/CodeIndex/bin/Debug/net8.0/cdidx.dll index . --commits HEAD --json
```

If you prefer `dotnet run`, that is also acceptable:

```bash
dotnet run --project src/CodeIndex -- . --json
```

### 4. Explore the repo with cdidx itself

Use `cdidx` as your primary navigation tool. Prefer structured and low-token queries first.

Do not only exercise the obvious happy-path commands. Regularly touch newer features and less-common options as well, because this exploration phase also serves as opportunistic regression and monkey testing for the freshly built binary.

If `status --json` reports `fold_ready: false` and you only need Unicode-aware `--exact` semantics on an older DB, prefer upgrading in place with:

```bash
dotnet ./src/CodeIndex/bin/Debug/net8.0/cdidx.dll backfill-fold --json
```

Use a full `cdidx . --json` rebuild only when you need a fresh source scan, when checkout-shaping git operations just happened, or when repo-wide stale-file purge matters more than speed. In the ordinary commit-to-commit loop, prefer `--commits` first, and use `--files` only for in-place edits/new files where no old path needs purging.

Typical sequence:

```bash
dotnet ./src/CodeIndex/bin/Debug/net8.0/cdidx.dll status --json
dotnet ./src/CodeIndex/bin/Debug/net8.0/cdidx.dll map --json --limit 10 --path src/ --exclude-tests
dotnet ./src/CodeIndex/bin/Debug/net8.0/cdidx.dll inspect QueryCommandRunner --exclude-tests --limit 5
dotnet ./src/CodeIndex/bin/Debug/net8.0/cdidx.dll search "AI" --path src/ --exclude-tests --snippet-lines 6 --json
```

Use `inspect` when you already have a likely symbol name. Use `search` for raw text or unsupported languages. Use `map` first when you need orientation.
Also rotate through features that are easy to neglect, such as JSON output variants, path filters, graph queries on supported languages, unsupported-language behavior, and help/usage flows.

### 5. Make a plan before editing

Before implementation, write a concrete plan that covers:
- what gap you found
- why it matters for AI or product adoption
- whether the change is non-breaking
- which files and tests are affected
- how you will verify it
- how language-specific behavior should differ
- how platform-specific behavior should differ, if paths/processes/filesystem semantics are involved

### 6. Implement immediately if safe

If the change is clearly non-breaking and high-value, implement it without waiting for more approval.

Examples:
- better ranking
- better snippets
- improved MCP tool output
- new additive CLI/MCP queries
- better docs/examples/branding
- safer backward-compatible schema additions
- stronger language-aware guards and messaging

### 7. Verify after each implementation

At minimum, do the checks that match the change:
- `dotnet test`
- targeted CLI smoke checks
- MCP behavior checks if MCP was touched
- documentation spot-checks
- language-specific behavior checks if logic differs by language
- platform-sensitive checks if behavior depends on files, paths, processes, console I/O, or SQLite cleanup

If `dotnet test` is blocked specifically by the sandbox rather than by the product or test code, a targeted `dangerouslyDisableSandbox: true` exception is acceptable for `dotnet test` only. Do not generalize that escape hatch to `dotnet build`, `dotnet run`, `gh`, `git`, `codex exec`, Python, or general shell convenience.

Examples:

```bash
dotnet test
dotnet ./src/CodeIndex/bin/Debug/net8.0/cdidx.dll status --json
dotnet ./src/CodeIndex/bin/Debug/net8.0/cdidx.dll map --json --path src/ --exclude-tests
dotnet ./src/CodeIndex/bin/Debug/net8.0/cdidx.dll inspect ResolveGitCommonDir --json --exclude-tests --limit 5
```

### 8. Commit exactly one task

Before committing, explicitly review:
1. Tests
2. TESTING_GUIDE.md
3. CHANGELOG.md — New entries go under `[Unreleased]` only. Never add to or modify an existing versioned heading. See CLAUDE.md "CHANGELOG style" for the full rules.
4. README.md
5. README `# Code Search Rules` / `# コードベース検索ルール`
6. DEVELOPER_GUIDE.md
7. CLAUDE.md
8. This file (`SELF_IMPROVEMENT.md`)
9. PR description, if a PR already exists

Then commit one coherent task.

### 9. Rebuild and refresh again after the commit

This is mandatory. The next round must start from the newest binary and newest DB.

Build first, then choose the lightest refresh mode that keeps the DB truthful:

```bash
dotnet build

# Default post-commit path: refresh from the last commit's diff
# This is safer than --files because git diff sees renames/deletes
dotnet ./src/CodeIndex/bin/Debug/net8.0/cdidx.dll index . --commits HEAD --json

# Escalate to a full scan after git reset/rebase/amend/switch/merge,
# or whenever files outside the last commit may have gone stale
dotnet ./src/CodeIndex/bin/Debug/net8.0/cdidx.dll . --json
```

### 10. Use the refreshed index to choose the next task

After each commit:
- inspect what the new binary can do
- search for newly exposed gaps
- repeat

## Language-Aware Guidance

Do not use one search strategy for every language.

- `references`, `callers`, and `callees` are meaningful only for languages where cdidx intentionally supports regex-based graph extraction.
- For unsupported languages, prefer `search`, `excerpt`, `definition`, or `files` instead of assuming graph data exists.
- Treat C#, Java, Go, Rust, TypeScript/JavaScript, Python, Kotlin, Ruby, C/C++, PHP, and Swift differently from Markdown, YAML, JSON, TOML, Shell, SQL, HTML/CSS, Vue, Svelte, and Terraform.
- When proposing new language-specific features, state clearly which languages are in scope and why.
- When a heuristic is language-specific, document the limitation in README and tests.

## Cross-Language Feature Expansion

When enhancing symbol extraction, reference extraction, or language-specific heuristics for one language, actively check whether the same improvement applies to structurally similar languages — especially C# and Java, which share many syntactic patterns (classes, interfaces, generics, annotations/attributes, access modifiers, method signatures).

- After implementing or refining a C# pattern, review the corresponding Java patterns in `SymbolExtractor.cs` and `ReferenceExtractor.cs` and apply the same improvement where the syntax permits.
- The same principle applies in reverse (Java → C#) and to other language pairs with strong overlap (TypeScript/JavaScript, Kotlin/Java, C/C++).
- Do not force a pattern onto a language where it does not fit. If the syntax diverges enough to make the port unreliable, skip it and document why.
- When the expansion is straightforward, include it in the same commit as the original enhancement so the languages stay in sync. When it requires non-trivial adaptation, make it a separate follow-up commit.

## Design Decision — C# Extraction Ceiling (Issue #384)

Context: the C# extraction layer — both `src/CodeIndex/Indexer/ReferenceExtractor.cs` (call / reference edges) and `src/CodeIndex/Indexer/SymbolExtractor.cs` (symbol rows, for example the #376 modifier-list case where `new interface` was silently dropped) — has accumulated a long tail of single-construct fixes (#200, #220, #233, #253, #257, #264, #274, #288, #291, #293, #301, #330, #338, #342, #343, #344, #349, #355, #356, #357, #361, #362, #363, #364, #367, #372, #374, #375, #376, #382 and siblings). The line-oriented regex layer in both extractors keeps running into the same four failure modes: wrapped declarations, string/comment bleed, nested bracket tracking, and scope attribution. Issue #384 asked for a recorded decision instead of one more per-construct fix.

**Decision: Hybrid, with Option B as the primary direction.** We already maintain a hand-written `StructuralLineMasker` (C# / Python / Rust / JS-TS) and a JS/TS lexer-driven class-body pass inside `SymbolExtractor` — the codebase is already on the Option B path for string/comment framing. The next step is to extend that lightweight scanner for C# (and Java where syntax overlaps) so the regex consumers in `SymbolExtractor` / `ReferenceExtractor` can query scope state instead of re-parsing it ad-hoc.

What the extended scanner must expose per line, at minimum:
- enclosing class / method stack (answers "which container does this line belong to?")
- brace / paren / angle-bracket depth (answers "am I inside an initializer / argument list / type argument list?")
- logical declaration start line (wrapped headers and `[Attribute]`-prefixed declarations collapse to a single anchor)
- carry-over `in_string` / `in_comment` flags from `MaskLines`

**Option A — externalizing per-language regex tables into data files — is deferred, not rejected.** The current pain is driven by cross-construct regex interactions on C# / Java, not by language breadth. Moving tables to data does not remove those interactions; it just makes the same conflicts cheaper to edit.

**Dependency rule:** no exception required. The extension is hand-written, following the precedent set by `StructuralLineMasker` and the JS/TS lexer. No Roslyn, no tree-sitter, no new production NuGet package. If a future bug convinces us that only a real parser is enough, that is a separate breaking-change proposal under the Breaking-Change Gate.

**Exit criteria — issues that should close or become materially simpler once the scanner exposes scope + declaration-start state:**
- Multi-line declarations: #361, #382.
- String / comment bleed beyond the current masker: #264, #274, #288, #291, #301, #363, #372.
- Nested initializer / bracket tracking: #257, #286, #293, #330.
- Scope attribution: #233, #242, #252, #316.

Issues outside that list stay on the ordinary per-bug track; this decision is explicitly not a commitment to refactor the extractor end-to-end in one go.

**Non-goals:**
- A full C# parser. We need enough scanner state to answer "enclosing class/method, bracket depth, inside a string/comment, logical declaration start" — nothing more.
- Simultaneous language rewrite. C# lands first (dogfooding priority per "Priority: C# First"); Java follows only where the syntax overlap makes the port trivial.

**Workflow implication:** the next landing C#/Java reference-extractor fix from the issue list above should be treated as the first real consumer of the extended scanner, so its API is shaped by a concrete fix rather than designed in isolation.

## Platform-Aware Guidance

Do not assume path handling, process cleanup, or file deletion behaves the same on every OS.

- Windows can hold SQLite files longer because of file locking and connection pooling, so cleanup code and tests must tolerate delayed release.
- Path separators, casing assumptions, shell commands, and process launch behavior differ across Windows, macOS, and Linux.
- If you change temp-file handling, DB lifecycle, or CLI process behavior, add verification that is robust across supported platforms.
- If a workaround is OS-specific, document why it exists instead of leaving it as unexplained test fragility.

## Product and Branding Lens

Self-improvement is not limited to search mechanics. Also evaluate:
- positioning versus `rg`, desktop search tools, and IDE search
- first-run clarity
- README opening clarity
- examples that AI agents will actually copy
- MCP discoverability
- names of commands and docs sections
- whether a new user can understand the value in 30 seconds

Good improvements are often:
- fewer round-trips
- lower token output
- clearer defaults
- better trust/freshness signals
- safer compatibility behavior
- sharper product framing

## Priority: C# First (Dogfooding)

cdidx is written in C#. Every improvement to C# analysis directly improves how this loop explores, navigates, and verifies cdidx's own codebase. This is a genuine dogfooding advantage — no other language gives the same feedback density.

When choosing what to improve next, **prioritize C# capabilities first**:

- C# symbol extraction accuracy (properties, events, records, pattern matching, etc.)
- C# reference/call-graph precision (generic methods, extension methods, LINQ, etc.)
- C# ranking quality (namespace awareness, visibility-weighted results, etc.)
- C#-specific MCP/CLI output improvements
- Fixing false positives or negatives that surface while working on the cdidx codebase itself

This does not mean other languages are unimportant. It means:
1. When two improvements have roughly equal value, pick the C# one.
2. When the loop surfaces a C#-specific defect during normal operation, treat it as a high-priority fix rather than a "nice to have."
3. When adding a cross-language feature, ensure the C# path is the most polished.

The reasoning is simple: a sharper C# experience makes the self-improvement loop itself more productive, which accelerates every subsequent improvement — including those for other languages.

## Suggested Prompt

If a user wants to start this loop, the minimal instruction can be:

```markdown
Read `SELF_IMPROVEMENT.md` and start implementing the next non-breaking improvement.
```

If the user wants more direction:

```markdown
Read `SELF_IMPROVEMENT.md`, inspect the current repo with cdidx itself, identify the next high-value non-breaking improvement for AI friendliness or adoption, implement it, verify it, commit exactly one task, rebuild cdidx from the latest commit, refresh `.cdidx/codeindex.db` with `--commits` when possible, use `--files` only for in-place edits/new files, and use a full scan when checkout-changing git operations or stale-file purge require it, then continue from the refreshed index. Ask before any breaking change.
```

---

<a id="自己改善ループ"></a>
# 自己改善ループ

このドキュメントは、このリポジトリの中から `cdidx` 自身を改善していく AI エージェント向けの、そのまま使える運用契約です。

次のようなタスクで使います:
- `cdidx` 自体を改善したい
- AI向け機能やブランディング上の欠けを見つけたい
- 非破壊な改善はすぐ実装したい
- ローカルインデックスを常に新鮮に保ちながら、コミット単位で改善を回したい

## 目的

`cdidx` を次の相手にとって、もっと役立つものにし続けることです:
- AIエージェント
- AIツールを使う人間の開発者
- ターミナル中心のユーザー
- MCPベースのコーディングワークフロー

このループ自体を、実装サイクルであると同時に、ローカルでビルドした最新版バイナリに対するリグレッションテストと軽いモンキーテストの場として扱います。

このループは「アイデアを出すだけ」ではありません。流れは次のとおりです:
1. `cdidx` 自身で現状を観察する
2. 次に改善すべき価値の高いギャップを見つける
3. 非破壊なら実装する
4. 検証する
5. コミットする
6. `cdidx` を再ビルドする
7. その新しいバイナリで `.cdidx/codeindex.db` を更新する（通常は差分更新を優先する）
8. 更新済みインデックスを使って次の改善を決める

## 絶対ルール

- まず作業ブランチを切る。名前は内容が分かるものにする。
- **1案件1コミット** を守る。
- 毎コミット前に、`CLAUDE.md` の「コミットごとのチェックリスト」を明示的に確認する。
- 毎コミット前に、README の `# Code Search Rules` と `# コードベース検索ルール` を見直し、AIの検索行動を変えるべきなら強化する。
- 毎コミット後に、ローカルソースの最新状態から `cdidx` を再ビルドし、その新しいバイナリで `.cdidx/codeindex.db` を更新する。
- 更新モードは「正しさを保てる範囲で最も軽いもの」を優先する。`--files` は把握している in-place 編集や新規追加だけに使い、rename/delete/拡張子変更を含む場合は旧 path も明示的に渡すか、通常どおり `--commits HEAD` を使う。checkout 全体が大きく動いた場合や repo 全体で stale file を掃除したい場合だけ `cdidx . --json` のフルスキャンへ上げる。
- リポジトリのコードを変更した後は、古いグローバルインストール版ではなく **ローカルでビルドした最新版バイナリ** (`dotnet ./src/CodeIndex/bin/Debug/net8.0/cdidx.dll`) を使う。**グローバル版には絶対に戻らないこと** — グローバル版は DB スキーマが古い、クエリ機能が欠けている、抽出ロジックが古くて誤った結果を返す、といった問題が起こりうる。このルールはリポジトリ追跡の `.claude/settings.json` で harness レベルでも強制されており、shell のコード検索・ファイル探索系コマンドを網羅的に deny している（`rg`、`grep`、`egrep`、`fgrep`、`zgrep`、`rgrep`、`ripgrep`、`ag`、`ack`、`ack-grep`、`git grep`、`find`、`locate`、`mlocate`、`mdfind`、`cdidx`）。代わりに組み込みの Grep / Glob ツールかローカルビルド版を使うこと。
- リポジトリ追跡の `.claude/settings.json` と `.claude/hooks/bash-guard.py` はポリシーファイルとして扱う。通常の自己改善作業では編集せず、Claude Code のガード挙動自体を変えるタスクのときだけ触る。現在のガードは deny ベースで、日常的な `dotnet`、`git`、`gh`、`codex exec`、`/tmp` 作業、読み取り中心の shell 確認は止めにくくしつつ、危険な shell パターンと機微なローカル read path は止める設計になっている。
- Claude Code のローカルプライバシー境界を尊重すること。ホーム配下の私物領域や資格情報系ファイルはポリシーで read deny されているため、自己改善タスクを進めやすくする目的でそれを緩めない。もし guard の変更自体が本当に必要なら、それを独立したタスクとして扱い、関連ドキュメントも同じコミットで更新する。
- `git reset`、`git rebase`、`git commit --amend`、`git switch`、`git merge` の後は、**ローカルビルド版** で `dotnet ./src/CodeIndex/bin/Debug/net8.0/cdidx.dll .`（フルスキャン。`--commits HEAD` ではない）を実行し、現在の checkout に対する stale file を掃除する。
- バグ調査、修正計画、変更検証のためにコード検索・ナビゲーションを行うときも、常に **ローカルビルド版** を使う。グローバルインストール版は使わない。これにより、このブランチの最新の抽出ルールと DB スキーマを反映した検索結果が得られる。
- このループ中に **ローカルビルド版** がクラッシュしたり、異常終了したり、新しい不具合を露呈した場合は、黙って回避したり古い版・グローバル版へ逃げたりしないこと。具体的な失敗内容をユーザーに通知し、その不具合によって自己改善ループがブロックされている、または結果の信頼性が損なわれていることを説明したうえで、別タスクまたは次の承認済み優先事項として修正提案を出すこと。
- このループ自体を、継続的なリグレッション確認と軽いモンキーテストとして扱うこと。最も安全で標準的なワークフローだけに偏らず、新しい機能、利用頻度の低い機能、枝葉末節のオプションも積極的に触り、間接的にクラッシュ、古いヘルプ文、想定漏れ、統合不具合をあぶり出すこと。
- 人間向け CLI の長時間処理では、生存表示を維持すること。重い処理中も対話ターミナルには進捗かスピナーが見え続けるべきであり、警告表示や再描画のせいで古い `Indexing...` 行だけが残ったり、固まったように見える無音画面になったりしてはならない。
- 変更が破壊的、移行負荷が高い、危険、またはユーザーに手間を強いる可能性があるなら、**実装前に必ず承認を取る**。
- 言語差分を無視しない。すべての言語で同じ検索が意味を持つと仮定しない。
- 次の改善が明確で非破壊なら、議論だけで止まらず実装を優先する。
- **ドッグフーディング駆動の機能挿入** — ループ中に「cdidx にこの機能があれば、もっと速く正確に検索できるのに」と気付くことがある。その場合、非破壊な変更であれば現在の計画に組み入れてそのまま実装して構わない。破壊的変更のリスクがある場合は、メモしておき、現在のタスク完了後にユーザーに実装可否を打診すること。
- ドキュメントとテストを挙動と同期させる。
- テストコード、共有テストヘルパー、テスト実行フロー、またはテスト規約を変更した場合は、同じコミットで `TESTING_GUIDE.md` も更新する。
- `git push` や `git tag` は、明示的に依頼されたときだけ行う。

## 破壊的変更のゲート

次にやろうとしている変更が以下に当てはまるなら、先にユーザーへ確認してください:
- DBレイアウト変更で古いインデックスを壊す、または強制再構築を招く可能性がある
- CLI/MCPのユーザー向け挙動を削除・改名・互換性低下させる
- 古いDBへの互換性を安全なフォールバックなしに下げる
- ユーザー側にワークフローや設定やプロンプトの手修正を要求する
- 危険な移行や破壊的なクリーンアップを伴う

DB/スキーマ変更が必要な場合は、新しいバイナリが古いレイアウトでクラッシュしない読み取り経路を設計してください。優先すべきなのは:
- 追加カラム
- 追加テーブル
- open 時の機会的移行
- その場移行できない場合の安全なフォールバック読み取り

## 標準ループ

### 1. ブランチを切る

変更前に、内容が分かる作業ブランチを作って切り替えます。

例:

```bash
git switch -c codex/ai-snippets
```

### 2. まず現在のソースをビルドする

自己改善の検索に入る前に、必ず現在のリポジトリ版をコンパイルしてください。

```bash
dotnet build
```

### 3. そのビルド済みバイナリでインデックスを更新する

編集中のコードと DB 形状と検索機能を一致させるため、古いグローバルツールではなく、**現在のコミットからビルドしたバイナリ** を使います。

状況に合う更新モードを1つ選んでください:

```bash
# checkout 直後の初回同期や、履歴を動かす git 操作の直後
dotnet ./src/CodeIndex/bin/Debug/net8.0/cdidx.dll . --json

# 手元の in-place 編集や新規追加だけを反映
# rename/delete の旧 path は、明示しない限り purge されない
dotnet ./src/CodeIndex/bin/Debug/net8.0/cdidx.dll index . --files src/CodeIndex/Cli/QueryCommandRunner.cs tests/CodeIndex.Tests/QueryCommandRunnerTests.cs --json

# 通常のコミット直後は、そのコミット差分だけを反映
# git diff が rename/delete も見られるので、こちらを既定にする
dotnet ./src/CodeIndex/bin/Debug/net8.0/cdidx.dll index . --commits HEAD --json
```

`dotnet run` でも構いません:

```bash
dotnet run --project src/CodeIndex -- . --json
```

### 4. cdidx 自身でリポジトリを観察する

主なナビゲーション手段は `cdidx` にしてください。まずは構造化された、低トークンの問い合わせを優先します。

分かりやすい happy path のコマンドだけをなぞらないこと。ここでの探索は、ビルドしたばかりのバイナリに対する日和見的なリグレッションテスト兼モンキーテストでもあります。

`status --json` が `fold_ready: false` を返し、古い DB に対して Unicode-aware な `--exact` だけを有効化したい場合は、まず次で in-place upgrade することを優先してください:

```bash
dotnet ./src/CodeIndex/bin/Debug/net8.0/cdidx.dll backfill-fold --json
```

ソース再走査も必要なとき、checkout を変える git 操作の直後、または repo 全体で stale file を掃除したいときだけ、`cdidx . --json` のフル更新を使います。通常のコミット間ループでは、rename/delete も追える `--commits` を先に考え、`--files` は in-place 編集や新規追加に限定してください。

典型例:

```bash
dotnet ./src/CodeIndex/bin/Debug/net8.0/cdidx.dll status --json
dotnet ./src/CodeIndex/bin/Debug/net8.0/cdidx.dll map --json --limit 10 --path src/ --exclude-tests
dotnet ./src/CodeIndex/bin/Debug/net8.0/cdidx.dll inspect QueryCommandRunner --exclude-tests --limit 5
dotnet ./src/CodeIndex/bin/Debug/net8.0/cdidx.dll search "AI" --path src/ --exclude-tests --snippet-lines 6 --json
```

候補シンボル名が分かっているなら `inspect`、生テキストや未対応言語なら `search`、全体像が欲しいなら `map` を優先します。
加えて、JSON 出力の別経路、path filter、対応言語での graph クエリ、未対応言語の挙動、help / usage など、見落とされやすい経路も意識的に回してください。

### 5. 編集前に計画を立てる

実装前に、次を含む具体的な計画を立ててください:
- どんなギャップを見つけたか
- それがAIや普及にとってなぜ重要か
- その変更が非破壊かどうか
- 影響ファイルと必要テスト
- どう検証するか
- 言語ごとの差をどう扱うか

### 6. 安全ならすぐ実装する

変更が明らかに非破壊で価値が高いなら、追加承認を待たずに実装します。

例:
- ランキング改善
- スニペット改善
- MCPツール出力改善
- 追加的なCLI/MCPクエリ
- ドキュメント、例、ブランディングの改善
- 後方互換を保ったスキーマ追加
- 言語差分を踏まえたガードやメッセージの改善

### 7. 実装ごとに検証する

変更に応じて、最低限次を実施します:
- `dotnet test`
- CLI のスモーク確認
- MCP を触ったなら MCP 挙動確認
- ドキュメントの spot check
- 言語差分があるなら、言語別の確認

`dotnet test` が製品やテストコードの問題ではなく sandbox そのものに塞がれている場合に限り、`dangerouslyDisableSandbox: true` を `dotnet test` 専用の例外として使ってよい。これを `dotnet build`、`dotnet run`、`gh`、`git`、`codex exec`、Python 実行、一般的な shell 利便性に広げてはいけない。

例:

```bash
dotnet test
dotnet ./src/CodeIndex/bin/Debug/net8.0/cdidx.dll status --json
dotnet ./src/CodeIndex/bin/Debug/net8.0/cdidx.dll map --json --path src/ --exclude-tests
dotnet ./src/CodeIndex/bin/Debug/net8.0/cdidx.dll inspect ResolveGitCommonDir --json --exclude-tests --limit 5
```

### 8. 1案件だけコミットする

コミット前に、明示的に次を確認します:
1. Tests
2. TESTING_GUIDE.md
3. CHANGELOG.md — 新エントリは `[Unreleased]` のみに書く。既存のバージョン見出しには追加も変更もしない。完全なルールは CLAUDE.md「CHANGELOGのスタイル」参照。
4. README.md
5. README `# Code Search Rules` / `# コードベース検索ルール`
6. DEVELOPER_GUIDE.md
7. CLAUDE.md
8. このファイル（`SELF_IMPROVEMENT.md`）
9. 既存PRがあるなら PR説明

そのうえで、1つのまとまりだけをコミットします。

### 9. コミット後にもう一度ビルドして更新する

これは必須です。次のラウンドは、必ず最新バイナリと最新DBから始めてください。

まずビルドし、そのうえで DB の正しさを保てる最も軽い更新モードを選びます:

```bash
dotnet build

# 通常の post-commit 経路: 直前コミットの差分を反映
# git diff が rename/delete も拾えるため、--files より安全
dotnet ./src/CodeIndex/bin/Debug/net8.0/cdidx.dll index . --commits HEAD --json

# git reset/rebase/amend/switch/merge の後や、
# 直前コミット外の stale file が疑われる場合はフル更新
dotnet ./src/CodeIndex/bin/Debug/net8.0/cdidx.dll . --json
```

### 10. 更新済みインデックスから次の案件を選ぶ

各コミット後に:
- 新しいバイナリで何ができるか観察する
- 新たに見えるギャップを探す
- 繰り返す

## 言語差分を前提にする指針

すべての言語で同じ検索戦略を使ってはいけません。

- `references`、`callers`、`callees` は、cdidx が意図的に正規表現ベースのグラフ抽出をサポートしている言語でのみ意味があります。
- 未対応言語では、グラフ結果がある前提で進めず、`search`、`excerpt`、`definition`、`files` を優先します。
- C#、Java、Go、Rust、TypeScript/JavaScript、Python、Kotlin、Ruby、C/C++、PHP、Swift と、Markdown、YAML、JSON、TOML、Shell、SQL、HTML/CSS、Vue、Svelte、Terraform は分けて考えてください。
- 新しい言語依存機能を提案するときは、どの言語を対象にするのか、その理由を明記してください。
- ヒューリスティックが言語依存なら、README とテストに制限事項を残してください。

## 言語横展開の指針

ある言語のシンボル抽出、参照抽出、言語固有ヒューリスティックを強化したとき、構文的に近い言語にも同じ改善が当てはまらないか能動的に確認してください。特に C# と Java は共通の構文パターンが多い（クラス、インターフェース、ジェネリクス、アノテーション/属性、アクセス修飾子、メソッドシグネチャ）ため、横展開しやすい組み合わせです。

- C# 向けのパターンを実装・改良したら、`SymbolExtractor.cs` と `ReferenceExtractor.cs` の対応する Java パターンを確認し、構文が許す範囲で同じ改善を適用してください。
- 逆方向（Java → C#）や、重なりの大きい他の言語ペア（TypeScript/JavaScript、Kotlin/Java、C/C++）にも同じ考え方が当てはまります。
- 構文が十分に異なり、移植すると信頼性が下がる場合は無理をせず、見送った理由を残してください。
- 横展開が素直にできるなら、元の強化と同じコミットに含めて言語間の同期を保ちます。非自明な適応が必要な場合は、別のフォローアップコミットにしてください。

## 設計判断 — C# 抽出の天井（Issue #384）

背景: C# 抽出層は `src/CodeIndex/Indexer/ReferenceExtractor.cs`（call / reference エッジ）と `src/CodeIndex/Indexer/SymbolExtractor.cs`（シンボル行、例えば #376 は interface 正規表現の修飾子リストから `new` が抜けていたため `new interface` が黙って落ちていた事案）の両方を指し、両者合わせて単一構文に対する修正が長く積み上がっている（#200、#220、#233、#253、#257、#264、#274、#288、#291、#293、#301、#330、#338、#342、#343、#344、#349、#355、#356、#357、#361、#362、#363、#364、#367、#372、#374、#375、#376、#382 など）。両抽出器の行指向正規表現層は、常に同じ 4 つの失敗モードにぶつかっている: 折り返し宣言、文字列／コメント混入、ネストした括弧追跡、スコープ帰属。Issue #384 は「これ以上の個別修正ではなく、方針を明文化せよ」という依頼だった。

**結論: ハイブリッド。主軸は Option B とする。** 手書きの `StructuralLineMasker`（C# / Python / Rust / JS-TS 対応）と、`SymbolExtractor` 内の JS/TS lexer ベースのクラス本体処理は、すでに Option B への地ならしになっている。次の一歩は、この軽量 scanner を C#（構文が重なる範囲で Java も）向けに拡張し、`SymbolExtractor` / `ReferenceExtractor` の regex 消費側が scope 状態を毎回再解析するのではなく問い合わせられるようにすることだ。

拡張 scanner が最低限行単位で公開すべき情報:
- 外側の class / method スタック（「この行はどの container に属するか」）
- 波括弧 / 丸括弧 / 山括弧の深さ（「initializer / 引数リスト / 型引数リストの内部か」）
- 論理宣言の開始行（折り返しヘッダーや `[Attribute]` 前置の宣言を単一のアンカーに畳む）
- `MaskLines` から引き継いだ `in_string` / `in_comment` フラグ

**Option A（言語別 regex テーブルをデータファイルへ外出し）は却下ではなく延期する。** 現在の痛みは C# / Java の構文相互作用から来ており、言語の幅から来ていない。テーブルをデータへ移しても相互作用自体は消えず、編集コストが少し下がるだけ。

**依存ルール:** 例外不要。拡張は `StructuralLineMasker` と JS/TS lexer の先例どおり、手書きで済ませる。Roslyn、tree-sitter、新規本番 NuGet パッケージはいずれも使わない。将来「本物の parser が必要」と判断するバグが出てきた場合は、破壊的変更ゲートを通した別提案として扱う。

**終了条件 — scanner が scope + 論理宣言開始状態を公開した時点で、close もしくは大幅に簡素化できる Issue:**
- 折り返し宣言: #361、#382。
- 現行マスカを越える文字列/コメント混入: #264、#274、#288、#291、#301、#363、#372。
- ネスト initializer / 括弧追跡: #257、#286、#293、#330。
- スコープ帰属: #233、#242、#252、#316。

これ以外の Issue は通常のバグ単位の修正トラックに残す。この判断は「抽出器を一度にまるごと書き換える」ことにコミットするものではない点に注意する。

**スコープ外:**
- 完全な C# parser。必要なのは「外側 class/method、括弧深さ、文字列/コメント内部か、論理宣言開始行」を答える程度の scanner 状態であり、それ以上は作らない。
- 言語同時書き換え。C# を先に着地させ（「優先順位: C# を最優先にする」の通り）、Java は構文が重なっていて移植が自明な範囲でのみ後追いする。

**運用への示唆:** 上記 Issue 一覧のうち次に着地する C# / Java reference-extractor 修正を、拡張 scanner の最初の実利用者として扱う。API の形は、机上設計ではなく具体的な修正に引きずられて決める。

## プラットフォーム差分を前提にする指針

パス処理、プロセス後始末、ファイル削除がすべての OS で同じだと考えてはいけません。

- Windows では SQLite の接続プールやファイルロックにより、DB ファイル解放が遅れることがあるため、後片付けコードやテストは遅延解放に耐える必要があります。
- パス区切り、大小文字前提、shell コマンド、プロセス起動挙動は Windows、macOS、Linux で異なります。
- 一時ファイル処理、DB ライフサイクル、CLI プロセス挙動を変える場合は、対応プラットフォーム全体で壊れにくい検証を追加してください。
- OS 固有の回避策を入れる場合は、説明のない不安定テストにせず、なぜ必要かをドキュメントに残してください。

## プロダクトとブランディングの観点

自己改善は検索機能だけに限りません。次も評価対象です:
- `rg`、デスクトップ検索、IDE検索に対する位置づけ
- 初回体験の分かりやすさ
- README 冒頭の伝わりやすさ
- AI が本当にコピペする例になっているか
- MCP の見つけやすさ
- コマンド名やドキュメント見出しの分かりやすさ
- 新規ユーザーが30秒で価値を理解できるか

良い改善は、たいてい次のどれかです:
- 往復回数が減る
- トークン出力が減る
- デフォルトが明快になる
- 信頼性や鮮度のシグナルが増える
- 互換性面が安全になる
- プロダクトの打ち出しが鋭くなる

## 優先順位: C# を最優先にする（ドッグフーディング）

cdidx は C# で書かれています。C# の解析を改善すれば、それがそのまま、このループが cdidx 自身のコードベースを探索・ナビゲート・検証する精度を高めます。これは本物のドッグフーディングの利点です。他のどの言語も同じ密度のフィードバックは返してくれません。

次に何を改善するか選ぶとき、**C# 関連の機能強化を最優先にしてください**:

- C# シンボル抽出の精度（プロパティ、イベント、record、パターンマッチングなど）
- C# の参照・コールグラフ精度（ジェネリックメソッド、拡張メソッド、LINQ など）
- C# のランキング品質（名前空間の考慮、可視性で重み付けした結果など）
- C# 固有の MCP/CLI 出力改善
- cdidx のコードベース自体を触っていて浮上した偽陽性・偽陰性の修正

他の言語が重要でないという意味ではありません。意味するのは:
1. ほぼ同等の価値を持つ改善が2つあるなら、C# 側を選ぶ。
2. ループの通常操作中に C# 固有の不具合が見つかったら、「あると嬉しい」ではなく優先修正として扱う。
3. 言語横断の機能を追加するときは、C# の経路を最も洗練させる。

理由は単純です。C# の体験が鋭くなれば自己改善ループ自体の生産性が上がり、その結果すべての後続改善——他言語向けも含め——が加速します。

## 推奨プロンプト

このループを始めるとき、最小の指示はこれで十分です:

```markdown
Read `SELF_IMPROVEMENT.md` and start implementing the next non-breaking improvement.
```

少し具体化したいなら:

```markdown
Read `SELF_IMPROVEMENT.md`, inspect the current repo with cdidx itself, identify the next high-value non-breaking improvement for AI friendliness or adoption, implement it, verify it, commit exactly one task, rebuild cdidx from the latest commit, refresh `.cdidx/codeindex.db` with `--commits` when possible, use `--files` only for in-place edits/new files, and use a full scan when checkout-changing git operations or stale-file purge require it, then continue from the refreshed index. Ask before any breaking change.
```
