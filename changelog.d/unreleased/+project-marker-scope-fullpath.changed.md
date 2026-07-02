---
category: changed
affected:
  - src/CodeIndex/Indexer/Scanning/FileIndexer.ProjectMarkers.cs
---

## English

- **Hotspot family scope derivation avoids repeated full-path normalization** — indexing now normalizes each file path once before walking project marker directories, reducing per-file path work on large codebases.

## 日本語

- **hotspot family scope の導出で重複する full-path 正規化を回避** — indexing 時に project marker ディレクトリを辿る前のファイルパス正規化を 1 回に抑え、大規模コードベースでのファイルごとのパス処理を削減しました。
