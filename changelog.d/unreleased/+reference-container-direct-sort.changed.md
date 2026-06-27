---
category: changed
affected:
  - src/CodeIndex/Indexer/References/ReferenceExtractor.cs
  - src/CodeIndex/Indexer/References/ReferenceExtractor.TypeReferences.cs
---

## English

- **Reference container candidate sorting now avoids LINQ sort pipelines** — reference extraction now precomputes container span keys and sorts bounded container candidates directly while preserving stable tie ordering, reducing allocation overhead in files with many nested symbols.

## 日本語

- **reference container 候補の並べ替えで LINQ sort pipeline を避けるようになりました** — 参照抽出は container span key を事前計算し、同値時の安定順序を保ったまま bounded container 候補を直接ソートすることで、入れ子シンボルが多いファイルでの割り当てオーバーヘッドを削減します。
