---
category: fixed
affected:
  - tests/CodeIndex.Tests/LspServerTests.cs
  - tests/CodeIndex.Tests/SymbolExtractorConfiguredPatternTests.cs
  - TESTING_GUIDE.md
---

## English

- **Windows release validation no longer flakes on SQLite WAL cleanup or pattern timeout snapshot churn** — the release tests now keep the external WAL writer alive through artifact comparison and reuse one published pattern extractor snapshot, preventing spurious missing sidecars and duplicate timeout diagnostics.

## 日本語

- **Windows のリリース検証で SQLite WAL cleanup や pattern timeout snapshot churn による不安定な失敗が起きないようになりました** — release test は artifact 比較が終わるまで外部 WAL writer を保持し、公開済み pattern extractor snapshot を1つだけ再利用することで、sidecar の予期しない消失や timeout diagnostic の重複を防ぎます。
