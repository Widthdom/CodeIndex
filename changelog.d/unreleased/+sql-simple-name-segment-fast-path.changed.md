---
title: Fast-path simple SQL name segments
category: changed
---

## English

- Avoid `StringBuilder` work for simple unquoted SQL identifiers when normalizing names and scanning qualified-name segments in reference extraction.

## 日本語

- SQL 参照抽出で単純な未クォート識別子を正規化・qualified name セグメント走査する際の `StringBuilder` 処理を避けるようにしました。
