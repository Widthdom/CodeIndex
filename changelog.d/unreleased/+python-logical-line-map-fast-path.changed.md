---
title: Fast-path single-line Python logical reference maps
category: changed
---

## English

- Avoid allocating physical line/column map arrays for single-line Python logical reference headers and type-factory statements; multi-line cases still use the existing remap arrays.

## 日本語

- 単一行の Python 論理参照ヘッダーと型ファクトリ文では物理行・列マップ配列を割り当てず、複数行の場合だけ従来のリマップ配列を使うようにしました。
