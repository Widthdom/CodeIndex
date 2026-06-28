using System.Diagnostics;

namespace CodeIndex;

internal static class SubprocessEnvironmentPolicy
{
    internal const string TestEnvironmentPrefix = "CDIDX_TEST_";

    private static readonly string[] BaseEnvironmentAllowlist =
    [
        "PATH",
        "HOME",
        "USERPROFILE",
        "HOMEDRIVE",
        "HOMEPATH",
        "XDG_CONFIG_HOME",
        "XDG_CACHE_HOME",
        "XDG_DATA_HOME",
        "TMPDIR",
        "TMP",
        "TEMP",
        "SystemRoot",
        "WINDIR",
        "COMSPEC",
    ];

    private static readonly string[] DotNetRuntimeEnvironmentAllowlist =
    [
        "DOTNET_ROOT",
        "DOTNET_ROOT_X64",
        "DOTNET_ROOT_X86",
        "DOTNET_ROOT_ARM64",
        "DOTNET_BUNDLE_EXTRACT_BASE_DIR",
    ];

    private static readonly string[] ProxyEnvironmentAllowlist =
    [
        "HTTP_PROXY",
        "HTTPS_PROXY",
        "ALL_PROXY",
        "NO_PROXY",
        "http_proxy",
        "https_proxy",
        "all_proxy",
        "no_proxy",
    ];

    private static readonly string[] CertificateEnvironmentAllowlist =
    [
        "SSL_CERT_FILE",
        "SSL_CERT_DIR",
        "CURL_CA_BUNDLE",
        "GIT_SSL_CAINFO",
    ];

    private static readonly string[] GitEnvironmentAllowlist =
    [
        "GIT_CONFIG_NOSYSTEM",
        "GIT_CONFIG_GLOBAL",
        "GIT_CONFIG_SYSTEM",
        "GIT_CEILING_DIRECTORIES",
        "GIT_DISCOVERY_ACROSS_FILESYSTEM",
        "GIT_OPTIONAL_LOCKS",
    ];

    private static readonly string[] UpgradeInstallerEnvironmentAllowlist =
    [
        "CDIDX_ALLOW_RISKY_INSTALL_DIR",
        "CDIDX_GITHUB_API_BASE_URL",
        "CDIDX_GITHUB_BASE_URL",
        "CDIDX_INSTALL_UPDATE_PATH",
        "CDIDX_RELEASE_GPG_FINGERPRINT",
        "CDIDX_REQUIRE_ATTESTATION",
        "CDIDX_STRICT_VERIFY",
        "CDIDX_VERIFY_POLICY",
    ];

    internal static void ApplyIsolatedWorkerEnvironment(ProcessStartInfo startInfo)
    {
        ApplyAllowlist(startInfo, BaseEnvironmentAllowlist, DotNetRuntimeEnvironmentAllowlist);
        CopyPrefixedEnvironmentVariables(startInfo, TestEnvironmentPrefix);
    }

    internal static void ApplyGitEnvironment(ProcessStartInfo startInfo)
    {
        ApplyAllowlist(
            startInfo,
            BaseEnvironmentAllowlist,
            ProxyEnvironmentAllowlist,
            CertificateEnvironmentAllowlist,
            GitEnvironmentAllowlist);
        if (!startInfo.Environment.ContainsKey("GIT_TERMINAL_PROMPT"))
            startInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";
    }

    internal static void ApplyUpgradeInstallerEnvironment(ProcessStartInfo startInfo)
        => ApplyAllowlist(
            startInfo,
            BaseEnvironmentAllowlist,
            ProxyEnvironmentAllowlist,
            CertificateEnvironmentAllowlist,
            UpgradeInstallerEnvironmentAllowlist);

    private static void ApplyAllowlist(ProcessStartInfo startInfo, params string[][] allowlistGroups)
    {
        startInfo.Environment.Clear();
        foreach (var group in allowlistGroups)
        {
            foreach (var name in group)
                CopyEnvironmentVariable(startInfo, name);
        }
    }

    private static void CopyEnvironmentVariable(ProcessStartInfo startInfo, string name)
    {
        var value = EnvironmentAccess.GetProcessEnvironmentVariable(name);
        if (!string.IsNullOrEmpty(value))
            startInfo.Environment[name] = value;
    }

    private static void CopyPrefixedEnvironmentVariables(ProcessStartInfo startInfo, string prefix)
    {
        foreach (var item in EnvironmentAccess.EnumerateProcessEnvironmentVariables())
        {
            if (!item.Key.StartsWith(prefix, StringComparison.Ordinal))
            {
                continue;
            }

            startInfo.Environment[item.Key] = item.Value;
        }
    }
}
