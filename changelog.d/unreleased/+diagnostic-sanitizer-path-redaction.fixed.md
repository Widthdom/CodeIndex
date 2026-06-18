---
category: fixed
affected:
  - src/CodeIndex/Diagnostics/DiagnosticSanitizer.cs
  - tests/CodeIndex.Tests/DiagnosticSanitizerTests.cs
---

## English

- **Diagnostic path redaction no longer times out on cold CI runs** — diagnostic message sanitization now redacts absolute paths without relying on a regular-expression timeout, preventing short messages from being replaced by the timeout fallback under heavily loaded release test runs.

## 日本語

- **diagnostic path redaction が cold CI run で timeout しないようになりました** — diagnostic message sanitization は regular-expression timeout に依存せず absolute path を redact するようになり、負荷の高い release test run で短い message が timeout fallback に置き換わる問題を防ぎます。
