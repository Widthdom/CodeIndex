using System.Globalization;
using System.Text.Json;
using CodeIndex.Models;

namespace CodeIndex.Cli;

internal static class UpdateChecker
{
    internal const string DisableEnvVar = "CDIDX_DISABLE_UPDATE_CHECK";
    internal const string DiagnosticsEnvVar = "CDIDX_UPDATE_CHECK_DIAGNOSTICS";
    private const string LatestReleaseUrl = "https://api.github.com/repos/Widthdom/CodeIndex/releases/latest";
    private const string ReleasesUrl = "https://api.github.com/repos/Widthdom/CodeIndex/releases?per_page=20";
    internal const long MaxLatestReleaseResponseBytes = 64 * 1024;
    internal const int MaxLatestReleaseJsonDepth = 16;
    internal const int MaxUpdateCheckCacheBytes = 8 * 1024;
    internal const int MaxUpdateCheckCacheJsonDepth = 8;
    internal const int MaxUpdateCheckCacheRootLength = 4096;
    private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(24);
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(2);
    internal static TimeProvider TimeProvider { get; set; } = System.TimeProvider.System;
    internal static Action<string>? CacheDiagnosticSinkForTesting { get; set; }

    internal static string? GetNewerReleaseHint(string currentVersion, CancellationToken cancellationToken = default)
        => GetNewerReleaseHint(
            currentVersion,
            ResolveDefaultCachePath(),
            TimeProvider.GetUtcNow(),
            FetchLatestReleaseTagAsync,
            cancellationToken);

    internal static UpdateCheckResult Check(string currentVersion, CancellationToken cancellationToken = default)
        => Check(
            currentVersion,
            ResolveDefaultCachePath(),
            TimeProvider.GetUtcNow(),
            FetchLatestReleaseTagAsync,
            cancellationToken);

    internal static UpdateCheckResult Check(
        string currentVersion,
        string cachePath,
        DateTimeOffset now,
        Func<CancellationToken, Task<string?>> fetchLatestReleaseTagAsync,
        CancellationToken cancellationToken = default)
    {
        if (IsDisabled())
            return CreateDisabledResult(currentVersion);

        var cache = ReadCache(cachePath);
        var fromCache = cache is not null && now - cache.CheckedAt < CacheTtl;
        string? latestTag = fromCache ? cache!.LatestTag : null;
        string? error = null;
        string? errorCategory = null;
        string? errorHint = null;

        if (!fromCache)
        {
            string? fetchedLatestTag = null;
            try
            {
                fetchedLatestTag = fetchLatestReleaseTagAsync(cancellationToken)
                    .GetAwaiter()
                    .GetResult();
                latestTag = string.IsNullOrWhiteSpace(fetchedLatestTag)
                    ? cache?.LatestTag
                    : fetchedLatestTag;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                latestTag = cache?.LatestTag;
                var failure = ClassifyFailure(ex);
                error = failure.Code;
                errorCategory = failure.Category;
                errorHint = failure.Hint;
            }

            if (!string.IsNullOrWhiteSpace(fetchedLatestTag))
                TryWriteCache(cachePath, new UpdateCheckCache(now, fetchedLatestTag));
        }

        return new UpdateCheckResult(
            currentVersion,
            latestTag,
            IsNewerRelease(latestTag, currentVersion),
            fromCache,
            error,
            errorCategory,
            errorHint);
    }

    internal static UpdateCheckResult CreateDisabledResult(string currentVersion)
        => new(
            currentVersion,
            null,
            false,
            false,
            "disabled",
            "configuration",
            "Unset CDIDX_DISABLE_UPDATE_CHECK to re-enable update checks.");

    internal static UpdateCheckFailure ClassifyFailure(Exception ex)
    {
        if (ex is UpdateCheckRateLimitException rateLimit)
        {
            return new(
                "rate_limited",
                "rate_limit",
                $"GitHub release API rate limit reached; retry after {rateLimit.NextRetryAt:O}. {rateLimit.Detail}");
        }

        if (ex is OperationCanceledException or TimeoutException)
        {
            return new(
                "timeout",
                "timeout",
                "Retry later, or set CDIDX_DISABLE_UPDATE_CHECK=1 to skip automatic update checks.");
        }

        if (ex is HttpRequestException or IOException)
        {
            return new(
                "network_failure",
                "network",
                "Check network access to GitHub releases, or set CDIDX_DISABLE_UPDATE_CHECK=1 to skip automatic update checks.");
        }

        if (ex is JsonException or InvalidDataException or NotSupportedException)
        {
            return new(
                "invalid_response",
                "response",
                "Retry later; release metadata could not be parsed within the safe response bounds.");
        }

        return new(
            "unexpected_failure",
            "unexpected",
            "Retry later; if this repeats, report the sanitized update-check error code.");
    }

    internal static string? GetNewerReleaseHint(
        string currentVersion,
        string cachePath,
        DateTimeOffset now,
        Func<CancellationToken, Task<string?>> fetchLatestReleaseTagAsync,
        CancellationToken cancellationToken = default)
    {
        if (IsDisabled())
            return null;

        var cache = ReadCache(cachePath);
        if (cache is not null
            && now - cache.CheckedAt < CacheTtl
            && cache.LatestTag is not null
            && IsNewerRelease(cache.LatestTag, currentVersion))
        {
            return FormatHint(cache.LatestTag);
        }

        if (cache is not null && now - cache.CheckedAt < CacheTtl)
            return null;

        string? latestTag = null;
        string? fetchedLatestTag = null;
        try
        {
            fetchedLatestTag = fetchLatestReleaseTagAsync(cancellationToken)
                .GetAwaiter()
                .GetResult();
            latestTag = string.IsNullOrWhiteSpace(fetchedLatestTag)
                ? cache?.LatestTag
                : fetchedLatestTag;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            latestTag = cache?.LatestTag;
        }

        if (!string.IsNullOrWhiteSpace(fetchedLatestTag))
            TryWriteCache(cachePath, new UpdateCheckCache(now, fetchedLatestTag));
        return IsNewerRelease(latestTag, currentVersion) ? FormatHint(latestTag!) : null;
    }

    internal static bool IsNewerRelease(string? latestTag, string currentVersion)
    {
        if (string.IsNullOrWhiteSpace(latestTag) || string.IsNullOrWhiteSpace(currentVersion))
            return false;

        return TryParseVersion(latestTag, out var latest)
            && TryParseVersion(currentVersion, out var current)
            && latest > current;
    }

    internal static bool IsDisabled()
    {
        var value = CdidxEnvironment.GetEnvironmentVariable(DisableEnvVar);
        return value is "1" || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
    }

    private static string FormatHint(string latestTag)
        => $"A newer release is available: {latestTag}";

    private static async Task<string?> FetchLatestReleaseTagAsync(CancellationToken cancellationToken)
    {
        using var client = GitHubHttpClientFactory.CreateDefaultHttpClient(RequestTimeout);
        return await FetchLatestReleaseTagAsync(client, RequestTimeout, cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<string?> FetchLatestReleaseTagAsync(
        HttpClient client,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var requestCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        requestCts.CancelAfter(timeout);
        using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseUrl);
        GitHubHttpClientFactory.ApplyDefaultHeaders(request.Headers);

        using var response = await client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            requestCts.Token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            await ThrowIfRateLimitedAsync(response, requestCts.Token).ConfigureAwait(false);
            return null;
        }

        return await ReadLatestReleaseTagAsync(response.Content, requestCts.Token).ConfigureAwait(false);
    }

    internal static async Task<string?> FetchLatestPrereleaseTagAsync(
        HttpClient client,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var requestCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        requestCts.CancelAfter(timeout);
        using var request = new HttpRequestMessage(HttpMethod.Get, ReleasesUrl);
        GitHubHttpClientFactory.ApplyDefaultHeaders(request.Headers);

        using var response = await client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            requestCts.Token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            await ThrowIfRateLimitedAsync(response, requestCts.Token).ConfigureAwait(false);
            return null;
        }

        return await ReadLatestPrereleaseTagAsync(response.Content, requestCts.Token).ConfigureAwait(false);
    }

    private static async Task ThrowIfRateLimitedAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var nextRetryAt = GitHubIssueReporter.GetRateLimitRetryAt(
            response,
            TimeProvider.GetUtcNow().UtcDateTime);
        if (nextRetryAt is null)
            return;

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var errorBody = await GitHubIssueReporter.ReadBoundedApiErrorBodyAsync(stream, cancellationToken)
            .ConfigureAwait(false);
        throw new UpdateCheckRateLimitException(
            nextRetryAt.Value,
            GitHubIssueReporter.BuildRateLimitErrorDetail((int)response.StatusCode, errorBody, nextRetryAt.Value));
    }

    internal static async Task<string?> ReadLatestReleaseTagAsync(HttpContent content, CancellationToken cancellationToken)
    {
        var payload = await BoundedHttpContentReader.ReadAsByteArrayAsync(
            content,
            MaxLatestReleaseResponseBytes,
            cancellationToken).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(
            payload.AsMemory(),
            new JsonDocumentOptions { MaxDepth = MaxLatestReleaseJsonDepth });
        return doc.RootElement.TryGetProperty("tag_name", out var tag)
            ? tag.GetString()
            : null;
    }

    internal static async Task<string?> ReadLatestPrereleaseTagAsync(HttpContent content, CancellationToken cancellationToken)
    {
        var payload = await BoundedHttpContentReader.ReadAsByteArrayAsync(
            content,
            MaxLatestReleaseResponseBytes,
            cancellationToken).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(
            payload.AsMemory(),
            new JsonDocumentOptions { MaxDepth = MaxLatestReleaseJsonDepth });
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
            return null;

        foreach (var release in doc.RootElement.EnumerateArray())
        {
            if (release.ValueKind != JsonValueKind.Object)
                continue;

            var isDraft = release.TryGetProperty("draft", out var draftElement)
                && draftElement.ValueKind == JsonValueKind.True;
            if (isDraft)
                continue;

            var isPrerelease = release.TryGetProperty("prerelease", out var prereleaseElement)
                && prereleaseElement.ValueKind == JsonValueKind.True;
            if (!isPrerelease)
                continue;

            if (release.TryGetProperty("tag_name", out var tagElement))
                return tagElement.GetString();
        }

        return null;
    }

    internal static string ResolveDefaultCachePath()
    {
        var xdgCacheHome = CdidxEnvironment.GetEnvironmentVariable("XDG_CACHE_HOME");
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        if (TryResolveCacheRoot(xdgCacheHome, "XDG_CACHE_HOME", out var xdgRoot))
            return Path.Combine(xdgRoot, "cdidx", "update-check.json");

        if (TryResolveCacheRoot(home, "user profile", out var homeRoot))
            return Path.Combine(homeRoot, ".cache", "cdidx", "update-check.json");

        if (TryResolveCacheRoot(localAppData, "local application data", out var localAppDataRoot))
            return Path.Combine(localAppDataRoot, "cdidx", "update-check.json");

        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "cdidx", "cache"));
        return Path.Combine(root, "update-check.json");
    }

    private static bool TryResolveCacheRoot(string? value, string source, out string root)
    {
        root = string.Empty;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        if (value.Length > MaxUpdateCheckCacheRootLength
            || value.Any(char.IsControl)
            || !Path.IsPathFullyQualified(value))
        {
            ReportCacheDiagnostic(
                "cache_root_invalid",
                value,
                new InvalidOperationException($"{source} must be a fully qualified cache root without control characters."));
            return false;
        }

        try
        {
            root = Path.GetFullPath(value);
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException
            or IOException
            or NotSupportedException
            or PathTooLongException)
        {
            ReportCacheDiagnostic("cache_root_invalid", value, ex);
            return false;
        }
    }

    private static UpdateCheckCache? ReadCache(string cachePath)
    {
        try
        {
            if (!File.Exists(cachePath))
                return null;

            var text = DataDirectorySecurity.ReadTextWithinLimit(cachePath, MaxUpdateCheckCacheBytes, FileShare.ReadWrite);
            if (text is null)
                return null;

            using var doc = JsonDocument.Parse(
                text,
                new JsonDocumentOptions { MaxDepth = MaxUpdateCheckCacheJsonDepth });
            var root = doc.RootElement;
            if (!root.TryGetProperty("checked_at", out var checkedAtElement)
                || !DateTimeOffset.TryParse(
                    checkedAtElement.GetString(),
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out var checkedAt))
            {
                return null;
            }

            var latestTag = root.TryGetProperty("latest_tag", out var tagElement)
                ? tagElement.GetString()
                : null;
            return new UpdateCheckCache(checkedAt, latestTag);
        }
        catch (Exception ex)
        {
            ReportCacheDiagnostic("cache_read_failed", cachePath, ex);
            return null;
        }
    }

    private static void TryWriteCache(string cachePath, UpdateCheckCache cache)
    {
        try
        {
            var directory = Path.GetDirectoryName(cachePath);
            if (!string.IsNullOrWhiteSpace(directory))
                DataDirectorySecurity.CreateSensitiveDirectory(directory);

            var payload = new
            {
                checked_at = cache.CheckedAt.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
                latest_tag = cache.LatestTag,
            };
            AtomicFileWriter.WriteJson(cachePath, payload, applyFileMode: DataDirectorySecurity.ApplyPrivateFileMode);
        }
        catch (Exception ex)
        {
            ReportCacheDiagnostic("cache_write_failed", cachePath, ex);
        }
    }

    private static void ReportCacheDiagnostic(string code, string cachePath, Exception ex)
    {
        if (!ShouldEmitCacheDiagnostics())
            return;

        var message =
            $"update_check_cache_diagnostic code={code} " +
            $"path={ConsoleUi.FormatBoundedValue(cachePath)} " +
            $"error={CommandErrorWriter.FormatSanitizedException(ex)}";
        var sink = CacheDiagnosticSinkForTesting;
        if (sink != null)
            sink(message);
        else
            CommandErrorWriter.WriteStderr(message);
    }

    private static bool ShouldEmitCacheDiagnostics()
    {
        var value = CdidxEnvironment.GetEnvironmentVariable(DiagnosticsEnvVar)?.Trim();
        return value is not null
            && (string.Equals(value, "1", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase));
    }

    private static bool TryParseVersion(string value, out Version version)
    {
        var trimmed = value.Trim();
        if (trimmed.StartsWith('v') || trimmed.StartsWith('V'))
            trimmed = trimmed[1..];

        var prereleaseStart = trimmed.IndexOfAny(['-', '+']);
        if (prereleaseStart >= 0)
            trimmed = trimmed[..prereleaseStart];

        return Version.TryParse(trimmed, out version!);
    }

    private sealed record UpdateCheckCache(DateTimeOffset CheckedAt, string? LatestTag);

    internal readonly record struct UpdateCheckFailure(string Code, string Category, string Hint);

    private sealed class UpdateCheckRateLimitException : HttpRequestException
    {
        internal UpdateCheckRateLimitException(DateTime nextRetryAt, string detail)
            : base("GitHub release API rate limit response.")
        {
            NextRetryAt = nextRetryAt;
            Detail = detail;
        }

        internal DateTime NextRetryAt { get; }

        internal string Detail { get; }
    }
}
