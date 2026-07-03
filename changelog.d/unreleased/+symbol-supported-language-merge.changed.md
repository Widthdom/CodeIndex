---
title: Merge symbol supported languages without LINQ
category: changed
---

## English

- **Merged symbol extractor supported languages with a pre-sized ordered set** — `cdidx` now avoids temporary LINQ iterator and distinct buffers while combining built-in, additional, and plugin symbol languages during indexing setup.

## 日本語

- **シンボル抽出器の対応言語を事前サイズ指定した順序付き集合で統合** — `cdidx` はインデックス準備時に組み込み・追加・プラグインシンボル言語を結合する際、一時的な LINQ iterator と distinct 用バッファを避けるようになりました。
