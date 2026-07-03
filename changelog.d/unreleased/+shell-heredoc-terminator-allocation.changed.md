---
title: Avoid shell heredoc terminator trim allocations
category: changed
---

## English

- **Shell heredoc terminator matching now avoids per-line trim strings** — shell symbol extraction compares heredoc terminators by source range, reducing allocations while indexing large shell scripts with heredoc bodies.

## 日本語

- **shell heredoc 終端照合で行ごとの trim 文字列生成を避けるようになりました** — shell シンボル抽出は heredoc 終端をソース範囲で比較し、heredoc 本文を含む巨大 shell script のインデックス作成時の割り当てを減らします。
