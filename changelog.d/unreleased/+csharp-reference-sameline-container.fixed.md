---
category: fixed
affected:
  - src/CodeIndex/Indexer/References/ReferenceExtractor.Core.cs
  - src/CodeIndex/Indexer/References/ReferenceExtractor.TypeReferences.cs
---

## English

- **C# reference extraction no longer rescans every container for same-line calls** — large files with many ordinary `Target()` call lines now resolve call containers through a per-line candidate index, avoiding a release-build CI timeout regression.

## 日本語

- **C# reference extraction が same-line call ごとに全 container を再走査しないようになりました** — 通常の `Target()` 呼び出し行が大量にあるファイルでは、行単位の候補 index で call container を解決し、release build CI の timeout 回帰を避けます。
