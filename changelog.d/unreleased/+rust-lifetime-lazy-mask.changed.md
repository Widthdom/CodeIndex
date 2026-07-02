---
title: Lazily mask Rust lifetime tokens
category: changed
---

## English

- Avoided character array allocation while scanning Rust lines with apostrophes unless a lifetime token actually needs masking.

## 日本語

- Rust 行で apostrophe を走査する際、lifetime token を実際にマスクする必要が出るまで文字配列の割り当てを避けるようにしました。
