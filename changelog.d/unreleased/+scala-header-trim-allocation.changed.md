---
title: Avoid Scala header continuation trim allocations
category: changed
---

## English

- **Scala braceless class scanning now avoids trim strings for continuation checks** — symbol extraction uses span-based suffix checks when deciding whether large Scala headers continue onto following lines.

## 日本語

- **Scala の braceless class 走査で継続判定用の trim 文字列生成を避けるようになりました** — シンボル抽出は巨大な Scala header が次行へ続くかを判定するとき、span ベースの suffix 判定を使います。
