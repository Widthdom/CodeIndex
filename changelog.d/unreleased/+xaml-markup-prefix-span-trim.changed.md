---
title: Avoid XAML markup prefix trim strings
category: changed
---

## English

- **XAML markup argument normalization now trims fixed prefixes with spans** — reference extraction avoids intermediate slice strings for common `{x:Type ...}`, `{x:Static ...}`, `TypeName=`, `Member=`, and `ResourceKey=` forms in large XAML files.

## 日本語

- **XAML markup argument 正規化が固定 prefix の trim に span を使うようになりました** — 大きな XAML ファイルでよく出る `{x:Type ...}`、`{x:Static ...}`、`TypeName=`、`Member=`、`ResourceKey=` 形式の中間 slice 文字列生成を避けます。
