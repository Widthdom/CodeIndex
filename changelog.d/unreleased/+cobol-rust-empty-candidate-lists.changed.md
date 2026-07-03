---
title: Avoid empty COBOL and Rust candidate lists
category: changed
affected:
  - src/CodeIndex/Indexer/References/ReferenceExtractor.cs
---

## English

- **COBOL callable and Rust enum reference setup now skips empty candidate lists** — files without matching symbols avoid sort buffers and per-line empty candidate scans during indexing.

## 日本語

- **COBOL callable / Rust enum の reference 準備が空 candidate list を作らないようになりました** — 対象 symbol が無いファイルでは、indexing 中の sort buffer と行ごとの空 candidate scan を避けます。
