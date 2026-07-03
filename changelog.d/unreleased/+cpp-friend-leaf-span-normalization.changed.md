---
title: Avoid C++ friend leaf trim allocations
category: changed
---

## English

- **C++ friend reference extraction now normalizes qualified leaves with spans** — indexing large C++ files avoids intermediate trim and slice strings while preserving friend type/function reference names.

## 日本語

- **C++ friend 参照抽出が qualified leaf の正規化に span を使うようになりました** — 巨大 C++ ファイルの indexing で中間的な trim / slice 文字列生成を避けつつ、friend type/function の参照名を維持します。
