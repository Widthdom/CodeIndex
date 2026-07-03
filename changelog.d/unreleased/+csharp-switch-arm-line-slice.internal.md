---
category: internal
affected:
  - src/CodeIndex/Indexer/References/Languages/CSharpReferenceExtractor.Support.cs
---

## English

- **Sliced C# switch-arm lines directly** - C# switch expression arm parsing now builds line arrays from the original body range instead of first materializing the whole arm text during reference indexing.

## 日本語

- **C# switch arm の行を直接 slice** - C# switch expression arm の解析で、参照インデックス作成中に arm 全体の文字列を先に作らず、元の body 範囲から行配列を構築するようにしました。
