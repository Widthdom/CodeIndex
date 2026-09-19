---
category: changed
affected:
  - src/CodeIndex/Indexer/ReferenceOccurrenceSearch.cs
  - src/CodeIndex/Indexer/CSharpTypeReferenceArity.cs
  - src/CodeIndex/Database/DbWriter.References.cs
  - tests/CodeIndex.Tests/ReferenceOccurrenceSearchTests.cs
  - docs/initial-index-performance.md
  - TESTING_GUIDE.md
---

## English

- **Reference position lookups avoid repeated scans of dense source lines** — all language writers use recorded columns when an exact non-overlapping name match is proven, and stop searching once later occurrences cannot be nearer. C# type and invocation arity lookups share the shortcut and one-pass fallback, preserving trimmed-context columns, escaped/Unicode names, constructor recognition, ties and legacy/plugin fallback behavior.

## 日本語

- **参照位置の検索で密なソース行の反復走査を削減します** — 全言語共通のwriterは、非重複の名前一致を証明できる場合に記録列を使い、後続の出現がより近くなり得ない位置で検索を止めます。C#の型引数数・呼出し引数数の検索も同じ短縮処理と1回の走査によるfallbackを共有し、trim済みcontextの列・escape／Unicode名・constructor判定・同距離時の選択・旧形式／pluginのfallbackを維持します。
