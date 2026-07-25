using System.Diagnostics;
using System.Globalization;
using System.ComponentModel;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using CodeIndex.Database;
using CodeIndex.Diagnostics;
using CodeIndex.Indexer;
using CodeIndex.Indexer.Extensibility;
using CodeIndex.Indexer.Hooks;
using CodeIndex.Lsp;
using CodeIndex.Mcp;
using CodeIndex.Models;
using Microsoft.Data.Sqlite;

namespace CodeIndex.Cli;

internal static partial class ProgramRunner
{
    internal static ProcessStartInfo CreateInstallerProcessStartInfo(string scriptPath, string releaseTag, string installDir)
    {
        var fullScriptPath = Path.GetFullPath(scriptPath);
        var startInfo = CodeIndex.ProcessLaunchPolicy.CreateNoShellStartInfo(
            fileName: ResolveTrustedBashPath(),
            workingDirectory: Path.GetDirectoryName(fullScriptPath) ?? string.Empty);
        CodeIndex.ProcessLaunchPolicy.AddArguments(startInfo, fullScriptPath, releaseTag);
        CodeIndex.SubprocessEnvironmentPolicy.ApplyUpgradeInstallerEnvironment(startInfo);
        startInfo.Environment["CDIDX_INSTALL_DIR"] = installDir;
        return startInfo;
    }

    private static bool TryGetUpgradeVerificationPolicy(out string verificationPolicy, out string error)
    {
        var policy = EnvironmentAccess.GetProcessEnvironmentVariable("CDIDX_VERIFY_POLICY");
        if (string.IsNullOrEmpty(policy) || string.Equals(policy, "strict", StringComparison.Ordinal))
        {
            verificationPolicy = "strict";
            error = string.Empty;
            return true;
        }
        if (string.Equals(policy, "compat", StringComparison.Ordinal))
        {
            verificationPolicy = "compat";
            error = string.Empty;
            return true;
        }

        verificationPolicy = string.Empty;
        error = $"CDIDX_VERIFY_POLICY must be 'compat' or 'strict' (got '{policy}').";
        return false;
    }

    private static void RequireUpgradeAssetProvenance(
        string assetPath,
        string assetName,
        string releaseTag,
        string verificationPolicy,
        bool suppressOutput,
        CancellationToken cancellationToken,
        out bool? verified)
    {
        var compat = string.Equals(verificationPolicy, "compat", StringComparison.Ordinal);
        verified = UpgradeAssetProvenanceVerifier(assetPath, releaseTag, cancellationToken);

        if (verified == true)
        {
            if (!suppressOutput)
                CommandErrorWriter.WriteStderr($"Verified independent release provenance for {assetName}.");
            return;
        }

        if (!compat)
            throw new InvalidDataException($"Independent release provenance verification failed for {assetName}; installer execution is blocked.");

        if (!suppressOutput)
            CommandErrorWriter.WriteStderr($"Warning: AUDIT: CDIDX_VERIFY_POLICY=compat permits {assetName} without independent release provenance verification.");
    }

    private static bool VerifyUpgradeAssetProvenance(string assetPath, string releaseTag, CancellationToken cancellationToken)
    {
        var startInfo = CreateUpgradeAttestationStartInfo(assetPath, releaseTag);
        var result = RunInstallerProcessDetailed(
            startInfo,
            TimeSpan.FromSeconds(30),
            cancellationToken,
            suppressOutput: true);
        return result.ExitCode == CommandExitCodes.Success;
    }

    internal static ProcessStartInfo CreateUpgradeAttestationStartInfo(string assetPath, string releaseTag)
    {
        var fullAssetPath = Path.GetFullPath(assetPath);
        var startInfo = CodeIndex.ProcessLaunchPolicy.CreateNoShellStartInfo(
            fileName: "gh",
            workingDirectory: Path.GetDirectoryName(fullAssetPath) ?? string.Empty);
        CodeIndex.ProcessLaunchPolicy.AddArguments(
            startInfo,
            "attestation",
            "verify",
            fullAssetPath,
            "-R",
            "Widthdom/CodeIndex",
            "--signer-workflow",
            ReleaseAttestationSignerWorkflow,
            "--source-ref",
            $"refs/tags/{releaseTag}");
        CodeIndex.SubprocessEnvironmentPolicy.ApplyUpgradeInstallerEnvironment(startInfo);
        return startInfo;
    }

    internal static string ResolveTrustedBashPath()
    {
        if (OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("The install.sh upgrade path requires a POSIX bash executable.");

        foreach (var candidate in new[] { "/bin/bash", "/usr/bin/bash" })
        {
            if (File.Exists(candidate))
                return candidate;
        }

        throw new FileNotFoundException("Could not find a trusted absolute bash path for running install.sh.");
    }
}
