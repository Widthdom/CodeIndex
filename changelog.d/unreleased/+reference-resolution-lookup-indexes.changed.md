---
category: changed
affected:
  - src/CodeIndex/Database/DbContext.cs
  - tests/CodeIndex.Tests/DatabaseTests.cs
  - TESTING_GUIDE.md
---

## English

- **Large reference graphs now resolve through bounded composite lookups** — Fresh databases and read migrations add file/name and folded-name/container indexes for source and ranked-candidate resolution across all languages, plus a partial covering index for reverse resolved-edge probes used by mutual-recursion refresh.

## 日本語

- **巨大な参照 graph の解決を bounded な複合 lookup で行うようになりました** — fresh database と read migration に、全言語の参照元・rank 候補解決で使う file/name および folded-name/container index と、相互再帰 refresh の解決済み逆辺 probe に使う partial covering index を追加します。
