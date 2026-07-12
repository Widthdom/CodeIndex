---
category: fixed
affected:
  - tests/CodeIndex.Tests/TestDeterminism.cs
---

## English

- **Concurrent test start gates no longer report a timeout after every worker is ready** — the shared test helper now recognizes the boundary race where the final worker reaches the gate immediately after the timed wait expires, preventing spurious release-pipeline failures under load.

## 日本語

- **全ワーカーの準備完了後に並行テストの開始ゲートがタイムアウトを誤報しないよう修正しました** — 最後のワーカーが時間制限付き待機の終了直後にゲートへ到達する境界競合を共通テストヘルパーが認識するようにし、高負荷時のリリースパイプラインの偽失敗を防ぎます。
