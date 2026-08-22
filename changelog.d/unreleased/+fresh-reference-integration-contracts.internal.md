---
category: internal
affected:
  - tests/CodeIndex.Tests/IndexCommandRunnerReferenceIndexBulkLoadTests.cs
  - tests/CodeIndex.Tests/McpServerToolsCallTests.cs
  - TESTING_GUIDE.md
---

## English

- **Fresh reference integration contracts now observe the complete raw and schema surface** — the full-scan lifecycle fixture includes raw reference-line RETURNING work and verifies its deferred-index snapshot, while the MCP readiness-failure fixture includes the `reference_lines` table when comparing the persisted schema with the canonical reference-index set.

## 日本語

- **fresh reference のintegration契約がraw処理とschemaの全範囲を観測するようになりました** — full-scan lifecycle fixtureはraw reference-line RETURNING処理とその遅延index snapshotを含め、MCP readiness失敗fixtureは永続化済みschemaをcanonical reference-index集合と比較するときに`reference_lines` tableも対象にします。
