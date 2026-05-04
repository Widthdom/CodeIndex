---
category: internal
affected:
  - src/CodeIndex/Indexer/ReferenceExtractor.cs
  - src/CodeIndex/Indexer/RustReferenceExtractor.cs
  - src/CodeIndex/Indexer/TypeScriptReferenceExtractor.cs
  - docs/DEVELOPER_GUIDE.md
---

## English

- **Added TypeScript and Rust reference-extractor helpers** — TypeScript type-reference emission now routes through a dedicated helper, and Rust macro/raw-identifier reference handling moved into `RustReferenceExtractor` without changing indexed reference semantics.

## 日本語

- **TypeScript と Rust の reference extractor helper を追加しました** — TypeScript の型参照発行を専用 helper 経由にし、Rust の macro / raw identifier 参照処理を `RustReferenceExtractor` へ移して、既存のインデックス済み reference の意味は維持しました。
