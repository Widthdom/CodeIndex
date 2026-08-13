---
category: changed
affected:
  - src/CodeIndex/Database/DbWriter.References.cs
  - src/CodeIndex/Database/DbWriter.ReferenceGraphRefreshScope.cs
---

## English

- **Initial reference resolution now constructs target-family keys once per symbol** — full, fresh, differential, scoped, and retained graph refreshes resolve candidate rows through primary-keyed TEMP facts instead of rebuilding long language/path/container/name keys for every physical candidate, with unchanged cross-language resolution states.

## 日本語

- **初回reference resolutionがtarget-family keyをsymbolごとに1回だけ構築するようになりました** — full / fresh / differential / scoped / retained graph refreshは、物理candidateごとに長いlanguage / path / container / name keyを再構築せずprimary-keyed TEMP factを通して解決し、言語横断のresolution stateを維持します。
