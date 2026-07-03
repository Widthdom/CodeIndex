---
title: Share empty C# helper lookups for non-C# files
category: changed
---

## English

- **Shared empty C# helper lookups outside C# reference extraction** — non-C# files no longer allocate empty C# type-name sets, value-receiver maps, or qualified-pattern maps during shared reference setup.

## 日本語

- **C# 以外で空の C# helper lookup を共有** — C# 以外のファイルでは、共通参照準備中に空の C# 型名セット、value receiver map、qualified pattern map を毎回確保しないようになりました。
