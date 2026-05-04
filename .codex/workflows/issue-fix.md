# Issue Fix Workflow

Use this workflow for GitHub issue implementation tasks.

## Inputs

- One or more GitHub issue URLs or issue numbers.
- Optional user notes.

## Steps

1. Read `AGENT_GUIDE.md`.
2. Read `.codex/workflows/issue-scope.md`.
3. Read each target issue and its comments.
4. If any target issue has a clear work-started comment by another actor, stop work on that issue and report it.
5. Create a branch from `origin/main` named:

   ```text
   fix-issue<issue-number-or-hyphen-joined-issue-numbers>
   ```

6. Comment in English on each in-scope issue that work has started on that branch.
7. Read required project guidance, including `docs/SELF_IMPROVEMENT.md` and any files referenced by `AGENT_GUIDE.md`.
8. Check the local CodeIndex index first with `dotnet ./src/CodeIndex/bin/Debug/net8.0/cdidx.dll status --check --json`. If it exits `0` with `index_matches_workspace: true`, keep the existing DB; otherwise refresh it using the documented project procedure.
9. For code search, use only the locally built binary:

   ```bash
   dotnet ./src/CodeIndex/bin/Debug/net8.0/cdidx.dll
   ```

10. Produce a plan of at most 10 lines. Include only likely changed files or areas, validation commands, and risks.
11. Implement the smallest correct fix.
12. Add or update tests for changed behavior.
13. Update matching documentation whenever the issue changes user-visible behavior, CLI/MCP output, flags, error messages, install/release behavior, or workflow behavior. For user-visible or behavior-changing changes, add a bilingual fragment under `changelog.d/unreleased/` and do not edit `CHANGELOG.md` during the ordinary issue-fix PR.
14. Run relevant validation commands.
15. Follow `.codex/workflows/precommit.md` before committing.
16. Commit with an English commit message that includes relevant issue numbers.
17. Run adversarial review using `.codex/workflows/adversarial-review.md`.
18. Address blocking or actionable review findings. Review loops are limited to two rounds unless the user explicitly asks for more.
19. Push the branch and create a PR.
20. In the PR body, include one `Fixes #...` line for each issue that should auto-close after merge.
21. Follow `.codex/workflows/pr-finalize.md`.
22. Final report must include:
    - PR link;
    - issues handled;
    - summary of changes;
    - validation performed;
    - review result;
    - unresolved limitations or follow-up candidates.

## Scope Limits

Do not expand beyond the requested issues unless `.codex/workflows/issue-scope.md` allows it.
Do not create new issues for ordinary improvement ideas; list them as follow-ups.
Do not perform unrelated refactors.
