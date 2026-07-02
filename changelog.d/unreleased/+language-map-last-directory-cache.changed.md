---
title: Cache the most recent language-map override lookup per indexer
category: changed
---

## English
- Add a fast path for repeated language-map override lookups in the same directory during large scans.

## 日本語
- 大規模 scan 中に同一 directory の language-map override lookup が続く場合の fast path を追加しました。
