---
category: changed
affected:
  - .github/workflows/dotnet.yml
  - .github/workflows/codeql.yml
  - .github/workflows/license-policy.yml
---

## English

- **Focused test workflows avoid repeated dependency setup** — the license-policy lane now caches NuGet packages and performs one locked framework-specific restore before running tests with `--no-restore`, while the C# CodeQL lane reuses a lock-file-keyed package cache.
- **Build lanes tolerate transient SDK download failures** — the shared .NET SDK setup is retried once when the initial setup action fails, while a repeated failure still stops the job.

## 日本語

- **focused test workflow で依存関係の重複 setup を回避しました** — license-policy lane は NuGet package をキャッシュし、framework を限定した locked restore を 1 回行った後、`--no-restore` でテストを実行します。C# CodeQL lane でも lock file キーの package cache を再利用します。
- **build lane が一時的な SDK download 失敗に耐えられるようになりました** — 共通の .NET SDK setup は最初の action が失敗した場合に 1 回だけ再試行し、再度失敗した場合は従来どおり job を停止します。
