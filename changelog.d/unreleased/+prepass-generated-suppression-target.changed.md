---
category: changed
affected:
  - src/CodeIndex/Indexer/CSharpStaticInterfacePrepass.cs
  - src/CodeIndex/Cli/IndexCommandRunner.FullScan.cs
  - src/CodeIndex/Cli/IndexCommandRunner.Update.cs
  - src/CodeIndex/Mcp/McpToolHandlers.cs
---

## English

- **Carried generated-suppression state on C# prepass targets** - CLI and MCP indexing now pass the already-known generated-code suppression decision through prepass targets, avoiding an extra full-target dictionary and repeated lookups while preserving generated-file reuse checks.

## 日本語

- **C# prepass target に generated-suppression 状態を持たせました** - CLI / MCP indexing は既に判定済みの generated-code suppression 結果を prepass target 経由で渡すようになり、generated file の再利用判定を保ったまま余分な全 target 辞書と lookup を避けます。
