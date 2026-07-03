---
title: Lazily allocate comment mask buffers
category: changed
---

## English

- Avoided per-line character array allocation in Go, CSS, Sass, and Stylus masking paths until a comment, string, or URL token actually needs masking.

## 日本語

- Go、CSS、Sass、Stylus のマスク処理で、コメント・文字列・URL token を実際に隠す必要が出るまで行ごとの文字配列割り当てを避けるようにしました。
