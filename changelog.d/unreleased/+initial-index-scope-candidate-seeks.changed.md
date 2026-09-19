---
category: changed
affected:
  - src/CodeIndex/Database/DbWriter.References.cs
  - tests/CodeIndex.Tests/DatabaseTests.cs
  - docs/initial-index-performance.md
  - TESTING_GUIDE.md
---

## English

- **Reference graph construction limits common-name candidate searches to eligible scopes** — initial indexing, scoped refreshes and retained-graph rebuilds use the existing file and container indexes before ranking candidates. This reduces work in repositories with many unrelated declarations sharing a name across all supported graph languages, while preserving tied candidates, language boundaries, C# attribute suffixes and references without a known source container.

## 日本語

- **参照グラフ構築時の同名候補の検索を対象スコープに限定します** — 初回インデックス・差分更新・保持グラフの再構築で、既存のファイル／コンテナ索引から候補を取得して順位付けします。全対応グラフ言語で、無関係な同名宣言が多いリポジトリの処理量を削減し、同順位候補・言語境界・C# attribute の接尾辞・参照元コンテナが不明な参照の挙動を維持します。
