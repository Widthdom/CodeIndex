---
title: Avoid Scala next-header trim-start allocations
category: changed
---

## English

- **Scala braceless header scanning now avoids trim-start strings for lookahead lines** — symbol extraction keeps the original line and first non-whitespace column when checking continuation lines in large Scala declarations.

## 日本語

- **Scala の braceless header 走査で先読み行の trim-start 文字列生成を避けるようになりました** — シンボル抽出は巨大な Scala 宣言の継続行を確認するとき、元の行と最初の非空白列を保持して判定します。
