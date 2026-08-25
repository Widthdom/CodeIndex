---
category: internal
affected:
  - tests/CodeIndex.Tests/ConsoleUiTests.cs
  - TESTING_GUIDE.md
---

## English

- **Shell completion probes tolerate cold startup on loaded CI hosts** — completion-script execution tests now use a bounded 30-second deadline while retaining process-tree termination on timeout, avoiding false failures when PowerShell starts slowly.

## 日本語

- **shell completion probe が負荷の高い CI host での cold start に耐えられるようになりました** — completion script の実行テストは timeout 時の process tree 終了を維持しながら上限 30 秒の deadline を使用し、PowerShell の起動が遅い場合の誤検知を防ぎます。
