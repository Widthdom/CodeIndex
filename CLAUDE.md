# Claude Code Entry Point

Read `AGENT_GUIDE.md`. It is the single source of truth for agent behavior in this repository, including the search/indexing policy, the workflow index, tool-specific notes, and all repository, commit, review, PR, status, and reference-extraction rules.

**Do not add new rules, policy, workflow pointers, or contract notes to this file.** This file is a thin redirect and must stay that way. Put new content in `AGENT_GUIDE.md` (or in the relevant `.codex/workflows/*.md` workflow). Claude Code-specific guidance goes under `Tool-Specific Notes > Claude Code` in `AGENT_GUIDE.md`.
