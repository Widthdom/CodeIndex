---
category: changed
affected:
  - src/CodeIndex/Cli/IndexCommandRunner.cs
  - src/CodeIndex/Cli/IndexCommandRunner.Update.cs
  - tests/CodeIndex.Tests/IndexCommandRunnerTests.cs
---

## English

- **Scoped updates skip the C# static-interface prepass when no target can be C#** - `cdidx index --files` now filters prepass candidates up front, avoiding duplicate language probes for large non-C# update sets.

## 日本語

- **C# になり得る target がない scoped update では C# static-interface prepass を省略します** - `cdidx index --files` は prepass 候補を先に絞り込み、大規模な非 C# update set で重複する言語 probe を避けます。
