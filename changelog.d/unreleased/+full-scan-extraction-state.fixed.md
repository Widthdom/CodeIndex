---
category: fixed
affected:
  - src/CodeIndex/Cli/IndexCommandRunner.FullScan.ExtractionPipeline.cs
  - src/CodeIndex/Cli/IndexCommandRunner.FullScan.ResultConsumer.cs
  - tests/CodeIndex.Tests/IndexCommandRunnerFullScanTests.cs
---

## English

- **Full-scan extraction now preserves shared state and worker-resource ownership** — a later C# workspace drift no longer drops text-index mutations already committed for earlier files, and a fatal parallel worker result defers shared queue, cache, hook, and cancellation cleanup until every peer worker has stopped.

## 日本語

- **full-scan extraction が共有 state と worker resource の ownership を保持するようになりました** — 後段の C# workspace drift が先行 file で commit 済みの text-index mutation を失わず、parallel worker の fatal result では全 peer worker の停止まで共有 queue、cache、hook、cancellation の cleanup を延期します。
