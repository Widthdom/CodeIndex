---
category: changed
affected:
  - src/CodeIndex/Database/DbWriter.BatchSql.cs
  - src/CodeIndex/Database/DbWriter.ChunkSymbolBatches.cs
  - src/CodeIndex/Database/DbWriter.References.cs
---

## English

- **Large-file writes reuse folded-name results across SQL batches** — Symbol
  and reference inserts now retain a size-capped folded-name cache for the full
  file call, avoiding repeated normalization of common names and containers when
  a file spans multiple 500-row batches in any language without retaining an
  unbounded set of generated names.

## 日本語

- **巨大ファイルの書き込みで SQL batch 間の folded-name 結果を再利用します** —
  symbol と reference の INSERT が、件数上限付き folded-name cache をファイル
  呼び出し全体で保持するようになりました。任意の言語で1ファイルが500行超の
  複数 batch に跨る場合も、生成された一意名を無制限に保持せず、共通名と
  container の再正規化を避けます。
