---
category: changed
affected:
  - .github/workflows/codeql.yml
  - .github/workflows/license-policy.yml
---

## English

- **Focused test workflows avoid repeated dependency setup** — the license-policy lane now caches NuGet packages and performs one locked framework-specific restore before running tests with `--no-restore`, while the C# CodeQL lane reuses a lock-file-keyed package cache.

## 日本語

- **focused test workflow で依存関係の重複 setup を回避しました** — license-policy lane は NuGet package をキャッシュし、framework を限定した locked restore を 1 回行った後、`--no-restore` でテストを実行します。C# CodeQL lane でも lock file キーの package cache を再利用します。
