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
        Assert.Contains("GitHub API rate limit exceeded while fetching the latest release", stderr);
        Assert.Contains("pass an explicit version", stderr);
        Assert.DoesNotContain("Check your network connection", stderr);
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
        Assert.Contains("GitHub API rate limit exceeded while fetching the latest release", stderr);
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
        Assert.Contains("Network error reaching GitHub while fetching", stderr);
        Assert.Contains("curl exit 7", stderr);
    }

    [Fact]
    public void ResolveVersion_UsesConfiguredGitHubApiBaseUrl()
    {
        if (OperatingSystem.IsWindows())
            return;

        var (exitCode, stdout, stderr) = RunInstallerSnippet(
            """
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

                echo "CURL_URL:$url"
                printf '{"tag_name":"v1.2.3"}' > "$output_path"
                printf '200'
                return 0
            }

            GITHUB_API_BASE_URL="https://mirror.example/api/"
            resolve_version ""
            """);

        Assert.Equal(0, exitCode);
        Assert.Contains("Fetching latest release version", stdout);
        Assert.Contains("Version: v1.2.3", stdout);
        Assert.Contains("CURL_URL:https://mirror.example/api/repos/Widthdom/CodeIndex/releases/latest", stdout);
        Assert.Equal(string.Empty, stderr);
    }

    [Fact]
    public void DownloadAndInstall_UsesConfiguredGitHubReleaseBaseUrl()
    {
        if (OperatingSystem.IsWindows())
            return;

        var installDir = Path.Combine(_tempRoot, "release_base_url_override_bin");
        var (exitCode, stdout, stderr) = RunInstallerSnippet(
            """
            VERSION="v1.11.0"
            RID="linux-x64"
            GITHUB_BASE_URL="https://mirror.example/github/"

            download_release_file() {
                echo "DOWNLOAD_URL:$1"
                return 1
            }

            download_and_install || true
            """,
            new Dictionary<string, string?>
            {
                ["CDIDX_INSTALL_DIR"] = installDir,
            },
            enforceStrictMode: false);

        Assert.Equal(0, exitCode);
        Assert.Contains("Downloading CodeIndex-linux-x64.tar.gz", stdout);
        Assert.Contains("DOWNLOAD_URL:https://mirror.example/github/Widthdom/CodeIndex/releases/download/v1.11.0/CodeIndex-linux-x64.tar.gz", stdout);
        Assert.Equal(string.Empty, stderr);
    }

    [Fact]
    public void ResolveVersion_WithoutOverrides_UsesDefaultGitHubApiBaseUrl()
    {
        if (OperatingSystem.IsWindows())
            return;

        var (exitCode, stdout, stderr) = RunInstallerSnippet(
            """
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

                echo "CURL_URL:$url"
                printf '{"tag_name":"v1.2.3"}' > "$output_path"
                printf '200'
                return 0
            }

            resolve_version ""
            """);

        Assert.Equal(0, exitCode);
        Assert.Contains("Fetching latest release version", stdout);
        Assert.Contains("Version: v1.2.3", stdout);
        Assert.Contains("CURL_URL:https://api.github.com/repos/Widthdom/CodeIndex/releases/latest", stdout);
        Assert.Equal(string.Empty, stderr);
    }

    [Fact]
    public void DownloadAndInstall_WithoutOverrides_UsesDefaultGitHubReleaseBaseUrl()
    {
        if (OperatingSystem.IsWindows())
            return;

        var installDir = Path.Combine(_tempRoot, "release_base_url_default_bin");
        var (exitCode, stdout, stderr) = RunInstallerSnippet(
            """
            VERSION="v1.11.0"
            RID="linux-x64"

            download_release_file() {
                echo "DOWNLOAD_URL:$1"
                return 1
            }

            download_and_install || true
            """,
            new Dictionary<string, string?>
            {
                ["CDIDX_INSTALL_DIR"] = installDir,
            },
            enforceStrictMode: false);

        Assert.Equal(0, exitCode);
        Assert.Contains("Downloading CodeIndex-linux-x64.tar.gz", stdout);
        Assert.Contains("DOWNLOAD_URL:https://github.com/Widthdom/CodeIndex/releases/download/v1.11.0/CodeIndex-linux-x64.tar.gz", stdout);
        Assert.Equal(string.Empty, stderr);
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
        Assert.Contains("version v0.99.0 exists and publishes linux-x64 assets", stderr);
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
    public void ResolveVersion_UsesJqWhenAvailable()
    {
        if (OperatingSystem.IsWindows())
            return;

        var (exitCode, stdout, stderr) = RunInstallerSnippet(
            """
            jq() {
                printf 'v9.9.9\n'
            }

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
            """);

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, stderr);
        Assert.Contains("Version: v9.9.9", stdout);
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
