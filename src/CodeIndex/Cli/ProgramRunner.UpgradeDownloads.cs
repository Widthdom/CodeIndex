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
    private static UpgradeJsonResult CreateUpgradeJsonResult(
        UpdateCheckResult result,
        string selectedChannel,
        string selectionSource,
        bool includePrerelease,
        string verificationPolicy,
        bool installAttempted,
        int? installExitCode,
        string? error,
        UpgradeHandoff? handoff = null,
        InstallerProcessResult? installerResult = null,
        string? installDirectoryError = null,
        bool? manifestProvenanceVerified = null,
        bool? installerProvenanceVerified = null)
        => new(
            result.CurrentVersion,
            result.LatestVersion,
            result.UpdateAvailable,
            result.FromCache,
            result.LatestVersion,
            selectedChannel,
            selectionSource,
            includePrerelease,
            error ?? result.Error,
            error is null ? result.ErrorCategory : null,
            error is null ? result.ErrorHint : null,
            installAttempted,
            installExitCode,
            installExitCode is null ? null : installExitCode == CommandExitCodes.Success,
            handoff?.Command,
            handoff?.Url,
            handoff?.Asset,
            handoff?.AssetUrl,
            result.LatestVersion is null ? null : UpgradeInstallerVerification,
            result.LatestVersion is null ? null : UpgradeInstallerTrustBoundary,
            installerResult is { ExitCode: not CommandExitCodes.Success } ? installerResult.StdoutTail : null,
            installerResult is { ExitCode: not CommandExitCodes.Success } ? installerResult.StderrTail : null,
            installerResult is { ExitCode: not CommandExitCodes.Success } ? installerResult.OutputTruncated : null,
            installDirectoryError,
            result.LatestVersion is null ? null : verificationPolicy,
            manifestProvenanceVerified,
            installerProvenanceVerified,
            GetUpgradeVerificationStatus(
                result.LatestVersion,
                verificationPolicy,
                manifestProvenanceVerified,
                installerProvenanceVerified),
            string.Equals(verificationPolicy, "compat", StringComparison.Ordinal)
                && (manifestProvenanceVerified == false || installerProvenanceVerified == false)
                ? "compat_provenance_bypass"
                : null);

    private static string? GetUpgradeVerificationStatus(
        string? selectedVersion,
        string verificationPolicy,
        bool? manifestProvenanceVerified,
        bool? installerProvenanceVerified)
    {
        if (selectedVersion is null)
            return null;
        if (manifestProvenanceVerified == true && installerProvenanceVerified == true)
            return "verified";
        if (manifestProvenanceVerified == false || installerProvenanceVerified == false)
            return string.Equals(verificationPolicy, "compat", StringComparison.Ordinal)
                ? "compat_bypass"
                : "verification_failed";
        return "not_attempted";
    }

    internal static string BuildReleasePageUrl(string releaseTag)
        => string.Format(
            CultureInfo.InvariantCulture,
            ReleasePageUrlTemplate,
            Uri.EscapeDataString(releaseTag.Trim()));

    internal static string BuildInstallerScriptUrl(string releaseTag)
        => BuildReleaseAssetUrl(releaseTag, InstallerScriptAssetName);

    internal static string BuildReleaseAssetUrl(string releaseTag, string assetName)
        => string.Format(
            CultureInfo.InvariantCulture,
            ReleaseAssetUrlTemplate,
            Uri.EscapeDataString(releaseTag.Trim()),
            Uri.EscapeDataString(assetName));

    private static HttpClient CreateUpgradeHttpClient()
        => GitHubHttpClientFactory.CreateReleaseDownloadHttpClient(TimeSpan.FromSeconds(20));

    internal static async Task<string> DownloadReleaseChecksumManifestAsync(
        HttpClient client,
        string releaseTag,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var downloadScope = OperationTimeoutScope.Create(
            OperationTimeoutCategories.UpgradeDownload,
            timeout,
            cancellationToken);
        using var response = await GitHubHttpClientFactory.SendWithRetryAsync(
            client,
            () =>
            {
                var request = new HttpRequestMessage(HttpMethod.Get, BuildReleaseAssetUrl(releaseTag, ReleaseChecksumAssetName));
                GitHubHttpClientFactory.ApplyReleaseDownloadHeaders(request.Headers);
                return request;
            },
            HttpCompletionOption.ResponseHeadersRead,
            downloadScope.Token).ConfigureAwait(false);
        await GitHubHttpClientFactory.EnsureSuccessStatusCodeWithBoundedDiagnosticsAsync(
            response,
            ReleaseChecksumAssetName,
            downloadScope.Token).ConfigureAwait(false);
        var bytes = await BoundedHttpContentReader.ReadAsByteArrayAsync(
            response.Content,
            MaxReleaseChecksumBytes,
            downloadScope.Token).ConfigureAwait(false);
        return Encoding.UTF8.GetString(bytes);
    }

    internal static async Task DownloadReleaseChecksumManifestToFileAsync(
        HttpClient client,
        string releaseTag,
        string manifestPath,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var downloadScope = OperationTimeoutScope.Create(
            OperationTimeoutCategories.UpgradeDownload,
            timeout,
            cancellationToken);
        using var response = await GitHubHttpClientFactory.SendWithRetryAsync(
            client,
            () =>
            {
                var request = new HttpRequestMessage(HttpMethod.Get, BuildReleaseAssetUrl(releaseTag, ReleaseChecksumAssetName));
                GitHubHttpClientFactory.ApplyReleaseDownloadHeaders(request.Headers);
                return request;
            },
            HttpCompletionOption.ResponseHeadersRead,
            downloadScope.Token).ConfigureAwait(false);
        await GitHubHttpClientFactory.EnsureSuccessStatusCodeWithBoundedDiagnosticsAsync(
            response,
            ReleaseChecksumAssetName,
            downloadScope.Token).ConfigureAwait(false);
        await BoundedHttpContentReader.WriteToPrivateFileAsync(
            response.Content,
            manifestPath,
            MaxReleaseChecksumBytes,
            downloadScope.Token).ConfigureAwait(false);
    }

    internal static string GetReleaseAssetChecksum(string checksumManifest, string assetName)
    {
        foreach (var rawLine in checksumManifest.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (line.Length < 66)
                continue;

            var checksum = line[..64];
            if (!IsSha256Hex(checksum) || !char.IsWhiteSpace(line[64]))
                continue;

            var fileName = line[65..].TrimStart();
            if (fileName.StartsWith('*'))
                fileName = fileName[1..];
            if (string.Equals(fileName, assetName, StringComparison.Ordinal))
                return checksum.ToLowerInvariant();
        }

        throw new InvalidDataException($"Release checksum manifest does not contain {assetName}.");
    }

    internal static void VerifyFileSha256(
        string path,
        string expectedSha256Hex,
        string assetName,
        CancellationToken cancellationToken = default)
    {
        if (!IsSha256Hex(expectedSha256Hex))
            throw new InvalidDataException($"Release checksum for {assetName} is not a valid SHA-256 digest.");

        using var stream = BoundedFile.OpenReadForHash(path);
        var actual = Sha256StreamHasher.ComputeHex(stream, cancellationToken);
        if (!string.Equals(actual, expectedSha256Hex, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(
                $"Downloaded {assetName} checksum mismatch: expected {expectedSha256Hex}, got {actual}.");
    }

    private static bool IsSha256Hex(string value)
    {
        if (value.Length != 64)
            return false;

        foreach (var ch in value)
        {
            if (!Uri.IsHexDigit(ch))
                return false;
        }

        return true;
    }

    internal static async Task DownloadInstallerScriptAsync(
        HttpClient client,
        string releaseTag,
        string scriptPath,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var downloadScope = OperationTimeoutScope.Create(
            OperationTimeoutCategories.UpgradeDownload,
            timeout,
            cancellationToken);
        using var response = await GitHubHttpClientFactory.SendWithRetryAsync(
            client,
            () =>
            {
                var request = new HttpRequestMessage(HttpMethod.Get, BuildInstallerScriptUrl(releaseTag));
                GitHubHttpClientFactory.ApplyReleaseDownloadHeaders(request.Headers);
                return request;
            },
            HttpCompletionOption.ResponseHeadersRead,
            downloadScope.Token).ConfigureAwait(false);
        await GitHubHttpClientFactory.EnsureSuccessStatusCodeWithBoundedDiagnosticsAsync(
            response,
            InstallerScriptAssetName,
            downloadScope.Token).ConfigureAwait(false);
        await BoundedHttpContentReader.WriteToPrivateFileAsync(
            response.Content,
            scriptPath,
            MaxInstallerScriptBytes,
            downloadScope.Token).ConfigureAwait(false);
    }
}
