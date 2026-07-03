---
title: Reduce Python symbol header fragment allocations
category: changed
---

## English

- Reduced temporary string allocations while building Python symbol header signatures by appending trimmed source ranges directly.

## 日本語

- Python シンボルのヘッダー署名を組み立てる際に、trim 済みのソース範囲を直接追加して一時文字列の割り当てを削減しました。
