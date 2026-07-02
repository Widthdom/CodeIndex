---
title: Lazily allocate Elixir symbol masks
category: changed
---

## English

- Avoided copying Elixir symbol-scan lines unless comments, strings, or sigils actually need masking.

## 日本語

- Elixir の symbol scan で、コメント、文字列、sigil のマスクが必要な行だけをコピーするようにしました。
