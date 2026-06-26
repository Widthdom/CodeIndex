---
category: changed
affected:
  - src/CodeIndex/Cli/IndexCommandRunner.cs
  - src/CodeIndex/Cli/IndexCommandRunner.FullScan.cs
  - tests/CodeIndex.Tests/IndexCommandRunnerTests.cs
---

## English

- **Full scans retain less read-ahead memory during sequential extraction** — non-precomputed full-scan workers now validate file content before handing work to the writer, and their read-ahead queue is bounded to one item, so queued work no longer keeps raw bytes alongside decoded content while preserving file issue reporting.

## 日本語

- **逐次抽出の full scan で先読みメモリ保持を削減しました** — precompute しない full scan worker は writer へ処理を渡す前にファイル内容を検証し、先読み queue を 1 item に制限するようになりました。これにより、file issue の記録を維持しながら queued work が decoded content と raw bytes を同時に保持しないようになりました。
