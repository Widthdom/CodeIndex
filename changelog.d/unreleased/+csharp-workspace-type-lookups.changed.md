---
category: changed
affected:
  - src/CodeIndex/Indexer/References/Languages/CSharpReferenceExtractor.Support.cs
---

## English

- **C# workspace pattern lookups no longer construct a discarded known-type set** — enum-shadowing analysis now builds only the raw non-enum conflict names it consumes, reducing fresh-prepass allocation while preserving qualified type and member lookup behavior.

## 日本語

- **C# workspace pattern lookupが未使用のknown-type setを構築しなくなりました** — enum shadowing解析は実際に使うraw non-enum conflict nameだけを構築し、qualified type / member lookupの挙動を維持しながら初回prepassのallocationを削減します。
