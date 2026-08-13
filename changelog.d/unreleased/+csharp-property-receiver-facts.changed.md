---
category: changed
affected:
  - src/CodeIndex/Database/DbWriter.References.cs
  - src/CodeIndex/Database/DbWriter.ReferenceGraphRefreshScope.cs
---

## English

- **Initial C# property-receiver normalization now seeks compact facts** — full, scoped, and retained graph refreshes drive the two normalization updates from flagged reference IDs and primary-keyed field/property target identities instead of scanning all references and repeatedly probing persistent target symbols.

## 日本語

- **初回C# property-receiver normalizationがcompact factをseekするようになりました** — full / scoped / retained graph refreshは、全referenceをscanして永続target symbolを繰り返しprobeせず、flag済みreference IDとprimary-keyed field / property target identityから2つのnormalization updateを駆動します。
