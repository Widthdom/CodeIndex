---
category: changed
affected:
  - .codex/config.toml
  - .codex/README.md
  - .codex/rules/codeindex.rules
  - .agent_harness/command_guard_core.py
  - .agent_harness/tests/test_command_guard_core.py
  - AGENT_GUIDE.md
---

## English
- **Codex can use normal-development GitHub CLI commands safely** - The `codeindex_workspace` permission profile now allows network access only to `github.com` and `api.github.com`, while command guards explicitly allow normal Issue/PR development commands such as `gh issue/pr list/view/create/edit/comment`, `gh pr ready`, `gh pr close`, `gh repo view`, and `gh status`, and continue blocking authentication, secrets, releases, repo creation/forking/deletion, arbitrary `gh api`, and PR merges.

## 日本語
- **Codex が通常開発用の GitHub CLI コマンドを安全に使えるようになりました** - `codeindex_workspace` permission profile は `github.com` と `api.github.com` だけに network access を許可し、command guard は `gh issue/pr list/view/create/edit/comment`、`gh pr ready`、`gh pr close`、`gh repo view`、`gh status` など通常の Issue / PR 開発コマンドを明示的に許可しつつ、認証、secret、release、repo 作成 / fork / delete、任意の `gh api`、PR merge は引き続き禁止します。
