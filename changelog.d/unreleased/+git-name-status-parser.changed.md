---
category: changed
affected:
  - src/CodeIndex/Cli/GitHelper.cs
  - tests/CodeIndex.Tests/GitHelperTests.cs
---

## English

- **Update indexing parses git name-status output without per-line split arrays** - `--commits` and ref-diff update paths now share a span-based parser for large changed-file lists.

## 日本語

- **update indexing が git name-status 出力を行ごとの split 配列なしで解析するようになりました** - `--commits` と ref-diff update path は、大きな changed-file list 向けの span-based parser を共有します。
