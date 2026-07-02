---
title: Lazily allocate Lua long-bracket masks
category: changed
---

## English

- Allocate Lua long comment/string mask buffers only for lines that actually need masking, reusing unchanged lines when long-bracket candidates are skipped inside strings or comments.

## 日本語

- Lua の long comment/string マスク用 buffer を実際にマスクが必要な行だけに遅延確保し、文字列やコメント内で long-bracket 候補をスキップした未変更行を再利用するようにしました。
