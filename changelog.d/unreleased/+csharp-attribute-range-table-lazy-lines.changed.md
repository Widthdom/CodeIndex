---
title: Lazily allocate CSharp attribute range rows
category: changed
---

## English

- Reduced C# reference extraction allocations by creating per-line attribute range lists only for lines that actually contain attribute spans.

## 日本語

- C# の参照抽出で、属性 span を実際に含む行だけ属性 range リストを作るようにして割り当てを削減しました。
