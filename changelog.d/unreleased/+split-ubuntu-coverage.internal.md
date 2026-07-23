---
category: internal
affected:
  - .github/workflows/dotnet.yml
  - tests/CodeIndex.Tests/CiWorkflowTests.cs
  - TESTING_GUIDE.md
  - DEVELOPER_GUIDE.md
---

## English

- **Ubuntu net8 coverage now runs in complementary shards** — `IndexCommandRunnerTests` and the remaining suite run in parallel with coverage enabled in both shards, while full restore, audit, lint, publish, and artifact work stays on one primary shard to shorten the Build and Test critical path without duplicating primary validation.

## 日本語

- **Ubuntu net8 coverage を補完的な shard に分割しました** — `IndexCommandRunnerTests` と残りの suite をどちらも coverage 有効のまま並列実行し、solution 全体の restore、audit、lint、publish、artifact 処理は1つの primary shard だけに残すことで、primary 検証を重複させず Build and Test の critical path を短縮します。
