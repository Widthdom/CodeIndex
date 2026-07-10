---
category: changed
affected:
  - src/CodeIndex/Indexer/References/Languages/SwiftReferenceExtractor.cs
---

## English

- **Swift word probes now compare spans** — Swift reference extraction avoids allocating a substring when checking keyword-like word boundaries.

## 日本語

- **Swift word probe が span 比較を使うようになりました** — Swift reference extraction で keyword 風の word boundary を確認するときに、一時 substring を作らないようにしました。
