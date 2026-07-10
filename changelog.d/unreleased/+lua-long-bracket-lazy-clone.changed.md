---
category: changed
affected:
  - src/CodeIndex/Indexer/References/Languages/LuaReferenceExtractor.cs
  - tests/CodeIndex.Tests/ReferenceExtractorLuaTests.cs
---

## English

- **Lua long-bracket masking now reuses unchanged line arrays** — Lua reference extraction now allocates a replacement line array only when long comments or long strings actually mask content.

## 日本語

- **Lua の long bracket マスキングで未変更の行配列を再利用するようになりました** — Lua の参照抽出は、long comment や long string が実際に内容をマスクする場合だけ置換用の行配列を割り当てます。
