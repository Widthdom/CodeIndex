---
title: Reuse hook callback lists
category: changed
---

## English

- **Reused hook callback payload lists when available** — isolated post-extraction hook callbacks now avoid copying symbol and reference payloads that are already backed by `List<T>`.

## 日本語

- **hook callback の payload list を再利用** — isolated post-extraction hook callback は、symbol / reference payload が既に `List<T>` で保持されている場合に余分なコピーを避けるようになりました。
