---
category: fixed
affected:
  - src/CodeIndex/Cli/IndexCommandRunner.cs
  - src/CodeIndex/Cli/QueryCommandRunner.cs
  - USER_GUIDE.md
---

## English

- **Aligned human-readable status labels** — `cdidx index` and `cdidx status` now pad shorter summary labels to the longest label in the block, so `SQL graph:` and `Languages:` line up with surrounding fields without shifting only the long labels.

## 日本語

- **人間向けステータス表示のラベル位置を揃えました** — `cdidx index` と `cdidx status` は、ブロック内の最長ラベルに合わせて短いサマリラベルをパディングするようになり、`SQL graph:` と `Languages:` だけをずらさずに周辺項目とコロン位置が揃います。
