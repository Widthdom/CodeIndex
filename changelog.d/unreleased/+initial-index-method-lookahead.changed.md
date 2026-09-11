---
category: changed
affected:
  - src/CodeIndex/Indexer/Symbols/SymbolExtractor.CSharpScanner.cs
  - src/CodeIndex/Indexer/Symbols/SymbolExtractor.Contracts.cs
---

## English

- **Bound confirmed method lookahead during indexing** — C#, Razor, Blazor and CSHTML stop merging method bodies once an accessor is ruled out, reducing temporary allocations in large files while preserving ranges and recovering affected constructors. Extractor contract 19 refreshes existing rows on the next ordinary full scan.

## 日本語

- **インデックス作成時の確認済みメソッドの先読みを制限** — C#・Razor・Blazor・CSHTML で accessor でないと判明した時点で body の連結を止め、大きいファイルの一時割り当てを減らします。範囲を維持し、影響を受けていた constructor も回復します。次の通常フルスキャンでは抽出契約19により既存行も更新します。
