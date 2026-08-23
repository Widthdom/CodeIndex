---
category: changed
affected:
  - src/CodeIndex/Indexer/BoundedRegex.cs
  - src/CodeIndex/Indexer/Symbols/
  - tests/CodeIndex.Tests/SymbolExtractorCSharpRegexProbeTests.cs
---

## English

- **Cold C# indexing avoids duplicate declaration regex probes** — the declaration loop and its recoverable-pattern check now share candidate-local deterministic misses for the same physical input, while merged multiline inputs, successful matches, timeouts, pattern priority, output, and cancellation behavior retain their previous contracts.

## 日本語

- **C# の初回インデックスで宣言 regex の重複 probe を回避します** — declaration loop と recoverable-pattern 判定が、同じ物理 input に対する candidate-local な deterministic miss を共有するようになりました。multiline 結合 input、成功 match、timeout、pattern priority、出力、cancellation の従来契約は維持します。
