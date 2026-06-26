---
category: changed
affected:
  - src/CodeIndex/Cli/IndexCommandRunner.cs
  - src/CodeIndex/Cli/IndexCommandRunner.Update.cs
  - tests/CodeIndex.Tests/IndexCommandRunnerTests.cs
---

## English

- **Scoped updates avoid an extra byte-count iterator** — update-mode index run metadata now measures readable bytes with a direct path selector instead of allocating a LINQ `Select` iterator over every target path.

## 日本語

- **scoped update の byte count で追加 iterator を避けます** — update mode の index run metadata は、全 target path に対する LINQ `Select` iterator を作らず、直接 path selector で readable bytes を測定するようになりました。
