---
title: Avoid PowerShell splat key HashSet allocation
category: changed
---

## English

- Deduplicate small PowerShell splat hashtable key lists without allocating a `HashSet` for the common no-duplicate case.

## 日本語

- PowerShell splat hashtable の小さな key list は、重複がない一般ケースで `HashSet` を割り当てずに重複排除するようにしました。
