---
category: changed
affected:
  - src/CodeIndex/Cli/IndexCommandRunner.Update.cs
  - src/CodeIndex/Cli/IndexCommandRunner.cs
  - src/CodeIndex/Indexer/Scanning/FileIndexer.Gitmodules.cs
---

## English

- **Update target normalization reuses the relative path helper** — commit, explicit-file, and `.gitmodules` update paths now use the directory-prefix fast path before falling back to `Path.GetRelativePath`.

## 日本語

- **update target normalization で relative path helper を再利用** — commit、明示 file、`.gitmodules` の update path は `Path.GetRelativePath` へ落ちる前に directory-prefix fast path を使うようにしました。
