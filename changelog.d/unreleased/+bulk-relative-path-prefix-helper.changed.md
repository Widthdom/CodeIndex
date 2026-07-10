---
category: changed
affected:
  - src/CodeIndex/Cli/IndexCommandRunner.DryRun.cs
  - src/CodeIndex/Cli/SolutionProjectResolver.cs
  - src/CodeIndex/Mcp/McpToolHandlers.cs
---

## English

- **Bulk indexing paths now use the prefix-based relative-path helper** - Dry-run scans, solution project expansion, and MCP missing-file cleanup avoid the heavier `Path.GetRelativePath` path when files are already under the indexed project root.

## 日本語

- **bulk indexing 経路で prefix ベースの relative-path helper を使うようになりました** - dry-run scan、solution project expansion、MCP の missing-file cleanup で、対象ファイルが project root 配下にある通常ケースでは重い `Path.GetRelativePath` 経路を避けます。
