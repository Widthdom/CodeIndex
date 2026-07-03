---
title: Lazily allocate CSharp qualified pattern lookups
category: changed
---

## English

- Reduced C# reference extraction allocations by creating qualified enum, constant-pattern, and type-pattern lookup dictionaries only when matching symbols are present.

## 日本語

- 対象 symbol がある場合だけ C# の qualified enum、constant-pattern、type-pattern lookup dictionary を作るようにして参照抽出の割り当てを削減しました。
