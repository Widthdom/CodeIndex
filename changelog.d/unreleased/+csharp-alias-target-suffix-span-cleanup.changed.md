---
title: Avoid C# alias target suffix cleanup strings
category: changed
---

## English

- **C# using-alias target normalization now trims nullable and array suffixes with spans** — reference extraction avoids repeated suffix slice strings while matching aliases against known type names in large C# indexes.

## 日本語

- **C# using alias target 正規化が nullable / array suffix の trim に span を使うようになりました** — 大規模 C# index で alias を既知型名と照合するとき、suffix ごとの slice 文字列生成を避けます。
