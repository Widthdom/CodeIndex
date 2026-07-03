---
category: changed
affected:
  - src/CodeIndex/Indexer/Scanning/FileIndexer.IgnoreRuleLoading.cs
---

## English

- **Reduced ignore-rule loading allocations** - directory scans now allocate ignore-rule lists only when a `.gitignore` or `.cdidxignore` file actually contributes rules, trimming hot-path allocation in large trees with sparse ignore files.

## 日本語

- **ignore rule loading の allocation を削減しました** - directory scan は `.gitignore` または `.cdidxignore` が実際に rule を追加した場合だけ ignore-rule list を確保するようになり、ignore file が少ない大規模 tree の hot path allocation を減らします。
