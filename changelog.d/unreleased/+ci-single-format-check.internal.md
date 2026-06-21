---
category: internal
affected:
  - .github/workflows/dotnet.yml
  - dev.sh
  - TESTING_GUIDE.md
---

## English

- **CI removes a duplicate formatting check** — the Build and Test workflow now relies on `make lint` as the single whitespace verifier, and `dev.sh lint` runs without a redundant restore.

## 日本語

- **CI の重複した formatting check を削除しました** — Build and Test workflow は `make lint` を唯一の whitespace verifier として使い、`dev.sh lint` は重複した restore なしで実行します。
