---
category: changed
affected:
  - src/CodeIndex/Cli/IndexCommandRunner.FullScan.cs
  - tests/CodeIndex.Tests/IndexCommandRunnerTests.cs
---

## English

- **Full scans format worker phase diagnostics only when observed** - parallel extraction workers now publish allocation-free `(path, phase)` slots and create human-readable strings only for heartbeat or stall diagnostics. Normal indexing no longer allocates a new phase string at every read, chunk, symbol, reference, and validation transition across indexed languages.

## 日本語

- **full scan の worker phase diagnostic を観測時だけ整形するようにしました** - parallel extraction worker は allocation-free な `(path, phase)` slot を公開し、heartbeat または stall diagnostic が必要な場合だけ人間向け文字列を生成します。通常 indexing では、全 indexed language の read / chunk / symbol / reference / validation 遷移ごとの phase 文字列確保が不要になります。
