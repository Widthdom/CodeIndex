---
category: changed
affected:
  - .github/workflows/dotnet.yml
  - TESTING_GUIDE.md
  - tests/CodeIndex.Tests/CiWorkflowTests.cs
---

## English

- **CI skips TRX telemetry summaries on clean test passes** — the build workflow now runs the test telemetry summarizer only after failed or pass-on-retry lanes, avoiding extra work on ordinary successful matrix lanes while keeping diagnostics for failures and flaky classifications.

## 日本語

- **CI はテストが初回成功した lane で TRX telemetry summary を省略するようになりました** — build workflow は失敗または retry 成功 lane の場合だけ test telemetry summarizer を実行し、通常成功した matrix lane の余分な処理を避けつつ、失敗時と flaky 分類時の診断は維持します。
