---
title: Avoid empty Python symbol lookup allocations
category: changed
---

## English

- Reduced Python reference extraction allocations by creating definition-container and header-symbol lookup dictionaries only when matching Python symbols exist.

## 日本語

- 対象の Python symbol がある場合だけ definition-container lookup と header-symbol lookup の dictionary を作るようにして、Python 参照抽出の割り当てを削減しました。
