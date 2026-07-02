---
title: Avoid gitmodules parser iterator overhead
category: changed
---

## English
- Avoid an iterator allocation while parsing `.gitmodules` submodule paths during repository scans.

## 日本語
- repository scan 中に `.gitmodules` の submodule path を解析するときの iterator allocation を回避しました。
