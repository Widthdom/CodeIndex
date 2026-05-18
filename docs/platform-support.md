# Platform Support

> **[日本語版はこちら / Japanese version](#プラットフォームサポート)**

This page defines the platform policy for official `cdidx` release assets.

## Official Release Assets

GitHub releases publish and checksum these first-class runtime identifiers:

| RID | Asset | Notes |
| --- | --- | --- |
| `linux-x64` | `CodeIndex-linux-x64.tar.gz` | Requires glibc. Alpine/musl images are not supported by the official tarball. |
| `linux-arm64` | `CodeIndex-linux-arm64.tar.gz` | Requires glibc. |
| `osx-arm64` | `CodeIndex-osx-arm64.tar.gz` | Apple Silicon macOS. |
| `win-x64` | `CodeIndex-win-x64.zip` | 64-bit Windows. |
| `win-arm64` | `CodeIndex-win-arm64.zip` | Windows on ARM64. |

The one-line `install.sh` installer supports the Unix tarball path for Linux and
macOS. It validates the detected RID against the official release asset list
before downloading, so unsupported RIDs fail with platform guidance instead of a
release-asset 404. Windows users should install from the release ZIP, use the
NuGet global tool, or build from source.

## Not Published As Release Assets

The following RIDs are not currently shipped as official GitHub release assets:

| RID or platform | Status | Recommended path |
| --- | --- | --- |
| `osx-x64` / Intel Mac | Not published | Install with `dotnet tool install -g cdidx` in a .NET SDK environment, or build from source with `dotnet publish src/CodeIndex/CodeIndex.csproj -c Release -r osx-x64 --self-contained true`. |
| `win-x86` / 32-bit Windows | Not published | Use a 64-bit Windows environment when possible, install the NuGet global tool with the .NET SDK, or build from source for `win-x86` if you must run there. |
| `linux-x86`, `linux-arm`, and other Linux RIDs | Not published | Use a glibc-based x64/arm64 environment, install the NuGet global tool, or build from source for the target RID. |
| Alpine / musl Linux | Not supported by official tarballs | Use a glibc-based image such as Debian or Ubuntu, or install/build in an SDK environment that matches your deployment target. |

Unsupported release-asset coverage does not mean the source code intentionally
blocks that RID. It means the project does not currently build, test, publish,
or checksum that binary in the release pipeline.

## Requesting Another Asset

Open a GitHub issue when you need an additional official release asset. Include:

- the target RID;
- where it will run, such as developer laptops, CI, enterprise fleet, or
  container base image;
- whether `dotnet tool install -g cdidx` or a source build is unavailable;
- any validation constraints that would need to become part of CI.

---

<a id="プラットフォームサポート"></a>
# プラットフォームサポート

このページは、公式 `cdidx` リリースアセットのプラットフォーム方針を定義します。

## 公式リリースアセット

GitHub Releases では、次の runtime identifier を first-class として公開し、
checksum 対象にしています。

| RID | アセット | 備考 |
| --- | --- | --- |
| `linux-x64` | `CodeIndex-linux-x64.tar.gz` | glibc が必要です。公式 tarball は Alpine/musl image をサポートしません。 |
| `linux-arm64` | `CodeIndex-linux-arm64.tar.gz` | glibc が必要です。 |
| `osx-arm64` | `CodeIndex-osx-arm64.tar.gz` | Apple Silicon macOS 向けです。 |
| `win-x64` | `CodeIndex-win-x64.zip` | 64-bit Windows 向けです。 |
| `win-arm64` | `CodeIndex-win-arm64.zip` | Windows on ARM64 向けです。 |

ワンライナーの `install.sh` は Linux と macOS の Unix tarball 経路を対象に
しています。download 前に検出 RID を公式 release asset 一覧と照合するため、
未対応 RID は release asset の 404 ではなく platform guidance として失敗します。
Windows では release ZIP、NuGet global tool、または source build を使ってください。

## 公式アセットとして未公開のもの

次の RID は、現時点では公式 GitHub release asset として公開していません。

| RID / platform | 状態 | 推奨経路 |
| --- | --- | --- |
| `osx-x64` / Intel Mac | 未公開 | .NET SDK のある環境で `dotnet tool install -g cdidx` を使うか、`dotnet publish src/CodeIndex/CodeIndex.csproj -c Release -r osx-x64 --self-contained true` で source build してください。 |
| `win-x86` / 32-bit Windows | 未公開 | 可能なら 64-bit Windows を使い、必要に応じて .NET SDK の NuGet global tool、または `win-x86` 向け source build を使ってください。 |
| `linux-x86`、`linux-arm`、その他 Linux RID | 未公開 | glibc ベースの x64/arm64 環境、NuGet global tool、または対象 RID 向け source build を使ってください。 |
| Alpine / musl Linux | 公式 tarball では非対応 | Debian / Ubuntu など glibc ベースの image を使うか、配布先に合う SDK 環境で install/build してください。 |

release asset が未公開であることは、その RID を source code 側で意図的に拒否
しているという意味ではありません。リリースパイプラインでそのバイナリを
build、test、publish、checksum していないという意味です。

## 追加アセットのリクエスト

追加の公式 release asset が必要な場合は GitHub issue を作成してください。
次の情報を含めると判断しやすくなります。

- 対象 RID;
- 開発者端末、CI、enterprise fleet、container base image などの実行場所;
- `dotnet tool install -g cdidx` や source build が使えない理由;
- CI に追加すべき検証条件。
