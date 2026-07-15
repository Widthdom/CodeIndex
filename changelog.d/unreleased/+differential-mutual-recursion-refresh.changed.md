---
category: changed
---

## English

- Refresh mutual-recursion flags only for references whose stored value differs from the current graph result, avoiding rewrites of every reference row while preserving legacy normalization and statement atomicity.

## 日本語

- 相互再帰フラグの保存値が現在のグラフ判定と異なる参照だけを更新し、legacy値の正規化とstatementのatomicityを維持しながら、全参照行の再書き込みを省きました。
