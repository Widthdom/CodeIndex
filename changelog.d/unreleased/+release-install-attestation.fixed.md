---
category: fixed
affected:
  - .github/workflows/release.yml
  - tests/CodeIndex.Tests/ReleaseWorkflowTests.cs
---

## English

- **Release install verification now authenticates GitHub attestation checks** — the published-release smoke job supplies its read-only automatic GitHub token to the installer, so the default strict provenance policy can verify release assets instead of failing because GitHub CLI is unauthenticated.

## 日本語

- **リリースのインストール検証で GitHub attestation を認証するようになりました** — 公開済みリリースの smoke job が read-only の自動 GitHub token を installer に渡すため、GitHub CLI の未認証エラーで失敗せず、既定の strict provenance policy で release asset を検証できます。
