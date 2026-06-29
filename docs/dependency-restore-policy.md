# Dependency Restore Policy

## English

CodeIndex keeps dependency restore behavior explicit across local builds, CI,
Docker, release, package metadata, and test infrastructure.

- `Directory.Build.props` enables `RestorePackagesWithLockFile=true` for every
  project, so each project participates in committed `packages.lock.json`
  coverage without forcing local restores into locked mode.
- CI restore surfaces (`dotnet.yml`, `release.yml`, `codeql.yml`, and
  `mutation-testing.yml`) run `dotnet restore CodeIndex.sln --locked-mode`.
- CI and mutation-test NuGet caches use exact lockfile-derived keys and do not
  use broad `restore-keys` fallbacks that can reuse stale package graphs.
- Docker resolves `TARGETARCH` to `linux-musl-x64` or `linux-musl-arm64`, runs
  `dotnet restore src/CodeIndex/CodeIndex.csproj --runtime "$rid"
  --locked-mode`, and then publishes the same RID with `--no-restore`.
- `src/CodeIndex/CodeIndex.csproj` targets `net8.0`, declares the supported
  musl RIDs, keeps trim/AOT analyzer settings explicit, derives package version
  metadata from `version.json`, and keeps Source Link / ILLink references
  build-only with `PrivateAssets=All`.
- `tests/CodeIndex.Tests/CodeIndex.Tests.csproj` targets both `net8.0` and
  `net9.0`; compatibility package references must stay synchronized with the
  committed net9 lock file.

When changing package references, restore flags, target frameworks, RIDs,
package metadata, or cache keys, update the matching tests and changelog
fragment in the same change.

## 日本語

CodeIndex は local build、CI、Docker、release、package metadata、test
infrastructure をまたぐ dependency restore の挙動を明示的に保ちます。

- `Directory.Build.props` は全 project に
  `RestorePackagesWithLockFile=true` を有効化し、local restore を locked
  mode に強制せず、各 project を commit 済み `packages.lock.json`
  coverage に参加させます。
- CI の restore surface（`dotnet.yml`、`release.yml`、`codeql.yml`、
  `mutation-testing.yml`）は `dotnet restore CodeIndex.sln --locked-mode`
  を実行します。
- CI と mutation-test の NuGet cache は lockfile 由来の完全一致 key を使い、
  古い package graph を再利用しうる broad な `restore-keys` fallback は使いません。
- Docker は `TARGETARCH` を `linux-musl-x64` または `linux-musl-arm64` に
  解決し、`dotnet restore src/CodeIndex/CodeIndex.csproj --runtime "$rid"
  --locked-mode` を実行してから、同じ RID を `--no-restore` 付きで publish
  します。
- `src/CodeIndex/CodeIndex.csproj` は `net8.0` を対象にし、supported musl
  RID、trim/AOT analyzer 設定、`version.json` 由来の package version
  metadata、`PrivateAssets=All` の Source Link / ILLink build-only reference
  を明示します。
- `tests/CodeIndex.Tests/CodeIndex.Tests.csproj` は `net8.0` と `net9.0` の
  両方を対象にします。compatibility package reference は commit 済み net9
  lock file と同期させてください。

package reference、restore flag、target framework、RID、package metadata、
cache key を変更する場合は、対応する test と changelog fragment を同じ変更に
含めてください。
