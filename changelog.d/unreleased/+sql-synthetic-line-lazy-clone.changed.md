---
category: changed
affected:
  - src/CodeIndex/Indexer/Symbols/SymbolExtractor.cs
---

## English

- **SQL symbol extraction now avoids synthetic line-array copies when no masking is needed** — indexing large SQL files now reuses the original line array unless comments or string literals actually require masking for supplemental symbol extraction.

## 日本語

- **SQL シンボル抽出でマスク不要時の synthetic 行配列コピーを避けるようになりました** — 大きな SQL ファイルの indexing では、補助シンボル抽出でコメントや文字列リテラルのマスクが実際に必要な場合だけ行配列をコピーします。
