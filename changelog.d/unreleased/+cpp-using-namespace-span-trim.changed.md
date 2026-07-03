---
title: Avoid C++ using-namespace trim strings
category: changed
---

## English

- **C++ using-namespace symbol extraction now trims targets with spans** — indexing large C++ files avoids intermediate slice strings when stripping comments and semicolons from `using namespace` directives.

## 日本語

- **C++ using namespace シンボル抽出が target の trim に span を使うようになりました** — 大きな C++ ファイルを index するとき、`using namespace` directive から comment や semicolon を取り除く中間 slice 文字列生成を避けます。
