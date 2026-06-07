---
category: internal
affected:
  - tests/CodeIndex.Tests/CodeIndex.Tests.runsettings
  - tests/CodeIndex.Tests/CiWorkflowTests.cs
  - TESTING_GUIDE.md
---

## English

- **CI test sessions now allow 45 minutes** — the shared VSTest runsettings timeout now has enough headroom for the Windows net9 full suite when runner variance pushes execution past 30 minutes.

## 日本語

- **CI test session の上限を45分にしました** — Windows net9 の full suite が runner のばらつきで30分を超える場合に備え、共有 VSTest runsettings の timeout に余裕を持たせました。
