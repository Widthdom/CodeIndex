---
category: changed
affected:
  - src/CodeIndex/Indexer/SolidityLanguageSupport.cs
  - tests/CodeIndex.Tests/SymbolExtractorSolidityTests.cs
---

## English

- **Solidity comment and string masking now reuses unmodified lines** — Solidity symbol and reference extraction no longer allocates a new line array or per-line builder when files do not contain comments or strings that need masking.

## 日本語

- **Solidity のコメント・文字列マスキングで未変更行を再利用するようになりました** — Solidity のシンボル抽出と参照抽出は、マスク対象のコメントや文字列がないファイルで新しい行配列や行ごとの builder を割り当てなくなりました。
