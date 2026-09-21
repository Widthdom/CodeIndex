---
category: internal
affected:
  - tests/CodeIndex.Tests/PerformanceTests.cs
  - TESTING_GUIDE.md
---

## English

- **Stabilized the source-line splitting allocation test in release CI** — removed the allocation comparison with `string.Split`, whose separator arrays can be reused from a shared pool. The test retains the existing 610,000-byte allocation ceiling and now compares every output line from the custom splitter with the generic split result. Production behavior is unchanged.

## 日本語

- **リリースCIの行分割メモリ割り当てテストを安定化しました** — 区切り位置の配列を共有プールから再利用できる `string.Split` との割り当て量比較を削除しました。既存の610,000バイト未満という上限を維持し、専用の行分割処理の出力を汎用処理の結果と全行比較するようにしました。製品の動作は変わりません。
