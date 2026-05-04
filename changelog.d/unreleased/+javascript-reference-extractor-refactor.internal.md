---
category: internal
affected:
  - docs/DEVELOPER_GUIDE.md
  - src/CodeIndex/Indexer/JavaScriptReferenceExtractor.cs
  - src/CodeIndex/Indexer/ReferenceExtractor.cs
---

## English

- **Split JavaScript and TypeScript constructor reference handling into a dedicated helper** — parenless zero-argument constructor reference emission now lives in `JavaScriptReferenceExtractor`, reducing `ReferenceExtractor` size while preserving existing JS/TS reference output.

## 日本語

- **JavaScript / TypeScript のコンストラクタ参照処理を専用ヘルパーへ分離** — 括弧なし zero-argument constructor の参照発行を `JavaScriptReferenceExtractor` に移し、既存の JS/TS 参照出力を維持したまま `ReferenceExtractor` を小さくしました。
