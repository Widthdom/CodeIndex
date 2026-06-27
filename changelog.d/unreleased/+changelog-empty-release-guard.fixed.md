---
category: fixed
affected:
  - tools/CodeIndex.Changelog/Program.cs
  - tests/CodeIndex.Tests/ChangelogToolTests.cs
  - .codex/workflows/release-changelog.md
---

## English

- **Release changelog preparation now requires at least one fragment** — `prepare` and `render` fail before writing files when `changelog.d/unreleased/` contains no release-note fragments, preventing empty release sections from being generated.

## 日本語

- **release changelog preparation が少なくとも 1 件の fragment を要求するようになりました** — `changelog.d/unreleased/` に release note fragment がない場合、`prepare` と `render` はファイルを書き換える前に失敗し、空の release section 生成を防ぎます。
