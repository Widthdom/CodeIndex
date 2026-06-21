---
category: internal
affected:
  - .github/workflows/dotnet.yml
  - TESTING_GUIDE.md
---

## English

- **CI test execution skips redundant restore checks** — the Build and Test workflow now runs `dotnet test` with `--no-restore --no-build` after the locked restore and Release build steps.

## 日本語

- **CI の test execution で重複した restore 確認を省くようにしました** — Build and Test workflow は locked restore と Release build の後、`dotnet test` を `--no-restore --no-build` で実行します。
