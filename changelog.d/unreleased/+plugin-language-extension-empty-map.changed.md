---
title: Reuse empty plugin extension maps
category: changed
---

## English

- Reused an immutable empty plugin extension map when no extractor extensions are registered, avoiding repeated dictionary allocations during language detection.

## 日本語

- extractor extension が未登録のときは immutable な空 plugin extension map を再利用し、言語判定中の dictionary 割り当てを繰り返さないようにしました。
