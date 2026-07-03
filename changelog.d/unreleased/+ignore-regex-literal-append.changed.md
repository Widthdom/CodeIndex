---
title: Reduce ignore regex literal escaping allocations
category: changed
---

## English
- Append ordinary ignore-rule regex literal characters directly instead of allocating one-character strings for `Regex.Escape`.

## 日本語
- ignore rule の regex literal で通常文字を直接 append し、`Regex.Escape` 用の 1 文字 string allocation を減らしました。
