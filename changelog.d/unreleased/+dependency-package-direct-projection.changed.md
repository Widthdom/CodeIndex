---
category: changed
affected:
  - src/CodeIndex/Indexer/DependencyPackageExtractor.cs
---

## English

- **Dependency package extraction now projects records directly** — Package symbol and dependency reference materialization now pre-sizes result lists and fills them with direct loops, reducing iterator overhead for large manifest and lock files with many package entries.

## 日本語

- **dependency package extraction が record を直接投影するようになりました** — package symbol と dependency reference の materialization は result list を事前サイズ指定して direct loop で埋めるようになり、多数の package entry を含む大きな manifest や lock file での iterator overhead を削減します。
