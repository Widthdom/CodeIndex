---
category: changed
affected:
  - tests/CodeIndex.Tests/ReferenceExtractorWarmup.cs
---

## English

- **C# reference extractor practical budget tests now run after a targeted warm-up** — The test assembly primes the representative C# plain-call extraction path before stopwatch-based budget guards run, keeping CI coverage and JIT startup variance out of the measured steady-state extractor budget.

## 日本語

- **C# reference extractor practical budget tests が targeted warm-up 後に実行されるようになりました** — test assembly は stopwatch-based budget guard の前に代表的な C# plain-call extraction path を事前実行し、CI coverage と JIT startup のばらつきを測定対象の steady-state extractor budget から外します。
