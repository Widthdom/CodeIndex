# Distribution Channels

This document compares supported and planned ways to install `cdidx`.

## Supported Channels

| Channel | Platform support | Prerequisites | Update path | Offline or mirrored use | Lifecycle policy |
|---|---|---|---|---|---|
| `install.sh` release assets | Linux/macOS self-contained tarballs for `linux-x64`, `linux-arm64`, and `osx-arm64` where a matching release asset exists | POSIX shell, `curl`, `tar`, and network access to the configured release host | Re-run the installer without a version for latest, or pass `vX.Y.Z` for an exact release | Supports `HTTPS_PROXY`, `HTTP_PROXY`, `NO_PROXY`, `CDIDX_GITHUB_BASE_URL`, `CDIDX_GITHUB_API_BASE_URL`, and local mirror self-tests | Primary self-contained installer for terminals, CI, containers, and ARM64 Unix hosts without .NET |
| Windows release ZIP assets | Windows self-contained ZIPs for `win-x64` and `win-arm64` where published | PowerShell or another ZIP extraction workflow | Download and replace with the desired release ZIP | Mirror the GitHub release ZIP and checksum assets through the same artifact controls | Supported release-asset path for Windows users who do not use NuGet |
| NuGet global tool | Any platform supported by .NET 8 global tools | .NET 8 SDK for `dotnet tool install/update`; .NET 8 runtime to run the installed tool | `dotnet tool update -g cdidx` | Use standard NuGet feeds, caches, and enterprise mirrors | Portable framework-dependent tool package; not RID-specific or self-contained |
| GHCR container image | Linux `linux/amd64` and `linux/arm64` images at `ghcr.io/widthdom/codeindex:<version>`; stable releases also publish `latest` | Container runtime; mount the target repository at `/repo` when indexing/querying project files. The runtime image includes `ca-certificates` but not `git`; git-aware metadata is best-effort unless a derived image adds `git` | Pull a newer release tag or `latest` | Mirror the GHCR image plus its registry provenance/SBOM attestations | Official OCI image for CI and agent container use |
| Manual image build | Any base image that can run the selected install path | Either `install.sh` prerequisites or a .NET SDK for source builds | Rebuild the image with a pinned release or source revision | Mirror release assets or NuGet feeds inside the image build network | Supported as a deployment pattern for customized images |
| Build from source | Windows, macOS, and Linux with a supported .NET SDK | .NET 8 SDK for production target; .NET 9 SDK if running the full test matrix | Pull source and rebuild | Works with restored package caches and internal NuGet mirrors | Contributor and advanced-user path |

## Planned or Community Channels

| Channel | Status | Notes |
|---|---|---|
| Homebrew | Available via `widthdom/tap/codeindex` when published for a release | Prefer this on macOS/Linux when you already use Homebrew. |
| winget | Planned | Should point at unmodified official binaries and preserve notices. |
| apt / rpm | Planned | Package metadata should make the license and update cadence clear. |
| Snap / Flatpak | Planned | Must document filesystem and sandbox implications for `.cdidx` databases. |

## Choosing a Channel

Use `install.sh` when you want a self-contained binary, especially in ARM64
cloud sessions, CI containers, or machines where .NET is not already managed.
Use the NuGet global tool when your workstation already has a managed .NET 8
toolchain and you want standard `dotnet tool` update behavior.

If both are installed, whichever `cdidx` appears first on `PATH` wins. Check
with:

```bash
command -v cdidx
cdidx --version
```

## Package Maintainers and Mirrors

Third-party package recipes, manifests, and mirrors may redistribute unmodified
official CodeIndex releases when required notices are preserved. Do not market a
renamed binary, fork, or service as a substitute for CodeIndex without a
separate written agreement. See [INTEGRATION_POLICY.md](INTEGRATION_POLICY.md)
and [COMMERCIAL_LICENSE.md](COMMERCIAL_LICENSE.md).

## Release Verification Checklist

Before publishing or updating a channel, verify:

- `install.sh` can install the latest release and an explicit `vX.Y.Z` release.
- `install.sh --doctor vX.Y.Z` reports the configured release and API hosts.
- `dotnet tool install -g cdidx --version <version>` succeeds on a clean .NET 8 tool environment.
- `docker pull ghcr.io/widthdom/codeindex:<version>` succeeds for published release images, and the registry exposes provenance/SBOM attestations.
- `cdidx --version` runs from each installed channel.
- `cdidx status --help` or another lightweight command runs without requiring a repository.
- Package metadata preserves license, homepage, and repository links.
