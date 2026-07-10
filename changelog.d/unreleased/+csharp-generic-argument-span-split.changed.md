---
category: changed
affected:
  - src/CodeIndex/Indexer/References/ReferenceExtractor.TypeReferences.cs
---

## English

- **C# generic argument mapping now splits spans directly** — implemented interface generic arguments no longer allocate a temporary list substring before top-level comma splitting.

## 日本語

- **C# generic argument mapping が span を直接分割するようになりました** — implemented interface の generic argument 解析で、top-level comma 分割前の一時的な list 文字列コピーを行わないようにしました。
