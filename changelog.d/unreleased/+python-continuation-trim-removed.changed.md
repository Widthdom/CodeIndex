---
title: Avoid Python continuation trim allocations
category: changed
---

## English

- Replaced Python logical line continuation `TrimEnd()` checks with an allocation-free trailing backslash scan.

## 日本語

- Python logical line continuation の `TrimEnd()` 判定を、割り当てなしの末尾 backslash scan に置き換えました。
