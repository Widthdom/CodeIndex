---
title: Reduce markdown anchor normalization allocations
category: changed
---

## English
- Normalize Markdown anchors with a single character buffer instead of lowercasing, list growth, array conversion, and trimming passes during reference extraction.

## 日本語
- reference extraction 中の Markdown anchor normalization で、lowercase 文字列、list growth、array conversion、trim の一時 allocation を避け、単一の char buffer で処理するようにしました。
