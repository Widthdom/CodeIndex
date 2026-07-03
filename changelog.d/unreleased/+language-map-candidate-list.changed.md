---
title: Avoid language map candidate iterator overhead
category: changed
---

## English

- Build language map override candidate paths directly during indexing cache misses to avoid iterator and array-copy overhead across language detection.

## 日本語

- indexing 中の cache miss で language map override 候補 path を直接構築し、language detection 全体で iterator と array copy の overhead を避けるようにしました。
