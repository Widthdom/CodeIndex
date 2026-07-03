---
title: Avoid catch type prefix trim strings
category: changed
---

## English

- **Catch type reference extraction now trims candidate prefixes with spans** — indexing large files avoids substring allocations when deciding whether a trailing catch identifier is a variable or part of a qualified type.

## 日本語

- **catch type reference 抽出が候補 prefix の trim に span を使うようになりました** — 大きなファイルを index するとき、catch の末尾識別子が変数か qualified type の一部かを判定するための substring 割り当てを避けます。
