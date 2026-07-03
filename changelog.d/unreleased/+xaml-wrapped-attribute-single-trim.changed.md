---
title: Wrapped XAML attribute extraction avoids duplicate trims
category: changed
---

## English

- **Wrapped XAML attribute extraction avoids duplicate trims** — wrapped `x:Name` and event-handler attributes now pass raw captured values to the shared XAML attribute helper, which already trims once before creating symbols.

## 日本語

- **wrapped XAML属性抽出で重複trimを避けるようになりました** — wrapped `x:Name` とevent handler属性は、symbol作成前に一度だけtrimする共有XAML属性helperへraw capture値を渡すようになりました。
