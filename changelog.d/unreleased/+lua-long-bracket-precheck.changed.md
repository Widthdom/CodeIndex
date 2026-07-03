---
title: Tighten Lua long-bracket prechecks
category: changed
---

## English

- Only run Lua long comment/string masking when a line contains a plausible long-bracket opener (`[[` or `[=`), avoiding full-line masking for ordinary table/index usage.

## 日本語

- Lua の long comment/string マスクを、`[[` または `[=` を含む long-bracket 開始候補がある場合だけ実行し、通常の table/index 利用だけのファイルで全行マスクを避けるようにしました。
