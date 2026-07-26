---
category: fixed
affected:
  - src/CodeIndex/Indexer/Scanning/FileIndexer.LanguageDetection.cs
  - tests/CodeIndex.Tests/PerformanceTests.cs
  - DEVELOPER_GUIDE.md
  - TESTING_GUIDE.md
---

## English

- **Reduced C / C++ header-detection allocations for large repositories** —
  Bounded lexical samples for ambiguous `.h` files are now scanned by newline
  index instead of materializing an array and a string for every sampled line.

## 日本語

- **巨大 repository の C / C++ header 判定 allocation を削減しました** —
  曖昧な `.h` file の bounded lexical sample を、sampled line ごとの array と string
  に実体化せず newline index で走査するようにしました。
