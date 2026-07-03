---
title: Skip empty hook disposal snapshots
category: changed
---

## English

- Skipped post-extraction hook disposal enumeration when no hooks were loaded, avoiding unnecessary work in the default indexing path.

## 日本語

- post-extraction hook が読み込まれていないときは dispose 時の列挙処理を省き、既定の indexing path で不要な処理を避けるようにしました。
