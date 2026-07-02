---
title: Avoid Rust attribute line list joins during reference extraction
category: changed
---

## English
- Build Rust attribute text incrementally during reference extraction instead of collecting line slices in a list and joining them.

## 日本語
- Rust reference extraction で attribute text を line slice の list に集めて join する代わりに、必要に応じて段階的に構築するようにしました。
