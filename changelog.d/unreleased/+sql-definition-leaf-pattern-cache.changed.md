---
title: Cache SQL definition leaf patterns
category: changed
---

## English

- Cache SQL definition leaf-name regex patterns while building definition-span suppressions, avoiding repeated qualified-name splitting for duplicate symbols in large SQL files.

## 日本語

- SQL の definition span suppression 構築中に definition leaf 名の正規表現パターンをキャッシュし、大規模 SQL ファイルで重複 symbol の qualified-name 分割を繰り返さないようにしました。
