---
category: changed
affected:
  - src/CodeIndex/Cli/IndexCommandRunner.Update.cs
  - src/CodeIndex/Cli/IndexCommandRunner.cs
---

## English

- **Update target paths** - Materialize absolute, relative, display, and index paths for update targets once, then reuse them through C# prepass, indexing, and byte-accounting paths.

## 日本語

- **更新対象パス** - 更新対象の absolute / relative / display / index path を一度だけ具体化し、C# prepass・indexing・バイト集計の各経路で再利用するようにしました。
