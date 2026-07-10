---
category: changed
affected:
  - src/CodeIndex/Cli/IndexCommandRunner.cs
  - src/CodeIndex/Cli/IndexWatchRunner.cs
---

## English

- **CLI indexing diagnostics reuse the relative-path prefix fast path** - Watch batch samples, scan diagnostics, and git-exclude setup now avoid full relative-path computation when paths are already under the project root.

## 日本語

- **CLI indexing diagnostics が relative path prefix fast path を再利用するようになりました** - watch batch sample、scan diagnostic、git-exclude setup は project root 配下の path で full relative-path 計算を避けます。
