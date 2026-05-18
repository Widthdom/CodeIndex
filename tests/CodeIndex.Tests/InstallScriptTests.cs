using System.Diagnostics;
using System.Runtime.Versioning;

namespace CodeIndex.Tests;

/// <summary>
/// Regression tests for the published one-liner installer script.
/// 公開ワンライナーインストーラーのリグレッションテスト。
/// </summary>
public sealed class InstallScriptTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(Path.GetTempPath(), $"cdidx_install_script_{Guid.NewGuid():N}");

    public InstallScriptTests()
    {
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        TestProjectHelper.DeleteDirectory(_tempRoot);
    }

    [Theory]
    [InlineData("linux", "x64", "linux-x64", "libe_sqlite3.so")]
    [InlineData("osx", "arm64", "osx-arm64", "libe_sqlite3.dylib")]
    public void Main_WithoutExplicitVersion_SkipsDownloadWhenLatestAlreadyInstalled(string osName, string archName, string rid, string nativeAssetName)
    {
        if (OperatingSystem.IsWindows())
            return;

        var installDir = Path.Combine(_tempRoot, $"bin_{osName}");
        var (exitCode, stdout, stderr) = RunInstallerSnippet(
            $$"""
            detect_platform() { OS_NAME="{{osName}}"; ARCH_NAME="{{archName}}"; RID="{{rid}}"; }
            download_and_install() { echo "DOWNLOAD_SHOULD_NOT_RUN"; }
            check_path() { :; }
            curl() {
                local output_path=""
                while [ $# -gt 0 ]; do
                    case "$1" in
                        -o)
                            output_path="$2"
                            shift 2
                            ;;
                        -w)
                            shift 2
                            ;;
                        *)
                            shift
                            ;;
                    esac
                done

                printf '{"tag_name":"v1.10.0"}' > "$output_path"
                printf '200'
                return 0
            }

            mkdir -p "{{installDir}}"
            cat > "{{Path.Combine(installDir, "cdidx")}}" <<'EOF'
            #!/usr/bin/env bash
            echo "cdidx v1.10.0"
            EOF
            chmod +x "{{Path.Combine(installDir, "cdidx")}}"
            printf '{"version":"1.10.0"}' > "{{Path.Combine(installDir, "version.json")}}"
            : > "{{Path.Combine(installDir, nativeAssetName)}}"
            printf 'license text' > "{{Path.Combine(installDir, "LICENSE")}}"
            printf 'commercial license text' > "{{Path.Combine(installDir, "COMMERCIAL_LICENSE.md")}}"
            printf 'integration policy text' > "{{Path.Combine(installDir, "INTEGRATION_POLICY.md")}}"
            printf 'trademark text' > "{{Path.Combine(installDir, "TRADEMARKS.md")}}"
            mkdir -p "{{Path.Combine(installDir, "LICENSES")}}"
            printf 'fsl text' > "{{Path.Combine(installDir, "LICENSES", "FSL-1.1-ALv2.txt")}}"
            printf 'apache text' > "{{Path.Combine(installDir, "LICENSES", "Apache-2.0.txt")}}"

            main
            """,
            new Dictionary<string, string?>
            {
                ["CDIDX_INSTALL_DIR"] = installDir,
            });

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, stderr);
        Assert.Contains("Fetching latest release version", stdout);
        Assert.Contains("Version: v1.10.0", stdout);
        Assert.Contains("cdidx 1.10.0 is already installed", stdout);
        Assert.DoesNotContain("DOWNLOAD_SHOULD_NOT_RUN", stdout);
    }

    [Theory]
    [InlineData("linux", "x64", "linux-x64", "libe_sqlite3.so")]
    [InlineData("osx", "arm64", "osx-arm64", "libe_sqlite3.dylib")]
    public void Main_WithoutExplicitVersion_UpgradesHealthyOlderInstallToLatest(string osName, string archName, string rid, string nativeAssetName)
    {
        if (OperatingSystem.IsWindows())
            return;

        var installDir = Path.Combine(_tempRoot, $"upgrade_{osName}");
        var (exitCode, stdout, stderr) = RunInstallerSnippet(
            $$"""
            detect_platform() { OS_NAME="{{osName}}"; ARCH_NAME="{{archName}}"; RID="{{rid}}"; }
            download_and_install() { echo "DOWNLOAD_RAN"; }
            check_path() { :; }
            curl() {
                local output_path=""
                while [ $# -gt 0 ]; do
                    case "$1" in
                        -o)
                            output_path="$2"
                            shift 2
                            ;;
                        -w)
                            shift 2
                            ;;
                        *)
                            shift
                            ;;
                    esac
                done

                printf '{"tag_name":"v1.2.3"}' > "$output_path"
                printf '200'
                return 0
            }

            mkdir -p "{{installDir}}"
            cat > "{{Path.Combine(installDir, "cdidx")}}" <<'EOF'
            #!/usr/bin/env bash
            echo "cdidx v1.0.0"
            EOF
            chmod +x "{{Path.Combine(installDir, "cdidx")}}"
            printf '{"version":"1.0.0"}' > "{{Path.Combine(installDir, "version.json")}}"
            : > "{{Path.Combine(installDir, nativeAssetName)}}"

            main
            """,
            new Dictionary<string, string?>
            {
                ["CDIDX_INSTALL_DIR"] = installDir,
            });

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, stderr);
        Assert.Contains("Fetching latest release version", stdout);
        Assert.Contains("Version: v1.2.3", stdout);
        Assert.Contains("Switching cdidx from 1.0.0 to 1.2.3", stdout);
        Assert.Contains("DOWNLOAD_RAN", stdout);
    }

    [Fact]
    public void Main_WithoutExplicitVersion_DoesNotShortCircuitBrokenZeroVersionInstall()
    {
        if (OperatingSystem.IsWindows())
            return;

        var installDir = Path.Combine(_tempRoot, "broken_bin");
        var (exitCode, stdout, stderr) = RunInstallerSnippet(
            $$"""
            detect_platform() { OS_NAME="linux"; ARCH_NAME="x64"; RID="linux-x64"; }
            download_and_install() { echo "DOWNLOAD_RAN"; }
            check_path() { :; }
            curl() {
                local output_path=""
                while [ $# -gt 0 ]; do
                    case "$1" in
                        -o)
                            output_path="$2"
                            shift 2
                            ;;
                        -w)
                            shift 2
                            ;;
                        *)
                            shift
                            ;;
                    esac
                done

                printf '{"tag_name":"v1.2.3"}' > "$output_path"
                printf '200'
                return 0
            }

            mkdir -p "{{installDir}}"
            cat > "{{Path.Combine(installDir, "cdidx")}}" <<'EOF'
            #!/usr/bin/env bash
            echo "cdidx v0.0.0"
            EOF
            chmod +x "{{Path.Combine(installDir, "cdidx")}}"

            main
            """,
            new Dictionary<string, string?>
            {
                ["CDIDX_INSTALL_DIR"] = installDir,
            },
            enforceStrictMode: false);

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, stderr);
        Assert.Contains("Fetching latest release version", stdout);
        Assert.Contains("Version: v1.2.3", stdout);
        Assert.Contains("DOWNLOAD_RAN", stdout);
        Assert.DoesNotContain("Skipping latest-release lookup", stdout);
    }

    [Fact]
    public void Main_ExplicitSameVersionBrokenInstall_ReinstallsInsteadOfSkipping()
    {
        if (OperatingSystem.IsWindows())
            return;

        var installDir = Path.Combine(_tempRoot, "explicit_broken_bin");
        var (exitCode, stdout, stderr) = RunInstallerSnippet(
            $$"""
            detect_platform() { OS_NAME="linux"; ARCH_NAME="x64"; RID="linux-x64"; }
            download_and_install() { echo "DOWNLOAD_RAN"; }
            check_path() { :; }
            curl() { echo "CURL_SHOULD_NOT_RUN"; return 99; }

            mkdir -p "{{installDir}}"
            cat > "{{Path.Combine(installDir, "cdidx")}}" <<'EOF'
            #!/usr/bin/env bash
            echo "cdidx v1.2.3"
            EOF
            chmod +x "{{Path.Combine(installDir, "cdidx")}}"
            printf '{"version":"1.2.3"}' > "{{Path.Combine(installDir, "version.json")}}"

            main v1.2.3
            """,
            new Dictionary<string, string?>
            {
                ["CDIDX_INSTALL_DIR"] = installDir,
            },
            enforceStrictMode: false);

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, stderr);
        Assert.Contains("Version: v1.2.3", stdout);
        Assert.Contains("Reinstalling cdidx 1.2.3 because it was requested explicitly", stdout);
        Assert.Contains("DOWNLOAD_RAN", stdout);
        Assert.DoesNotContain("already installed", stdout);
        Assert.DoesNotContain("CURL_SHOULD_NOT_RUN", stdout);
    }

    [Fact]
    public void Main_ExplicitSameVersionHealthyInstall_ReinstallsInsteadOfSkipping()
    {
        if (OperatingSystem.IsWindows())
            return;

        var installDir = Path.Combine(_tempRoot, "explicit_healthy_bin");
        var (exitCode, stdout, stderr) = RunInstallerSnippet(
            $$"""
            detect_platform() { OS_NAME="linux"; ARCH_NAME="x64"; RID="linux-x64"; }
            download_and_install() { echo "DOWNLOAD_RAN"; }
            check_path() { :; }
            curl() { echo "CURL_SHOULD_NOT_RUN"; return 99; }

            mkdir -p "{{installDir}}"
            cat > "{{Path.Combine(installDir, "cdidx")}}" <<'EOF'
            #!/usr/bin/env bash
            echo "cdidx v1.2.3"
            EOF
            chmod +x "{{Path.Combine(installDir, "cdidx")}}"
            printf '{"version":"1.2.3"}' > "{{Path.Combine(installDir, "version.json")}}"
            : > "{{Path.Combine(installDir, "libe_sqlite3.so")}}"

            main v1.2.3
            """,
            new Dictionary<string, string?>
            {
                ["CDIDX_INSTALL_DIR"] = installDir,
            },
            enforceStrictMode: false);

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, stderr);
        Assert.Contains("Version: v1.2.3", stdout);
        Assert.Contains("Reinstalling cdidx 1.2.3 because it was requested explicitly", stdout);
        Assert.Contains("DOWNLOAD_RAN", stdout);
        Assert.DoesNotContain("already installed", stdout);
        Assert.DoesNotContain("CURL_SHOULD_NOT_RUN", stdout);
    }

    [Theory]
    [InlineData("linux", "x64", "linux-x64")]
    [InlineData("osx", "arm64", "osx-arm64")]
    public void Main_WithoutExplicitVersion_LatestMatchingBrokenInstall_ReinstallsInsteadOfSkipping(string osName, string archName, string rid)
    {
        if (OperatingSystem.IsWindows())
            return;

        var installDir = Path.Combine(_tempRoot, $"latest_matching_broken_bin_{osName}");
        var (exitCode, stdout, stderr) = RunInstallerSnippet(
            $$"""
            detect_platform() { OS_NAME="{{osName}}"; ARCH_NAME="{{archName}}"; RID="{{rid}}"; }
            download_and_install() { echo "DOWNLOAD_RAN"; }
            check_path() { :; }
            curl() {
                local output_path=""
                while [ $# -gt 0 ]; do
                    case "$1" in
                        -o)
                            output_path="$2"
                            shift 2
                            ;;
                        -w)
                            shift 2
                            ;;
                        *)
                            shift
                            ;;
                    esac
                done

                printf '{"tag_name":"v1.2.3"}' > "$output_path"
                printf '200'
                return 0
            }

            mkdir -p "{{installDir}}"
            cat > "{{Path.Combine(installDir, "cdidx")}}" <<'EOF'
            #!/usr/bin/env bash
            echo "cdidx v1.2.3"
            EOF
            chmod +x "{{Path.Combine(installDir, "cdidx")}}"
            printf '{"version":"1.2.3"}' > "{{Path.Combine(installDir, "version.json")}}"

            main
            """,
            new Dictionary<string, string?>
            {
                ["CDIDX_INSTALL_DIR"] = installDir,
            },
            enforceStrictMode: false);

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, stderr);
        Assert.Contains("Fetching latest release version", stdout);
        Assert.Contains("Version: v1.2.3", stdout);
        Assert.Contains("Reinstalling cdidx 1.2.3 because the existing install is incomplete", stdout);
        Assert.Contains("DOWNLOAD_RAN", stdout);
        Assert.DoesNotContain("Skipping latest-release lookup", stdout);
    }

    [Fact]
    public void ResolveVersion_RateLimitedLatestLookup_PrintsSpecificError()
    {
        if (OperatingSystem.IsWindows())
            return;

        var (exitCode, stdout, stderr) = RunInstallerSnippet(
            """
            curl() {
                local output_path=""
                while [ $# -gt 0 ]; do
                    case "$1" in
                        -o)
                            output_path="$2"
                            shift 2
                            ;;
                        -w)
                            shift 2
                            ;;
                        *)
                            shift
                            ;;
                    esac
                done

                printf '{"message":"API rate limit exceeded for this IP."}' > "$output_path"
                printf '403'
                return 0
            }

            resolve_version ""
            """);

        Assert.Equal(1, exitCode);
        Assert.Contains("Fetching latest release version", stdout);
        Assert.Contains("GitHub API rate limit exceeded while fetching", stderr);
        Assert.Contains("pass an explicit version", stderr);
        Assert.DoesNotContain("Check your network connection", stderr);
    }

    [Fact]
    public void ResolveVersion_ForbiddenLatestLookup_PrintsProxyAndVersionPinHints()
    {
        if (OperatingSystem.IsWindows())
            return;

        var (exitCode, stdout, stderr) = RunInstallerSnippet(
            """
            curl() {
                local output_path=""
                while [ $# -gt 0 ]; do
                    case "$1" in
                        -o)
                            output_path="$2"
                            shift 2
                            ;;
                        -w)
                            shift 2
                            ;;
                        *)
                            shift
                            ;;
                    esac
                done

                printf '{"message":"Forbidden"}' > "$output_path"
                printf '403'
                return 0
            }

            resolve_version ""
            """);

        Assert.Equal(1, exitCode);
        Assert.Contains("Fetching latest release version", stdout);
        Assert.Contains("GitHub API returned HTTP 403 while fetching", stderr);
        Assert.Contains("/releases/latest", stderr);
        Assert.Contains("curl -fsSL https://raw.githubusercontent.com/Widthdom/CodeIndex/vX.Y.Z/install.sh | bash -s -- vX.Y.Z", stderr);
        Assert.Contains("bash ./install.sh vX.Y.Z", stderr);
        Assert.Contains("from a checkout", stderr);
        Assert.Contains("CDIDX_GITHUB_API_BASE_URL", stderr);
        Assert.Contains("CONNECT tunnel failed, response 403", stderr);
    }

    [Fact]
    public void ResolveVersion_ForbiddenConfiguredLatestLookup_PrintsConfiguredApiHints()
    {
        if (OperatingSystem.IsWindows())
            return;

        var (exitCode, stdout, stderr) = RunInstallerSnippet(
            """
            curl() {
                local output_path=""
                while [ $# -gt 0 ]; do
                    case "$1" in
                        -o)
                            output_path="$2"
                            shift 2
                            ;;
                        -w)
                            shift 2
                            ;;
                        *)
                            shift
                            ;;
                    esac
                done

                printf '{"message":"Forbidden"}' > "$output_path"
                printf '403'
                return 0
            }

            resolve_version ""
            """,
            new Dictionary<string, string?>
            {
                ["CDIDX_GITHUB_API_BASE_URL"] = "https://mirror.example.test/api",
            });

        Assert.Equal(1, exitCode);
        Assert.Contains("Fetching latest release version", stdout);
        Assert.Contains("configured latest-release API (https://mirror.example.test/api) returned HTTP 403", stderr);
        Assert.Contains("Check the configured API endpoint, credentials, path ACL, or proxy policy.", stderr);
        Assert.Contains("curl -fsSL https://raw.githubusercontent.com/Widthdom/CodeIndex/vX.Y.Z/install.sh | bash -s -- vX.Y.Z", stderr);
        Assert.Contains("bash ./install.sh vX.Y.Z", stderr);
        Assert.Contains("from a checkout", stderr);
        Assert.DoesNotContain("set CDIDX_GITHUB_API_BASE_URL to a reachable internal mirror API", stderr);
        Assert.Contains("CONNECT tunnel failed, response 403", stderr);
    }

    [Theory]
    [InlineData("curl: (56) CONNECT tunnel failed, response 403")]
    [InlineData("curl: (56) Received HTTP code 403 from proxy after CONNECT")]
    public void ResolveVersion_TunnelForbiddenLatestLookup_PrintsProxyDenyGuidance(string curlStderr)
    {
        if (OperatingSystem.IsWindows())
            return;

        var (exitCode, stdout, stderr) = RunInstallerSnippet(
            $$"""
            curl() {
                printf '%s\n' "{{curlStderr}}" >&2
                return 56
            }

            resolve_version ""
            """);

        Assert.Equal(1, exitCode);
        Assert.Contains("Fetching latest release version", stdout);
        Assert.Contains(curlStderr, stderr);
        Assert.Contains("CONNECT tunnel failed with HTTP 403 while reaching GitHub API", stderr);
        Assert.Contains("curl exit 56", stderr);
        Assert.Contains("CONNECT-stage HTTP 403", stderr);
        Assert.Contains("route substitution alone will not fix it", stderr);
        Assert.Contains("allow-list at least one required API or artifact host path", stderr);
        Assert.DoesNotContain("Network error reaching GitHub API while fetching", stderr);
    }

    [Fact]
    public void DownloadAndInstall_ForbiddenGitHubAssetDownload_PrintsGitHubAndAllowListHints()
    {
        if (OperatingSystem.IsWindows())
            return;

        var installDir = Path.Combine(_tempRoot, "forbidden_asset_target");

        var (exitCode, stdout, stderr) = RunInstallerSnippet(
            """
            VERSION="v1.2.3"
            OS_NAME="linux"
            ARCH_NAME="x64"
            RID="linux-x64"

            curl() {
                local output_path=""
                while [ $# -gt 0 ]; do
                    case "$1" in
                        -o)
                            output_path="$2"
                            shift 2
                            ;;
                        -w)
                            shift 2
                            ;;
                        *)
                            shift
                            ;;
                    esac
                done

                : > "$output_path"
                printf '403'
                return 0
            }

            download_and_install
            echo "UNREACHABLE"
            """,
            new Dictionary<string, string?>
            {
                ["CDIDX_INSTALL_DIR"] = installDir,
            });

        Assert.Equal(1, exitCode);
        Assert.DoesNotContain("UNREACHABLE", stdout);
        Assert.False(File.Exists(Path.Combine(installDir, "cdidx")));
        Assert.False(File.Exists(Path.Combine(installDir, "version.json")));
        Assert.False(File.Exists(Path.Combine(installDir, "libe_sqlite3.so")));
        Assert.Contains("HTTP 403", stderr);
        Assert.Contains("GitHub release host", stderr);
        Assert.Contains("GitHub may be blocking or rate-limiting this route.", stderr);
        Assert.Contains("allow-list at least one artifact host path", stderr);
    }

    [Theory]
    [InlineData("curl: (56) CONNECT tunnel failed, response 403")]
    [InlineData("curl: (56) Received HTTP code 403 from proxy after CONNECT")]
    public void DownloadAndInstall_TunnelForbiddenAssetDownload_PrintsProxyDenyGuidance(string curlStderr)
    {
        if (OperatingSystem.IsWindows())
            return;

        var installDir = Path.Combine(_tempRoot, "tunnel_forbidden_asset_target");

        var (exitCode, _, stderr) = RunInstallerSnippet(
            $$"""
            VERSION="v1.2.3"
            OS_NAME="linux"
            ARCH_NAME="x64"
            RID="linux-x64"

            curl() {
                printf '%s\n' "{{curlStderr}}" >&2
                return 56
            }

            download_and_install
            """,
            new Dictionary<string, string?>
            {
                ["CDIDX_INSTALL_DIR"] = installDir,
            });

        Assert.Equal(1, exitCode);
        Assert.Contains(curlStderr, stderr);
        Assert.Contains("CONNECT tunnel failed with HTTP 403 while reaching GitHub release host", stderr);
        Assert.Contains("curl exit 56", stderr);
        Assert.Contains("CONNECT-stage HTTP 403", stderr);
        Assert.Contains("route substitution alone will not fix it", stderr);
        Assert.Contains("allow-list at least one required API or artifact host path", stderr);
        Assert.DoesNotContain("Network error reaching GitHub release host while fetching", stderr);
    }

    [Fact]
    public void DownloadAndInstall_ForbiddenConfiguredAssetDownload_PrintsConfiguredMirrorHints()
    {
        if (OperatingSystem.IsWindows())
            return;

        var installDir = Path.Combine(_tempRoot, "forbidden_configured_asset_target");

        var (exitCode, stdout, stderr) = RunInstallerSnippet(
            """
            VERSION="v1.2.3"
            OS_NAME="linux"
            ARCH_NAME="x64"
            RID="linux-x64"

            curl() {
                local output_path=""
                while [ $# -gt 0 ]; do
                    case "$1" in
                        -o)
                            output_path="$2"
                            shift 2
                            ;;
                        -w)
                            shift 2
                            ;;
                        *)
                            shift
                            ;;
                    esac
                done

                : > "$output_path"
                printf '403'
                return 0
            }

            download_and_install
            echo "UNREACHABLE"
            """,
            new Dictionary<string, string?>
            {
                ["CDIDX_INSTALL_DIR"] = installDir,
                ["CDIDX_GITHUB_BASE_URL"] = "https://mirror.example.test/releases",
            });

        Assert.Equal(1, exitCode);
        Assert.DoesNotContain("UNREACHABLE", stdout);
        Assert.False(File.Exists(Path.Combine(installDir, "cdidx")));
        Assert.False(File.Exists(Path.Combine(installDir, "version.json")));
        Assert.False(File.Exists(Path.Combine(installDir, "libe_sqlite3.so")));
        Assert.Contains("configured release host (https://mirror.example.test/releases)", stderr);
        Assert.Contains("Check the configured mirror/proxy path, credentials, or access policy.", stderr);
        Assert.DoesNotContain("GitHub may be blocking or rate-limiting this route.", stderr);
        Assert.Contains("allow-list at least one artifact host path", stderr);
    }

    [Fact]
    public void Main_RateLimitedLatestLookup_StopsBeforeSuccessPath()
    {
        if (OperatingSystem.IsWindows())
            return;

        var installDir = Path.Combine(_tempRoot, "rate_limited_lookup_bin");
        var (exitCode, stdout, stderr) = RunInstallerSnippet(
            """
            detect_platform() { OS_NAME="linux"; ARCH_NAME="x64"; RID="linux-x64"; }
            download_and_install() { echo "DOWNLOAD_SHOULD_NOT_RUN"; }
            check_path() { :; }
            curl() {
                local output_path=""
                while [ $# -gt 0 ]; do
                    case "$1" in
                        -o)
                            output_path="$2"
                            shift 2
                            ;;
                        -w)
                            shift 2
                            ;;
                        *)
                            shift
                            ;;
                    esac
                done

                printf '{"message":"API rate limit exceeded for this IP."}' > "$output_path"
                printf '403'
                return 0
            }

            main
            """,
            new Dictionary<string, string?>
            {
                ["CDIDX_INSTALL_DIR"] = installDir,
            },
            enforceStrictMode: false);

        Assert.Equal(1, exitCode);
        Assert.Contains("Fetching latest release version", stdout);
        Assert.DoesNotContain("Done!", stdout);
        Assert.DoesNotContain("DOWNLOAD_SHOULD_NOT_RUN", stdout);
        Assert.Contains("GitHub API rate limit exceeded while fetching", stderr);
    }

    [Fact]
    public void Main_NetworkFailureDuringLatestLookup_StopsBeforeSuccessPath()
    {
        if (OperatingSystem.IsWindows())
            return;

        var installDir = Path.Combine(_tempRoot, "network_failure_lookup_bin");
        var (exitCode, stdout, stderr) = RunInstallerSnippet(
            """
            detect_platform() { OS_NAME="linux"; ARCH_NAME="x64"; RID="linux-x64"; }
            download_and_install() { echo "DOWNLOAD_SHOULD_NOT_RUN"; }
            check_path() { :; }
            curl() {
                printf 'curl: (7) Failed to connect to api.github.com port 443 after 0 ms: Connection refused\n' >&2
                return 7
            }

            main
            """,
            new Dictionary<string, string?>
            {
                ["CDIDX_INSTALL_DIR"] = installDir,
            },
            enforceStrictMode: false);

        Assert.Equal(1, exitCode);
        Assert.Contains("Fetching latest release version", stdout);
        Assert.DoesNotContain("Done!", stdout);
        Assert.DoesNotContain("DOWNLOAD_SHOULD_NOT_RUN", stdout);
        Assert.Contains("curl: (7) Failed to connect to api.github.com port 443 after 0 ms: Connection refused", stderr);
        Assert.Contains("Network error reaching GitHub API while fetching", stderr);
        Assert.Contains("curl exit 7", stderr);
    }

    [Fact]
    public void DownloadAndInstall_MissingAsset_DoesNotCreateFilesInEmptyInstallDir()
    {
        if (OperatingSystem.IsWindows())
            return;

        var installDir = Path.Combine(_tempRoot, "missing_asset_empty_target");
        var payloadDir = Path.Combine(_tempRoot, "missing_asset_empty_payload");
        var archivePath = Path.Combine(_tempRoot, "missing_asset_empty.tar.gz");
        var checksumsPath = Path.Combine(_tempRoot, "missing_asset_empty.sha256sums.txt");

        var (exitCode, stdout, stderr) = RunInstallerSnippet(
            $$"""
            mkdir -p "{{payloadDir}}"
            cat > "{{Path.Combine(payloadDir, "cdidx")}}" <<'EOF'
            #!/usr/bin/env bash
            echo "cdidx v1.2.3"
            EOF
            chmod +x "{{Path.Combine(payloadDir, "cdidx")}}"
            printf '{"version":"1.2.3"}' > "{{Path.Combine(payloadDir, "version.json")}}"
            tar czf "{{archivePath}}" -C "{{payloadDir}}" .

            if command -v sha256sum > /dev/null 2>&1; then
                checksum="$(sha256sum "{{archivePath}}" | awk '{print $1}')"
            elif command -v shasum > /dev/null 2>&1; then
                checksum="$(shasum -a 256 "{{archivePath}}" | awk '{print $1}')"
            else
                checksum="$(openssl dgst -sha256 "{{archivePath}}" | awk '{print $NF}')"
            fi
            printf '%s  CodeIndex-linux-x64.tar.gz\n' "$checksum" > "{{checksumsPath}}"

            VERSION="v1.2.3"
            OS_NAME="linux"
            ARCH_NAME="x64"
            RID="linux-x64"

            curl() {
                local output_path=""
                local url=""
                while [ $# -gt 0 ]; do
                    case "$1" in
                        -o)
                            output_path="$2"
                            shift 2
                            ;;
                        -w)
                            shift 2
                            ;;
                        *)
                            url="$1"
                            shift
                            ;;
                    esac
                done

                case "$url" in
                    */sha256sums.txt) cp "{{checksumsPath}}" "$output_path" ;;
                    *) cp "{{archivePath}}" "$output_path" ;;
                esac

                printf '200'
                return 0
            }

            download_status=0
            if ( download_and_install ); then
                echo "UNEXPECTED_SUCCESS"
            else
                download_status=$?
            fi

            echo "DOWNLOAD_STATUS:$download_status"
            [ -e "{{Path.Combine(installDir, "cdidx")}}" ] && echo "CDIDX_PRESENT" || echo "CDIDX_MISSING"
            [ -e "{{Path.Combine(installDir, "version.json")}}" ] && echo "VERSION_PRESENT" || echo "VERSION_MISSING"
            [ -e "{{Path.Combine(installDir, "libe_sqlite3.so")}}" ] && echo "LIB_PRESENT" || echo "LIB_MISSING"
            """,
            new Dictionary<string, string?>
            {
                ["CDIDX_INSTALL_DIR"] = installDir,
            },
            enforceStrictMode: false);

        Assert.Equal(0, exitCode);
        Assert.DoesNotContain("UNEXPECTED_SUCCESS", stdout);
        Assert.Contains("DOWNLOAD_STATUS:1", stdout);
        Assert.Contains("CDIDX_MISSING", stdout);
        Assert.Contains("VERSION_MISSING", stdout);
        Assert.Contains("LIB_MISSING", stdout);
        Assert.Contains("Required runtime asset missing from release tarball: libe_sqlite3.so", stderr);
    }

    [Fact]
    public void DownloadAndInstall_OptionalLicenseAssetsAreInstalledWhenPresent()
    {
        if (OperatingSystem.IsWindows())
            return;

        var installDir = Path.Combine(_tempRoot, "license_assets_target");
        var payloadDir = Path.Combine(_tempRoot, "license_assets_payload");
        var archivePath = Path.Combine(_tempRoot, "license_assets.tar.gz");
        var checksumsPath = Path.Combine(_tempRoot, "license_assets.sha256sums.txt");

        var (exitCode, stdout, stderr) = RunInstallerSnippet(
            $$"""
            mkdir -p "{{payloadDir}}"
            mkdir -p "{{Path.Combine(payloadDir, "LICENSES")}}"
            cat > "{{Path.Combine(payloadDir, "cdidx")}}" <<'EOF'
            #!/usr/bin/env bash
            echo "cdidx v1.2.3"
            EOF
            chmod +x "{{Path.Combine(payloadDir, "cdidx")}}"
            printf '{"version":"1.2.3"}' > "{{Path.Combine(payloadDir, "version.json")}}"
            printf 'new-lib' > "{{Path.Combine(payloadDir, "libe_sqlite3.so")}}"
            printf 'license text' > "{{Path.Combine(payloadDir, "LICENSE")}}"
            printf 'commercial license text' > "{{Path.Combine(payloadDir, "COMMERCIAL_LICENSE.md")}}"
            printf 'integration policy text' > "{{Path.Combine(payloadDir, "INTEGRATION_POLICY.md")}}"
            printf 'trademark text' > "{{Path.Combine(payloadDir, "TRADEMARKS.md")}}"
            printf 'fsl text' > "{{Path.Combine(payloadDir, "LICENSES", "FSL-1.1-ALv2.txt")}}"
            printf 'apache text' > "{{Path.Combine(payloadDir, "LICENSES", "Apache-2.0.txt")}}"
            tar czf "{{archivePath}}" -C "{{payloadDir}}" .

            if command -v sha256sum > /dev/null 2>&1; then
                checksum="$(sha256sum "{{archivePath}}" | awk '{print $1}')"
            elif command -v shasum > /dev/null 2>&1; then
                checksum="$(shasum -a 256 "{{archivePath}}" | awk '{print $1}')"
            else
                checksum="$(openssl dgst -sha256 "{{archivePath}}" | awk '{print $NF}')"
            fi
            printf '%s  CodeIndex-linux-x64.tar.gz\n' "$checksum" > "{{checksumsPath}}"

            VERSION="v1.2.3"
            OS_NAME="linux"
            ARCH_NAME="x64"
            RID="linux-x64"

            curl() {
                local output_path=""
                local url=""
                while [ $# -gt 0 ]; do
                    case "$1" in
                        -o)
                            output_path="$2"
                            shift 2
                            ;;
                        -w)
                            shift 2
                            ;;
                        *)
                            url="$1"
                            shift
                            ;;
                    esac
                done

                case "$url" in
                    */sha256sums.txt) cp "{{checksumsPath}}" "$output_path" ;;
                    *) cp "{{archivePath}}" "$output_path" ;;
                esac

                printf '200'
                return 0
            }

            download_and_install
            echo "INSTALL_OK"
            echo "LICENSE:$(cat "{{Path.Combine(installDir, "LICENSE")}}")"
            echo "COMMERCIAL:$(cat "{{Path.Combine(installDir, "COMMERCIAL_LICENSE.md")}}")"
            echo "INTEGRATION:$(cat "{{Path.Combine(installDir, "INTEGRATION_POLICY.md")}}")"
            echo "TRADEMARKS:$(cat "{{Path.Combine(installDir, "TRADEMARKS.md")}}")"
            echo "FSL:$(cat "{{Path.Combine(installDir, "LICENSES", "FSL-1.1-ALv2.txt")}}")"
            echo "APACHE:$(cat "{{Path.Combine(installDir, "LICENSES", "Apache-2.0.txt")}}")"
            """,
            new Dictionary<string, string?>
            {
                ["CDIDX_INSTALL_DIR"] = installDir,
            });

        Assert.Equal(0, exitCode);
        Assert.Contains("INSTALL_OK", stdout);
        Assert.Contains("LICENSE:license text", stdout);
        Assert.Contains("COMMERCIAL:commercial license text", stdout);
        Assert.Contains("INTEGRATION:integration policy text", stdout);
        Assert.Contains("TRADEMARKS:trademark text", stdout);
        Assert.Contains("FSL:fsl text", stdout);
        Assert.Contains("APACHE:apache text", stdout);
        Assert.Equal("license text", File.ReadAllText(Path.Combine(installDir, "LICENSE")));
        Assert.Equal("commercial license text", File.ReadAllText(Path.Combine(installDir, "COMMERCIAL_LICENSE.md")));
        Assert.Equal("integration policy text", File.ReadAllText(Path.Combine(installDir, "INTEGRATION_POLICY.md")));
        Assert.Equal("trademark text", File.ReadAllText(Path.Combine(installDir, "TRADEMARKS.md")));
        Assert.Equal("fsl text", File.ReadAllText(Path.Combine(installDir, "LICENSES", "FSL-1.1-ALv2.txt")));
        Assert.Equal("apache text", File.ReadAllText(Path.Combine(installDir, "LICENSES", "Apache-2.0.txt")));
        Assert.Equal(string.Empty, stderr);
    }

    [Fact]
    public void DownloadAndInstall_StageDirMktempFailure_AbortsBeforeInstallWritesUnderStrictMode()
    {
        if (OperatingSystem.IsWindows())
            return;

        var installDir = Path.Combine(_tempRoot, "stage_mktemp_failure_target");
        var payloadDir = Path.Combine(_tempRoot, "stage_mktemp_failure_payload");
        var archivePath = Path.Combine(_tempRoot, "stage_mktemp_failure.tar.gz");
        var checksumsPath = Path.Combine(_tempRoot, "stage_mktemp_failure.sha256sums.txt");
        var cpLogPath = Path.Combine(_tempRoot, "stage_mktemp_failure_cp.log");

        var (exitCode, _, stderr) = RunInstallerSnippet(
            $$"""
            mkdir -p "{{payloadDir}}"
            cat > "{{Path.Combine(payloadDir, "cdidx")}}" <<'EOF'
            #!/usr/bin/env bash
            echo "cdidx v1.2.3"
            EOF
            chmod +x "{{Path.Combine(payloadDir, "cdidx")}}"
            printf '{"version":"1.2.3"}' > "{{Path.Combine(payloadDir, "version.json")}}"
            printf 'new-lib' > "{{Path.Combine(payloadDir, "libe_sqlite3.so")}}"
            tar czf "{{archivePath}}" -C "{{payloadDir}}" .

            if command -v sha256sum > /dev/null 2>&1; then
                checksum="$(sha256sum "{{archivePath}}" | awk '{print $1}')"
            elif command -v shasum > /dev/null 2>&1; then
                checksum="$(shasum -a 256 "{{archivePath}}" | awk '{print $1}')"
            else
                checksum="$(openssl dgst -sha256 "{{archivePath}}" | awk '{print $NF}')"
            fi
            printf '%s  CodeIndex-linux-x64.tar.gz\n' "$checksum" > "{{checksumsPath}}"

            VERSION="v1.2.3"
            OS_NAME="linux"
            ARCH_NAME="x64"
            RID="linux-x64"

            curl() {
                local output_path=""
                local url=""
                while [ $# -gt 0 ]; do
                    case "$1" in
                        -o)
                            output_path="$2"
                            shift 2
                            ;;
                        -w)
                            shift 2
                            ;;
                        *)
                            url="$1"
                            shift
                            ;;
                    esac
                done

                case "$url" in
                    */sha256sums.txt) command cp "{{checksumsPath}}" "$output_path" ;;
                    *) command cp "{{archivePath}}" "$output_path" ;;
                esac

                printf '200'
                return 0
            }

            cp() {
                local dst="${@: -1}"
                case "$dst" in
                    /cdidx|/version.json|/libe_sqlite3.so)
                        printf '%s\n' "$dst" >> "{{cpLogPath}}"
                        ;;
                esac
                command cp "$@"
            }

            mktemp() {
                if [ "${1:-}" = "-d" ] && [ "${2:-}" = "{{installDir}}/.cdidx-stage.XXXXXX" ]; then
                    return 1
                fi
                command mktemp "$@"
            }

            download_and_install
            """,
            new Dictionary<string, string?>
            {
                ["CDIDX_INSTALL_DIR"] = installDir,
            });

        Assert.Equal(1, exitCode);
        Assert.Contains("Failed to create staging directory under", stderr);
        Assert.False(File.Exists(Path.Combine(installDir, "cdidx")));
        Assert.False(File.Exists(Path.Combine(installDir, "version.json")));
        Assert.False(File.Exists(Path.Combine(installDir, "libe_sqlite3.so")));
        Assert.Empty(Directory.Exists(installDir) ? Directory.GetFileSystemEntries(installDir, ".cdidx-*") : []);
        Assert.True(!File.Exists(cpLogPath) || string.IsNullOrWhiteSpace(File.ReadAllText(cpLogPath)));
    }

    [Fact]
    public void DownloadAndInstall_BackupDirMktempFailure_PreservesExistingHealthyInstallUnderStrictMode()
    {
        if (OperatingSystem.IsWindows())
            return;

        var installDir = Path.Combine(_tempRoot, "backup_mktemp_failure_target");
        var payloadDir = Path.Combine(_tempRoot, "backup_mktemp_failure_payload");
        var archivePath = Path.Combine(_tempRoot, "backup_mktemp_failure.tar.gz");
        var checksumsPath = Path.Combine(_tempRoot, "backup_mktemp_failure.sha256sums.txt");

        var (exitCode, _, stderr) = RunInstallerSnippet(
            $$"""
            mkdir -p "{{installDir}}"
            cat > "{{Path.Combine(installDir, "cdidx")}}" <<'EOF'
            #!/usr/bin/env bash
            echo "HEALTHY_OLD_BINARY"
            EOF
            chmod +x "{{Path.Combine(installDir, "cdidx")}}"
            printf '{"version":"1.0.0"}' > "{{Path.Combine(installDir, "version.json")}}"
            printf 'healthy-lib' > "{{Path.Combine(installDir, "libe_sqlite3.so")}}"

            mkdir -p "{{payloadDir}}"
            cat > "{{Path.Combine(payloadDir, "cdidx")}}" <<'EOF'
            #!/usr/bin/env bash
            echo "NEW_BINARY"
            EOF
            chmod +x "{{Path.Combine(payloadDir, "cdidx")}}"
            printf '{"version":"1.2.3"}' > "{{Path.Combine(payloadDir, "version.json")}}"
            printf 'new-lib' > "{{Path.Combine(payloadDir, "libe_sqlite3.so")}}"
            tar czf "{{archivePath}}" -C "{{payloadDir}}" .

            if command -v sha256sum > /dev/null 2>&1; then
                checksum="$(sha256sum "{{archivePath}}" | awk '{print $1}')"
            elif command -v shasum > /dev/null 2>&1; then
                checksum="$(shasum -a 256 "{{archivePath}}" | awk '{print $1}')"
            else
                checksum="$(openssl dgst -sha256 "{{archivePath}}" | awk '{print $NF}')"
            fi
            printf '%s  CodeIndex-linux-x64.tar.gz\n' "$checksum" > "{{checksumsPath}}"

            VERSION="v1.2.3"
            OS_NAME="linux"
            ARCH_NAME="x64"
            RID="linux-x64"

            curl() {
                local output_path=""
                local url=""
                while [ $# -gt 0 ]; do
                    case "$1" in
                        -o)
                            output_path="$2"
                            shift 2
                            ;;
                        -w)
                            shift 2
                            ;;
                        *)
                            url="$1"
                            shift
                            ;;
                    esac
                done

                case "$url" in
                    */sha256sums.txt) command cp "{{checksumsPath}}" "$output_path" ;;
                    *) command cp "{{archivePath}}" "$output_path" ;;
                esac

                printf '200'
                return 0
            }

            mktemp() {
                if [ "${1:-}" = "-d" ] && [ "${2:-}" = "{{installDir}}/.cdidx-backup.XXXXXX" ]; then
                    return 1
                fi
                command mktemp "$@"
            }

            download_and_install
            """,
            new Dictionary<string, string?>
            {
                ["CDIDX_INSTALL_DIR"] = installDir,
            });

        Assert.Equal(1, exitCode);
        Assert.Contains("Failed to create backup directory under", stderr);
        Assert.True(File.Exists(Path.Combine(installDir, "cdidx")));
        Assert.True(File.Exists(Path.Combine(installDir, "version.json")));
        Assert.True(File.Exists(Path.Combine(installDir, "libe_sqlite3.so")));
        Assert.Contains("HEALTHY_OLD_BINARY", File.ReadAllText(Path.Combine(installDir, "cdidx")));
        Assert.Equal("""{"version":"1.0.0"}""", File.ReadAllText(Path.Combine(installDir, "version.json")));
        Assert.Equal("healthy-lib", File.ReadAllText(Path.Combine(installDir, "libe_sqlite3.so")));
        Assert.Empty(Directory.GetFileSystemEntries(installDir, ".cdidx-*"));
    }

    [Fact]
    public void DownloadAndInstall_MissingAsset_DoesNotOverwriteExistingHealthyInstall()
    {
        if (OperatingSystem.IsWindows())
            return;

        var installDir = Path.Combine(_tempRoot, "missing_asset_existing_target");
        var payloadDir = Path.Combine(_tempRoot, "missing_asset_existing_payload");
        var archivePath = Path.Combine(_tempRoot, "missing_asset_existing.tar.gz");
        var checksumsPath = Path.Combine(_tempRoot, "missing_asset_existing.sha256sums.txt");

        var (exitCode, stdout, stderr) = RunInstallerSnippet(
            $$"""
            mkdir -p "{{installDir}}"
            cat > "{{Path.Combine(installDir, "cdidx")}}" <<'EOF'
            #!/usr/bin/env bash
            echo "HEALTHY_OLD_BINARY"
            EOF
            chmod +x "{{Path.Combine(installDir, "cdidx")}}"
            printf '{"version":"1.0.0"}' > "{{Path.Combine(installDir, "version.json")}}"
            printf 'healthy-lib' > "{{Path.Combine(installDir, "libe_sqlite3.so")}}"

            mkdir -p "{{payloadDir}}"
            cat > "{{Path.Combine(payloadDir, "cdidx")}}" <<'EOF'
            #!/usr/bin/env bash
            echo "BROKEN_NEW_BINARY"
            EOF
            chmod +x "{{Path.Combine(payloadDir, "cdidx")}}"
            printf '{"version":"1.2.3"}' > "{{Path.Combine(payloadDir, "version.json")}}"
            tar czf "{{archivePath}}" -C "{{payloadDir}}" .

            if command -v sha256sum > /dev/null 2>&1; then
                checksum="$(sha256sum "{{archivePath}}" | awk '{print $1}')"
            elif command -v shasum > /dev/null 2>&1; then
                checksum="$(shasum -a 256 "{{archivePath}}" | awk '{print $1}')"
            else
                checksum="$(openssl dgst -sha256 "{{archivePath}}" | awk '{print $NF}')"
            fi
            printf '%s  CodeIndex-linux-x64.tar.gz\n' "$checksum" > "{{checksumsPath}}"

            VERSION="v1.2.3"
            OS_NAME="linux"
            ARCH_NAME="x64"
            RID="linux-x64"

            curl() {
                local output_path=""
                local url=""
                while [ $# -gt 0 ]; do
                    case "$1" in
                        -o)
                            output_path="$2"
                            shift 2
                            ;;
                        -w)
                            shift 2
                            ;;
                        *)
                            url="$1"
                            shift
                            ;;
                    esac
                done

                case "$url" in
                    */sha256sums.txt) cp "{{checksumsPath}}" "$output_path" ;;
                    *) cp "{{archivePath}}" "$output_path" ;;
                esac

                printf '200'
                return 0
            }

            download_status=0
            if ( download_and_install ); then
                echo "UNEXPECTED_SUCCESS"
            else
                download_status=$?
            fi

            echo "DOWNLOAD_STATUS:$download_status"
            grep -q 'HEALTHY_OLD_BINARY' "{{Path.Combine(installDir, "cdidx")}}" && echo "OLD_BINARY_PRESERVED"
            [ "$(cat "{{Path.Combine(installDir, "version.json")}}")" = '{"version":"1.0.0"}' ] && echo "OLD_VERSION_JSON_PRESERVED"
            [ "$(cat "{{Path.Combine(installDir, "libe_sqlite3.so")}}")" = 'healthy-lib' ] && echo "OLD_LIB_PRESERVED"
            if grep -q 'BROKEN_NEW_BINARY' "{{Path.Combine(installDir, "cdidx")}}"; then
                echo "BROKEN_BINARY_OVERWROTE"
            fi
            """,
            new Dictionary<string, string?>
            {
                ["CDIDX_INSTALL_DIR"] = installDir,
            },
            enforceStrictMode: false);

        Assert.Equal(0, exitCode);
        Assert.DoesNotContain("UNEXPECTED_SUCCESS", stdout);
        Assert.Contains("DOWNLOAD_STATUS:1", stdout);
        Assert.Contains("OLD_BINARY_PRESERVED", stdout);
        Assert.Contains("OLD_VERSION_JSON_PRESERVED", stdout);
        Assert.Contains("OLD_LIB_PRESERVED", stdout);
        Assert.DoesNotContain("BROKEN_BINARY_OVERWROTE", stdout);
        Assert.Contains("Required runtime asset missing from release tarball: libe_sqlite3.so", stderr);
    }

    [Fact]
    public void DownloadAndInstall_MoveFailure_RollsBackExistingHealthyInstall()
    {
        if (OperatingSystem.IsWindows())
            return;

        var installDir = Path.Combine(_tempRoot, "move_failure_existing_target");
        var payloadDir = Path.Combine(_tempRoot, "move_failure_existing_payload");
        var archivePath = Path.Combine(_tempRoot, "move_failure_existing.tar.gz");
        var checksumsPath = Path.Combine(_tempRoot, "move_failure_existing.sha256sums.txt");

        var (exitCode, stdout, stderr) = RunInstallerSnippet(
            $$"""
            mkdir -p "{{installDir}}"
            cat > "{{Path.Combine(installDir, "cdidx")}}" <<'EOF'
            #!/usr/bin/env bash
            echo "HEALTHY_OLD_BINARY"
            EOF
            chmod +x "{{Path.Combine(installDir, "cdidx")}}"
            printf '{"version":"1.0.0"}' > "{{Path.Combine(installDir, "version.json")}}"
            printf 'healthy-lib' > "{{Path.Combine(installDir, "libe_sqlite3.so")}}"

            mkdir -p "{{payloadDir}}"
            cat > "{{Path.Combine(payloadDir, "cdidx")}}" <<'EOF'
            #!/usr/bin/env bash
            echo "NEW_BINARY"
            EOF
            chmod +x "{{Path.Combine(payloadDir, "cdidx")}}"
            printf '{"version":"1.2.3"}' > "{{Path.Combine(payloadDir, "version.json")}}"
            printf 'new-lib' > "{{Path.Combine(payloadDir, "libe_sqlite3.so")}}"
            tar czf "{{archivePath}}" -C "{{payloadDir}}" .

            if command -v sha256sum > /dev/null 2>&1; then
                checksum="$(sha256sum "{{archivePath}}" | awk '{print $1}')"
            elif command -v shasum > /dev/null 2>&1; then
                checksum="$(shasum -a 256 "{{archivePath}}" | awk '{print $1}')"
            else
                checksum="$(openssl dgst -sha256 "{{archivePath}}" | awk '{print $NF}')"
            fi
            printf '%s  CodeIndex-linux-x64.tar.gz\n' "$checksum" > "{{checksumsPath}}"

            VERSION="v1.2.3"
            OS_NAME="linux"
            ARCH_NAME="x64"
            RID="linux-x64"

            curl() {
                local output_path=""
                local url=""
                while [ $# -gt 0 ]; do
                    case "$1" in
                        -o)
                            output_path="$2"
                            shift 2
                            ;;
                        -w)
                            shift 2
                            ;;
                        *)
                            url="$1"
                            shift
                            ;;
                    esac
                done

                case "$url" in
                    */sha256sums.txt) cp "{{checksumsPath}}" "$output_path" ;;
                    *) cp "{{archivePath}}" "$output_path" ;;
                esac

                printf '200'
                return 0
            }

            stage_move_failures=0
            mv() {
                local src="$1"
                case "$src" in
                    */.cdidx-stage.*/*)
                        stage_move_failures=$((stage_move_failures + 1))
                        if [ "$stage_move_failures" -eq 2 ]; then
                            return 1
                        fi
                        command mv "$@"
                        ;;
                    *)
                        command mv "$@"
                        ;;
                esac
            }

            download_status=0
            if download_and_install; then
                echo "UNEXPECTED_SUCCESS"
            else
                download_status=$?
            fi

            echo "DOWNLOAD_STATUS:$download_status"
            echo "STAGE_MOVE_FAILURES:$stage_move_failures"
            grep -q 'HEALTHY_OLD_BINARY' "{{Path.Combine(installDir, "cdidx")}}" && echo "OLD_BINARY_PRESERVED"
            [ "$(cat "{{Path.Combine(installDir, "version.json")}}")" = '{"version":"1.0.0"}' ] && echo "OLD_VERSION_JSON_PRESERVED"
            [ "$(cat "{{Path.Combine(installDir, "libe_sqlite3.so")}}")" = 'healthy-lib' ] && echo "OLD_LIB_PRESERVED"
            if grep -q 'NEW_BINARY' "{{Path.Combine(installDir, "cdidx")}}"; then
                echo "NEW_BINARY_OVERWROTE"
            fi
            if [ "$(cat "{{Path.Combine(installDir, "version.json")}}")" = '{"version":"1.2.3"}' ]; then
                echo "NEW_VERSION_JSON_OVERWROTE"
            fi
            if [ "$(cat "{{Path.Combine(installDir, "libe_sqlite3.so")}}")" = 'new-lib' ]; then
                echo "NEW_LIB_OVERWROTE"
            fi
            """,
            new Dictionary<string, string?>
            {
                ["CDIDX_INSTALL_DIR"] = installDir,
            },
            enforceStrictMode: false);

        Assert.Equal(0, exitCode);
        Assert.DoesNotContain("UNEXPECTED_SUCCESS", stdout);
        Assert.Contains("DOWNLOAD_STATUS:1", stdout);
        Assert.Contains("STAGE_MOVE_FAILURES:2", stdout);
        Assert.Contains("OLD_BINARY_PRESERVED", stdout);
        Assert.Contains("OLD_VERSION_JSON_PRESERVED", stdout);
        Assert.Contains("OLD_LIB_PRESERVED", stdout);
        Assert.DoesNotContain("NEW_BINARY_OVERWROTE", stdout);
        Assert.DoesNotContain("NEW_VERSION_JSON_OVERWROTE", stdout);
        Assert.DoesNotContain("NEW_LIB_OVERWROTE", stdout);
        Assert.Contains("Restoring previous install", stderr);
    }

    [Fact]
    public void DownloadAndInstall_BackupMoveFailure_PreservesExistingHealthyInstall()
    {
        if (OperatingSystem.IsWindows())
            return;

        var installDir = Path.Combine(_tempRoot, "backup_failure_existing_target");
        var payloadDir = Path.Combine(_tempRoot, "backup_failure_existing_payload");
        var archivePath = Path.Combine(_tempRoot, "backup_failure_existing.tar.gz");
        var checksumsPath = Path.Combine(_tempRoot, "backup_failure_existing.sha256sums.txt");

        var (exitCode, stdout, stderr) = RunInstallerSnippet(
            $$"""
            mkdir -p "{{installDir}}"
            cat > "{{Path.Combine(installDir, "cdidx")}}" <<'EOF'
            #!/usr/bin/env bash
            echo "HEALTHY_OLD_BINARY"
            EOF
            chmod +x "{{Path.Combine(installDir, "cdidx")}}"
            printf '{"version":"1.0.0"}' > "{{Path.Combine(installDir, "version.json")}}"
            printf 'healthy-lib' > "{{Path.Combine(installDir, "libe_sqlite3.so")}}"

            mkdir -p "{{payloadDir}}"
            cat > "{{Path.Combine(payloadDir, "cdidx")}}" <<'EOF'
            #!/usr/bin/env bash
            echo "NEW_BINARY"
            EOF
            chmod +x "{{Path.Combine(payloadDir, "cdidx")}}"
            printf '{"version":"1.2.3"}' > "{{Path.Combine(payloadDir, "version.json")}}"
            printf 'new-lib' > "{{Path.Combine(payloadDir, "libe_sqlite3.so")}}"
            tar czf "{{archivePath}}" -C "{{payloadDir}}" .

            if command -v sha256sum > /dev/null 2>&1; then
                checksum="$(sha256sum "{{archivePath}}" | awk '{print $1}')"
            elif command -v shasum > /dev/null 2>&1; then
                checksum="$(shasum -a 256 "{{archivePath}}" | awk '{print $1}')"
            else
                checksum="$(openssl dgst -sha256 "{{archivePath}}" | awk '{print $NF}')"
            fi
            printf '%s  CodeIndex-linux-x64.tar.gz\n' "$checksum" > "{{checksumsPath}}"

            VERSION="v1.2.3"
            OS_NAME="linux"
            ARCH_NAME="x64"
            RID="linux-x64"

            curl() {
                local output_path=""
                local url=""
                while [ $# -gt 0 ]; do
                    case "$1" in
                        -o)
                            output_path="$2"
                            shift 2
                            ;;
                        -w)
                            shift 2
                            ;;
                        *)
                            url="$1"
                            shift
                            ;;
                    esac
                done

                case "$url" in
                    */sha256sums.txt) cp "{{checksumsPath}}" "$output_path" ;;
                    *) cp "{{archivePath}}" "$output_path" ;;
                esac

                printf '200'
                return 0
            }

            backup_move_failures=0
            mv() {
                local src="$1"
                local dst="$2"
                case "$src|$dst" in
                    "{{Path.Combine(installDir, "version.json")}}"|*/.cdidx-backup.*/version.json)
                        backup_move_failures=$((backup_move_failures + 1))
                        return 1
                        ;;
                    *)
                        command mv "$@"
                        ;;
                esac
            }

            download_status=0
            if download_and_install; then
                echo "UNEXPECTED_SUCCESS"
            else
                download_status=$?
            fi

            echo "DOWNLOAD_STATUS:$download_status"
            echo "BACKUP_MOVE_FAILURES:$backup_move_failures"
            [ -e "{{Path.Combine(installDir, "cdidx")}}" ] && echo "CDIDX_PRESENT" || echo "CDIDX_MISSING"
            [ -e "{{Path.Combine(installDir, "version.json")}}" ] && echo "VERSION_PRESENT" || echo "VERSION_MISSING"
            [ -e "{{Path.Combine(installDir, "libe_sqlite3.so")}}" ] && echo "LIB_PRESENT" || echo "LIB_MISSING"
            grep -q 'HEALTHY_OLD_BINARY' "{{Path.Combine(installDir, "cdidx")}}" && echo "OLD_BINARY_PRESERVED"
            [ "$(cat "{{Path.Combine(installDir, "version.json")}}")" = '{"version":"1.0.0"}' ] && echo "OLD_VERSION_JSON_PRESERVED"
            [ "$(cat "{{Path.Combine(installDir, "libe_sqlite3.so")}}")" = 'healthy-lib' ] && echo "OLD_LIB_PRESERVED"
            if grep -q 'NEW_BINARY' "{{Path.Combine(installDir, "cdidx")}}"; then
                echo "NEW_BINARY_OVERWROTE"
            fi
            if [ "$(cat "{{Path.Combine(installDir, "version.json")}}")" = '{"version":"1.2.3"}' ]; then
                echo "NEW_VERSION_JSON_OVERWROTE"
            fi
            if [ "$(cat "{{Path.Combine(installDir, "libe_sqlite3.so")}}")" = 'new-lib' ]; then
                echo "NEW_LIB_OVERWROTE"
            fi
            """,
            new Dictionary<string, string?>
            {
                ["CDIDX_INSTALL_DIR"] = installDir,
            },
            enforceStrictMode: false);

        Assert.Equal(0, exitCode);
        Assert.DoesNotContain("UNEXPECTED_SUCCESS", stdout);
        Assert.Contains("DOWNLOAD_STATUS:1", stdout);
        Assert.Contains("BACKUP_MOVE_FAILURES:1", stdout);
        Assert.Contains("CDIDX_PRESENT", stdout);
        Assert.Contains("VERSION_PRESENT", stdout);
        Assert.Contains("LIB_PRESENT", stdout);
        Assert.Contains("OLD_BINARY_PRESERVED", stdout);
        Assert.Contains("OLD_VERSION_JSON_PRESERVED", stdout);
        Assert.Contains("OLD_LIB_PRESERVED", stdout);
        Assert.DoesNotContain("NEW_BINARY_OVERWROTE", stdout);
        Assert.DoesNotContain("NEW_VERSION_JSON_OVERWROTE", stdout);
        Assert.DoesNotContain("NEW_LIB_OVERWROTE", stdout);
        Assert.Contains("Install aborted before replacing the current install", stderr);
    }

    [Fact]
    public void DownloadAndInstall_RollbackFailure_PreservesRecoveryArtifacts()
    {
        if (OperatingSystem.IsWindows())
            return;

        var installDir = Path.Combine(_tempRoot, "rollback_failure_existing_target");
        var payloadDir = Path.Combine(_tempRoot, "rollback_failure_existing_payload");
        var archivePath = Path.Combine(_tempRoot, "rollback_failure_existing.tar.gz");
        var checksumsPath = Path.Combine(_tempRoot, "rollback_failure_existing.sha256sums.txt");

        var (exitCode, stdout, stderr) = RunInstallerSnippet(
            $$"""
            mkdir -p "{{installDir}}"
            cat > "{{Path.Combine(installDir, "cdidx")}}" <<'EOF'
            #!/usr/bin/env bash
            echo "HEALTHY_OLD_BINARY"
            EOF
            chmod +x "{{Path.Combine(installDir, "cdidx")}}"
            printf '{"version":"1.0.0"}' > "{{Path.Combine(installDir, "version.json")}}"
            printf 'healthy-lib' > "{{Path.Combine(installDir, "libe_sqlite3.so")}}"

            mkdir -p "{{payloadDir}}"
            cat > "{{Path.Combine(payloadDir, "cdidx")}}" <<'EOF'
            #!/usr/bin/env bash
            echo "NEW_BINARY"
            EOF
            chmod +x "{{Path.Combine(payloadDir, "cdidx")}}"
            printf '{"version":"1.2.3"}' > "{{Path.Combine(payloadDir, "version.json")}}"
            printf 'new-lib' > "{{Path.Combine(payloadDir, "libe_sqlite3.so")}}"
            tar czf "{{archivePath}}" -C "{{payloadDir}}" .

            if command -v sha256sum > /dev/null 2>&1; then
                checksum="$(sha256sum "{{archivePath}}" | awk '{print $1}')"
            elif command -v shasum > /dev/null 2>&1; then
                checksum="$(shasum -a 256 "{{archivePath}}" | awk '{print $1}')"
            else
                checksum="$(openssl dgst -sha256 "{{archivePath}}" | awk '{print $NF}')"
            fi
            printf '%s  CodeIndex-linux-x64.tar.gz\n' "$checksum" > "{{checksumsPath}}"

            VERSION="v1.2.3"
            OS_NAME="linux"
            ARCH_NAME="x64"
            RID="linux-x64"

            curl() {
                local output_path=""
                local url=""
                while [ $# -gt 0 ]; do
                    case "$1" in
                        -o)
                            output_path="$2"
                            shift 2
                            ;;
                        -w)
                            shift 2
                            ;;
                        *)
                            url="$1"
                            shift
                            ;;
                    esac
                done

                case "$url" in
                    */sha256sums.txt) cp "{{checksumsPath}}" "$output_path" ;;
                    *) cp "{{archivePath}}" "$output_path" ;;
                esac

                printf '200'
                return 0
            }

            restore_move_failures=0
            mv() {
                local src="$1"
                local dst="$2"

                if [ "$src" = "{{Path.Combine(installDir, "version.json")}}" ]; then
                    case "$dst" in
                        */.cdidx-backup.*/version.json)
                            return 1
                            ;;
                    esac
                fi

                if [ "$dst" = "{{Path.Combine(installDir, "cdidx")}}" ]; then
                    case "$src" in
                        */.cdidx-backup.*/cdidx)
                            restore_move_failures=$((restore_move_failures + 1))
                            return 1
                            ;;
                    esac
                fi

                command mv "$@"
            }

            download_status=0
            if download_and_install; then
                echo "UNEXPECTED_SUCCESS"
            else
                download_status=$?
            fi

            echo "DOWNLOAD_STATUS:$download_status"
            echo "RESTORE_MOVE_FAILURES:$restore_move_failures"
            [ -e "{{Path.Combine(installDir, "cdidx")}}" ] && echo "CDIDX_PRESENT" || echo "CDIDX_MISSING"
            [ -e "{{Path.Combine(installDir, "version.json")}}" ] && echo "VERSION_PRESENT" || echo "VERSION_MISSING"
            [ -e "{{Path.Combine(installDir, "libe_sqlite3.so")}}" ] && echo "LIB_PRESENT" || echo "LIB_MISSING"
            shopt -s nullglob
            for path in "{{installDir}}"/.cdidx-backup.*; do
                echo "BACKUP_DIR:$path"
                [ -f "$path/cdidx" ] && echo "BACKUP_HAS_CDIDX"
            done
            for path in "{{installDir}}"/.cdidx-stage.*; do
                echo "STAGE_DIR:$path"
                [ -f "$path/cdidx" ] && echo "STAGE_HAS_CDIDX"
            done
            """,
            new Dictionary<string, string?>
            {
                ["CDIDX_INSTALL_DIR"] = installDir,
            },
            enforceStrictMode: false);

        Assert.Equal(0, exitCode);
        Assert.DoesNotContain("UNEXPECTED_SUCCESS", stdout);
        Assert.Contains("DOWNLOAD_STATUS:1", stdout);
        Assert.Contains("RESTORE_MOVE_FAILURES:1", stdout);
        Assert.Contains("CDIDX_MISSING", stdout);
        Assert.Contains("VERSION_PRESENT", stdout);
        Assert.Contains("LIB_PRESENT", stdout);
        Assert.Contains("BACKUP_DIR:", stdout);
        Assert.Contains("BACKUP_HAS_CDIDX", stdout);
        Assert.Contains("STAGE_DIR:", stdout);
        Assert.Contains("STAGE_HAS_CDIDX", stdout);
        Assert.Contains("Rollback incomplete. Preserving recovery artifacts for manual recovery.", stderr);
        Assert.Contains("Backup:", stderr);
        Assert.Contains("Stage:", stderr);
    }

    [Fact]
    public void DownloadReleaseFile_Http404_PrintsHttpSpecificError()
    {
        if (OperatingSystem.IsWindows())
            return;

        var (exitCode, _, stderr) = RunInstallerSnippet(
            $$"""
            VERSION="v0.99.0"
            RID="linux-x64"
            curl() {
                local output_path=""
                while [ $# -gt 0 ]; do
                    case "$1" in
                        -o)
                            output_path="$2"
                            shift 2
                            ;;
                        -w)
                            shift 2
                            ;;
                        *)
                            shift
                            ;;
                    esac
                done

                : > "$output_path"
                printf '404'
                return 0
            }

            download_release_file "https://example.test/releases/download/v0.99.0/CodeIndex-linux-x64.tar.gz" "{{Path.Combine(_tempRoot, "archive.tar.gz")}}" "CodeIndex-linux-x64.tar.gz"
            """);

        Assert.Equal(1, exitCode);
        Assert.Contains("HTTP 404", stderr);
        Assert.Contains("version v0.99.0 exists", stderr);
        Assert.Contains("linux-x64 assets", stderr);
        Assert.DoesNotContain("Check your network connection", stderr);
    }

    [Fact]
    public void DownloadAndInstall_MissingChecksumEntry_PrintsActionableError()
    {
        if (OperatingSystem.IsWindows())
            return;

        var installDir = Path.Combine(_tempRoot, "missing_checksum_target");
        var payloadDir = Path.Combine(_tempRoot, "missing_checksum_payload");
        var archivePath = Path.Combine(_tempRoot, "missing_checksum.tar.gz");
        var checksumsPath = Path.Combine(_tempRoot, "missing_checksum.sha256sums.txt");

        var (exitCode, stdout, stderr) = RunInstallerSnippet(
            $$"""
            mkdir -p "{{payloadDir}}"
            cat > "{{Path.Combine(payloadDir, "cdidx")}}" <<'EOF'
            #!/usr/bin/env bash
            echo "cdidx v1.2.3"
            EOF
            chmod +x "{{Path.Combine(payloadDir, "cdidx")}}"
            printf '{"version":"1.2.3"}' > "{{Path.Combine(payloadDir, "version.json")}}"
            : > "{{Path.Combine(payloadDir, "libe_sqlite3.so")}}"
            tar czf "{{archivePath}}" -C "{{payloadDir}}" .

            printf '%s  %s\n' deadbeef CodeIndex-linux-arm64.tar.gz > "{{checksumsPath}}"

            VERSION="v1.2.3"
            OS_NAME="linux"
            ARCH_NAME="x64"
            RID="linux-x64"

            curl() {
                local output_path=""
                local url=""
                while [ $# -gt 0 ]; do
                    case "$1" in
                        -o)
                            output_path="$2"
                            shift 2
                            ;;
                        -w)
                            shift 2
                            ;;
                        *)
                            url="$1"
                            shift
                            ;;
                    esac
                done

                case "$url" in
                    */sha256sums.txt) cp "{{checksumsPath}}" "$output_path" ;;
                    *) cp "{{archivePath}}" "$output_path" ;;
                esac

                printf '200'
                return 0
            }

            download_and_install
            """,
            new Dictionary<string, string?>
            {
                ["CDIDX_INSTALL_DIR"] = installDir,
            });

        Assert.Equal(1, exitCode);
        Assert.Contains("Verifying checksum...", stdout);
        Assert.DoesNotContain("Installed cdidx", stdout);
        Assert.Contains("Checksum for CodeIndex-linux-x64.tar.gz not found in sha256sums.txt.", stderr);
    }

    [Fact]
    public void CheckExisting_DifferentVersion_UsesNeutralSwitchingWording()
    {
        if (OperatingSystem.IsWindows())
            return;

        var installDir = Path.Combine(_tempRoot, "bin_switch");
        var (exitCode, stdout, stderr) = RunInstallerSnippet(
            $$"""
            mkdir -p "{{installDir}}"
            cat > "{{Path.Combine(installDir, "cdidx")}}" <<'EOF'
            #!/usr/bin/env bash
            echo "cdidx v1.10.0"
            EOF
            chmod +x "{{Path.Combine(installDir, "cdidx")}}"

            VERSION="v0.99.0"
            detect_existing_install
            check_existing
            """,
            new Dictionary<string, string?>
            {
                ["CDIDX_INSTALL_DIR"] = installDir,
            });

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, stderr);
        Assert.Contains("Switching cdidx from 1.10.0 to 0.99.0", stdout);
        Assert.DoesNotContain("Upgrading cdidx from 1.10.0 to 0.99.0", stdout);
    }

    [Fact]
    public void DetectPlatform_MacosX64_PrintsActionableUnsupportedRidGuidance()
    {
        if (OperatingSystem.IsWindows())
            return;

        var (exitCode, _, stderr) = RunInstallerSnippet(
            """
            uname() {
                case "$1" in
                    -s) printf '%s\n' "Darwin" ;;
                    -m) printf '%s\n' "x86_64" ;;
                    *)  printf '%s\n' "unknown" ;;
                esac
            }

            detect_platform
            """,
            enforceStrictMode: false);

        Assert.Equal(1, exitCode);
        Assert.Contains("macOS x86_64 (Intel) binaries are not published as CodeIndex-osx-x64.tar.gz", stderr);
        Assert.Contains("dotnet tool install -g cdidx", stderr);
        Assert.Contains("dotnet publish src/CodeIndex/CodeIndex.csproj -c Release -r osx-x64 --self-contained true", stderr);
        Assert.Contains("docs/platform-support.md", stderr);
        Assert.DoesNotContain("Rosetta 2 with osx-arm64", stderr);
    }

    [Fact]
    public void DetectPlatform_UnsupportedArchitecture_PrintsPublishedRidPolicyAndSourceBuildGuidance()
    {
        if (OperatingSystem.IsWindows())
            return;

        var (exitCode, _, stderr) = RunInstallerSnippet(
            """
            uname() {
                case "$1" in
                    -s) printf '%s\n' "Linux" ;;
                    -m) printf '%s\n' "i686" ;;
                    *)  printf '%s\n' "unknown" ;;
                esac
            }

            detect_platform
            """,
            enforceStrictMode: false);

        Assert.Equal(1, exitCode);
        Assert.Contains("Unsupported architecture: i686", stderr);
        Assert.Contains("linux-x64, linux-arm64, osx-arm64, win-x64, win-arm64", stderr);
        Assert.Contains("linux-x86, osx-x64, and win-x86 are not currently shipped", stderr);
        Assert.Contains("dotnet tool install -g cdidx", stderr);
        Assert.Contains("dotnet publish src/CodeIndex/CodeIndex.csproj -c Release -r <rid> --self-contained true", stderr);
        Assert.Contains("issues/new?title=Request%20official%20release%20asset%20for%20RID", stderr);
        Assert.Contains("docs/platform-support.md", stderr);
    }

    [Fact]
    public void ValidatePublishedReleaseRid_UnsupportedResolvedRid_FailsBeforeDownloadWithActionableGuidance()
    {
        if (OperatingSystem.IsWindows())
            return;

        var (exitCode, _, stderr) = RunInstallerSnippet(
            """
            RID="linux-arm"
            validate_published_release_rid
            """,
            enforceStrictMode: false);

        Assert.Equal(1, exitCode);
        Assert.Contains("Unsupported release asset RID: linux-arm", stderr);
        Assert.Contains("linux-x64, linux-arm64, osx-arm64, win-x64, win-arm64", stderr);
        Assert.Contains("dotnet tool install -g cdidx", stderr);
        Assert.Contains("dotnet publish src/CodeIndex/CodeIndex.csproj -c Release -r linux-arm --self-contained true", stderr);
        Assert.Contains("issues/new?title=Request%20official%20release%20asset%20for%20RID", stderr);
        Assert.Contains("docs/platform-support.md", stderr);
    }

    [Fact]
    public void ResolveVersion_UsesJqWhenAvailable()
    {
        if (OperatingSystem.IsWindows())
            return;

        var toolDir = Path.Combine(_tempRoot, "jq_stub_bin");
        Directory.CreateDirectory(toolDir);
        var jqPath = Path.Combine(toolDir, "jq");
        File.WriteAllText(jqPath, "#!/usr/bin/env bash\nprintf 'v9.9.9\\n'\n");
        File.SetUnixFileMode(jqPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        var (exitCode, stdout, stderr) = RunInstallerSnippet(
            """
            curl() {
                local output_path=""
                while [ $# -gt 0 ]; do
                    case "$1" in
                        -o)
                            output_path="$2"
                            shift 2
                            ;;
                        -w)
                            shift 2
                            ;;
                        *)
                            shift
                            ;;
                    esac
                done

                printf '{"tag_name":"v1.2.3"}' > "$output_path"
                printf '200'
                return 0
            }

            resolve_version ""
            """,
            new Dictionary<string, string?>
            {
                ["PATH"] = toolDir + Path.PathSeparator + (Environment.GetEnvironmentVariable("PATH") ?? string.Empty),
            });

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, stderr);
        Assert.Contains("Version: v9.9.9", stdout);
    }

    [Fact]
    public void ResolveVersion_UsesConfiguredApiBaseUrl()
    {
        if (OperatingSystem.IsWindows())
            return;

        var urlLogPath = Path.Combine(_tempRoot, "configured_api_base_url.log");
        var (exitCode, stdout, stderr) = RunInstallerSnippet(
            $$"""
            curl() {
                local output_path=""
                local url=""
                while [ $# -gt 0 ]; do
                    case "$1" in
                        -o)
                            output_path="$2"
                            shift 2
                            ;;
                        -w)
                            shift 2
                            ;;
                        *)
                            url="$1"
                            shift
                            ;;
                    esac
                done

                printf '%s\n' "$url" > "{{urlLogPath}}"
                printf '{"tag_name":"v1.2.3"}' > "$output_path"
                printf '200'
                return 0
            }

            resolve_version ""
            cat "{{urlLogPath}}"
            """,
            new Dictionary<string, string?>
            {
                ["CDIDX_GITHUB_API_BASE_URL"] = "https://mirror.example/api",
            });

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, stderr);
        Assert.Contains("Version: v1.2.3", stdout);
        Assert.Contains("https://mirror.example/api/repos/Widthdom/CodeIndex/releases/latest", stdout);
    }

    [Fact]
    public void ResolveVersion_ConfiguredApiBase403_PrintsConfiguredEndpointGuidance()
    {
        if (OperatingSystem.IsWindows())
            return;

        var (exitCode, stdout, stderr) = RunInstallerSnippet(
            """
            curl() {
                local output_path=""
                while [ $# -gt 0 ]; do
                    case "$1" in
                        -o)
                            output_path="$2"
                            shift 2
                            ;;
                        -w)
                            shift 2
                            ;;
                        *)
                            shift
                            ;;
                    esac
                done

                printf '{"message":"forbidden"}' > "$output_path"
                printf '403'
                return 0
            }

            resolve_version ""
            """,
            new Dictionary<string, string?>
            {
                ["CDIDX_GITHUB_API_BASE_URL"] = "https://mirror.example/api",
            });

        Assert.Equal(1, exitCode);
        Assert.Contains("Fetching latest release version", stdout);
        Assert.Contains("configured latest-release API (https://mirror.example/api) returned HTTP 403", stderr);
        Assert.Contains("https://mirror.example/api/repos/Widthdom/CodeIndex/releases/latest", stderr);
        Assert.DoesNotContain("GitHub API returned HTTP 403", stderr);
    }

    [Fact]
    public void DownloadAndInstall_ConfiguredReleaseBaseNetworkFailure_PrintsConfiguredHostGuidance()
    {
        if (OperatingSystem.IsWindows())
            return;

        var installDir = Path.Combine(_tempRoot, "configured_release_base_network_failure");
        var (exitCode, _, stderr) = RunInstallerSnippet(
            """
            VERSION="v1.2.3"
            OS_NAME="linux"
            ARCH_NAME="x64"
            RID="linux-x64"

            curl() {
                printf 'curl: (6) Could not resolve host: mirror.example\n' >&2
                return 7
            }

            download_and_install
            """,
            new Dictionary<string, string?>
            {
                ["CDIDX_INSTALL_DIR"] = installDir,
                ["CDIDX_GITHUB_BASE_URL"] = "https://mirror.example/releases",
            });

        Assert.Equal(1, exitCode);
        Assert.Contains("curl: (6) Could not resolve host: mirror.example", stderr);
        Assert.Contains("Network error reaching configured release host (https://mirror.example/releases)", stderr);
        Assert.Contains("https://mirror.example/releases/Widthdom/CodeIndex/releases/download/v1.2.3/CodeIndex-linux-x64.tar.gz", stderr);
        Assert.DoesNotContain("Network error reaching GitHub", stderr);
    }

    [Fact]
    public void SelfTestLocalMirror_WithoutExplicitInstallDir_UsesIsolatedTempInstallDir()
    {
        if (OperatingSystem.IsWindows())
            return;

        var homeDir = Path.Combine(_tempRoot, "self_test_home");
        var realInstallDir = Path.Combine(homeDir, ".local", "bin");
        var (exitCode, stdout, stderr) = RunInstallerSnippet(
            $$"""
            export HOME="{{homeDir}}"
            mkdir -p "{{realInstallDir}}"
            printf '%s\n' '#!/usr/bin/env bash' 'echo "REAL_EXISTING_BINARY"' > "{{Path.Combine(realInstallDir, "cdidx")}}"
            chmod +x "{{Path.Combine(realInstallDir, "cdidx")}}"
            printf '{"version":"9.9.9"}' > "{{Path.Combine(realInstallDir, "version.json")}}"
            printf 'real-lib' > "{{Path.Combine(realInstallDir, "libe_sqlite3.so")}}"

            detect_platform() { OS_NAME="linux"; ARCH_NAME="x64"; RID="linux-x64"; }
            wait_for_local_mirror_ready() { :; }
            python3() { sleep 30; }
            curl() { :; }
            main() {
                echo "MAIN_INSTALL_DIR:$INSTALL_DIR"
                mkdir -p "$INSTALL_DIR"
                printf '%s\n' '#!/usr/bin/env bash' 'echo "cdidx v1.2.3"' > "$INSTALL_DIR/cdidx"
                chmod +x "$INSTALL_DIR/cdidx"
                printf '{"version":"1.2.3"}' > "$INSTALL_DIR/version.json"
                printf 'self-test-lib' > "$INSTALL_DIR/libe_sqlite3.so"
            }

            run_local_mirror_self_test v1.2.3
            grep -q 'REAL_EXISTING_BINARY' "{{Path.Combine(realInstallDir, "cdidx")}}" && echo "REAL_INSTALL_PRESERVED"
            [ "$INSTALL_DIR" = "{{realInstallDir}}" ] && echo "USED_REAL_INSTALL_DIR"
            [ -f "$INSTALL_DIR/cdidx" ] && echo "SELF_TEST_INSTALL_PRESENT"
            """,
            enforceStrictMode: false);

        Assert.Equal(0, exitCode);
        Assert.Contains("Using isolated self-test install dir:", stdout);
        Assert.Contains("REAL_INSTALL_PRESERVED", stdout);
        Assert.Contains("SELF_TEST_INSTALL_PRESENT", stdout);
        Assert.DoesNotContain($"MAIN_INSTALL_DIR:{realInstallDir}", stdout);
        Assert.DoesNotContain("USED_REAL_INSTALL_DIR", stdout);
        Assert.DoesNotContain("ERROR:", stderr);
    }

    [Fact]
    public void SelfTestLocalMirror_WithExplicitInstallDir_UsesRequestedInstallDir()
    {
        if (OperatingSystem.IsWindows())
            return;

        var installDir = Path.Combine(_tempRoot, "self_test_explicit_install");
        var (exitCode, stdout, stderr) = RunInstallerSnippet(
            """
            detect_platform() { OS_NAME="linux"; ARCH_NAME="x64"; RID="linux-x64"; }
            wait_for_local_mirror_ready() { :; }
            python3() { sleep 30; }
            curl() { :; }
            main() {
                echo "MAIN_INSTALL_DIR:$INSTALL_DIR"
                mkdir -p "$INSTALL_DIR"
                printf '%s\n' '#!/usr/bin/env bash' 'echo "cdidx v1.2.3"' > "$INSTALL_DIR/cdidx"
                chmod +x "$INSTALL_DIR/cdidx"
                printf '{"version":"1.2.3"}' > "$INSTALL_DIR/version.json"
                printf 'self-test-lib' > "$INSTALL_DIR/libe_sqlite3.so"
            }

            run_local_mirror_self_test v1.2.3
            [ -f "$INSTALL_DIR/cdidx" ] && echo "EXPLICIT_INSTALL_PRESENT"
            """,
            new Dictionary<string, string?>
            {
                ["CDIDX_INSTALL_DIR"] = installDir,
            },
            enforceStrictMode: false);

        Assert.Equal(0, exitCode);
        Assert.Contains($"MAIN_INSTALL_DIR:{installDir}", stdout);
        Assert.Contains("Using explicit self-test install dir:", stdout);
        Assert.Contains("EXPLICIT_INSTALL_PRESENT", stdout);
        Assert.DoesNotContain("ERROR:", stderr);
    }

    [Fact]
    public void SelfTestLocalMirror_EmptyInstallDirEnv_StillUsesIsolatedTempInstallDir()
    {
        if (OperatingSystem.IsWindows())
            return;

        var homeDir = Path.Combine(_tempRoot, "self_test_empty_env_home");
        var realInstallDir = Path.Combine(homeDir, ".local", "bin");
        var (exitCode, stdout, stderr) = RunInstallerSnippet(
            $$"""
            export HOME="{{homeDir}}"
            mkdir -p "{{realInstallDir}}"
            printf '%s\n' '#!/usr/bin/env bash' 'echo "REAL_EXISTING_BINARY"' > "{{Path.Combine(realInstallDir, "cdidx")}}"
            chmod +x "{{Path.Combine(realInstallDir, "cdidx")}}"
            printf '{"version":"9.9.9"}' > "{{Path.Combine(realInstallDir, "version.json")}}"
            printf 'real-lib' > "{{Path.Combine(realInstallDir, "libe_sqlite3.so")}}"

            detect_platform() { OS_NAME="linux"; ARCH_NAME="x64"; RID="linux-x64"; }
            wait_for_local_mirror_ready() { :; }
            python3() { sleep 30; }
            curl() { :; }
            main() {
                echo "MAIN_INSTALL_DIR:$INSTALL_DIR"
                mkdir -p "$INSTALL_DIR"
                printf '%s\n' '#!/usr/bin/env bash' 'echo "cdidx v1.2.3"' > "$INSTALL_DIR/cdidx"
                chmod +x "$INSTALL_DIR/cdidx"
                printf '{"version":"1.2.3"}' > "$INSTALL_DIR/version.json"
                printf 'self-test-lib' > "$INSTALL_DIR/libe_sqlite3.so"
            }

            run_local_mirror_self_test v1.2.3
            grep -q 'REAL_EXISTING_BINARY' "{{Path.Combine(realInstallDir, "cdidx")}}" && echo "REAL_INSTALL_PRESERVED"
            [ "$INSTALL_DIR" = "{{realInstallDir}}" ] && echo "USED_REAL_INSTALL_DIR"
            echo "SELF_TEST_DONE"
            """,
            new Dictionary<string, string?>
            {
                ["CDIDX_INSTALL_DIR"] = "",
            },
            enforceStrictMode: false);

        Assert.Equal(0, exitCode);
        Assert.Contains("Using isolated self-test install dir:", stdout);
        Assert.Contains("REAL_INSTALL_PRESERVED", stdout);
        Assert.DoesNotContain($"MAIN_INSTALL_DIR:{realInstallDir}", stdout);
        Assert.DoesNotContain("USED_REAL_INSTALL_DIR", stdout);
        Assert.DoesNotContain("ERROR:", stderr);
    }

    [Fact]
    public void SelfTestLocalMirror_AddsLoopbackNoProxyAndUsesCurlNoProxy()
    {
        if (OperatingSystem.IsWindows())
            return;

        var installDir = Path.Combine(_tempRoot, "self_test_no_proxy_install");
        var curlLogPath = Path.Combine(_tempRoot, "self_test_no_proxy.log");
        var (exitCode, stdout, stderr) = RunInstallerSnippet(
            $$"""
            detect_platform() { OS_NAME="linux"; ARCH_NAME="x64"; RID="linux-x64"; }
            python3() { sleep 30; }
            curl() {
                case " $* " in
                    *" --noproxy 127.0.0.1,localhost "*) printf 'HAS_NOPROXY\n' >> "{{curlLogPath}}" ;;
                    *) printf 'MISSING_NOPROXY\n' >> "{{curlLogPath}}" ;;
                esac
                printf 'NO_PROXY=%s\n' "${NO_PROXY:-}" >> "{{curlLogPath}}"
                printf 'no_proxy=%s\n' "${no_proxy:-}" >> "{{curlLogPath}}"
                printf '200'
                return 0
            }
            main() {
                local output_file
                output_file="$(mktemp)"
                curl_http_get "http://127.0.0.1:18765/download" "$output_file" > /dev/null
                rm -f "$output_file"
                mkdir -p "$INSTALL_DIR"
                printf '%s\n' '#!/usr/bin/env bash' 'echo "cdidx v1.2.3"' > "$INSTALL_DIR/cdidx"
                chmod +x "$INSTALL_DIR/cdidx"
                printf '{"version":"1.2.3"}' > "$INSTALL_DIR/version.json"
                printf 'self-test-lib' > "$INSTALL_DIR/libe_sqlite3.so"
            }

            run_local_mirror_self_test v1.2.3
            cat "{{curlLogPath}}"
            """,
            new Dictionary<string, string?>
            {
                ["CDIDX_INSTALL_DIR"] = installDir,
                ["NO_PROXY"] = "example.internal",
                ["no_proxy"] = "svc.local",
            },
            enforceStrictMode: false);

        Assert.Equal(0, exitCode);
        Assert.DoesNotContain("MISSING_NOPROXY", stdout);
        Assert.Contains("HAS_NOPROXY", stdout);
        Assert.Contains("NO_PROXY=example.internal,127.0.0.1,localhost", stdout);
        Assert.Contains("no_proxy=svc.local,127.0.0.1,localhost", stdout);
        Assert.DoesNotContain("ERROR:", stderr);
    }

    [Fact]
    public void SelfTestLocalMirror_CustomPort_UsesConfiguredPort()
    {
        if (OperatingSystem.IsWindows())
            return;

        var installDir = Path.Combine(_tempRoot, "self_test_custom_port_install");
        var (exitCode, stdout, stderr) = RunInstallerSnippet(
            """
            detect_platform() { OS_NAME="linux"; ARCH_NAME="x64"; RID="linux-x64"; }
            wait_for_local_mirror_ready() {
                echo "READY_URL:$1"
            }
            python3() { sleep 30; }
            curl() { :; }
            main() {
                mkdir -p "$INSTALL_DIR"
                printf '%s\n' '#!/usr/bin/env bash' 'echo "cdidx v1.2.3"' > "$INSTALL_DIR/cdidx"
                chmod +x "$INSTALL_DIR/cdidx"
                printf '{"version":"1.2.3"}' > "$INSTALL_DIR/version.json"
                printf 'self-test-lib' > "$INSTALL_DIR/libe_sqlite3.so"
            }

            run_local_mirror_self_test v1.2.3
            """,
            new Dictionary<string, string?>
            {
                ["CDIDX_INSTALL_DIR"] = installDir,
                ["CDIDX_LOCAL_MIRROR_PORT"] = "18766",
            },
            enforceStrictMode: false);

        Assert.Equal(0, exitCode);
        Assert.Contains("READY_URL:http://127.0.0.1:18766/Widthdom/CodeIndex/releases/download/v1.2.3/CodeIndex-linux-x64.tar.gz", stdout);
        Assert.Contains("Running local mirror self-test against http://127.0.0.1:18766/", stdout);
        Assert.DoesNotContain("ERROR:", stderr);
    }

    [Fact]
    public void SelfTestLocalMirror_LocalMirrorStartFailure_PrintsSelfTestSpecificError()
    {
        if (OperatingSystem.IsWindows())
            return;

        var installDir = Path.Combine(_tempRoot, "self_test_start_failure");
        var (exitCode, stdout, stderr) = RunInstallerSnippet(
            """
            detect_platform() { OS_NAME="linux"; ARCH_NAME="x64"; RID="linux-x64"; }
            python3() {
                echo "SELF_TEST_BIND_FAILED" >&2
                return 1
            }
            curl() { :; }
            main() { echo "MAIN_SHOULD_NOT_RUN"; }

            run_local_mirror_self_test v1.2.3
            """,
            new Dictionary<string, string?>
            {
                ["CDIDX_INSTALL_DIR"] = installDir,
            },
            enforceStrictMode: false);

        Assert.Equal(1, exitCode);
        Assert.DoesNotContain("MAIN_SHOULD_NOT_RUN", stdout);
        Assert.Contains("Local mirror self-test could not start a loopback HTTP server", stderr);
        Assert.Contains("This is a self-test harness failure, not an external network/proxy problem.", stderr);
        Assert.Contains("SELF_TEST_BIND_FAILED", stderr);
        Assert.DoesNotContain("Check your connection or corporate proxy", stderr);
        Assert.DoesNotContain("CDIDX_LOCAL_MIRROR_PORT", stderr);
    }

    [Fact]
    public void SelfTestLocalMirror_AddressAlreadyInUse_PrintsPortSpecificGuidance()
    {
        if (OperatingSystem.IsWindows())
            return;

        var installDir = Path.Combine(_tempRoot, "self_test_port_in_use");
        var (exitCode, stdout, stderr) = RunInstallerSnippet(
            """
            detect_platform() { OS_NAME="linux"; ARCH_NAME="x64"; RID="linux-x64"; }
            python3() {
                echo "OSError: [Errno 48] Address already in use" >&2
                return 1
            }
            curl() { :; }
            main() { echo "MAIN_SHOULD_NOT_RUN"; }

            run_local_mirror_self_test v1.2.3
            """,
            new Dictionary<string, string?>
            {
                ["CDIDX_INSTALL_DIR"] = installDir,
            },
            enforceStrictMode: false);

        Assert.Equal(1, exitCode);
        Assert.DoesNotContain("MAIN_SHOULD_NOT_RUN", stdout);
        Assert.Contains("Address already in use", stderr);
        Assert.Contains("CDIDX_LOCAL_MIRROR_PORT", stderr);
        Assert.Contains("free port", stderr);
    }

    [Fact]
    public void SelfTestLocalMirror_BindPermissionFailure_PrintsPermissionSpecificGuidance()
    {
        if (OperatingSystem.IsWindows())
            return;

        var installDir = Path.Combine(_tempRoot, "self_test_permission_failure");
        var (exitCode, stdout, stderr) = RunInstallerSnippet(
            """
            detect_platform() { OS_NAME="linux"; ARCH_NAME="x64"; RID="linux-x64"; }
            python3() {
                echo "PermissionError: [Errno 1] Operation not permitted" >&2
                return 1
            }
            curl() { :; }
            main() { echo "MAIN_SHOULD_NOT_RUN"; }

            run_local_mirror_self_test v1.2.3
            """,
            new Dictionary<string, string?>
            {
                ["CDIDX_INSTALL_DIR"] = installDir,
            },
            enforceStrictMode: false);

        Assert.Equal(1, exitCode);
        Assert.DoesNotContain("MAIN_SHOULD_NOT_RUN", stdout);
        Assert.Contains("Local mirror self-test could not start a loopback HTTP server", stderr);
        Assert.Contains("PermissionError: [Errno 1] Operation not permitted", stderr);
        Assert.Contains("does not permit binding a loopback TCP port", stderr);
        Assert.DoesNotContain("CDIDX_LOCAL_MIRROR_PORT", stderr);
        Assert.DoesNotContain("free port", stderr);
    }

    [Fact]
    public void SelfTestLocalMirror_CdidxInstallDirPointsAtHomeLocalBin_AbortsToProtectRealInstall()
    {
        if (OperatingSystem.IsWindows())
            return;

        var homeDir = Path.Combine(_tempRoot, "self_test_guard_home");
        var realInstallDir = Path.Combine(homeDir, ".local", "bin");
        var (exitCode, stdout, stderr) = RunInstallerSnippet(
            $$"""
            export HOME="{{homeDir}}"
            mkdir -p "{{realInstallDir}}"
            printf '%s\n' '#!/usr/bin/env bash' 'echo "REAL_EXISTING_BINARY"' > "{{Path.Combine(realInstallDir, "cdidx")}}"
            chmod +x "{{Path.Combine(realInstallDir, "cdidx")}}"
            printf '{"version":"9.9.9"}' > "{{Path.Combine(realInstallDir, "version.json")}}"
            printf 'real-lib' > "{{Path.Combine(realInstallDir, "libe_sqlite3.so")}}"

            detect_platform() { OS_NAME="linux"; ARCH_NAME="x64"; RID="linux-x64"; }
            wait_for_local_mirror_ready() { :; }
            python3() { sleep 30; }
            curl() { :; }
            main() { echo "MAIN_SHOULD_NOT_RUN"; }

            run_local_mirror_self_test v1.2.3
            """,
            new Dictionary<string, string?>
            {
                ["CDIDX_INSTALL_DIR"] = realInstallDir,
            },
            enforceStrictMode: false);

        Assert.NotEqual(0, exitCode);
        Assert.DoesNotContain("MAIN_SHOULD_NOT_RUN", stdout);
        Assert.Contains("points at a real install path", stderr);
        Assert.Contains("--self-test-allow-overwrite", stderr);
        Assert.Contains("Local mirror self-test aborted to protect an existing install", stderr);

        var realCdidx = File.ReadAllText(Path.Combine(realInstallDir, "cdidx"));
        Assert.Contains("REAL_EXISTING_BINARY", realCdidx);
    }

    [Fact]
    public void SelfTestLocalMirror_CdidxInstallDirContainingExistingCdidx_AbortsToProtectRealInstall()
    {
        if (OperatingSystem.IsWindows())
            return;

        var customInstallDir = Path.Combine(_tempRoot, "self_test_guard_custom");
        Directory.CreateDirectory(customInstallDir);
        var preExistingBinary = Path.Combine(customInstallDir, "cdidx");
        File.WriteAllText(preExistingBinary, "#!/usr/bin/env bash\necho 'REAL_EXISTING_BINARY'\n");
        File.SetUnixFileMode(preExistingBinary, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        var (exitCode, stdout, stderr) = RunInstallerSnippet(
            """
            detect_platform() { OS_NAME="linux"; ARCH_NAME="x64"; RID="linux-x64"; }
            wait_for_local_mirror_ready() { :; }
            python3() { sleep 30; }
            curl() { :; }
            main() { echo "MAIN_SHOULD_NOT_RUN"; }

            run_local_mirror_self_test v1.2.3
            """,
            new Dictionary<string, string?>
            {
                ["CDIDX_INSTALL_DIR"] = customInstallDir,
            },
            enforceStrictMode: false);

        Assert.NotEqual(0, exitCode);
        Assert.DoesNotContain("MAIN_SHOULD_NOT_RUN", stdout);
        Assert.Contains("points at a real install path", stderr);
        Assert.Contains("--self-test-allow-overwrite", stderr);

        var realCdidx = File.ReadAllText(preExistingBinary);
        Assert.Contains("REAL_EXISTING_BINARY", realCdidx);
    }

    [Fact]
    public void SelfTestLocalMirror_EnvSelfTestAllowOverwrite_IsNotHonoredAsSilentBypass()
    {
        // Regression: a pre-exported SELF_TEST_ALLOW_OVERWRITE=1 in the caller's
        // shell or CI must NOT silently bypass the install-dir guard. The only
        // supported escape hatch is the explicit --self-test-allow-overwrite CLI flag.
        // 呼び出し側シェルや CI に残った SELF_TEST_ALLOW_OVERWRITE=1 は
        // install-dir ガードを黙って無効化してはならない。唯一の解除手段は
        // --self-test-allow-overwrite CLI フラグ。
        if (OperatingSystem.IsWindows())
            return;

        var homeDir = Path.Combine(_tempRoot, "self_test_env_bypass_home");
        var realInstallDir = Path.Combine(homeDir, ".local", "bin");
        var (exitCode, stdout, stderr) = RunInstallerSnippet(
            $$"""
            export HOME="{{homeDir}}"
            mkdir -p "{{realInstallDir}}"
            printf '%s\n' '#!/usr/bin/env bash' 'echo "REAL_EXISTING_BINARY"' > "{{Path.Combine(realInstallDir, "cdidx")}}"
            chmod +x "{{Path.Combine(realInstallDir, "cdidx")}}"

            detect_platform() { OS_NAME="linux"; ARCH_NAME="x64"; RID="linux-x64"; }
            wait_for_local_mirror_ready() { :; }
            python3() { sleep 30; }
            curl() { :; }
            main() { echo "MAIN_SHOULD_NOT_RUN"; }

            # After sourcing install.sh the SELF_TEST_ALLOW_OVERWRITE shell var
            # must be reset to 0 regardless of any inherited env value.
            # install.sh を source した後、SELF_TEST_ALLOW_OVERWRITE は env から
            # 継承した値にかかわらず 0 にリセットされていること。
            echo "ALLOW_FLAG_AFTER_SOURCE:${SELF_TEST_ALLOW_OVERWRITE:-unset}"
            run_local_mirror_self_test v1.2.3
            """,
            new Dictionary<string, string?>
            {
                ["CDIDX_INSTALL_DIR"] = realInstallDir,
                ["SELF_TEST_ALLOW_OVERWRITE"] = "1",
            },
            enforceStrictMode: false);

        Assert.Contains("ALLOW_FLAG_AFTER_SOURCE:0", stdout);
        Assert.NotEqual(0, exitCode);
        Assert.DoesNotContain("MAIN_SHOULD_NOT_RUN", stdout);
        Assert.Contains("points at a real install path", stderr);
        Assert.Contains("--self-test-allow-overwrite", stderr);
        Assert.Contains("Local mirror self-test aborted to protect an existing install", stderr);

        var realCdidx = File.ReadAllText(Path.Combine(realInstallDir, "cdidx"));
        Assert.Contains("REAL_EXISTING_BINARY", realCdidx);
    }

    [Theory]
    [InlineData("/usr/local/bin/")]
    [InlineData("/usr/bin/")]
    [InlineData("/opt/homebrew/bin/")]
    [InlineData("/opt/local/bin/")]
    public void SelfTestLocalMirror_WellKnownSystemPathWithTrailingSlash_IsStillRiskyAndAborts(string systemPath)
    {
        // The risky-path detector must normalize trailing slashes so
        // CDIDX_INSTALL_DIR="/usr/local/bin/" is caught just like "/usr/local/bin".
        // 末尾スラッシュがあっても既知のシステム install 先として検出されること。
        if (OperatingSystem.IsWindows())
            return;

        var (exitCode, stdout, stderr) = RunInstallerSnippet(
            $$"""
            export BINARY_NAME="cdidx_does_not_exist_here"
            is_self_test_install_dir_risky "{{systemPath}}"
            echo "RISKY_EXIT:$?"
            """,
            null,
            enforceStrictMode: false);

        Assert.Equal(0, exitCode);
        Assert.Contains("RISKY_EXIT:0", stdout);
        _ = stderr;
    }

    [Fact]
    public void SelfTestLocalMirror_HomeLocalBinWithTrailingSlash_IsStillRiskyAndAborts()
    {
        // A CDIDX_INSTALL_DIR of "$HOME/.local/bin/" (with trailing slash, no
        // existing cdidx yet) must still abort — previously it slipped past the
        // exact-string comparison and only got caught after a real binary existed.
        // 末尾スラッシュ付きの "$HOME/.local/bin/" も、実バイナリがまだ無い段階で
        // ガードに引っかかること。
        if (OperatingSystem.IsWindows())
            return;

        var homeDir = Path.Combine(_tempRoot, "self_test_trailing_slash_home");
        var realInstallDir = Path.Combine(homeDir, ".local", "bin");
        Directory.CreateDirectory(realInstallDir);
        var installDirWithSlash = realInstallDir.EndsWith("/") ? realInstallDir : realInstallDir + "/";

        var (exitCode, stdout, stderr) = RunInstallerSnippet(
            $$"""
            export HOME="{{homeDir}}"
            detect_platform() { OS_NAME="linux"; ARCH_NAME="x64"; RID="linux-x64"; }
            wait_for_local_mirror_ready() { :; }
            python3() { sleep 30; }
            curl() { :; }
            main() { echo "MAIN_SHOULD_NOT_RUN"; }

            run_local_mirror_self_test v1.2.3
            """,
            new Dictionary<string, string?>
            {
                ["CDIDX_INSTALL_DIR"] = installDirWithSlash,
            },
            enforceStrictMode: false);

        Assert.NotEqual(0, exitCode);
        Assert.DoesNotContain("MAIN_SHOULD_NOT_RUN", stdout);
        Assert.Contains("points at a real install path", stderr);
        Assert.Contains("--self-test-allow-overwrite", stderr);
    }

    [Fact]
    public void SelfTestLocalMirror_AllowOverwriteCliFlag_ProceedsDespiteRiskyInstallDir()
    {
        if (OperatingSystem.IsWindows())
            return;

        var homeDir = Path.Combine(_tempRoot, "self_test_allow_cli_home");
        var realInstallDir = Path.Combine(homeDir, ".local", "bin");
        var (exitCode, stdout, stderr) = RunInstallerSnippet(
            $$"""
            export HOME="{{homeDir}}"
            mkdir -p "{{realInstallDir}}"
            printf '%s\n' '#!/usr/bin/env bash' 'echo "REAL_EXISTING_BINARY"' > "{{Path.Combine(realInstallDir, "cdidx")}}"
            chmod +x "{{Path.Combine(realInstallDir, "cdidx")}}"

            detect_platform() { OS_NAME="linux"; ARCH_NAME="x64"; RID="linux-x64"; }
            wait_for_local_mirror_ready() { :; }
            python3() { sleep 30; }
            curl() { :; }
            main() {
                echo "MAIN_INSTALL_DIR:$INSTALL_DIR"
                echo "ALLOW_FLAG_VALUE:${SELF_TEST_ALLOW_OVERWRITE:-0}"
                mkdir -p "$INSTALL_DIR"
                printf '%s\n' '#!/usr/bin/env bash' 'echo "cdidx v1.2.3"' > "$INSTALL_DIR/cdidx"
                chmod +x "$INSTALL_DIR/cdidx"
                printf '{"version":"1.2.3"}' > "$INSTALL_DIR/version.json"
                printf 'self-test-lib' > "$INSTALL_DIR/libe_sqlite3.so"
            }

            # Simulate CLI dispatch of `install.sh --self-test-local-mirror --self-test-allow-overwrite v1.2.3`
            set -- --self-test-allow-overwrite v1.2.3
            while [ $# -gt 0 ]; do
                case "$1" in
                    --self-test-allow-overwrite)
                        SELF_TEST_ALLOW_OVERWRITE=1
                        shift
                        ;;
                    --*)
                        echo "UNKNOWN_OPTION:$1" >&2
                        exit 2
                        ;;
                    *)
                        break
                        ;;
                esac
            done
            run_local_mirror_self_test "${1:-}"
            echo "CLI_OVERWRITE_ALLOWED"
            """,
            new Dictionary<string, string?>
            {
                ["CDIDX_INSTALL_DIR"] = realInstallDir,
            },
            enforceStrictMode: false);

        Assert.Equal(0, exitCode);
        Assert.Contains("CLI_OVERWRITE_ALLOWED", stdout);
        Assert.Contains("ALLOW_FLAG_VALUE:1", stdout);
        Assert.DoesNotContain("Local mirror self-test aborted", stderr);
    }

    [Fact]
    public void SelfTestLocalMirror_CdidxInstallDirPointsAtFreshCustomDir_ProceedsWithoutGuard()
    {
        if (OperatingSystem.IsWindows())
            return;

        var safeInstallDir = Path.Combine(_tempRoot, "self_test_safe_custom_install");
        var (exitCode, stdout, stderr) = RunInstallerSnippet(
            """
            detect_platform() { OS_NAME="linux"; ARCH_NAME="x64"; RID="linux-x64"; }
            wait_for_local_mirror_ready() { :; }
            python3() { sleep 30; }
            curl() { :; }
            main() {
                echo "MAIN_INSTALL_DIR:$INSTALL_DIR"
                mkdir -p "$INSTALL_DIR"
                printf '%s\n' '#!/usr/bin/env bash' 'echo "cdidx v1.2.3"' > "$INSTALL_DIR/cdidx"
                chmod +x "$INSTALL_DIR/cdidx"
                printf '{"version":"1.2.3"}' > "$INSTALL_DIR/version.json"
                printf 'self-test-lib' > "$INSTALL_DIR/libe_sqlite3.so"
            }

            run_local_mirror_self_test v1.2.3
            echo "SAFE_CUSTOM_DONE"
            """,
            new Dictionary<string, string?>
            {
                ["CDIDX_INSTALL_DIR"] = safeInstallDir,
            },
            enforceStrictMode: false);

        Assert.Equal(0, exitCode);
        Assert.Contains("SAFE_CUSTOM_DONE", stdout);
        Assert.Contains($"MAIN_INSTALL_DIR:{safeInstallDir}", stdout);
        Assert.DoesNotContain("Local mirror self-test aborted", stderr);
    }

    [Fact]
    public void DownloadAndInstall_UsesConfiguredReleaseBaseUrl()
    {
        if (OperatingSystem.IsWindows())
            return;

        var installDir = Path.Combine(_tempRoot, "configured_release_base_install");
        var payloadDir = Path.Combine(_tempRoot, "configured_release_base_payload");
        var archivePath = Path.Combine(_tempRoot, "configured_release_base.tar.gz");
        var checksumsPath = Path.Combine(_tempRoot, "configured_release_base.sha256sums.txt");
        var urlLogPath = Path.Combine(_tempRoot, "configured_release_base_urls.log");

        var (exitCode, stdout, stderr) = RunInstallerSnippet(
            $$"""
            mkdir -p "{{payloadDir}}"
            printf '%s\n' '#!/usr/bin/env bash' 'echo "cdidx v1.2.3"' > "{{Path.Combine(payloadDir, "cdidx")}}"
            chmod +x "{{Path.Combine(payloadDir, "cdidx")}}"
            printf '{"version":"1.2.3"}' > "{{Path.Combine(payloadDir, "version.json")}}"
            printf 'new-lib' > "{{Path.Combine(payloadDir, "libe_sqlite3.so")}}"
            tar czf "{{archivePath}}" -C "{{payloadDir}}" .

            if command -v sha256sum > /dev/null 2>&1; then
                checksum="$(sha256sum "{{archivePath}}" | awk '{print $1}')"
            elif command -v shasum > /dev/null 2>&1; then
                checksum="$(shasum -a 256 "{{archivePath}}" | awk '{print $1}')"
            else
                checksum="$(openssl dgst -sha256 "{{archivePath}}" | awk '{print $NF}')"
            fi
            printf '%s  CodeIndex-linux-x64.tar.gz\n' "$checksum" > "{{checksumsPath}}"

            VERSION="v1.2.3"
            OS_NAME="linux"
            ARCH_NAME="x64"
            RID="linux-x64"

            curl() {
                local output_path=""
                local url=""
                while [ $# -gt 0 ]; do
                    case "$1" in
                        -o)
                            output_path="$2"
                            shift 2
                            ;;
                        -w)
                            shift 2
                            ;;
                        *)
                            url="$1"
                            shift
                            ;;
                    esac
                done

                printf '%s\n' "$url" >> "{{urlLogPath}}"
                case "$url" in
                    */sha256sums.txt) command cp "{{checksumsPath}}" "$output_path" ;;
                    *) command cp "{{archivePath}}" "$output_path" ;;
                esac

                printf '200'
                return 0
            }

            download_and_install
            cat "{{urlLogPath}}"
            """,
            new Dictionary<string, string?>
            {
                ["CDIDX_INSTALL_DIR"] = installDir,
                ["CDIDX_GITHUB_BASE_URL"] = "https://mirror.example/releases",
            });

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, stderr);
        Assert.Contains("https://mirror.example/releases/Widthdom/CodeIndex/releases/download/v1.2.3/CodeIndex-linux-x64.tar.gz", stdout);
        Assert.Contains("https://mirror.example/releases/Widthdom/CodeIndex/releases/download/v1.2.3/sha256sums.txt", stdout);
    }

    [Fact]
    public void ReinstallReal_WithoutVersionArgument_FailsWithUsageError()
    {
        if (OperatingSystem.IsWindows())
            return;

        var (exitCode, _, stderr) = RunInstallerSnippet(
            """
            run_reinstall_real
            """,
            enforceStrictMode: false);

        Assert.NotEqual(0, exitCode);
        Assert.Contains("--reinstall-real requires a version argument", stderr);
    }

    [Fact]
    public void ReinstallReal_PrefixesBareVersionWithV()
    {
        if (OperatingSystem.IsWindows())
            return;

        var (exitCode, stdout, _) = RunInstallerSnippet(
            """
            need_cmd() { :; }
            detect_platform() { OS_NAME="linux"; ARCH_NAME="x64"; RID="linux-x64"; }
            main() {
                echo "MAIN_VERSION_ARG:$1"
                mkdir -p "$INSTALL_DIR"
                cat > "$INSTALL_DIR/cdidx" <<'SH'
            #!/usr/bin/env bash
            case "$1" in
                --version) echo "cdidx v1.2.3" ;;
                search)
                    # Real cdidx search emits range-form with the first
                    # snippet line equal to `  def greet(name):` — the
                    # tightened awk requires this exact shape.
                    # 実 cdidx 準拠の範囲形 + 直後 1 行目完全一致の snippet。
                    printf '%s\n%s\n' "sample.py:1-6" "  def greet(name):"
                    ;;
                *)
                    if [ "$2" = "--db" ] && [ -n "$3" ]; then
                        mkdir -p "$(dirname "$3")"
                        printf 'mock-db' > "$3"
                    fi
                    ;;
            esac
            SH
                chmod +x "$INSTALL_DIR/cdidx"
            }

            run_reinstall_real 1.2.3
            """,
            enforceStrictMode: false);

        Assert.Equal(0, exitCode);
        Assert.Contains("MAIN_VERSION_ARG:v1.2.3", stdout);
    }

    [Fact]
    public void ReinstallReal_IgnoresCdidxInstallDirAndUsesIsolatedTempDir()
    {
        if (OperatingSystem.IsWindows())
            return;

        var homeDir = Path.Combine(_tempRoot, "reinstall_real_home");
        var realInstallDir = Path.Combine(homeDir, ".local", "bin");
        Directory.CreateDirectory(realInstallDir);
        var realBinaryPath = Path.Combine(realInstallDir, "cdidx");
        File.WriteAllText(realBinaryPath, "#!/usr/bin/env bash\necho REAL_EXISTING_BINARY\n");
        File.SetUnixFileMode(realBinaryPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        var (exitCode, stdout, _) = RunInstallerSnippet(
            $$"""
            export HOME="{{homeDir}}"
            need_cmd() { :; }
            detect_platform() { OS_NAME="linux"; ARCH_NAME="x64"; RID="linux-x64"; }
            main() {
                echo "MAIN_INSTALL_DIR:$INSTALL_DIR"
                mkdir -p "$INSTALL_DIR"
                cat > "$INSTALL_DIR/cdidx" <<'SH'
            #!/usr/bin/env bash
            case "$1" in
                --version) echo "cdidx v1.2.3" ;;
                search)
                    # Real cdidx search emits range-form with the first
                    # snippet line equal to `  def greet(name):` — the
                    # tightened awk requires this exact shape.
                    # 実 cdidx 準拠の範囲形 + 直後 1 行目完全一致の snippet。
                    printf '%s\n%s\n' "sample.py:1-6" "  def greet(name):"
                    ;;
                *)
                    if [ "$2" = "--db" ] && [ -n "$3" ]; then
                        mkdir -p "$(dirname "$3")"
                        printf 'mock-db' > "$3"
                    fi
                    ;;
            esac
            SH
                chmod +x "$INSTALL_DIR/cdidx"
            }

            run_reinstall_real v1.2.3
            grep -q 'REAL_EXISTING_BINARY' "{{realBinaryPath}}" && echo "REAL_INSTALL_PRESERVED"
            [ "$INSTALL_DIR" = "{{realInstallDir}}" ] && echo "USED_REAL_INSTALL_DIR"
            case "$INSTALL_DIR" in
                /tmp/cdidx-reinstall-real.*) echo "USED_ISOLATED_TMPDIR" ;;
                *) echo "DID_NOT_USE_ISOLATED_TMPDIR:$INSTALL_DIR" ;;
            esac
            """,
            new Dictionary<string, string?>
            {
                ["CDIDX_INSTALL_DIR"] = realInstallDir,
            },
            enforceStrictMode: false);

        Assert.Equal(0, exitCode);
        Assert.Contains("REAL_INSTALL_PRESERVED", stdout);
        Assert.Contains("USED_ISOLATED_TMPDIR", stdout);
        Assert.DoesNotContain("USED_REAL_INSTALL_DIR", stdout);
        Assert.DoesNotContain($"MAIN_INSTALL_DIR:{realInstallDir}", stdout);
    }

    [Fact]
    public void ReinstallReal_ExercisesVersionIndexAndSearch()
    {
        if (OperatingSystem.IsWindows())
            return;

        var versionSentinel = Path.Combine(_tempRoot, "reinstall_real_version_invoked");
        var searchSentinel = Path.Combine(_tempRoot, "reinstall_real_search_invoked");

        var (exitCode, stdout, _) = RunInstallerSnippet(
            $$"""
            need_cmd() { :; }
            detect_platform() { OS_NAME="linux"; ARCH_NAME="x64"; RID="linux-x64"; }
            main() {
                mkdir -p "$INSTALL_DIR"
                cat > "$INSTALL_DIR/cdidx" <<SH
            #!/usr/bin/env bash
            case "\$1" in
                --version)
                    # run_reinstall_real requires the entire --version output
                    # to be EXACTLY `cdidx v<ver>` on one non-empty line with
                    # no trailing diagnostic text and no additional lines.
                    # A sentinel file is used to confirm invocation instead
                    # of echoing an extra marker line, because the multi-line
                    # check would otherwise reject this stub.
                    # 先頭行形式チェックは出力全体が `cdidx v<ver>` の 1 行だけ
                    # であることを要求するため、呼び出し確認にはマーカー行で
                    # はなくセンチネルファイルを使う。
                    printf 'invoked' > "{{versionSentinel}}"
                    echo "cdidx v1.2.3"
                    ;;
                search)
                    # Real cdidx search output is the strict range form
                    # `sample.py:<start>-<end>` with the first snippet line
                    # indented by exactly two spaces and equal to the real
                    # Python source line. The tightened awk state machine
                    # requires that first indented line to be exactly
                    # `  def greet(name):`, so the stub must mirror that
                    # exact shape rather than emit a grep-like single line.
                    # 実 cdidx search 出力は範囲形 `sample.py:<start>-<end>` と
                    # 直後の 2 スペースインデント行が `  def greet(name):` に
                    # 完全一致する形。新しい awk 状態機械は「ヘッダ直後 1 行目が
                    # exactly `  def greet(name):`」を要求するため stub も
                    # 同形で返す。
                    printf 'invoked' > "{{searchSentinel}}"
                    printf '%s\n%s\n' "sample.py:1-6" "  def greet(name):"
                    ;;
                *)
                    echo "INVOKED_INDEX"
                    if [ "\$2" = "--db" ] && [ -n "\$3" ]; then
                        mkdir -p "\$(dirname "\$3")"
                        printf 'mock-db' > "\$3"
                    fi
                    ;;
            esac
            SH
                chmod +x "$INSTALL_DIR/cdidx"
            }

            run_reinstall_real v1.2.3
            """,
            enforceStrictMode: false);

        Assert.Equal(0, exitCode);
        Assert.True(File.Exists(versionSentinel), "cdidx --version stub was not invoked by run_reinstall_real / run_reinstall_real が cdidx --version を呼んでいない");
        Assert.Contains("INVOKED_INDEX", stdout);
        Assert.True(File.Exists(searchSentinel), "cdidx search stub was not invoked by run_reinstall_real / run_reinstall_real が cdidx search を呼んでいない");
        Assert.Contains("Real reinstall validation passed", stdout);
    }

    [Fact]
    public void ReinstallReal_BinaryMissingAfterInstall_FailsLoudly()
    {
        if (OperatingSystem.IsWindows())
            return;

        var (exitCode, _, stderr) = RunInstallerSnippet(
            """
            need_cmd() { :; }
            detect_platform() { OS_NAME="linux"; ARCH_NAME="x64"; RID="linux-x64"; }
            main() {
                # Intentionally do not create the binary to simulate a broken install.
                # バイナリを作らないことで壊れたインストールを模す。
                :
            }

            run_reinstall_real v1.2.3
            """,
            enforceStrictMode: false);

        Assert.NotEqual(0, exitCode);
        Assert.Contains("installed binary not found", stderr);
    }

    [Fact]
    public void ReinstallReal_SearchFails_AbortsWithError()
    {
        if (OperatingSystem.IsWindows())
            return;

        var (exitCode, _, stderr) = RunInstallerSnippet(
            """
            need_cmd() { :; }
            detect_platform() { OS_NAME="linux"; ARCH_NAME="x64"; RID="linux-x64"; }
            main() {
                mkdir -p "$INSTALL_DIR"
                cat > "$INSTALL_DIR/cdidx" <<'SH'
            #!/usr/bin/env bash
            case "$1" in
                --version) echo "cdidx v1.2.3" ;;
                search)    exit 3 ;;
                *)
                    if [ "$2" = "--db" ] && [ -n "$3" ]; then
                        mkdir -p "$(dirname "$3")"
                        printf 'mock-db' > "$3"
                    fi
                    ;;
            esac
            SH
                chmod +x "$INSTALL_DIR/cdidx"
            }

            run_reinstall_real v1.2.3
            """,
            enforceStrictMode: false);

        Assert.NotEqual(0, exitCode);
        Assert.Contains("search returned a non-zero exit code", stderr);
    }

    [Fact]
    public void ReinstallReal_VersionMismatch_AbortsWithError()
    {
        if (OperatingSystem.IsWindows())
            return;

        var (exitCode, _, stderr) = RunInstallerSnippet(
            """
            need_cmd() { :; }
            detect_platform() { OS_NAME="linux"; ARCH_NAME="x64"; RID="linux-x64"; }
            main() {
                mkdir -p "$INSTALL_DIR"
                cat > "$INSTALL_DIR/cdidx" <<'SH'
            #!/usr/bin/env bash
            case "$1" in
                --version) echo "cdidx v9.9.9" ;;
                *)        : ;;
            esac
            SH
                chmod +x "$INSTALL_DIR/cdidx"
            }

            run_reinstall_real v1.2.3
            """,
            enforceStrictMode: false);

        Assert.NotEqual(0, exitCode);
        Assert.Contains("expected exactly one version token v1.2.3", stderr);
        Assert.Contains("9.9.9", stderr);
    }

    [Fact]
    public void ReinstallReal_VersionWithoutLeftBoundary_AbortsWithError()
    {
        if (OperatingSystem.IsWindows())
            return;

        var (exitCode, _, stderr) = RunInstallerSnippet(
            """
            need_cmd() { :; }
            detect_platform() { OS_NAME="linux"; ARCH_NAME="x64"; RID="linux-x64"; }
            main() {
                mkdir -p "$INSTALL_DIR"
                cat > "$INSTALL_DIR/cdidx" <<'SH'
            #!/usr/bin/env bash
            case "$1" in
                --version) echo "prefixv1.2.3" ;;
                *)        : ;;
            esac
            SH
                chmod +x "$INSTALL_DIR/cdidx"
            }

            run_reinstall_real v1.2.3
            """,
            enforceStrictMode: false);

        Assert.NotEqual(0, exitCode);
        Assert.Contains("expected exactly one version token v1.2.3", stderr);
    }

    [Fact]
    public void ReinstallReal_VersionWithTrailingDigit_AbortsWithError()
    {
        if (OperatingSystem.IsWindows())
            return;

        var (exitCode, _, stderr) = RunInstallerSnippet(
            """
            need_cmd() { :; }
            detect_platform() { OS_NAME="linux"; ARCH_NAME="x64"; RID="linux-x64"; }
            main() {
                mkdir -p "$INSTALL_DIR"
                cat > "$INSTALL_DIR/cdidx" <<'SH'
            #!/usr/bin/env bash
            case "$1" in
                --version) echo "cdidx v1.2.30" ;;
                *)        : ;;
            esac
            SH
                chmod +x "$INSTALL_DIR/cdidx"
            }

            run_reinstall_real v1.2.3
            """,
            enforceStrictMode: false);

        Assert.NotEqual(0, exitCode);
        Assert.Contains("expected exactly one version token v1.2.3", stderr);
    }

    [Fact]
    public void ReinstallReal_SearchOutputWithoutStructuredMatch_AbortsWithError()
    {
        if (OperatingSystem.IsWindows())
            return;

        var (exitCode, _, stderr) = RunInstallerSnippet(
            """
            need_cmd() { :; }
            detect_platform() { OS_NAME="linux"; ARCH_NAME="x64"; RID="linux-x64"; }
            main() {
                mkdir -p "$INSTALL_DIR"
                cat > "$INSTALL_DIR/cdidx" <<'SH'
            #!/usr/bin/env bash
            case "$1" in
                --version) echo "cdidx v1.2.3" ;;
                search)
                    # Successful exit but no structured match — e.g. diagnostic text
                    # that happens to mention "greet". Must NOT false-pass.
                    # 構造化されていない "greet" を含む診断文は false pass させてはならない。
                    echo "searching for greet returned 0 matches"
                    ;;
                *)
                    if [ "$2" = "--db" ] && [ -n "$3" ]; then
                        mkdir -p "$(dirname "$3")"
                        printf 'mock-db' > "$3"
                    fi
                    ;;
            esac
            SH
                chmod +x "$INSTALL_DIR/cdidx"
            }

            run_reinstall_real v1.2.3
            """,
            enforceStrictMode: false);

        Assert.NotEqual(0, exitCode);
        Assert.Contains("did not return a structured match", stderr);
    }

    [Fact]
    public void ReinstallReal_VersionMixedOutput_AbortsWithError()
    {
        if (OperatingSystem.IsWindows())
            return;

        var (exitCode, _, stderr) = RunInstallerSnippet(
            """
            need_cmd() { :; }
            detect_platform() { OS_NAME="linux"; ARCH_NAME="x64"; RID="linux-x64"; }
            main() {
                mkdir -p "$INSTALL_DIR"
                cat > "$INSTALL_DIR/cdidx" <<'SH'
            #!/usr/bin/env bash
            case "$1" in
                --version)
                    # Mixed output: requested tag appears in a diagnostic line
                    # while a different version is actually running. Must abort.
                    # 要求タグが診断文に混ざりつつ別 version が走る出力は false pass させない。
                    echo "warning: requested version v1.2.3 not installed; running v9.9.9"
                    ;;
                search) echo "sample.py:1: def greet(name):" ;;
                *)
                    if [ "$2" = "--db" ] && [ -n "$3" ]; then
                        mkdir -p "$(dirname "$3")"
                        printf 'mock-db' > "$3"
                    fi
                    ;;
            esac
            SH
                chmod +x "$INSTALL_DIR/cdidx"
            }

            run_reinstall_real v1.2.3
            """,
            enforceStrictMode: false);

        Assert.NotEqual(0, exitCode);
        Assert.Contains("expected exactly one version token", stderr);
    }

    [Fact]
    public void ReinstallReal_SearchPathPrefix_AbortsWithError()
    {
        if (OperatingSystem.IsWindows())
            return;

        var (exitCode, _, stderr) = RunInstallerSnippet(
            """
            need_cmd() { :; }
            detect_platform() { OS_NAME="linux"; ARCH_NAME="x64"; RID="linux-x64"; }
            main() {
                mkdir -p "$INSTALL_DIR"
                cat > "$INSTALL_DIR/cdidx" <<'SH'
            #!/usr/bin/env bash
            case "$1" in
                --version) echo "cdidx v1.2.3" ;;
                search)
                    # Path-prefix false positive: "other/sample.py:1" contains
                    # "sample.py:1" as a substring. The second line mentions
                    # "greet" in a diagnostic, so a split-condition check
                    # would false-pass. Must abort because the structured
                    # match is not anchored at the scratch project's own
                    # sample.py path.
                    # `other/sample.py:1` が `sample.py:1` を部分一致で含み、
                    # かつ 'greet' を含む診断文が同居するケース。行頭アンカーで
                    # 除外する必要がある。
                    printf '%s\n%s\n' "other/sample.py:1" "searching for greet returned 0 matches"
                    ;;
                *)
                    if [ "$2" = "--db" ] && [ -n "$3" ]; then
                        mkdir -p "$(dirname "$3")"
                        printf 'mock-db' > "$3"
                    fi
                    ;;
            esac
            SH
                chmod +x "$INSTALL_DIR/cdidx"
            }

            run_reinstall_real v1.2.3
            """,
            enforceStrictMode: false);

        Assert.NotEqual(0, exitCode);
        Assert.Contains("did not return a structured match", stderr);
    }

    [Fact]
    public void ReinstallReal_VersionDiagnosticOnlyRequestedToken_AbortsWithError()
    {
        if (OperatingSystem.IsWindows())
            return;

        var (exitCode, _, stderr) = RunInstallerSnippet(
            """
            need_cmd() { :; }
            detect_platform() { OS_NAME="linux"; ARCH_NAME="x64"; RID="linux-x64"; }
            main() {
                mkdir -p "$INSTALL_DIR"
                cat > "$INSTALL_DIR/cdidx" <<'SH'
            #!/usr/bin/env bash
            case "$1" in
                --version)
                    # Diagnostic-only output whose sole v<semver> token equals
                    # the requested one but does NOT represent the binary's
                    # actual reported version. First-line form check must reject.
                    # v<semver> token は要求版と一致するものの、実バイナリの
                    # バージョン行ではなく診断文のみというケース。先頭行形式
                    # チェックで弾く必要がある。
                    echo "see /releases/v1.2.3/notes for details"
                    ;;
                search) echo "sample.py:1: def greet(name):" ;;
                *)
                    if [ "$2" = "--db" ] && [ -n "$3" ]; then
                        mkdir -p "$(dirname "$3")"
                        printf 'mock-db' > "$3"
                    fi
                    ;;
            esac
            SH
                chmod +x "$INSTALL_DIR/cdidx"
            }

            run_reinstall_real v1.2.3
            """,
            enforceStrictMode: false);

        Assert.NotEqual(0, exitCode);
        Assert.Contains("first non-empty line of cdidx --version must be exactly", stderr);
    }

    [Fact]
    public void ReinstallReal_SearchHeaderPlusDiagnosticGreet_AbortsWithError()
    {
        if (OperatingSystem.IsWindows())
            return;

        var (exitCode, _, stderr) = RunInstallerSnippet(
            """
            need_cmd() { :; }
            detect_platform() { OS_NAME="linux"; ARCH_NAME="x64"; RID="linux-x64"; }
            main() {
                mkdir -p "$INSTALL_DIR"
                cat > "$INSTALL_DIR/cdidx" <<'SH'
            #!/usr/bin/env bash
            case "$1" in
                --version) echo "cdidx v1.2.3" ;;
                search)
                    # Split-evidence false positive: structured range-form
                    # header on one line, but no indented snippet line under
                    # it — the `greet` token appears only on a separate
                    # non-indented diagnostic line. The awk state machine
                    # requires the verbatim source signature `def greet(name):`
                    # on a 2-space-indented line belonging to the header's
                    # match block, so this non-indented diagnostic is rejected.
                    # ヘッダは正しい range 形だが、`greet` は非インデント診断行に
                    # しか存在しない split-evidence ケース。awk 状態機械は
                    # match block 内のインデント行に verbatim な
                    # `def greet(name):` を要求するため弾かれる。
                    printf '%s\n%s\n' "sample.py:1-6" "warning: greet query returned 0 matches"
                    ;;
                *)
                    if [ "$2" = "--db" ] && [ -n "$3" ]; then
                        mkdir -p "$(dirname "$3")"
                        printf 'mock-db' > "$3"
                    fi
                    ;;
            esac
            SH
                chmod +x "$INSTALL_DIR/cdidx"
            }

            run_reinstall_real v1.2.3
            """,
            enforceStrictMode: false);

        Assert.NotEqual(0, exitCode);
        Assert.Contains("did not return a structured match", stderr);
    }

    [Fact]
    public void ReinstallReal_VersionFirstLineTrailingDiagnostic_AbortsWithError()
    {
        if (OperatingSystem.IsWindows())
            return;

        var (exitCode, _, stderr) = RunInstallerSnippet(
            """
            need_cmd() { :; }
            detect_platform() { OS_NAME="linux"; ARCH_NAME="x64"; RID="linux-x64"; }
            main() {
                mkdir -p "$INSTALL_DIR"
                cat > "$INSTALL_DIR/cdidx" <<'SH'
            #!/usr/bin/env bash
            case "$1" in
                --version)
                    # Trailing-diagnostic shape: first non-empty line starts with
                    # `cdidx v1.2.3` (so token enumeration sees one distinct token
                    # equal to the requested tag) but adds bare diagnostic text
                    # after the version. Real cdidx --version is either
                    # `cdidx v<ver>` alone or `cdidx v<ver> (<build metadata>)`
                    # (#1550), so trailing text without parens must be rejected.
                    # 先頭行が `cdidx v1.2.3` で始まるが末尾に括弧無し診断文が続く
                    # ケース。実バイナリの --version 出力は `cdidx v<ver>` 単独か
                    # `cdidx v<ver> (<build metadata>)` の形のみ許容するため
                    # (#1550)、括弧で囲われない末尾文字列は弾く。
                    echo "cdidx v1.2.3 warning: expected package missing"
                    ;;
                search) echo "sample.py:1: def greet(name):" ;;
                *)
                    if [ "$2" = "--db" ] && [ -n "$3" ]; then
                        mkdir -p "$(dirname "$3")"
                        printf 'mock-db' > "$3"
                    fi
                    ;;
            esac
            SH
                chmod +x "$INSTALL_DIR/cdidx"
            }

            run_reinstall_real v1.2.3
            """,
            enforceStrictMode: false);

        Assert.NotEqual(0, exitCode);
        Assert.Contains("first non-empty line of cdidx --version must be exactly", stderr);
    }

    [Fact]
    public void ReinstallReal_VersionFirstLineWithBuildMetadata_AcceptsAndContinues()
    {
        if (OperatingSystem.IsWindows())
            return;

        // Real cdidx --version output may include the build-metadata suffix
        // since #1550 (e.g. `cdidx v1.2.3 (commit abc1234, built 2026-05-12,
        // clean)`). The reinstall validator must accept that shape so dev
        // builds and tagged releases stay distinguishable in bug reports.
        // The mock binary still indexes a scratch project (returning a tiny
        // search result), so the reinstall validation should succeed.
        // #1550 で導入された build-metadata 付き --version 出力を validator が
        // 受け入れることを検証。括弧で囲ったメタデータは許容する。
        var (exitCode, _, stderr) = RunInstallerSnippet(
            """
            need_cmd() { :; }
            detect_platform() { OS_NAME="linux"; ARCH_NAME="x64"; RID="linux-x64"; }
            main() {
                mkdir -p "$INSTALL_DIR"
                cat > "$INSTALL_DIR/cdidx" <<'SH'
            #!/usr/bin/env bash
            case "$1" in
                --version)
                    echo "cdidx v1.2.3 (commit abc1234, built 2026-05-12, clean)"
                    ;;
                search)
                    echo "sample.py:1:def greet(name):"
                    ;;
                *)
                    if [ "$2" = "--db" ] && [ -n "$3" ]; then
                        mkdir -p "$(dirname "$3")"
                        printf 'mock-db' > "$3"
                    fi
                    ;;
            esac
            SH
                chmod +x "$INSTALL_DIR/cdidx"
            }

            run_reinstall_real v1.2.3
            """,
            enforceStrictMode: false);

        Assert.Equal(0, exitCode);
        Assert.DoesNotContain("first non-empty line of cdidx --version must be exactly", stderr);
    }

    [Fact]
    public void ReinstallReal_SearchHeaderInlineDiagnostic_AbortsWithError()
    {
        if (OperatingSystem.IsWindows())
            return;

        var (exitCode, _, stderr) = RunInstallerSnippet(
            """
            need_cmd() { :; }
            detect_platform() { OS_NAME="linux"; ARCH_NAME="x64"; RID="linux-x64"; }
            main() {
                mkdir -p "$INSTALL_DIR"
                cat > "$INSTALL_DIR/cdidx" <<'SH'
            #!/usr/bin/env bash
            case "$1" in
                --version) echo "cdidx v1.2.3" ;;
                search)
                    # Header-inline diagnostic: the structured path prefix and
                    # a greet-mentioning diagnostic share a single line. The
                    # tightened awk requires either a strict grep form whose
                    # entire line is exactly `sample.py:<N>:def greet(name):`
                    # (no trailing diagnostic text, no space between the colon
                    # and the signature) or a strict range-form header with
                    # nothing trailing (`^sample\.py:[0-9]+-[0-9]+$`) followed
                    # by an exact `  def greet(name):` first-indent snippet.
                    # This adversarial line matches neither, so it must abort.
                    # ヘッダ行自体に診断文 `greet` が続くケース。行全体を
                    # `sample.py:<N>:def greet(name):` に完全一致させる grep 形
                    # でも、末尾アンカー付き range 形でもないため弾かれる必要がある。
                    printf '%s\n' "sample.py:1-6 warning: greet query returned 0 matches"
                    ;;
                *)
                    if [ "$2" = "--db" ] && [ -n "$3" ]; then
                        mkdir -p "$(dirname "$3")"
                        printf 'mock-db' > "$3"
                    fi
                    ;;
            esac
            SH
                chmod +x "$INSTALL_DIR/cdidx"
            }

            run_reinstall_real v1.2.3
            """,
            enforceStrictMode: false);

        Assert.NotEqual(0, exitCode);
        Assert.Contains("did not return a structured match", stderr);
    }

    [Fact]
    public void ReinstallReal_SearchIndentedDiagnosticWithoutDef_AbortsWithError()
    {
        if (OperatingSystem.IsWindows())
            return;

        var (exitCode, _, stderr) = RunInstallerSnippet(
            """
            need_cmd() { :; }
            detect_platform() { OS_NAME="linux"; ARCH_NAME="x64"; RID="linux-x64"; }
            main() {
                mkdir -p "$INSTALL_DIR"
                cat > "$INSTALL_DIR/cdidx" <<'SH'
            #!/usr/bin/env bash
            case "$1" in
                --version) echo "cdidx v1.2.3" ;;
                search)
                    # Indented-diagnostic shape: strict range-form header
                    # followed by a two-space-indented line that mentions
                    # greet only as part of a diagnostic, not as real Python
                    # source. The tightened awk requires the first indented
                    # line under a range header to be exactly
                    # `  def greet(name):`, so this false-passes neither the
                    # substring check nor the adjacency check.
                    # ヘッダは正しい range 形だが、直後の 2 スペースインデント行が
                    # 診断文で `  def greet(name):` と完全一致しないケース。
                    # ヘッダ直後 1 行目の完全一致を要求して弾く。
                    printf '%s\n%s\n' "sample.py:1-6" "  warning: greet query returned 0 matches"
                    ;;
                *)
                    if [ "$2" = "--db" ] && [ -n "$3" ]; then
                        mkdir -p "$(dirname "$3")"
                        printf 'mock-db' > "$3"
                    fi
                    ;;
            esac
            SH
                chmod +x "$INSTALL_DIR/cdidx"
            }

            run_reinstall_real v1.2.3
            """,
            enforceStrictMode: false);

        Assert.NotEqual(0, exitCode);
        Assert.Contains("did not return a structured match", stderr);
    }

    [Fact]
    public void ReinstallReal_VersionMultiLineExactFirstLinePlusDiagnostic_AbortsWithError()
    {
        if (OperatingSystem.IsWindows())
            return;

        var (exitCode, _, stderr) = RunInstallerSnippet(
            """
            need_cmd() { :; }
            detect_platform() { OS_NAME="linux"; ARCH_NAME="x64"; RID="linux-x64"; }
            main() {
                mkdir -p "$INSTALL_DIR"
                cat > "$INSTALL_DIR/cdidx" <<'SH'
            #!/usr/bin/env bash
            case "$1" in
                --version)
                    # Multi-line shape where the FIRST non-empty line is
                    # exactly `cdidx v1.2.3` (so the exact-first-line check
                    # passes and token enumeration sees one distinct token
                    # equal to the requested tag), but a second non-empty
                    # line carries a diagnostic. Real cdidx --version output
                    # is literally `cdidx v<ver>` on ONE non-empty line with
                    # no additional lines, so the non-empty-line-count guard
                    # must reject this multi-line shape.
                    # 先頭行が完全一致するが後続行に診断文が続く multi-line
                    # 形。非空行数チェックで弾く必要がある。
                    printf '%s\n%s\n' "cdidx v1.2.3" "warning: expected package v1.2.3 missing"
                    ;;
                search) echo "sample.py:1: def greet(name):" ;;
                *)
                    if [ "$2" = "--db" ] && [ -n "$3" ]; then
                        mkdir -p "$(dirname "$3")"
                        printf 'mock-db' > "$3"
                    fi
                    ;;
            esac
            SH
                chmod +x "$INSTALL_DIR/cdidx"
            }

            run_reinstall_real v1.2.3
            """,
            enforceStrictMode: false);

        Assert.NotEqual(0, exitCode);
        Assert.Contains("must emit exactly one non-empty line", stderr);
    }

    [Fact]
    public void ReinstallReal_SearchHeaderLineContainsDefGreetDiagnostic_AbortsWithError()
    {
        if (OperatingSystem.IsWindows())
            return;

        var (exitCode, _, stderr) = RunInstallerSnippet(
            """
            need_cmd() { :; }
            detect_platform() { OS_NAME="linux"; ARCH_NAME="x64"; RID="linux-x64"; }
            main() {
                mkdir -p "$INSTALL_DIR"
                cat > "$INSTALL_DIR/cdidx" <<'SH'
            #!/usr/bin/env bash
            case "$1" in
                --version) echo "cdidx v1.2.3" ;;
                search)
                    # Header-line diagnostic that deliberately contains the
                    # substring `def greet`, matching the strict grep-form
                    # header `^sample\.py:[0-9]+:` but not the verbatim
                    # scratch source signature `def greet(name):`. A loose
                    # check that accepted the `def greet` substring alone
                    # would false-pass; the tightened check requires the
                    # full argument-list shape.
                    # grep 形ヘッダにマッチするが、`def greet` を診断文内にしか
                    # 含まず verbatim な `def greet(name):` ではないケース。
                    # 引数リスト付きの完全な形を要求することで弾かれる。
                    printf '%s\n' "sample.py:1: warning: expected code signature def greet missing"
                    ;;
                *)
                    if [ "$2" = "--db" ] && [ -n "$3" ]; then
                        mkdir -p "$(dirname "$3")"
                        printf 'mock-db' > "$3"
                    fi
                    ;;
            esac
            SH
                chmod +x "$INSTALL_DIR/cdidx"
            }

            run_reinstall_real v1.2.3
            """,
            enforceStrictMode: false);

        Assert.NotEqual(0, exitCode);
        Assert.Contains("did not return a structured match", stderr);
    }

    [Fact]
    public void ReinstallReal_SearchIndentedLineContainsDefGreetDiagnostic_AbortsWithError()
    {
        if (OperatingSystem.IsWindows())
            return;

        var (exitCode, _, stderr) = RunInstallerSnippet(
            """
            need_cmd() { :; }
            detect_platform() { OS_NAME="linux"; ARCH_NAME="x64"; RID="linux-x64"; }
            main() {
                mkdir -p "$INSTALL_DIR"
                cat > "$INSTALL_DIR/cdidx" <<'SH'
            #!/usr/bin/env bash
            case "$1" in
                --version) echo "cdidx v1.2.3" ;;
                search)
                    # Strict range-form header followed by a two-space-indented
                    # diagnostic that deliberately contains the substring
                    # `def greet` without the real argument list. A loose
                    # indented-line check that accepted any `def greet`
                    # substring would false-pass; the tightened check
                    # requires the verbatim scratch source signature
                    # `def greet(name):`.
                    # 範囲形ヘッダの下に 2 スペースインデントで `def greet` を
                    # 含む診断文が続くが、verbatim な `def greet(name):` では
                    # ないケース。引数リスト付きの完全な形を要求して弾く。
                    printf '%s\n%s\n' "sample.py:1-6" "  warning: expected code signature def greet missing"
                    ;;
                *)
                    if [ "$2" = "--db" ] && [ -n "$3" ]; then
                        mkdir -p "$(dirname "$3")"
                        printf 'mock-db' > "$3"
                    fi
                    ;;
            esac
            SH
                chmod +x "$INSTALL_DIR/cdidx"
            }

            run_reinstall_real v1.2.3
            """,
            enforceStrictMode: false);

        Assert.NotEqual(0, exitCode);
        Assert.Contains("did not return a structured match", stderr);
    }

    [Fact]
    public void ReinstallReal_SearchHeaderLineContainsVerbatimSignatureDiagnostic_AbortsWithError()
    {
        if (OperatingSystem.IsWindows())
            return;

        var (exitCode, _, stderr) = RunInstallerSnippet(
            """
            need_cmd() { :; }
            detect_platform() { OS_NAME="linux"; ARCH_NAME="x64"; RID="linux-x64"; }
            main() {
                mkdir -p "$INSTALL_DIR"
                cat > "$INSTALL_DIR/cdidx" <<'SH'
            #!/usr/bin/env bash
            case "$1" in
                --version) echo "cdidx v1.2.3" ;;
                search)
                    # Grep-form-style header whose diagnostic prose embeds the
                    # FULL verbatim signature `def greet(name):` as a substring.
                    # A substring check that accepted `def greet(name):`
                    # anywhere on the line would false-pass; the tightened
                    # check requires the whole line to be exactly
                    # `sample.py:<N>:def greet(name):` with nothing before
                    # or after the signature, so this adversarial shape is
                    # rejected even though it carries the verbatim argument
                    # list.
                    # grep 形ヘッダの診断文内に verbatim な `def greet(name):`
                    # を substring として埋め込んだケース。行全体の完全一致を
                    # 要求するため弾かれる。
                    printf '%s\n' "sample.py:1: warning: expected code signature def greet(name): missing"
                    ;;
                *)
                    if [ "$2" = "--db" ] && [ -n "$3" ]; then
                        mkdir -p "$(dirname "$3")"
                        printf 'mock-db' > "$3"
                    fi
                    ;;
            esac
            SH
                chmod +x "$INSTALL_DIR/cdidx"
            }

            run_reinstall_real v1.2.3
            """,
            enforceStrictMode: false);

        Assert.NotEqual(0, exitCode);
        Assert.Contains("did not return a structured match", stderr);
    }

    [Fact]
    public void ReinstallReal_SearchIndentedLineContainsVerbatimSignatureDiagnostic_AbortsWithError()
    {
        if (OperatingSystem.IsWindows())
            return;

        var (exitCode, _, stderr) = RunInstallerSnippet(
            """
            need_cmd() { :; }
            detect_platform() { OS_NAME="linux"; ARCH_NAME="x64"; RID="linux-x64"; }
            main() {
                mkdir -p "$INSTALL_DIR"
                cat > "$INSTALL_DIR/cdidx" <<'SH'
            #!/usr/bin/env bash
            case "$1" in
                --version) echo "cdidx v1.2.3" ;;
                search)
                    # Strict range-form header followed by a two-space-indented
                    # diagnostic whose prose embeds the FULL verbatim signature
                    # `def greet(name):` as a substring. A substring check would
                    # false-pass; the tightened check requires the first indented
                    # line under the range header to be exactly
                    # `  def greet(name):` (no prefix / suffix prose), so this
                    # adversarial shape is rejected even though it carries the
                    # verbatim argument list inside the indented block.
                    # 範囲形ヘッダ直後の 2 スペースインデント診断行に verbatim な
                    # `def greet(name):` を substring として埋め込んだケース。
                    # ヘッダ直後 1 行目の完全一致を要求するため弾かれる。
                    printf '%s\n%s\n' "sample.py:1-6" "  warning: expected code signature def greet(name): missing"
                    ;;
                *)
                    if [ "$2" = "--db" ] && [ -n "$3" ]; then
                        mkdir -p "$(dirname "$3")"
                        printf 'mock-db' > "$3"
                    fi
                    ;;
            esac
            SH
                chmod +x "$INSTALL_DIR/cdidx"
            }

            run_reinstall_real v1.2.3
            """,
            enforceStrictMode: false);

        Assert.NotEqual(0, exitCode);
        Assert.Contains("did not return a structured match", stderr);
    }

    [Fact]
    public void ReinstallReal_SearchRangeBlockNonFirstIndentMatchesSignature_AbortsWithError()
    {
        if (OperatingSystem.IsWindows())
            return;

        var (exitCode, _, stderr) = RunInstallerSnippet(
            """
            need_cmd() { :; }
            detect_platform() { OS_NAME="linux"; ARCH_NAME="x64"; RID="linux-x64"; }
            main() {
                mkdir -p "$INSTALL_DIR"
                cat > "$INSTALL_DIR/cdidx" <<'SH'
            #!/usr/bin/env bash
            case "$1" in
                --version) echo "cdidx v1.2.3" ;;
                search)
                    # Strict range-form header + a non-matching decoy
                    # indented line + a LATER indented line that is exactly
                    # `  def greet(name):`. A permissive check that scanned
                    # the whole range block for the signature anywhere in
                    # block-adjacent indented output would false-pass; the
                    # tightened check arms a one-shot `expect_first_indent`
                    # flag that is consumed by the decoy line, so the
                    # later-adjacent verbatim signature never counts as the
                    # block's first snippet line and the match is rejected.
                    # 範囲形ヘッダの後、1 行目が非一致の decoy インデント行で、
                    # 2 行目以降に exactly `  def greet(name):` を置いたケース。
                    # ヘッダ直後 1 行目の完全一致のみを許可する one-shot フラグで
                    # 弾かれる（後置の verbatim 署名は隣接条件を満たさない）。
                    printf '%s\n%s\n%s\n' "sample.py:1-6" "  warning: no matches" "  def greet(name):"
                    ;;
                *)
                    if [ "$2" = "--db" ] && [ -n "$3" ]; then
                        mkdir -p "$(dirname "$3")"
                        printf 'mock-db' > "$3"
                    fi
                    ;;
            esac
            SH
                chmod +x "$INSTALL_DIR/cdidx"
            }

            run_reinstall_real v1.2.3
            """,
            enforceStrictMode: false);

        Assert.NotEqual(0, exitCode);
        Assert.Contains("did not return a structured match", stderr);
    }

    [Fact]
    public void ReinstallReal_SearchGrepFormExactFullLine_Succeeds()
    {
        if (OperatingSystem.IsWindows())
            return;

        var (exitCode, stdout, _) = RunInstallerSnippet(
            """
            need_cmd() { :; }
            detect_platform() { OS_NAME="linux"; ARCH_NAME="x64"; RID="linux-x64"; }
            main() {
                mkdir -p "$INSTALL_DIR"
                cat > "$INSTALL_DIR/cdidx" <<'SH'
            #!/usr/bin/env bash
            case "$1" in
                --version) echo "cdidx v1.2.3" ;;
                search)
                    # Positive test for the strict grep-form branch of the
                    # tightened awk state machine: the single line
                    # `sample.py:<N>:def greet(name):` with no space between
                    # the colon and the signature and no trailing diagnostic
                    # prose must still match. Without this test the grep-form
                    # accept path has no dedicated regression coverage — all
                    # other success-path stubs use the range form.
                    # grep 形成功分岐のポジティブテスト。行全体が
                    # `sample.py:<N>:def greet(name):`（コロン直後に空白なし・
                    # 末尾診断なし）である完全一致を通す awk 分岐を確認する。
                    printf '%s\n' "sample.py:1:def greet(name):"
                    ;;
                *)
                    if [ "$2" = "--db" ] && [ -n "$3" ]; then
                        mkdir -p "$(dirname "$3")"
                        printf 'mock-db' > "$3"
                    fi
                    ;;
            esac
            SH
                chmod +x "$INSTALL_DIR/cdidx"
            }

            run_reinstall_real v1.2.3
            """,
            enforceStrictMode: false);

        Assert.Equal(0, exitCode);
        Assert.Contains("Real reinstall validation passed", stdout);
    }

    [Fact]
    public void ReinstallReal_SearchLaterRangeHeaderRearmsFirstIndent_Succeeds()
    {
        if (OperatingSystem.IsWindows())
            return;

        var (exitCode, stdout, _) = RunInstallerSnippet(
            """
            need_cmd() { :; }
            detect_platform() { OS_NAME="linux"; ARCH_NAME="x64"; RID="linux-x64"; }
            main() {
                mkdir -p "$INSTALL_DIR"
                cat > "$INSTALL_DIR/cdidx" <<'SH'
            #!/usr/bin/env bash
            case "$1" in
                --version) echo "cdidx v1.2.3" ;;
                search)
                    # Positive test for one-shot flag re-arming: the first
                    # range-form header arms the `expect_first_indent` flag,
                    # the first indented line under it is a non-matching
                    # decoy that consumes the flag and abandons the block,
                    # and THEN a second range-form header re-arms the flag
                    # whose first indented line is exactly
                    # `  def greet(name):`. The tightened awk state machine
                    # must accept this shape — otherwise a single decoy
                    # anywhere in the output would permanently disarm
                    # matching.
                    # one-shot フラグの再武装成功ケース。最初の範囲形ヘッダ
                    # 直後の 1 行目が非一致 decoy で block を放棄した後、
                    # 2 つ目の範囲形ヘッダで flag が再び立ち、その直後 1 行目
                    # が exactly `  def greet(name):` で完全一致して通る。
                    # 先頭 block の decoy が検証を永続的に無効化しないことを
                    # 保証する。
                    printf '%s\n%s\n%s\n%s\n' "sample.py:1-6" "  warning: no matches" "sample.py:10-15" "  def greet(name):"
                    ;;
                *)
                    if [ "$2" = "--db" ] && [ -n "$3" ]; then
                        mkdir -p "$(dirname "$3")"
                        printf 'mock-db' > "$3"
                    fi
                    ;;
            esac
            SH
                chmod +x "$INSTALL_DIR/cdidx"
            }

            run_reinstall_real v1.2.3
            """,
            enforceStrictMode: false);

        Assert.Equal(0, exitCode);
        Assert.Contains("Real reinstall validation passed", stdout);
    }

    [Fact]
    public void ReinstallReal_DoesNotRequestJsonFromSearch()
    {
        if (OperatingSystem.IsWindows())
            return;

        var (exitCode, _, stderr) = RunInstallerSnippet(
            """
            need_cmd() { :; }
            detect_platform() { OS_NAME="linux"; ARCH_NAME="x64"; RID="linux-x64"; }
            main() {
                mkdir -p "$INSTALL_DIR"
                cat > "$INSTALL_DIR/cdidx" <<'SH'
            #!/usr/bin/env bash
            case "$1" in
                --version) echo "cdidx v1.2.3" ;;
                search)
                    # Simulate a trimmed release build: reject --json with exit 4.
                    # trimmed release では --json を exit 4 で拒否する。
                    for arg in "$@"; do
                        if [ "$arg" = "--json" ]; then
                            echo "--json not available on this trimmed build" >&2
                            exit 4
                        fi
                    done
                    # Real cdidx search emits range-form with the first
                    # snippet line equal to `  def greet(name):` — the
                    # tightened awk requires this exact shape.
                    # 実 cdidx 準拠の範囲形 + 直後 1 行目完全一致の snippet。
                    printf '%s\n%s\n' "sample.py:1-6" "  def greet(name):"
                    ;;
                *)
                    if [ "$2" = "--db" ] && [ -n "$3" ]; then
                        mkdir -p "$(dirname "$3")"
                        printf 'mock-db' > "$3"
                    fi
                    ;;
            esac
            SH
                chmod +x "$INSTALL_DIR/cdidx"
            }

            run_reinstall_real v1.2.3
            """,
            enforceStrictMode: false);

        Assert.Equal(0, exitCode);
        Assert.DoesNotContain("--json not available", stderr);
        Assert.DoesNotContain("search returned a non-zero exit code", stderr);
    }

    // --- Doctor diagnostic mode (#436) / --doctor ネットワーク診断 (#436) ---

    [Fact]
    public void Doctor_AllProbesReturn200_ExitsZeroAndReportsReachable()
    {
        if (OperatingSystem.IsWindows())
            return;

        var (exitCode, stdout, stderr) = RunInstallerSnippet(
            """
            need_cmd() { :; }
            detect_platform() { OS_NAME="linux"; ARCH_NAME="x64"; RID="linux-x64"; }
            curl() {
                local url=""
                while [ $# -gt 0 ]; do
                    case "$1" in
                        -w)
                            shift 2
                            ;;
                        -o)
                            shift 2
                            ;;
                        -*)
                            shift
                            ;;
                        *)
                            url="$1"
                            shift
                            ;;
                    esac
                done
                printf '%s' "200"
                return 0
            }

            run_doctor v1.2.3
            """);

        Assert.Equal(0, exitCode);
        Assert.Contains("cdidx installer doctor", stdout);
        Assert.Contains("Proxy environment variables", stdout);
        Assert.Contains("Probing version: v1.2.3 (explicit argument)", stdout);
        Assert.Contains("Result: HTTP 200", stdout);
        Assert.Contains("Doctor summary", stdout);
        Assert.Contains("API probe: reachable", stdout);
        Assert.Contains("Release asset probe: reachable", stdout);
        Assert.Contains("Checksums probe: reachable", stdout);
        Assert.Contains("all probed endpoints are reachable", stdout);
        Assert.DoesNotContain("Doctor detected", stderr);
    }

    [Fact]
    public void Doctor_ConnectTunnel403_PrintsUpstreamProxyGuidanceAndExitsOne()
    {
        if (OperatingSystem.IsWindows())
            return;

        var (exitCode, stdout, stderr) = RunInstallerSnippet(
            """
            need_cmd() { :; }
            detect_platform() { OS_NAME="linux"; ARCH_NAME="x64"; RID="linux-x64"; }
            curl() {
                # Simulate a proxy that denies the CONNECT tunnel with HTTP 403.
                # curl exit 56 + the specific stderr text are what
                # `is_proxy_tunnel_403` keys on, so this reproduces the exact
                # shape the doctor must surface the advisory for.
                # CONNECT トンネル段階で HTTP 403 を返す proxy を模擬。
                # curl exit 56 + 特定 stderr を `is_proxy_tunnel_403` が検出する。
                printf '%s\n' "curl: (56) CONNECT tunnel failed, response 403" >&2
                return 56
            }

            run_doctor v1.2.3
            """);

        Assert.Equal(1, exitCode);
        Assert.Contains("CONNECT tunnel failed with HTTP 403", stderr);
        Assert.Contains("upstream proxy/egress policy before TLS", stderr);
        Assert.Contains("Route substitution alone will not fix it", stderr);
        Assert.Contains("allow-list at least one artifact host path", stderr);
        Assert.Contains("CDIDX_GITHUB_BASE_URL", stderr);
        Assert.Contains("Doctor detected at least one unreachable endpoint", stderr);
        Assert.Contains("API probe: FAILED", stdout);
        Assert.Contains("Release asset probe: FAILED", stdout);
        Assert.Contains("Checksums probe: FAILED", stdout);
    }

    [Fact]
    public void Doctor_PrintsConfiguredProxyEnvVars()
    {
        if (OperatingSystem.IsWindows())
            return;

        var (exitCode, stdout, _) = RunInstallerSnippet(
            """
            need_cmd() { :; }
            detect_platform() { OS_NAME="linux"; ARCH_NAME="x64"; RID="linux-x64"; }
            curl() { printf '%s' "200"; return 0; }

            run_doctor v1.2.3
            """,
            new Dictionary<string, string?>
            {
                ["HTTP_PROXY"] = "http://proxy:8080",
                ["HTTPS_PROXY"] = "http://proxy:8080",
                ["NO_PROXY"] = "127.0.0.1,localhost",
            });

        Assert.Equal(0, exitCode);
        Assert.Contains("HTTP_PROXY=http://proxy:8080", stdout);
        Assert.Contains("HTTPS_PROXY=http://proxy:8080", stdout);
        Assert.Contains("NO_PROXY=127.0.0.1,localhost", stdout);
        Assert.Contains("ALL_PROXY=(unset)", stdout);
    }

    [Fact]
    public void Doctor_RedactsProxyUrlCredentialsBeforePrinting()
    {
        if (OperatingSystem.IsWindows())
            return;

        // `--doctor` is the command users paste into logs, issues, and support
        // transcripts when they're trying to diagnose a proxy-blocked install.
        // If the proxy URL contains credentials (a common pattern on corporate
        // MITM proxies, e.g. `http://alice:hunter2@proxy.example.com:8080`), the
        // raw value would leak those credentials to whoever receives the
        // output. Redact the userinfo portion before printing while keeping the
        // host/port visible for reachability diagnosis.
        // `--doctor` の出力は proxy 起因のインストール失敗を診断するため
        // log / issue / サポート窓口に貼られやすい。proxy URL に資格情報が
        // 入る形（例: 企業 MITM の `http://alice:hunter2@proxy.example.com:8080`）
        // では raw 値を表示すると secret が共有先に漏れるため、reachability 診断
        // に必要な host/port は保ったまま userinfo 部分だけを redact する。
        var (exitCode, stdout, stderr) = RunInstallerSnippet(
            """
            need_cmd() { :; }
            detect_platform() { OS_NAME="linux"; ARCH_NAME="x64"; RID="linux-x64"; }
            curl() { printf '%s' "200"; return 0; }

            run_doctor v1.2.3
            """,
            new Dictionary<string, string?>
            {
                ["HTTP_PROXY"] = "http://alice:hunter2@proxy.example.com:8080",
                ["HTTPS_PROXY"] = "https://bob@secure-proxy.example.com:3128",
                ["ALL_PROXY"] = "socks5://carol:s3cret@socks.example.com:1080",
                ["NO_PROXY"] = "127.0.0.1,localhost",
            });

        Assert.Equal(0, exitCode);
        Assert.DoesNotContain("hunter2", stdout);
        Assert.DoesNotContain("hunter2", stderr);
        Assert.DoesNotContain("s3cret", stdout);
        Assert.DoesNotContain("s3cret", stderr);
        Assert.DoesNotContain("alice:hunter2", stdout);
        Assert.DoesNotContain("bob@secure-proxy", stdout);
        Assert.DoesNotContain("carol:s3cret", stdout);
        Assert.Contains("HTTP_PROXY=http://<redacted>@proxy.example.com:8080", stdout);
        Assert.Contains("HTTPS_PROXY=https://<redacted>@secure-proxy.example.com:3128", stdout);
        Assert.Contains("ALL_PROXY=socks5://<redacted>@socks.example.com:1080", stdout);
        Assert.Contains("NO_PROXY=127.0.0.1,localhost", stdout);
    }

    [Fact]
    public void Doctor_CredentialLessProxyUrlsAreNotRewritten()
    {
        if (OperatingSystem.IsWindows())
            return;

        // The redactor must only touch values that actually carry a
        // `scheme://userinfo@host` shape. Plain `scheme://host:port` or
        // bare-host NO_PROXY values would otherwise be rewritten into a form
        // that looks like it has been redacted, which is both misleading and
        // would make diagnostic output harder to read.
        // redactor は `scheme://userinfo@host` の形を持つ値のみを書き換える。
        // `scheme://host:port` や bare host の NO_PROXY 値まで書き換えると、
        // 資格情報があったかのような誤った印象を与え、診断ログが読みにくくなる。
        var (exitCode, stdout, _) = RunInstallerSnippet(
            """
            need_cmd() { :; }
            detect_platform() { OS_NAME="linux"; ARCH_NAME="x64"; RID="linux-x64"; }
            curl() { printf '%s' "200"; return 0; }

            run_doctor v1.2.3
            """,
            new Dictionary<string, string?>
            {
                ["HTTP_PROXY"] = "http://proxy.example.com:8080",
                ["NO_PROXY"] = "127.0.0.1,localhost,*.internal",
            });

        Assert.Equal(0, exitCode);
        Assert.DoesNotContain("<redacted>", stdout);
        Assert.Contains("HTTP_PROXY=http://proxy.example.com:8080", stdout);
        Assert.Contains("NO_PROXY=127.0.0.1,localhost,*.internal", stdout);
    }

    [Fact]
    public void Doctor_ExplicitVersionArgument_ProbesReleaseAssetForThatVersion()
    {
        if (OperatingSystem.IsWindows())
            return;

        var (exitCode, stdout, _) = RunInstallerSnippet(
            """
            need_cmd() { :; }
            detect_platform() { OS_NAME="linux"; ARCH_NAME="x64"; RID="linux-x64"; }
            curl() { printf '%s' "200"; return 0; }

            run_doctor v9.8.7
            """);

        Assert.Equal(0, exitCode);
        Assert.Contains("Probing version: v9.8.7 (explicit argument)", stdout);
        Assert.Contains("/releases/download/v9.8.7/CodeIndex-linux-x64.tar.gz", stdout);
        Assert.Contains("/releases/download/v9.8.7/sha256sums.txt", stdout);
    }

    [Fact]
    public void Doctor_BareVersionArgument_GetsVPrefixBeforeProbing()
    {
        if (OperatingSystem.IsWindows())
            return;

        var (exitCode, stdout, _) = RunInstallerSnippet(
            """
            need_cmd() { :; }
            detect_platform() { OS_NAME="linux"; ARCH_NAME="x64"; RID="linux-x64"; }
            curl() { printf '%s' "200"; return 0; }

            run_doctor 1.2.3
            """);

        Assert.Equal(0, exitCode);
        Assert.Contains("Probing version: v1.2.3 (explicit argument)", stdout);
        Assert.Contains("/releases/download/v1.2.3/CodeIndex-linux-x64.tar.gz", stdout);
    }

    [Fact]
    public void Doctor_NoExplicitVersion_ReadsVersionJson()
    {
        if (OperatingSystem.IsWindows())
            return;

        // `default_self_test_version` reads the version.json sitting next to
        // this script, so when no explicit version is passed the doctor should
        // use that as the probe target.
        // 明示バージョン無しなら `default_self_test_version` が拾う version.json を使う。
        var (exitCode, stdout, _) = RunInstallerSnippet(
            """
            need_cmd() { :; }
            detect_platform() { OS_NAME="linux"; ARCH_NAME="x64"; RID="linux-x64"; }
            curl() { printf '%s' "200"; return 0; }
            default_self_test_version() { printf '%s' "v4.5.6"; }

            run_doctor
            """);

        Assert.Equal(0, exitCode);
        Assert.Contains("Probing version: v4.5.6 (version.json)", stdout);
        Assert.Contains("/releases/download/v4.5.6/CodeIndex-linux-x64.tar.gz", stdout);
    }

    [Fact]
    public void Doctor_NoExplicitVersionAndNoVersionJson_SkipsReleaseProbes()
    {
        if (OperatingSystem.IsWindows())
            return;

        // When neither an explicit argument nor a version.json is available,
        // `default_self_test_version` returns v0.0.0. The doctor must still run
        // the API probe and clearly mark the release probes as skipped instead
        // of silently probing a bogus v0.0.0 release URL.
        // 明示引数も version.json も無い場合、default_self_test_version は
        // v0.0.0 を返す。doctor は API probe だけ走らせ、リリース系 probe は
        // skip 扱いで表示し、偽の v0.0.0 URL を probe しない。
        var (exitCode, stdout, _) = RunInstallerSnippet(
            """
            need_cmd() { :; }
            detect_platform() { OS_NAME="linux"; ARCH_NAME="x64"; RID="linux-x64"; }
            curl() { printf '%s' "200"; return 0; }
            default_self_test_version() { printf '%s' "v0.0.0"; }

            run_doctor
            """);

        Assert.Equal(0, exitCode);
        Assert.Contains("Probing version: unknown", stdout);
        Assert.Contains("Release asset probe: skipped (no version)", stdout);
        Assert.Contains("Checksums probe: skipped (no version)", stdout);
        Assert.DoesNotContain("/releases/download/v0.0.0/", stdout);
    }

    [Fact]
    public void Doctor_ApiReturnsHttp404_ExitsOneAndFlagsApiProbe()
    {
        if (OperatingSystem.IsWindows())
            return;

        // Simulate an API endpoint that answers (curl exit 0) but the response
        // itself is 404. Reachability is not confirmed — the probe must fail
        // and the summary must show the API probe as FAILED, while the
        // release probes (still 200 via the default curl path) stay reachable
        // because the doctor does not short-circuit on a single failure.
        // curl は 0 で終了するが HTTP 404 を返す API を模擬。probe は失敗扱い
        // とし、サマリで API が FAILED となること、release 系は 200 を
        // 返して reachable のままであること（doctor は最初の失敗で止まらない）
        // を確認する。
        var (exitCode, stdout, _) = RunInstallerSnippet(
            """
            need_cmd() { :; }
            detect_platform() { OS_NAME="linux"; ARCH_NAME="x64"; RID="linux-x64"; }
            curl() {
                local url=""
                while [ $# -gt 0 ]; do
                    case "$1" in
                        -w|-o) shift 2 ;;
                        -*) shift ;;
                        *) url="$1"; shift ;;
                    esac
                done
                case "$url" in
                    *api.github.com*) printf '%s' "404" ;;
                    *) printf '%s' "200" ;;
                esac
                return 0
            }

            run_doctor v1.2.3
            """);

        Assert.Equal(1, exitCode);
        Assert.Contains("API probe: FAILED", stdout);
        Assert.Contains("Release asset probe: reachable", stdout);
        Assert.Contains("Checksums probe: reachable", stdout);
    }

    [Fact]
    public void Doctor_UnknownFlagAfterDoctor_IsTreatedAsVersion()
    {
        if (OperatingSystem.IsWindows())
            return;

        // The dispatcher forwards the first positional token after --doctor
        // verbatim as the version argument. Passing a bare string such as
        // `notaversion` is normalized by `v` prefixing to `vnotaversion` and
        // used as the probe version — no `error()` abort, because the doctor
        // is a diagnostic command and must always print the summary so users
        // can see what was actually attempted.
        // --doctor の直後に置いた最初の token はそのままバージョン引数として
        // 扱われる。不正な文字列でも `v` prefix を付けて probe を走らせ、
        // サマリまで必ず出す（診断コマンドなので途中 abort せず、実際に
        // 何を試したかを常に表示する）。
        var (exitCode, stdout, _) = RunInstallerSnippet(
            """
            need_cmd() { :; }
            detect_platform() { OS_NAME="linux"; ARCH_NAME="x64"; RID="linux-x64"; }
            curl() { printf '%s' "200"; return 0; }

            run_doctor notaversion
            """);

        Assert.Equal(0, exitCode);
        Assert.Contains("Probing version: vnotaversion (explicit argument)", stdout);
    }

    [Fact]
    public void Doctor_DispatcherWiresDoctorCliFlag()
    {
        if (OperatingSystem.IsWindows())
            return;

        // The CLI dispatcher at the bottom of install.sh must route
        // --doctor [<version>] to run_doctor, not into main(). Build an
        // isolated PATH shim that replaces curl + uname with stubs, so the
        // real dispatcher actually runs end-to-end without trying to reach
        // the network or read the host platform.
        // install.sh の CLI dispatcher が --doctor を run_doctor に振ることを
        // 確認する。curl と uname を PATH shim で差し替え、実ネットワークや
        // ホスト platform 依存に触れずに dispatcher を end-to-end で走らせる。
        var shimDir = Path.Combine(Path.GetTempPath(), $"cdidx_doctor_shim_{Guid.NewGuid():N}");
        Directory.CreateDirectory(shimDir);
        try
        {
            var curlStub = Path.Combine(shimDir, "curl");
            File.WriteAllText(curlStub, "#!/usr/bin/env bash\nprintf '%s' \"200\"\n");
            File.SetUnixFileMode(curlStub, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var unameStub = Path.Combine(shimDir, "uname");
            // `uname -s` -> Linux, `uname -m` -> x86_64, anything else -> unknown.
            // install.sh の detect_platform が呼ぶ `uname -s` / `uname -m` の両方に応える。
            File.WriteAllText(unameStub, "#!/usr/bin/env bash\ncase \"$1\" in\n  -s) echo Linux ;;\n  -m) echo x86_64 ;;\n  *) echo unknown ;;\nesac\n");
            File.SetUnixFileMode(unameStub, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

            var psi = new ProcessStartInfo
            {
                FileName = "bash",
                WorkingDirectory = GetRepositoryRoot(),
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add(GetInstallScriptPath());
            psi.ArgumentList.Add("--doctor");
            psi.ArgumentList.Add("v7.6.5");
            psi.Environment["PATH"] = $"{shimDir}:{Environment.GetEnvironmentVariable("PATH") ?? "/usr/bin:/bin"}";

            using var process = Process.Start(psi)
                ?? throw new InvalidOperationException("Failed to start install.sh --doctor / dispatcher 起動失敗");
            var stdOut = process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();
            process.WaitForExit();

            Assert.Equal(0, process.ExitCode);
            Assert.Contains("cdidx installer doctor", stdOut);
            Assert.Contains("Probing version: v7.6.5 (explicit argument)", stdOut);
            Assert.Contains("all probed endpoints are reachable", stdOut);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(shimDir);
        }
    }

    [UnsupportedOSPlatform("windows")]
    private static (int ExitCode, string StdOut, string StdErr) RunInstallerSnippet(string snippet, IReadOnlyDictionary<string, string?>? extraEnvironment = null, bool enforceStrictMode = true)
    {
        var scriptPath = Path.Combine(Path.GetTempPath(), $"cdidx_install_snippet_{Guid.NewGuid():N}.sh");
        try
        {
            File.WriteAllText(scriptPath, $$"""
                #!/usr/bin/env bash
                {{(enforceStrictMode ? "set -euo pipefail" : "")}}
                export CDIDX_INSTALL_SH_LIB_ONLY=1
                source "{{GetInstallScriptPath()}}"
                {{snippet}}
                """);
            File.SetUnixFileMode(scriptPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

            var psi = new ProcessStartInfo
            {
                FileName = "bash",
                WorkingDirectory = GetRepositoryRoot(),
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add(scriptPath);
            if (extraEnvironment != null)
            {
                foreach (var kvp in extraEnvironment)
                    psi.Environment[kvp.Key] = kvp.Value;
            }

            using var process = Process.Start(psi)
                ?? throw new InvalidOperationException("Failed to start bash install snippet / bash install snippet の起動に失敗");
            var stdOut = process.StandardOutput.ReadToEnd();
            var stdErr = process.StandardError.ReadToEnd();
            process.WaitForExit();
            return (process.ExitCode, stdOut, stdErr);
        }
        finally
        {
            TestProjectHelper.DeleteFile(scriptPath);
        }
    }

    private static string GetInstallScriptPath() => Path.Combine(GetRepositoryRoot(), "install.sh");

    private static string GetRepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "CodeIndex.sln")) || File.Exists(Path.Combine(dir.FullName, "install.sh")))
                return dir.FullName;
            dir = dir.Parent;
        }

        throw new InvalidOperationException("Could not locate repository root / リポジトリルートを特定できませんでした");
    }
}
