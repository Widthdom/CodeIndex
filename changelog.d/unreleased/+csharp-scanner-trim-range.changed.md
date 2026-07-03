---
title: Reduce C# scanner trim allocations
category: changed
---

## English

- Reduced C# symbol scanner allocations by using span-based return type suffix checks and appending trimmed multiline signature ranges directly.

## 日本語

- C# シンボル scanner で戻り値型サフィックス判定を span ベースにし、複数行 signature の trim 済み範囲を直接追加して割り当てを削減しました。
