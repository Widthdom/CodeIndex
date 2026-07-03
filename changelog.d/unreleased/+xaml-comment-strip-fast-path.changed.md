---
title: Avoid XML comment-strip allocation on plain XAML lines
category: changed
---

## English

- Reuse plain XAML/XML lines directly when comment stripping has no work to do, avoiding per-line `StringBuilder` allocation during reference extraction.

## 日本語

- XAML/XML のコメント除去で処理対象のコメントがない行は元の行をそのまま使い、参照抽出中の行ごとの `StringBuilder` 割り当てを避けるようにしました。
