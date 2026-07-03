---
title: Go start-column fallback avoids trim strings
category: changed
---

## English

- **Go start-column fallback avoids trim strings** — Go symbol extraction now computes fallback indentation by scanning the line instead of allocating a `TrimStart` result when a symbol name cannot be located directly.

## 日本語

- **Goのstart column fallbackでtrim文字列を避けるようになりました** — Goシンボル抽出はシンボル名を直接見つけられない場合、`TrimStart`結果を割り当てずに行頭空白を走査してfallback indentationを計算するようになりました。
