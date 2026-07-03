---
title: Avoid XAML markup segment trim strings
category: changed
---

## English

- **XAML markup argument splitting now trims segments with spans** — reference extraction avoids substring allocations while walking comma-separated markup extension arguments in large XAML files.

## 日本語

- **XAML markup argument 分割が segment の trim に span を使うようになりました** — 大きな XAML ファイルで markup extension の comma-separated argument を走査するとき、substring 割り当てを避けます。
