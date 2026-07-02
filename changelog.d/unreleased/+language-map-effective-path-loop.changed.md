---
title: Avoid language map effective path iterator overhead
category: changed
---

## English

- Load effective language map override files directly from cached path stamps to avoid an iterator allocation during indexing cache misses.

## 日本語

- indexing 中の cache miss で iterator allocation を避けるため、effective language map override file を cached path stamp から直接読み込むようにしました。
