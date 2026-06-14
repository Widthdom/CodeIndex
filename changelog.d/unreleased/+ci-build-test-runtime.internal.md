---
category: internal
affected:
  - .github/workflows/dotnet.yml
  - tests/CodeIndex.Tests/CiWorkflowTests.cs
  - DEVELOPER_GUIDE.md
  - TESTING_GUIDE.md
---

## English

- **Build and Test CI now avoids repeated slow validation work** — vulnerability auditing, formatting validation, and XPlat Code Coverage collection now run on the representative `ubuntu-latest` / `net8.0` lane while the full OS/framework matrix continues to run the test suite.

## 日本語

- **Build and Test CI が重い検証処理の重複を避けるようになりました** — 脆弱性監査、format 検証、XPlat Code Coverage 収集を代表 `ubuntu-latest` / `net8.0` lane に寄せ、OS/framework の full matrix では引き続き test suite を実行します。
