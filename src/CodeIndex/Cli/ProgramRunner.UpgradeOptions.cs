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
    private static int WriteUpgradeUsageError(
        string message,
        bool wantsJson,
        JsonSerializerOptions jsonOptions)
        => CommandErrorWriter.WriteJsonOrHuman(
            wantsJson,
            jsonOptions,
            message,
            CommandExitCodes.UsageError,
            "use `cdidx upgrade [--check-only] [--channel stable|latest|prerelease] [--prerelease] [--version vX.Y.Z]`.");

    private static bool TryApplyUpgradeChannel(
        string rawChannel,
        out string selectedChannel,
        out bool includePrerelease,
        out string error)
    {
        selectedChannel = "stable";
        includePrerelease = false;
        error = string.Empty;

        switch (rawChannel.Trim().ToLowerInvariant())
        {
            case "stable":
                selectedChannel = "stable";
                return true;
            case "latest":
                selectedChannel = "latest";
                return true;
            case "prerelease":
            case "preview":
                selectedChannel = "prerelease";
                includePrerelease = true;
                return true;
            default:
                error = $"unsupported upgrade channel '{rawChannel}'.";
                return false;
        }
    }

    private static bool TryNormalizeReleaseTag(string rawVersion, out string? normalizedVersion, out string error)
    {
        normalizedVersion = null;
        error = string.Empty;

        var trimmed = rawVersion.Trim();
        if (trimmed.Length == 0)
        {
            error = "--version requires a non-empty release tag.";
            return false;
        }

        normalizedVersion = trimmed[0] is 'v' or 'V'
            ? "v" + trimmed[1..]
            : "v" + trimmed;
        if (!IsValidUpgradeReleaseTag(normalizedVersion))
        {
            error = "--version must be a release tag shaped like vX.Y.Z or vX.Y.Z-prerelease.";
            normalizedVersion = null;
            return false;
        }

        return true;
    }

    internal static bool IsValidUpgradeReleaseTag(string releaseTag)
    {
        if (string.IsNullOrWhiteSpace(releaseTag) || releaseTag[0] != 'v')
            return false;

        var rest = releaseTag[1..];
        var prereleaseStart = rest.IndexOf('-');
        var core = prereleaseStart >= 0 ? rest[..prereleaseStart] : rest;
        var prerelease = prereleaseStart >= 0 ? rest[(prereleaseStart + 1)..] : null;
        var parts = core.Split('.');
        if (parts.Length != 3 || parts.Any(part => part.Length == 0 || !part.All(char.IsDigit)))
            return false;

        if (prerelease == null)
            return true;

        var identifiers = prerelease.Split('.');
        return identifiers.Length > 0
            && identifiers.All(identifier =>
                identifier.Length > 0
                && identifier.All(ch => char.IsAsciiLetterOrDigit(ch) || ch == '-'));
    }

    private static bool IsPrereleaseTag(string releaseTag)
        => releaseTag.Contains('-', StringComparison.Ordinal);

    private static void WriteUpgradeInstallerTrustDiagnostic()
        => CommandErrorWriter.WriteStderr($"Installer verification: {UpgradeInstallerVerification}; {UpgradeInstallerTrustBoundary}");

    internal static UpgradeHandoff CreateWindowsUpgradeHandoff(string releaseTag, Architecture processArchitecture)
    {
        var normalizedTag = releaseTag.Trim();
        var nugetVersion = normalizedTag.Length > 0 && (normalizedTag[0] is 'v' or 'V')
            ? normalizedTag[1..]
            : normalizedTag;
        var asset = processArchitecture == Architecture.Arm64
            ? "CodeIndex-win-arm64.zip"
            : "CodeIndex-win-x64.zip";
        return new UpgradeHandoff(
            $"dotnet tool update -g cdidx --version {nugetVersion}",
            BuildReleasePageUrl(normalizedTag),
            asset,
            BuildReleaseAssetUrl(normalizedTag, asset));
    }

    private static UpdateCheckResult CheckLatestPrerelease(string appVersion, CancellationToken cancellationToken)
    {
        if (UpdateChecker.IsDisabled())
            return UpdateChecker.CreateDisabledResult(appVersion);

        try
        {
            using var client = UpgradeHttpClientFactory();
            var tag = UpdateChecker.FetchLatestPrereleaseTagAsync(
                    client,
                    TimeSpan.FromSeconds(20),
                    cancellationToken)
                .GetAwaiter()
                .GetResult();
            return new UpdateCheckResult(
                appVersion,
                tag,
                UpdateChecker.IsNewerRelease(tag, appVersion),
                FromCache: false,
                Error: tag is null ? "prerelease_not_found" : null,
                ErrorCategory: tag is null ? "release_metadata" : null,
                ErrorHint: tag is null
                    ? "Retry later, omit --prerelease, or pass --version to use a known prerelease tag."
                    : null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            var failure = UpdateChecker.ClassifyFailure(ex);
            return new UpdateCheckResult(
                appVersion,
                null,
                false,
                FromCache: false,
                Error: failure.Code,
                ErrorCategory: failure.Category,
                ErrorHint: failure.Hint);
        }
    }
}
