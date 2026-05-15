# Codex Entry Point

Read `AGENT_GUIDE.md`. It is the single source of truth for agent behavior in this repository, including the code search and safety policy (no `grep`/`rg`/`find`/etc., use the locally built `cdidx`), the workflow index, and all repository, commit, review, and PR rules.

**Do not add new rules, policy, workflow pointers, or contract notes to this file.** This file is a thin redirect and must stay that way. Put new content in `AGENT_GUIDE.md` (or in the relevant `.codex/workflows/*.md` workflow). Codex-specific guidance, if any, goes under `Tool-Specific Notes` in `AGENT_GUIDE.md`.
