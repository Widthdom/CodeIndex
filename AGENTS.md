# Codex Entry Point

Read `AGENT_GUIDE.md` first. It is the shared source of truth for CodeIndex agent behavior, including the code search and safety policy that forbids `grep`/`rg`/`find`/etc. in favor of the locally built `cdidx` binary.

For task-specific procedures, read the relevant workflow in `.codex/workflows/`:

- issue fixing: `.codex/workflows/issue-fix.md`
- changelog fragments: `.codex/workflows/changelog-fragment.md`
- release changelog: `.codex/workflows/release-changelog.md`
- adversarial review: `.codex/workflows/adversarial-review.md`
- commit checks: `.codex/workflows/precommit.md`
- PR finalization and CI checks: `.codex/workflows/pr-finalize.md`
- related/new issue scope control: `.codex/workflows/issue-scope.md`

Do not duplicate workflow or policy rules in this file. Update `AGENT_GUIDE.md` or the relevant workflow file instead.
