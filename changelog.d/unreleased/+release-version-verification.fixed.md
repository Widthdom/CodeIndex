---
category: fixed
affected:
  - .github/workflows/release.yml
  - src/CodeIndex/CodeIndex.csproj
---

## English

- **Release install verification accepts the `--version` build-metadata suffix** — the release workflow's install check compared `cdidx --version` for an exact `cdidx v<tag>` match, so it failed on every release after the build-aware `--version` change (#1550) because the binary now prints a trailing ` (commit <sha>, built <date>, <clean|dirty>)` block. The check is now anchored on the `cdidx v<tag>` prefix, matching the `install.sh` validator.
- **Tagged release builds no longer stamp `--version` as `dirty`** — the RID-targeted `dotnet publish` step rewrites `packages.lock.json` with runtime-specific entries before the build-metadata target re-runs, which made a clean tagged release report `dirty`. The dirty probe now excludes every `packages.lock.json`; genuine lock drift is still caught by the locked-mode restore in CI.

## 日本語

- **リリースのインストール検証が `--version` のビルドメタデータ接尾辞を受け入れるようになりました** — release workflow のインストール検証は `cdidx --version` を `cdidx v<tag>` と完全一致で比較していたため、ビルド情報付き `--version` 化 (#1550) 以降はバイナリが末尾に ` (commit <sha>, built <date>, <clean|dirty>)` を出力するようになり、すべてのリリースで失敗していました。検証を `cdidx v<tag>` 接頭辞での照合に変更し、`install.sh` のバリデータと揃えました。
- **タグ付きリリースビルドの `--version` が `dirty` と刻まれなくなりました** — RID 指定の `dotnet publish` がビルドメタデータターゲット再実行前に `packages.lock.json` へ runtime 固有エントリを書き込むため、クリーンなタグ付きリリースまで `dirty` と報告されていました。dirty 判定からすべての `packages.lock.json` を除外しました。lock の本当のドリフトは CI の locked-mode restore が引き続き検出します。
