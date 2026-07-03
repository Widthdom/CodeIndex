---
title: XAML markup tail normalization avoids trim chains
category: changed
---

## English

- **XAML markup tail normalization avoids trim chains** — XAML reference extraction now trims quoted markup arguments, static-member suffixes, and closing markup characters with spans so large XAML-heavy indexes allocate fewer temporary strings.

## 日本語

- **XAML markup末尾の正規化でtrim連鎖を避けるようになりました** — XAML参照抽出は引用付きmarkup引数、static memberの接尾辞、閉じmarkup文字をspanでtrimするようになり、大規模なXAML中心のインデックス作成で一時文字列の割当を減らします。
