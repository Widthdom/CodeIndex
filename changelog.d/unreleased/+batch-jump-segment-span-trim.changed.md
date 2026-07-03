---
title: Batch jump segment trimming avoids intermediate strings
category: changed
---

## English

- **Batch jump segment trimming avoids intermediate strings** — Batch reference extraction now trims command segments with spans and only materializes text when the jump-target regex must run, reducing temporary strings while indexing large `.bat` and `.cmd` files.

## 日本語

- **Batchのjump segment trimで中間文字列を避けるようになりました** — Batch参照抽出はcommand segmentをspanでtrimし、jump targetの正規表現が必要な時だけ文字列化するため、大きな`.bat` / `.cmd`ファイルのインデックス作成時に一時文字列を減らします。
