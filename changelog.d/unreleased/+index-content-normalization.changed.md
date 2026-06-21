---
category: changed
affected:
  - src/CodeIndex/Indexer/Scanning/FileContentLoader.cs
  - tests/CodeIndex.Tests/FileIndexerTests.cs
---

## English

- **Indexing now performs less duplicate work while loading file content** — content loading now combines line-ending cleanup, line-leading invisible stripping, and line counting, and stable files are read directly into their final byte array so large indexes spend less time in repeated scans and buffer copies.

## 日本語

- **インデックス作成時のファイル内容読み込みで重複作業が減りました** — content loading は改行正規化、行頭不可視文字の除去、行数計測をまとめて行い、安定したファイルは最終的な byte 配列へ直接読み込むため、大きなインデックスで繰り返し走査と buffer copy にかかる時間を減らします。
