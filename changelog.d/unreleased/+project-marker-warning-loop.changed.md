---
title: Avoid project marker warning iterator overhead
category: changed
---

## English

- Build project marker fingerprint warnings with a direct loop instead of LINQ to avoid iterator overhead during full indexing scans.

## 日本語

- full indexing scan 中の iterator overhead を避けるため、project marker fingerprint の warning 配列を LINQ ではなく直接ループで構築するようにしました。
