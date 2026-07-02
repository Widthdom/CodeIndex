---
title: Avoid scan subdirectory wrapper iterator overhead
category: changed
---

## English

- Process directory traversal subdirectory paths directly in the normal scan path to avoid a wrapper iterator allocation during full indexing scans.

## 日本語

- full indexing scan 中の wrapper iterator allocation を避けるため、通常の directory traversal では subdirectory path を直接処理するようにしました。
