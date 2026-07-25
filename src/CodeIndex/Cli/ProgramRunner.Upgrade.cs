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
    internal static int RunCheckUpdates(
        string[] cmdArgs,
        JsonSerializerOptions jsonOptions,
        string appVersion,
        CancellationToken cancellationToken = default)
    {
        var wantsJson = cmdArgs.Contains("--json", StringComparer.Ordinal);
        foreach (var arg in cmdArgs)
        {
            if (arg == "--json")
                continue;
            return CommandErrorWriter.WriteJsonOrHuman(
                wantsJson,
                jsonOptions,
                $"--check-updates does not accept '{arg}'.",
                CommandExitCodes.UsageError,
                "use `cdidx --check-updates` or `cdidx --check-updates --json`.");
        }

        var result = UpdateChecker.Check(appVersion, cancellationToken);
        if (wantsJson)
        {
            CommandOutputWriter.WriteJson(
                result,
                CliJsonSerializerContextFactory.Create(jsonOptions).UpdateCheckResult);
            return CommandExitCodes.Success;
        }

        if (result.UpdateAvailable && result.LatestVersion != null)
            Console.WriteLine($"A newer cdidx release is available: {result.LatestVersion} (current: {result.CurrentVersion}).");
        else if (result.Error != null)
            Console.WriteLine($"Could not check for updates; using cached release metadata if available (current: {result.CurrentVersion}).");
        else
            Console.WriteLine($"cdidx is up to date (current: {result.CurrentVersion}).");
        return CommandExitCodes.Success;
    }

    internal static int RunUpgrade(
        string[] cmdArgs,
        JsonSerializerOptions jsonOptions,
        string appVersion,
        CancellationToken cancellationToken = default)
    {
        var checkOnly = false;
        var wantsJson = cmdArgs.Contains("--json", StringComparer.Ordinal);
        var selectedChannel = "stable";
        var includePrerelease = false;
        var selectionSource = "latest";
        string? explicitVersion = null;
        for (var i = 0; i < cmdArgs.Length; i++)
        {
            var arg = cmdArgs[i];
            if (arg is "--check-only" or "--check-updates")
            {
                checkOnly = true;
                continue;
            }
            if (arg == "--json")
            {
                continue;
            }
            if (arg == "--prerelease")
            {
                selectedChannel = "prerelease";
                includePrerelease = true;
                selectionSource = "prerelease";
                continue;
            }
            if (arg == "--channel")
            {
                if (i + 1 >= cmdArgs.Length)
                    return WriteUpgradeUsageError("--channel requires a value: stable, latest, or prerelease.", wantsJson, jsonOptions);

                if (!TryApplyUpgradeChannel(cmdArgs[++i], out selectedChannel, out includePrerelease, out var channelError))
                    return WriteUpgradeUsageError(channelError, wantsJson, jsonOptions);

                selectionSource = selectedChannel;
                continue;
            }
            if (arg.StartsWith("--channel=", StringComparison.Ordinal))
            {
                if (!TryApplyUpgradeChannel(arg["--channel=".Length..], out selectedChannel, out includePrerelease, out var channelError))
                    return WriteUpgradeUsageError(channelError, wantsJson, jsonOptions);

                selectionSource = selectedChannel;
                continue;
            }
            if (arg == "--version")
            {
                if (i + 1 >= cmdArgs.Length)
                    return WriteUpgradeUsageError("--version requires a release tag such as v1.29.0.", wantsJson, jsonOptions);

                if (!TryNormalizeReleaseTag(cmdArgs[++i], out explicitVersion, out var versionError))
                    return WriteUpgradeUsageError(versionError, wantsJson, jsonOptions);

                selectionSource = "explicit_version";
                continue;
            }
            if (arg.StartsWith("--version=", StringComparison.Ordinal))
            {
                if (!TryNormalizeReleaseTag(arg["--version=".Length..], out explicitVersion, out var versionError))
                    return WriteUpgradeUsageError(versionError, wantsJson, jsonOptions);

                selectionSource = "explicit_version";
                continue;
            }
            return WriteUpgradeUsageError($"upgrade does not accept '{arg}'.", wantsJson, jsonOptions);
        }

        if (!TryGetUpgradeVerificationPolicy(out var verificationPolicy, out var verificationPolicyError))
            return WriteUpgradeUsageError(verificationPolicyError, wantsJson, jsonOptions);

        if (explicitVersion != null && IsPrereleaseTag(explicitVersion) && selectedChannel == "stable")
        {
            selectedChannel = "prerelease";
            includePrerelease = true;
        }

        var result = explicitVersion != null
            ? new UpdateCheckResult(
                appVersion,
                explicitVersion,
                UpdateChecker.IsNewerRelease(explicitVersion, appVersion),
                FromCache: false,
                Error: null)
            : includePrerelease
                ? CheckLatestPrerelease(appVersion, cancellationToken)
                : UpdateChecker.Check(appVersion, cancellationToken);

        var shouldInstall = result.LatestVersion != null && (explicitVersion != null || result.UpdateAvailable);
        if (checkOnly || !shouldInstall)
        {
            var metadataFailureExitCode = result.Error is null
                ? CommandExitCodes.Success
                : CommandExitCodes.RuntimeError;
            if (wantsJson)
            {
                Console.WriteLine(JsonSerializer.Serialize(
                    CreateUpgradeJsonResult(
                        result,
                        selectedChannel,
                        selectionSource,
                        includePrerelease,
                        verificationPolicy,
                        installAttempted: false,
                        installExitCode: null,
                        error: null),
                    jsonOptions));
            }
            else if (result.UpdateAvailable && result.LatestVersion != null)
                Console.WriteLine($"A newer cdidx {selectedChannel} release is available: {result.LatestVersion} (current: {result.CurrentVersion}).");
            else if (result.Error != null)
                Console.WriteLine($"Could not select a cdidx {selectedChannel} release ({result.Error}); current: {result.CurrentVersion}.");
            else
                Console.WriteLine($"cdidx is up to date (current: {result.CurrentVersion}).");
            return metadataFailureExitCode;
        }

        var selectedReleaseTag = result.LatestVersion!;
        bool? manifestProvenanceVerified = null;
        bool? installerProvenanceVerified = null;

        if (OperatingSystem.IsWindows())
        {
            var handoff = CreateWindowsUpgradeHandoff(selectedReleaseTag, RuntimeInformation.ProcessArchitecture);
            if (wantsJson)
            {
                Console.WriteLine(JsonSerializer.Serialize(
                    CreateUpgradeJsonResult(
                        result,
                        selectedChannel,
                        selectionSource,
                        includePrerelease,
                        verificationPolicy,
                        installAttempted: false,
                        installExitCode: null,
                        error: "windows_handoff_required",
                        handoff: handoff),
                    jsonOptions));
            }
            else
            {
                CommandErrorWriter.WriteStderr("Error: cdidx upgrade cannot replace the running Windows binary directly.");
                CommandErrorWriter.WriteStderr($"Hint: update via NuGet global tool: {handoff.Command}");
                CommandErrorWriter.WriteStderr($"Release page: {handoff.Url}");
                CommandErrorWriter.WriteStderr($"Manual zip asset: {handoff.Asset} ({handoff.AssetUrl})");
            }
            return CommandExitCodes.FeatureUnavailable;
        }

        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            if (wantsJson)
            {
                Console.WriteLine(JsonSerializer.Serialize(
                    CreateUpgradeJsonResult(
                        result,
                        selectedChannel,
                        selectionSource,
                        includePrerelease,
                        verificationPolicy,
                        installAttempted: false,
                        installExitCode: null,
                        error: "unsupported_platform"),
                    jsonOptions));
            }
            else
            {
                CommandErrorWriter.WriteStderr("Error: cdidx upgrade currently requires a POSIX shell installer on Linux or macOS.");
                CommandErrorWriter.WriteStderr("Hint: download the latest release asset manually, or rerun install.sh from a shell environment.");
            }
            return CommandExitCodes.FeatureUnavailable;
        }

        var installDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!TryCheckInstallDirectoryWritable(installDir, out var installDirectoryError))
        {
            if (wantsJson)
            {
                Console.WriteLine(JsonSerializer.Serialize(
                    CreateUpgradeJsonResult(
                        result,
                        selectedChannel,
                        selectionSource,
                        includePrerelease,
                        verificationPolicy,
                        installAttempted: false,
                        installExitCode: null,
                        error: "install_directory_not_writable",
                        installDirectoryError: installDirectoryError),
                    jsonOptions));
            }
            else
            {
                CommandErrorWriter.WriteStderr($"Error: install directory is not writable: {installDir}");
                if (installDirectoryError != null)
                    CommandErrorWriter.WriteStderr($"Reason: {installDirectoryError}");
                WriteUpgradeInstallerTrustDiagnostic();
                CommandErrorWriter.WriteStderr("Hint: rerun with permissions that can write this directory, or reinstall cdidx into a per-user directory.");
            }
            return CommandExitCodes.UsageError;
        }

        string? scriptDirectory = null;
        string? scriptPath = null;
        string? checksumManifestPath = null;
        try
        {
            scriptDirectory = DataDirectorySecurity.CreateSensitiveTempDirectory(UpgradeInstallerDirectoryPrefix).FullName;
            scriptPath = Path.Combine(scriptDirectory, "install.sh");
            checksumManifestPath = Path.Combine(scriptDirectory, ReleaseChecksumAssetName);
            using (var client = UpgradeHttpClientFactory())
            {
                DownloadReleaseChecksumManifestToFileAsync(
                        client,
                        selectedReleaseTag,
                        checksumManifestPath,
                        TimeSpan.FromSeconds(20),
                        cancellationToken)
                    .GetAwaiter()
                    .GetResult();
                RequireUpgradeAssetProvenance(
                    checksumManifestPath,
                    ReleaseChecksumAssetName,
                    selectedReleaseTag,
                    verificationPolicy,
                    wantsJson,
                    cancellationToken,
                    out manifestProvenanceVerified);
                var checksumManifest = File.ReadAllText(checksumManifestPath, Encoding.UTF8);
                var expectedInstallerSha256 = GetReleaseAssetChecksum(checksumManifest, InstallerScriptAssetName);

                DownloadInstallerScriptAsync(
                        client,
                        selectedReleaseTag,
                        scriptPath,
                        TimeSpan.FromSeconds(20),
                        cancellationToken)
                    .GetAwaiter()
                    .GetResult();
                RequireUpgradeAssetProvenance(
                    scriptPath,
                    InstallerScriptAssetName,
                    selectedReleaseTag,
                    verificationPolicy,
                    wantsJson,
                    cancellationToken,
                    out installerProvenanceVerified);
                if (!wantsJson)
                    CommandErrorWriter.WriteStderr($"Verifying {InstallerScriptAssetName} checksum...");
                VerifyFileSha256(scriptPath, expectedInstallerSha256, InstallerScriptAssetName, cancellationToken);
                if (!wantsJson)
                    CommandErrorWriter.WriteStderr($"Verified {InstallerScriptAssetName} checksum.");
            }

            var startInfo = CreateInstallerProcessStartInfo(scriptPath, selectedReleaseTag, installDir);
            var installerResult = RunInstallerProcessDetailed(
                startInfo,
                InstallerRunTimeout,
                cancellationToken,
                suppressOutput: wantsJson);
            var installExitCode = installerResult.ExitCode;
            if (wantsJson)
            {
                var error = installExitCode == CommandExitCodes.Success
                    ? null
                    : $"installer_exit_code_{installExitCode.ToString(CultureInfo.InvariantCulture)}";
                Console.WriteLine(JsonSerializer.Serialize(
                    CreateUpgradeJsonResult(
                        result,
                        selectedChannel,
                        selectionSource,
                        includePrerelease,
                        verificationPolicy,
                        installAttempted: true,
                        installExitCode: installExitCode,
                        error: error,
                        installerResult: installerResult,
                        manifestProvenanceVerified: manifestProvenanceVerified,
                        installerProvenanceVerified: installerProvenanceVerified),
                    jsonOptions));
            }
            return installExitCode;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            if (wantsJson)
            {
                Console.WriteLine(JsonSerializer.Serialize(
                    CreateUpgradeJsonResult(
                        result,
                        selectedChannel,
                        selectionSource,
                        includePrerelease,
                        verificationPolicy,
                        installAttempted: false,
                        installExitCode: null,
                        error: ex.GetType().Name,
                        manifestProvenanceVerified: manifestProvenanceVerified,
                        installerProvenanceVerified: installerProvenanceVerified),
                    jsonOptions));
            }
            else
            {
                CommandErrorWriter.WriteStderr($"Error: upgrade failed before install.sh completed ({FormatSanitizedExceptionSummary(ex)}).");
                WriteUpgradeInstallerTrustDiagnostic();
                CommandErrorWriter.WriteStderr("Hint: rerun `install.sh` manually for the desired release.");
            }
            return CommandExitCodes.InstallError;
        }
        finally
        {
            if (scriptPath != null)
                TryDeleteUpgradeInstallerScript(scriptPath);
            if (scriptDirectory != null)
                TryDeleteUpgradeInstallerDirectory(scriptDirectory);
        }
    }
}
