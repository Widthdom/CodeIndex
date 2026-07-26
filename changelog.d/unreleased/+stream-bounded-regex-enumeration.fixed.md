---
category: fixed
affected:
  - src/CodeIndex/Indexer/BoundedRegex.cs
  - tests/CodeIndex.Tests/BoundedRegexTests.cs
  - DEVELOPER_GUIDE.md
  - TESTING_GUIDE.md
---

## English

- **Made bounded regex enumeration demand-driven** — Extractors that stop after
  reaching a result limit no longer force every remaining regex match on the
  source line to be materialized first, while right-to-left instances preserve
  their default reverse match order. Secondary reference scanners now use the
  same streaming path across Fortran, Visual Basic, F#, Pascal, Objective-C,
  Haskell, Elixir, Smalltalk, Lua, Dart, Razor, JSON, JavaScript, GitHub Actions,
  and C++ compound requirements without changing their configured timeouts.
  Bounded reference scans now stop before requesting another match and skip
  all later extraction phases on the same dense line once capacity is reached.

## 日本語

- **bounded regex enumeration を demand-driven にしました** — 結果上限に達して
  停止する extractor は、source line に残るすべての regex match を先に実体化せず、
  right-to-left instance の既定の逆順も維持します。Fortran、Visual Basic、F#、
  Pascal、Objective-C、Haskell、Elixir、Smalltalk、Lua、Dart、Razor、JSON、
  JavaScript、GitHub Actions、C++ compound requirement の secondary reference
  scanner も設定済み timeout を変えず同じ逐次経路を使います。bounded reference
  scan は上限到達後に次の match を要求せず、同じ dense line の後続 extraction phase
  も省略します。
