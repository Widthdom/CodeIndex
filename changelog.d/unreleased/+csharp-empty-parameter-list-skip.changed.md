---
title: Skip empty CSharp parameter list splitting
category: changed
---

## English

- Reduced C# reference extraction allocations by returning before parameter segment splitting for functions with empty parameter lists.

## 日本語

- parameter list が空の C# 関数では parameter segment 分割前に戻るようにして、参照抽出時の割り当てを削減しました。
