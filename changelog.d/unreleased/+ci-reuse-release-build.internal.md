---
category: internal
affected:
  - .github/workflows/dotnet.yml
  - TESTING_GUIDE.md
---

## English

- **CI skips one redundant test-project build** — the `ubuntu-latest` / `net8.0` test lane now reuses the earlier Release solution build instead of rebuilding the same test project before running tests.

## 日本語

- **CI の重複した test-project build を 1 回省くようにしました** — `ubuntu-latest` / `net8.0` の test lane は、テスト実行前に同じ test project を再ビルドせず、直前の Release solution build を再利用します。
