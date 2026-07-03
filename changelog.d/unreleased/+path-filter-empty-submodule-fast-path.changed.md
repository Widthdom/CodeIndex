---
title: Skip submodule path work when none are configured
category: changed
---

## English

- **Skipped submodule path bookkeeping for repositories without submodules** — path filtering now avoids per-segment cumulative path strings and submodule lookups when no `.gitmodules` paths were loaded.

## 日本語

- **submodule 未設定リポジトリで submodule path 管理をスキップ** — path filtering は `.gitmodules` 由来の path が読み込まれていない場合、各セグメントの累積path文字列生成と submodule lookup を避けるようになりました。
