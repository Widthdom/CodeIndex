---
category: changed
affected:
  - tools/CodeIndex.Changelog/Program.cs
  - .github/workflows/release.yml
  - .codex/workflows/release-changelog.md
---

## English

- **GitHub release notes now use the install-focused template** — `release-notes` now emits the `What's Changed` compare link plus Homebrew and NuGet install/update commands, using the previous version supplied by the release workflow from the latest non-draft, non-prerelease GitHub Release.

## 日本語

- **GitHub Release notes が install 重視のテンプレートを使うようになりました** — `release-notes` は `What's Changed` の compare link と Homebrew / NuGet の install/update command を出力し、前回リリースバージョンは release workflow が最新の draft / prerelease ではない GitHub Release から渡します。
