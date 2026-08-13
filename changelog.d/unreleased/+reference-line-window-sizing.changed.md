---
category: changed
affected:
  - src/CodeIndex/Database/DbWriter.References.cs
---

## English

- **Initial reference-line persistence no longer hashes every context twice** — language-neutral atomic windows are bounded from worst-case batch rows before the single deduplicating materialization pass, removing a redundant tuple set while preserving window, rollback, and context-identity contracts.

## 日本語

- **初回reference-line永続化で全contextを二重hashしなくなりました** — 言語共通のatomic windowは単一の重複排除materializationより前にbatch rowの最悪ケースからboundされ、window、rollback、context identity契約を維持しながら冗長なtuple setを除去します。
