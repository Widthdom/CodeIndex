---
category: internal
affected:
  - src/CodeIndex/Indexer/References/ReferenceExtractor.Core.cs
---

## English

- **Avoided C# qualified-prefix lists for single segments** - C# reference extraction now skips temporary list/reverse/join work when a qualified prefix contains only one segment.

## 日本語

- **C# qualified prefix の single segment で list を省略** - C# 参照抽出で qualified prefix が 1 segment だけの場合、一時 list / reverse / join を行わないようにしました。
