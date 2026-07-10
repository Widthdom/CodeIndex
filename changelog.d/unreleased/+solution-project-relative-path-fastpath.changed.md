---
category: changed
affected:
  - src/CodeIndex/Cli/SolutionProjectResolver.cs
---

## English

- **Project resolver paths** - Reuse normalized workspace roots and the prefix-aware relative path helper when resolving project info, project globs, and traversal diagnostics.

## 日本語

- **project resolver の path 処理** - project info・project glob・traversal diagnostic の生成で正規化済み workspace root と prefix-aware な relative path helper を再利用するようにしました。
