---
category: fixed
affected:
  - .github/scripts/configure-windows-test-host.ps1
  - tests/CodeIndex.Tests/CiWorkflowTests.cs
  - TESTING_GUIDE.md
---

## English

- **Windows CI setup now tolerates an unavailable Defender service** — Windows test and release jobs warn and continue when `WinDefend` is stopped or unavailable, while still treating exclusion configuration and verification failures as fatal whenever Defender is running.

## 日本語

- **Windows CI setup が Defender service の利用不能を許容するようになりました** — Windows の test / release job は `WinDefend` が停止中または利用不能の場合に warning を出して継続し、Defender が稼働中の除外設定失敗と検証失敗は引き続き fatal として扱います。
