---
category: fixed
affected:
  - tests/CodeIndex.Tests/LspServerTests.cs
  - tests/CodeIndex.Tests/PostExtractionHookTests.cs
  - TESTING_GUIDE.md
---

## English

- **Release validation no longer flakes when macOS runners are heavily loaded** — LSP cancellation coverage now awaits an asynchronous signal without blocking thread-pool progress, and post-extraction hook timeout coverage keeps a wider separation between the callback budget and the delayed worker completion.

## 日本語

- **macOS runner の高負荷時にも release validation が不安定に失敗しないようになりました** — LSP cancellation coverage は thread pool の進行を block しない非同期 signal を待機し、post-extraction hook timeout coverage は callback budget と遅延 worker の完了時刻に十分な間隔を確保します。
