---
title: Scan CSharp parameter segments without lists
category: changed
---

## English

- Reduced C# reference extraction allocations by scanning top-level parameter segments with spans instead of materializing a list of parameter segment strings.

## 日本語

- C# の top-level parameter segment を文字列リスト化せず span で走査するようにして、参照抽出時の割り当てを削減しました。
