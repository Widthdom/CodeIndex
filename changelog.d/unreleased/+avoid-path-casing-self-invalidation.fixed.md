---
category: fixed
affected:
  - TESTING_GUIDE.md
  - src/CodeIndex/Cli/PathCasing.cs
  - tests/CodeIndex.Tests/PathCasingTests.cs
---

## English

- **Exact-case path comparisons no longer mutate directories during indexing** — path equality now resolves ordinal matches and impossible unequal-length matches, while parent-boundary checks accept exact-case matches and descendants, before either operation probes filesystem case sensitivity. Empty nested workspace members therefore keep their discovery snapshot stable instead of receiving a temporary case-probe entry after traversal.

## 日本語

- **大小文字まで一致する path 比較で index 中の directory を変更しないよう修正しました** — filesystem の case sensitivity を probe する前に、path equality は ordinal 一致と長さの異なる不一致を確定し、親境界の判定は大小文字まで一致する同一 path と descendant を受理します。これにより、空の nested workspace member は走査後に一時 case-probe entry を作られず、discovery snapshot を安定して維持します。
