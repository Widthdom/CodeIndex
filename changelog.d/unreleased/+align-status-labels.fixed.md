---
category: fixed
affected:
  - src/CodeIndex/Cli/ConsoleUi.cs
  - src/CodeIndex/Cli/IndexCommandRunner.cs
  - src/CodeIndex/Cli/QueryCommandRunner.cs
  - USER_GUIDE.md
---

## English

- **Aligned human status and index summary labels** — `cdidx index` and `cdidx status` now pad label/value rows from a shared formatter so labels such as `SQL graph` no longer shift the colon out of column.

## 日本語

- **human status / index summary のラベル位置を揃えました** — `cdidx index` と `cdidx status` は共通 formatter で label/value 行を padding するようになり、`SQL graph` などの長いラベルでもコロン位置がずれなくなりました。
