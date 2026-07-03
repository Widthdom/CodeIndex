---
title: Avoid plugin extension map materialization for single lookups
category: changed
---

## English

- Added a direct plugin extension lookup path so language detection and unknown-extension checks avoid materializing the full plugin extension map for each file.

## 日本語

- plugin extension を直接 lookup する経路を追加し、言語判定と unknown-extension 判定でファイルごとに plugin extension map 全体を materialize しないようにしました。
