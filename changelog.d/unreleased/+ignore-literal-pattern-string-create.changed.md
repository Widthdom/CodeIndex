---
title: Reduce ignore literal pattern allocations
category: changed
---

## English
- Build literal ignore-rule patterns with a pre-scan and `string.Create`, avoiding wasted builders for wildcard rules.

## 日本語
- ignore rule の literal pattern を事前 scan と `string.Create` で構築し、wildcard rule で不要な builder allocation が出ないようにしました。
