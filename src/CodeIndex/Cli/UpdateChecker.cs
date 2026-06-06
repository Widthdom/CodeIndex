using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using CodeIndex.Models;

namespace CodeIndex.Cli;

internal static class UpdateChecker
{
    internal const string DisableEnvVar = "CDIDX_DISABLE_UPDATE_CHECK";
    private const string LatestReleaseUrl = "https://api.github.com/repos/Widthdom/CodeIndex/releases/latest";
    internal const long MaxLatestReleaseResponseBytes = 64 * 1024;
    internal const int MaxLatestReleaseJsonDepth = 16;
    internal const int MaxUpdateCheckCacheBytes = 8 * 1024;
    internal const int MaxUpdateCheckCacheJsonDepth = 8;
    private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(24);
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(2);

    internal static string? GetNewerReleaseHint(string currentVersion, CancellationToken cancellationToken = default)
        => GetNewerReleaseHint(
            currentVersion,
            ResolveDefaultCachePath(),
            DateTimeOffset.UtcNow,
            FetchLatestReleaseTagAsync,
            cancellationToken);

    internal static UpdateCheckResult Check(string currentVersion, CancellationToken cancellationToken = default)
        => Check(
            currentVersion,
            ResolveDefaultCachePath(),
            DateTimeOffset.UtcNow,
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
            return new UpdateCheckResult(currentVersion, null, false, false, "disabled");

        var cache = ReadCache(cachePath);
        var fromCache = cache is not null && now - cache.CheckedAt < CacheTtl;
        string? latestTag = fromCache ? cache!.LatestTag : null;
        string? error = null;

        if (!fromCache)
        {
            try
            {
                latestTag = fetchLatestReleaseTagAsync(cancellationToken)
                    .GetAwaiter()
                    .GetResult();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                latestTag = cache?.LatestTag;
                error = ex.GetType().Name;
            }

            TryWriteCache(cachePath, new UpdateCheckCache(now, latestTag));
        }

        return new UpdateCheckResult(
            currentVersion,
            latestTag,
            IsNewerRelease(latestTag, currentVersion),
            fromCache,
            error);
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
        try
        {
            latestTag = fetchLatestReleaseTagAsync(cancellationToken)
                .GetAwaiter()
                .GetResult();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            latestTag = cache?.LatestTag;
        }

        TryWriteCache(cachePath, new UpdateCheckCache(now, latestTag));
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

    private static bool IsDisabled()
    {
        var value = Environment.GetEnvironmentVariable(DisableEnvVar);
        return value is "1" || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
    }

    private static string FormatHint(string latestTag)
        => $"A newer release is available: {latestTag}";

    private static async Task<string?> FetchLatestReleaseTagAsync(CancellationToken cancellationToken)
    {
        using var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
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
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("cdidx", ConsoleUi.LoadVersion()));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

        using var response = await client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            requestCts.Token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            return null;

        return await ReadLatestReleaseTagAsync(response.Content, requestCts.Token).ConfigureAwait(false);
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

    private static string ResolveDefaultCachePath()
    {
        var xdgCacheHome = Environment.GetEnvironmentVariable("XDG_CACHE_HOME");
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var root = !string.IsNullOrWhiteSpace(xdgCacheHome)
            ? Path.Combine(xdgCacheHome, "cdidx")
            : !string.IsNullOrWhiteSpace(home)
                ? Path.Combine(home, ".cache", "cdidx")
                : Path.Combine(Path.GetTempPath(), "cdidx");
        return Path.Combine(root, "update-check.json");
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
        catch
        {
            return null;
        }
    }

    private static void TryWriteCache(string cachePath, UpdateCheckCache cache)
    {
        try
        {
            var directory = Path.GetDirectoryName(cachePath);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            var payload = new
            {
                checked_at = cache.CheckedAt.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
                latest_tag = cache.LatestTag,
            };
            AtomicFileWriter.WriteJson(cachePath, payload);
        }
        catch
        {
        }
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
}
