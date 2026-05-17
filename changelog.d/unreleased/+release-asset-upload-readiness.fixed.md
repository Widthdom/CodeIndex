---
category: fixed
affected:
  - .github/workflows/release.yml
---

## English

- **Release verification now waits for uploaded assets before probing downloads** — the release workflow now waits until every release file is visible in the GitHub API as an uploaded, non-empty asset, then verifies the installer assets with ranged GET requests for up to ten minutes so newly created releases do not fail on slow GitHub asset processing or HEAD/CDN mismatches.

## 日本語

- **リリース検証が download 確認前に asset の upload 完了を待つようになりました** — release workflow は公開対象の全ファイルが GitHub API 上で uploaded かつ非空として見えるまで待ってから、installer が取得する asset を最大 10 分間 Range GET で確認するようになり、新規リリース時の GitHub asset 処理遅延や HEAD/CDN の不一致による失敗を防ぎます。
