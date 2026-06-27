---
category: changed
affected:
  - src/CodeIndex/Indexer/References/Languages/TypeScriptReferenceExtractor.cs
  - src/CodeIndex/Indexer/References/Languages/SwiftReferenceExtractor.cs
---

## English

- **Type alias reference expansion now avoids per-line LINQ distinct pipelines** — TypeScript and Swift alias reference extraction now walks alias bindings directly while preserving first-seen alias order, reducing iterator and set-enumerator overhead when large files have many alias declarations.

## 日本語

- **type alias reference 展開が行ごとの LINQ distinct pipeline を避けるようになりました** — TypeScript と Swift の alias reference extraction は first-seen alias order を保ったまま alias binding を直接走査し、alias 宣言が多い大きなファイルでの iterator と set-enumerator のオーバーヘッドを削減します。
