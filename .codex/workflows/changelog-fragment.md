# Changelog Fragment Workflow

This workflow is shared by Codex, Claude Code, and other coding agents.

Use it for ordinary implementation PRs that change user-visible behavior,
CLI/MCP output, install/release behavior, documentation contracts, or agent
workflow behavior.

## Rule

Do not edit `CHANGELOG.md` in ordinary implementation PRs.
Add one or more bilingual fragments under `changelog.d/unreleased/` instead.
Treat any request to "update the changelog" as a fragment request unless the task is explicitly a release-preparation PR that aggregates existing fragments into `CHANGELOG.md`.

## Steps

1. Decide whether the change is user-visible or behavior-changing.
2. If no changelog entry is required, explain why in the PR body.
3. If required, create a fragment named `<issue>.<category>.md`,
   `<issue>-<issue>.<category>.md`, or `+<slug>.<category>.md`.
   - If the work is **not** issue-based, use `+<slug>.<category>.md`.
4. Use one of the allowed categories: `added`, `changed`, `fixed`,
   `deprecated`, `removed`, `security`, `docs`, `internal`.
5. Include required front matter.
   - For issue-based work, `issues` must list one or more issue numbers.
   - For non-issue work, **omit the `issues` field entirely**.
   - Do **not** write `issues: null` or `issues: []` (both fail validation/pipeline checks).
6. Include both `## English` and `## 日本語` sections.
7. Do not include release headings or compare-link footer definitions.
8. Validate fragments before committing.

## Validation

Run the fragment validator before committing any changelog fragment:

```bash
dotnet run --project tools/CodeIndex.Changelog -- check
```

The command validates every `changelog.d/unreleased/*.md` fragment and fails
with file-specific messages. For example, a non-issue fragment that writes
`issues: null` fails with an invalid issue-number error; omit the `issues`
field entirely instead.

The validator and release preparation tool reject oversized inputs before
parsing: at most 512 unreleased fragments, each fragment at most 131072 bytes,
`CHANGELOG.md` at most 8388608 bytes, and `version.json` at most 16384 bytes.

## Template

```md
---
category: fixed
issues:
  - 960
affected:
  - src/CodeIndex/Cli/QueryCommandRunner.cs
  - tests/CodeIndex.Tests/QueryCommandRunnerTests.cs
---

## English

- **C# impact now preserves the original query string on verbatim misses (#960)** — `impact` now keeps the user-entered spelling when a C# verbatim lookup misses, so human and JSON output no longer report a misleading canonicalized query.

## 日本語

- **C# の verbatim 識別子が見つからない場合でも `impact` が元の検索文字列を維持するようになりました (#960)** — `impact` は C# verbatim lookup の miss 時にユーザー入力の綴りを保持するため、human / JSON 出力が誤解を招く canonicalized query を返さなくなりました。
```

### Non-issue Fragment Template

```md
---
category: docs
affected:
  - .codex/workflows/changelog-fragment.md
---

## English

- **Non-issue changelog fragments omit `issues` front matter** — use `+<slug>.<category>.md` for workflow, docs, or infrastructure changes that are not tied to GitHub issues, and leave out `issues` entirely so validation does not fail on `issues: null` or `issues: []`.

## 日本語

- **issue 非対応の changelog フラグメントでは `issues` の front matter を省略します** — GitHub issue に紐づかない workflow / docs / infrastructure 変更は `+<slug>.<category>.md` を使い、`issues` 自体は書かないことで `issues: null` や `issues: []` の検証失敗を防ぎます。
```

### Non-issue Template

```md
---
category: docs
affected:
  - .codex/workflows/changelog-fragment.md
---

## English

- **Clarified non-issue fragment front matter requirements** — non-issue changelog fragments now omit `issues` entirely, preventing `issues: null` / `issues: []` validation failures in agent-generated fragments.

## 日本語

- **issue 非対応フラグメントの front matter 要件を明確化** — issue 非対応の changelog フラグメントでは `issues` 自体を記載しないようにし、エージェント生成時の `issues: null` / `issues: []` による検証失敗を防止しました。
```
