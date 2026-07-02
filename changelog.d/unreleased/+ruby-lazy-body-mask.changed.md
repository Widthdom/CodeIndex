---
title: Lazily allocate Ruby body masks
category: changed
---

## English

- Avoided character array allocation for Ruby body-scan lines until comments, strings, percent literals, or heredocs actually require masking.

## 日本語

- Ruby の body scan で、コメント・文字列・percent literal・heredoc を実際にマスクする必要が出るまで文字配列の割り当てを避けるようにしました。
