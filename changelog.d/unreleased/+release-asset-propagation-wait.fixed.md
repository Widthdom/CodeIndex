---
category: fixed
affected:
  - .github/workflows/release.yml
---

## English

- **Release workflow now waits for `sha256sums.txt` to propagate before verifying the install** — the install-verification step polled only the platform tarball, so a release could fail when the CDN had not yet served `sha256sums.txt` (the v1.22.1 release failed with `Failed to download sha256sums.txt ... HTTP 404`). The wait step now polls every asset `install.sh` downloads, so an asset that propagates slowly is waited out instead of failing the release.

## 日本語

- **リリースワークフローがインストール検証前に `sha256sums.txt` の伝播を待つようになりました** — インストール検証ステップはプラットフォーム tarball のみをポーリングしていたため、CDN がまだ `sha256sums.txt` を配信していない場合にリリースが失敗していました（v1.22.1 のリリースが `Failed to download sha256sums.txt ... HTTP 404` で失敗）。待機ステップは `install.sh` がダウンロードする全 asset をポーリングするようになり、伝播の遅い asset があってもリリースを失敗させず待ち切ります。
