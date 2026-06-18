---
category: security
affected:
  - src/CodeIndex/CodeIndex.csproj
  - tests/CodeIndex.Tests/CodeIndex.Tests.csproj
  - src/CodeIndex/packages.lock.json
  - tests/CodeIndex.Tests/packages.lock.json
  - DEVELOPER_GUIDE.md
---

## English

- **Release audit now resolves the patched native SQLite bundle** — `Microsoft.Data.Sqlite` and the `SQLitePCLRaw.bundle_e_sqlite3` graph are pinned to patched versions so the GitHub Actions NuGet vulnerability audit no longer resolves the vulnerable `SQLitePCLRaw.lib.e_sqlite3` package.

## 日本語

- **release audit が修正済み native SQLite bundle を解決するようになりました** — `Microsoft.Data.Sqlite` と `SQLitePCLRaw.bundle_e_sqlite3` graph を修正済み version に固定し、GitHub Actions の NuGet vulnerability audit が脆弱な `SQLitePCLRaw.lib.e_sqlite3` package を解決しないようにしました。
