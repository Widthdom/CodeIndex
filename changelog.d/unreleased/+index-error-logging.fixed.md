---
category: fixed
affected:
  - src/CodeIndex/Cli/IndexCommandRunner.FullScan.cs
  - src/CodeIndex/Cli/IndexCommandRunner.Update.cs
  - tests/CodeIndex.Tests/GlobalToolLogTests.cs
---

## English

- **Index file failures now include sanitized stack traces in the persistent log** — per-file index exceptions now use the global tool log's stack-preserving exception formatter, making `.sql`, `.js`, and other extraction failures diagnosable without changing stderr or JSON error output.

## 日本語

- **インデックス対象ファイル単位の失敗で、永続ログに sanitization 済み stack trace を残すようにしました** — `.sql` や `.js` などの抽出例外を原因調査できるよう、stderr / JSON のエラー出力は変えずに global tool log の stack 付き例外フォーマッタを使います。
