---
title: Cache TypeScript namespace parameter shadows
category: changed
---

## English

- Cache TypeScript namespace alias parameter-shadow ranges by alias name so repeated imports of the same alias do not rescan every prepared line.

## 日本語

- TypeScript namespace alias の parameter shadow range を alias 名ごとにキャッシュし、同じ alias の import が繰り返される場合に prepared line 全体を再走査しないようにしました。
