---
category: changed
affected:
  - src/CodeIndex/Indexer/Scanning/FileIndexer.LanguageDetection.cs
---

## English

- **Filename-prefix language detection skips impossible names** — ordinary files no longer scan the Dockerfile/Containerfile/Makefile prefix table unless their first character and length can match a suffixed special filename.

## 日本語

- **filename-prefix language detection が一致不能な名前を skip** — 通常ファイルでは、先頭文字と長さが suffixed special filename に一致し得る場合だけ Dockerfile/Containerfile/Makefile prefix table を走査するようにしました。
