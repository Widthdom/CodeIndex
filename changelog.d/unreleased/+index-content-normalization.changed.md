---
category: changed
affected:
  - src/CodeIndex/Indexer/Scanning/FileContentLoader.cs
  - tests/CodeIndex.Tests/FileIndexerTests.cs
---

## English

- **Indexing now normalizes file content with fewer full-string passes** — content loading now combines line-ending cleanup, line-leading invisible stripping, and line counting so large files spend less time in repeated post-read scans.

## 日本語

- **インデックス作成時のファイル内容正規化がより少ない全体走査で済むようになりました** — content loading は改行正規化、行頭不可視文字の除去、行数計測をまとめて行うため、大きなファイルで読み込み後の繰り返し走査にかかる時間を減らします。
