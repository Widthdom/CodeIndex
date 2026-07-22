---
category: changed
affected:
  - src/CodeIndex/Database/DbWriter.References.cs
  - tests/CodeIndex.Tests/DatabaseTests.cs
  - TESTING_GUIDE.md
---

## English

- **Reference-graph refresh no longer rewrites already-current identity rows** — Source-symbol identity, target-resolution state, and self-reference maintenance now use NULL-safe changed-value predicates across every indexed language. Stable graph rebuilds still recompute candidates and preserve the existing transaction, cancellation, readiness, and final `changes()` contracts, but avoid three table-wide rounds of index-maintaining no-op writes. Trigger-backed regression coverage fixes zero physical identity writes on a stable rerun, one repair per corrupted phase, and rollback across refresh phases.

## 日本語

- **reference graph refreshが最新identity rowを再書込みしなくなりました** — 全index対象言語でsource-symbol identity、target-resolution state、self-reference maintenanceにNULL-safeな変更値predicateを適用します。安定graph rebuildでもcandidate再計算と既存transaction・cancellation・readiness・最終`changes()`契約は維持しつつ、index maintenanceを伴うtable全体のno-op write 3巡を避けます。triggerによる回帰coverageで、安定rerunの物理identity write 0回、corrupt phaseごとのrepair 1回、refresh phase間のrollbackを固定します。
