# Claude Code Entry Point

Read `AGENT_GUIDE.md` first. It is the shared source of truth for CodeIndex agent behavior, including Claude Code-specific notes, the search/indexing policy, and the status and reference-extraction contracts.

For task-specific procedures, read the relevant workflow in `.codex/workflows/`:

- issue fixing: `.codex/workflows/issue-fix.md`
- changelog fragments: `.codex/workflows/changelog-fragment.md`
- release changelog: `.codex/workflows/release-changelog.md`
- adversarial review: `.codex/workflows/adversarial-review.md`
- commit checks: `.codex/workflows/precommit.md`
- PR finalization and CI checks: `.codex/workflows/pr-finalize.md`
- related/new issue scope control: `.codex/workflows/issue-scope.md`

The `.codex/workflows/` directory is a shared workflow library for all coding agents, not only Codex.

Do not duplicate workflow or policy rules in this file. Update `AGENT_GUIDE.md` or the relevant workflow file instead.
