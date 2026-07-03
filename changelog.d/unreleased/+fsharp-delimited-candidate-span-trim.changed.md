---
title: Avoid F# delimited candidate trim strings
category: changed
---

## English

- **F# record and union extraction now trims delimited candidates with spans** — symbol extraction avoids chained trim strings while scanning large F# type declarations for fields and union cases.

## 日本語

- **F# record / union 抽出が delimited candidate の trim に span を使うようになりました** — 大きな F# type 宣言から field や union case を抽出するとき、連鎖した trim 文字列生成を避けます。
