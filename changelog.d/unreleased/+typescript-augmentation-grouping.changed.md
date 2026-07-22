---
category: changed
affected:
  - src/CodeIndex/Database/DbWriter.TypeScriptAugmentations.cs
  - tests/CodeIndex.Tests/DatabaseTests.cs
  - TESTING_GUIDE.md
---

## English

- **Large TypeScript interface sets use allocation-bounded augmentation grouping** — augmentation rebuilds now index declarations in one pass, materialize declaration-index lists only for genuinely merged interfaces, and classify each module file once. A controlled 50,002-declaration benchmark reduced current-thread allocations by about 18% while preserving first-seen group and declaration order.

## 日本語

- **大規模なTypeScript interface集合でaugmentation groupingのallocationを抑制しました** — augmentation rebuildは宣言を1 passでindex化し、実際にmergeされるinterfaceだけdeclaration-index listをmaterializeし、各module fileを1回だけ分類します。50,002宣言の制御benchmarkではfirst-seen groupと宣言順を維持したまま、current-thread allocationを約18%削減しました。
