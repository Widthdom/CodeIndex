---
category: fixed
issues:
  - 3977
  - 3902
  - 3953
  - 3944
affected:
  - src/CodeIndex/Database/DbSymbolReader.cs
  - src/CodeIndex/Cli/QueryCommandRunner.cs
  - src/CodeIndex/Mcp/McpToolDefinitions.cs
  - USER_GUIDE.md
  - DEVELOPER_GUIDE.md
---

## English

- Improved `unused` filtering with the `--confidence` alias and the `--actionable` preset for private medium-confidence cleanup candidates.
- Routed serialization contracts, DTO/config/metadata surfaces, generated/documentation surfaces, and test-only hooks into the low-confidence unused bucket.
- Filled `unused --count --json` bucket and confidence summaries.

## 日本語

- `unused` に `--confidence` alias と、private かつ medium confidence の削除候補へ絞る `--actionable` preset を追加しました。
- serialization contract、DTO/config/metadata surface、generated/documentation surface、test-only hook を低 confidence の unused bucket に寄せるようにしました。
- `unused --count --json` の bucket / confidence summary を出力するようにしました。
