---
title: Fast-path dotted unquoted SQL names
category: changed
---

## English

- Parse dotted unquoted SQL names without the quoted-name `StringBuilder` loop when normalizing references and containers.

## 日本語

- SQL 参照やコンテナ名の正規化で、未クォートの dotted name はクォート対応の `StringBuilder` ループを通さずに解析するようにしました。
