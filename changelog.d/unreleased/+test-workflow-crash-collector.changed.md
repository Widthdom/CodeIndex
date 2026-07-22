---
category: changed
affected:
  - .github/scripts/run-dotnet-tests.ps1
  - tests/CodeIndex.Tests/CiWorkflowTests.cs
  - TESTING_GUIDE.md
---

## English

- **Clean CI test runs no longer pay crash-collector overhead** — VSTest crash collection is enabled only on the failed-run retry, while reproducible failures still run under the collector and retain diagnostic artifacts.

## 日本語

- **cleanなCIテスト実行でcrash collectorのoverheadを負わなくなりました** — VSTestのcrash収集は失敗後retryだけで有効にし、再現する失敗は引き続きcollector有効下で実行して診断artifactを保持します。
