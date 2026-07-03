---
title: Lazily allocate Dockerfile and shell name sets
category: changed
affected:
  - src/CodeIndex/Indexer/References/Languages/DockerfileReferenceExtractor.cs
  - src/CodeIndex/Indexer/References/Languages/ShellReferenceExtractor.cs
---

## English

- **Dockerfile and shell reference extraction now lazily allocates name sets** — files without stage, variable, callable, or global-alias symbols avoid empty `HashSet` allocations while building per-file reference context.

## 日本語

- **Dockerfile / shell の参照抽出が name set を遅延確保するようになりました** — stage、variable、callable、global alias のシンボルが無いファイルで、ファイル単位の参照コンテキスト構築時に空 `HashSet` を割り当てないようにしました。
