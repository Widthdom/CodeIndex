---
category: fixed
affected:
  - src/CodeIndex/Indexer/BoundedRegex.cs
  - src/CodeIndex/Indexer/References/ReferenceExtractor.cs
  - tests/CodeIndex.Tests/BoundedRegexTests.cs
  - tests/CodeIndex.Tests/PerformanceTests.cs
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
  Symbol and dependency extractors now stream their remaining multi-match
  patterns across scientific/native, Pascal/Ada, SQL, Python, Swift, GraphQL,
  markup/XAML, shell, Ruby, Perl, Elixir, CSS, HDL, C++, and manifest parsing.
  Count-only scans no longer retain a `MatchCollection`, while preserving the
  previous all-or-nothing result if matching times out.
  Dense XAML supplemental symbol scans now stop as soon as the shared
  structured-data symbol budget needs its diagnostic marker, instead of
  building an unbounded temporary list and trimming it after all phases.
  Symbol container assignment now reuses one path buffer across members instead
  of allocating a stack snapshot and a second list for every nested symbol.
  Prolog goal scans now allocate per-line call lists only when a known goal is
  emitted and update directive metadata in place instead of copying the list.
  JSON symbol and reference extraction now build UTF-8 line-offset arrays at
  exact capacity instead of retaining a growing list alongside its final copy.

## 日本語

- **bounded regex enumeration を demand-driven にしました** — 結果上限に達して
  停止する extractor は、source line に残るすべての regex match を先に実体化せず、
  right-to-left instance の既定の逆順も維持します。Fortran、Visual Basic、F#、
  Pascal、Objective-C、Haskell、Elixir、Smalltalk、Lua、Dart、Razor、JSON、
  JavaScript、GitHub Actions、C++ compound requirement の secondary reference
  scanner も設定済み timeout を変えず同じ逐次経路を使います。bounded reference
  scan は上限到達後に次の match を要求せず、同じ dense line の後続 extraction phase
  も省略します。symbol / dependency extractor も scientific / native、Pascal / Ada、
  SQL、Python、Swift、GraphQL、markup / XAML、shell、Ruby、Perl、Elixir、CSS、HDL、
  C++、manifest parsing の残存 multi-match pattern を逐次走査します。count のみの scan
  は `MatchCollection` を保持せず、matching timeout 時は従来どおり all-or-nothing の
  結果を返します。dense XAML の supplemental symbol scan は、共有 structured-data
  symbol budget の diagnostic marker が必要になった時点で停止し、全 phase の後まで
  無制限の一時 list を構築してから trim しません。symbol の container assignment も
  member ごとに stack snapshot と2つ目の list を割り当てず、1つの path buffer を再利用します。
  Prolog goal scan も既知 goal を出力するときだけ per-line call list を割り当て、directive
  metadata は list を copy せず in-place で更新します。JSON symbol / reference extraction
  も UTF-8 line-offset array を exact capacity で構築し、成長中の list と最終 copy を同時に
  保持しません。
