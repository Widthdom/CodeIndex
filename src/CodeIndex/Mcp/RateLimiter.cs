using System.Globalization;
using CodeIndex.Cli;

namespace CodeIndex.Mcp;

/// <summary>
/// Token-bucket rate limiter keyed by (partition, caller). MCP tool calls consume tokens from
/// their required partitions; over-quota callers receive a structured `-32000` JSON-RPC error
/// including `retry_after_ms` (issues #1560 and #4547).
/// (partition, caller) ごとのトークンバケット型レート制限。MCP ツール呼び出しは必須 partition の
/// token を消費し、超過時は `retry_after_ms` を含む `-32000` JSON-RPC error を返す（#1560、#4547）。
/// </summary>
internal sealed class RateLimiter
{
    internal const long MaxDiagnosticIntervalMilliseconds = int.MaxValue;
    internal const string ToolsCallPreValidationBucketName = "(tools/call pre-validation)";
    internal const string InvalidToolBucketName = "(invalid tools/call)";
    private readonly object _gate = new();
    private readonly Dictionary<RateLimiterBucketKey, TokenBucket> _buckets = new();
    private readonly RateLimiterOptions _options;
    private readonly Func<DateTimeOffset> _clock;
    private DateTimeOffset _nextPruneAt = DateTimeOffset.MinValue;
    private DateTimeOffset? _lastPruneAt;
    private int _lastPrunedBucketCount;
    private int _bucketLimitRejectionCount;

    public RateLimiter(RateLimiterOptions options, Func<DateTimeOffset>? clock = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        if (_options.MaxBucketCount < 1)
            throw new ArgumentOutOfRangeException(nameof(options), _options.MaxBucketCount, "Rate limiter bucket cap must be at least 1.");
        if (_options.BucketIdleTtl <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(options), _options.BucketIdleTtl, "Rate limiter bucket idle TTL must be positive.");
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public RateLimiterOptions Options => _options;

    internal int BucketCount
    {
        get
        {
            lock (_gate)
            {
                return _buckets.Count;
            }
        }
    }

    /// <summary>
    /// Try to take one token from the (tool, caller) bucket. When rate limiting is disabled
    /// (the env-driven default), the call is always allowed and the limiter performs no
    /// bookkeeping. When enabled, the bucket refills at `RefillTokensPerSecond` up to
    /// `BurstCapacity` and a denied call returns the smallest `retry_after_ms` needed for the
    /// next token to be available.
    /// (tool, caller) のバケットから 1 トークン取得する。レート制限が無効（環境変数未指定の既定）
    /// なら常に許可し、有効時は `RefillTokensPerSecond` で `BurstCapacity` まで補充されるバケットから
    /// 引き、不足時は次トークン到達までの `retry_after_ms` を返す。
    /// </summary>
    public RateLimiterDecision TryAcquire(string tool, string caller)
    {
        if (!_options.IsEnabled)
            return RateLimiterDecision.Allow;

        var key = BuildKey(tool, caller);
        lock (_gate)
        {
            // Read the decision timestamp under the same lock as bucket mutation. A caller
            // waiting on the gate must not receive retry timing based on a stale pre-wait
            // timestamp, and concurrent calls must observe clock values in decision order.
            // bucket 更新と同じ lock 内で判定時刻を読む。gate 待機前の古い時刻で retry を
            // 算出せず、並行 call が判定順に clock 値を観測するようにする。
            var now = _clock();
            PruneIdleBuckets(now);
            return TryAcquireLocked(key, now);
        }
    }

    /// <summary>
    /// Acquire a required caller-wide bucket and, when supplied, a secondary known-tool
    /// bucket under one lock. If the secondary layer rejects after the primary token was
    /// charged, retry timing covers the earliest point at which both layers and bucket-cap
    /// capacity can admit the retry (#4547).
    /// caller-wide の必須 bucket と、指定時は secondary known-tool bucket を 1 lock 内で
    /// 取得する。primary token 消費後に secondary layer が拒否した場合も、両 layer と
    /// bucket cap capacity が再試行を許可できる最短時刻を retry として返す（#4547）。
    /// </summary>
    public RateLimiterDecision TryAcquireHierarchy(string primaryTool, string? secondaryTool, string caller)
    {
        if (!_options.IsEnabled)
            return RateLimiterDecision.Allow;

        var primaryKey = BuildKey(primaryTool, caller);
        var secondaryKey = secondaryTool is null ? (RateLimiterBucketKey?)null : BuildKey(secondaryTool, caller);
        if (secondaryKey == primaryKey)
            secondaryKey = null;

        lock (_gate)
        {
            var now = _clock();
            PruneIdleBuckets(now);
            // The advertised hierarchy boundary may be an exact requested-bucket expiry.
            // Remove those keys eagerly even when the amortized global sweep is not due, so
            // the retry observes the same reset modeled by the recovery calculation (#4547).
            // 通知した hierarchy 境界が要求 bucket の正確な expiry の場合がある。償却された
            // global sweep の時刻前でも対象キーを除去し、回復計算と同じ reset を観測させる。
            RemoveRequestedBucketIfExpired(primaryKey, now);
            if (secondaryKey is { } requestedSecondaryKey)
                RemoveRequestedBucketIfExpired(requestedSecondaryKey, now);

            var primaryDecision = TryAcquireLocked(primaryKey, now);
            if (!primaryDecision.Allowed || secondaryKey is null)
                return primaryDecision;

            var secondaryDecision = TryAcquireLocked(secondaryKey.Value, now);
            if (secondaryDecision.Allowed)
                return secondaryDecision;

            var hierarchyRetryAfterMs = ComputeHierarchyRetryAfterMilliseconds(
                now,
                primaryKey,
                secondaryKey.Value,
                secondaryDecision.RetryAfterMs);
            return RateLimiterDecision.Deny(hierarchyRetryAfterMs);
        }
    }

    private RateLimiterDecision TryAcquireLocked(RateLimiterBucketKey key, DateTimeOffset now)
    {
        if (!_buckets.TryGetValue(key, out var bucket))
        {
            if (_buckets.Count >= _options.MaxBucketCount)
            {
                // The scheduled sweep is an amortization detail, not a reason to keep an
                // already-expired bucket when capacity is exhausted. Re-check expiry now
                // before denying a legitimate new key (#4547).
                // 定期 sweep は償却のための実装詳細。容量枯渇時に期限切れ bucket を保持して
                // 正常な新規キーを拒否しないよう、拒否前に現在時刻で再確認する（#4547）。
                if (_lastPruneAt != now)
                    PruneIdleBuckets(now, force: true);
                if (_buckets.Count >= _options.MaxBucketCount)
                {
                    _bucketLimitRejectionCount++;
                    return RateLimiterDecision.Deny(ComputeBucketLimitRetryAfterMilliseconds(now));
                }
            }

            bucket = new TokenBucket(_options.BurstCapacity, now);
            _buckets[key] = bucket;
        }
        return bucket.TryAcquire(now, _options.RefillTokensPerSecond, _options.BurstCapacity);
    }

    internal RateLimiterDiagnostics SnapshotDiagnostics()
    {
        lock (_gate)
        {
            var now = _clock();
            return new RateLimiterDiagnostics(
                BucketCount: _buckets.Count,
                BucketIdleTtlSeconds: Math.Ceiling(_options.BucketIdleTtl.TotalSeconds),
                MaxBucketCount: _options.MaxBucketCount,
                BucketLimitRejectionCount: _bucketLimitRejectionCount,
                NextPruneInMs: ComputeNextPruneInMilliseconds(now, _nextPruneAt),
                LastPruneAgeMs: _lastPruneAt.HasValue ? ComputeElapsedMilliseconds(now, _lastPruneAt.Value) : null,
                LastPrunedBucketCount: _lastPrunedBucketCount);
        }
    }

    internal static RateLimiterBucketKey BuildKey(string tool, string caller) => new(tool, caller);

    private long ComputeHierarchyRetryAfterMilliseconds(
        DateTimeOffset now,
        RateLimiterBucketKey primaryKey,
        RateLimiterBucketKey secondaryKey,
        long fallbackRetryAfterMs)
    {
        // A secondary denial can happen after the primary token has already been charged.
        // Evaluate every state-change boundary (requested-token refill and idle expiry) and
        // return the first millisecond at which a no-intervening-traffic retry can acquire
        // both requested partitions without exceeding the bucket cap (#4547).
        // secondary 拒否時には primary token が消費済みの場合がある。要求 bucket の refill と
        // 全 idle expiry の各境界を評価し、途中 traffic が無い場合に両 partition を cap 内で
        // 取得できる最初の millisecond を返す（#4547）。
        var candidateDelays = new SortedSet<long>();
        foreach (var bucket in _buckets.Values)
            candidateDelays.Add(ComputeDelayUntilIdleExpiryMilliseconds(now, bucket));

        AddNextTokenCandidate(primaryKey);
        AddNextTokenCandidate(secondaryKey);

        // Eligibility is monotonic without intervening traffic: tokens only refill, idle
        // buckets only expire, and requested expired buckets are modeled as fresh. Binary
        // search therefore avoids an O(bucket-count^2) scan on an abuse-sensitive path.
        // 途中 traffic が無ければ token は補充され、idle bucket は減るだけで、expired request
        // bucket は fresh として扱うため許可可否は単調になる。二分探索で濫用対象経路の
        // O(bucket-count^2) scan を避ける。
        var orderedCandidateDelays = candidateDelays.ToArray();
        var firstAllowedIndex = -1;
        var low = 0;
        var high = orderedCandidateDelays.Length - 1;
        while (low <= high)
        {
            var middle = low + ((high - low) / 2);
            var delayMs = orderedCandidateDelays[middle];
            var candidateTime = AddMillisecondsClamped(now, Math.Max(1, delayMs));
            if (CanAcquireHierarchyAt(candidateTime, primaryKey, secondaryKey))
            {
                firstAllowedIndex = middle;
                high = middle - 1;
            }
            else
            {
                low = middle + 1;
            }
        }
        if (firstAllowedIndex >= 0)
            return Math.Max(1, orderedCandidateDelays[firstAllowedIndex]);

        return Math.Max(MaxDiagnosticIntervalMilliseconds, fallbackRetryAfterMs);

        void AddNextTokenCandidate(RateLimiterBucketKey key)
        {
            if (_buckets.TryGetValue(key, out var bucket)
                && bucket.TryGetNextTokenDelayMilliseconds(
                    now,
                    _options.RefillTokensPerSecond,
                    _options.BurstCapacity) is { } delayMs)
            {
                candidateDelays.Add(delayMs);
            }
        }
    }

    private bool CanAcquireHierarchyAt(
        DateTimeOffset candidateTime,
        RateLimiterBucketKey primaryKey,
        RateLimiterBucketKey secondaryKey)
    {
        var retainedBucketCount = 0;
        foreach (var bucket in _buckets.Values)
        {
            if (!IsExpiredAt(bucket, candidateTime))
                retainedBucketCount++;
        }

        var primaryRetained = IsRetained(primaryKey, candidateTime, out var primaryBucket);
        var secondaryRetained = IsRetained(secondaryKey, candidateTime, out var secondaryBucket);
        var requiredNewBuckets = (primaryRetained ? 0 : 1) + (secondaryRetained ? 0 : 1);
        if (retainedBucketCount + requiredNewBuckets > _options.MaxBucketCount)
            return false;
        if ((!primaryRetained || primaryBucket!.WouldAllowAt(candidateTime, _options.RefillTokensPerSecond, _options.BurstCapacity))
            && (!secondaryRetained || secondaryBucket!.WouldAllowAt(candidateTime, _options.RefillTokensPerSecond, _options.BurstCapacity)))
        {
            return _options.BurstCapacity >= 1.0 || requiredNewBuckets == 0;
        }
        return false;
    }

    private bool IsRetained(RateLimiterBucketKey key, DateTimeOffset candidateTime, out TokenBucket? bucket)
    {
        if (_buckets.TryGetValue(key, out bucket) && !IsExpiredAt(bucket, candidateTime))
            return true;
        bucket = null;
        return false;
    }

    private void RemoveRequestedBucketIfExpired(RateLimiterBucketKey key, DateTimeOffset now)
    {
        if (_buckets.TryGetValue(key, out var bucket) && IsExpiredAt(bucket, now))
            _buckets.Remove(key);
    }

    private bool IsExpiredAt(TokenBucket bucket, DateTimeOffset candidateTime)
    {
        var cutoff = ComputeIdleCutoff(candidateTime, _options.BucketIdleTtl);
        return bucket.LastTouched <= cutoff;
    }

    private long ComputeDelayUntilIdleExpiryMilliseconds(DateTimeOffset now, TokenBucket bucket)
    {
        DateTimeOffset expiresAt;
        try
        {
            expiresAt = bucket.LastTouched + _options.BucketIdleTtl;
        }
        catch (ArgumentOutOfRangeException)
        {
            expiresAt = DateTimeOffset.MaxValue;
        }
        return ComputePositiveDelayMilliseconds(now, expiresAt);
    }

    private static long ComputePositiveDelayMilliseconds(DateTimeOffset now, DateTimeOffset target)
    {
        if (target <= now)
            return 1;
        try
        {
            return Math.Max(1, (long)Math.Ceiling((target - now).TotalMilliseconds));
        }
        catch (ArgumentOutOfRangeException)
        {
            return Math.Max(1, (long)Math.Ceiling((DateTimeOffset.MaxValue - now).TotalMilliseconds));
        }
    }

    private static DateTimeOffset AddMillisecondsClamped(DateTimeOffset value, long milliseconds)
    {
        try
        {
            return value.AddMilliseconds(milliseconds);
        }
        catch (ArgumentOutOfRangeException)
        {
            return DateTimeOffset.MaxValue;
        }
    }

    private long ComputeBucketLimitRetryAfterMilliseconds(DateTimeOffset now)
    {
        // The cap can recover only when the least-recently-touched bucket becomes idle.
        // Report that real interval instead of a fixed short retry that would make a
        // legitimate caller spin until the normal idle-prune window opens (#4547).
        // cap から回復できる最短時刻は、最も古く触れられた bucket が idle になる時刻。
        // 固定の短い retry 値ではなく実際の残り時間を返し、通常の idle prune まで
        // 正常な caller が再試行ループに入ることを防ぐ（#4547）。
        var earliestLastTouched = _buckets.Values.Min(bucket => bucket.LastTouched);
        DateTimeOffset expiresAt;
        try
        {
            expiresAt = earliestLastTouched + _options.BucketIdleTtl;
        }
        catch (ArgumentOutOfRangeException)
        {
            expiresAt = DateTimeOffset.MaxValue;
        }

        if (expiresAt <= now)
            return 1;

        try
        {
            return Math.Max(1, (long)Math.Ceiling((expiresAt - now).TotalMilliseconds));
        }
        catch (ArgumentOutOfRangeException)
        {
            return (long)Math.Ceiling((DateTimeOffset.MaxValue - now).TotalMilliseconds);
        }
    }

    private void PruneIdleBuckets(DateTimeOffset now, bool force = false)
    {
        var idleTtl = _options.BucketIdleTtl;
        if (idleTtl <= TimeSpan.Zero || (!force && now < _nextPruneAt))
            return;

        var cutoff = ComputeIdleCutoff(now, idleTtl);
        List<RateLimiterBucketKey>? expiredKeys = null;
        foreach (var (key, bucket) in _buckets)
        {
            if (bucket.LastTouched <= cutoff)
                (expiredKeys ??= new List<RateLimiterBucketKey>()).Add(key);
        }

        if (expiredKeys is not null)
        {
            foreach (var key in expiredKeys)
                _buckets.Remove(key);
        }

        _lastPruneAt = now;
        _lastPrunedBucketCount = expiredKeys?.Count ?? 0;
        _nextPruneAt = ComputeNextPruneAt(now, idleTtl);
    }

    private static TimeSpan ComputePruneInterval(TimeSpan idleTtl)
    {
        var interval = TimeSpan.FromTicks(Math.Max(TimeSpan.TicksPerMillisecond, idleTtl.Ticks / 4));
        return interval <= TimeSpan.FromMinutes(1) ? interval : TimeSpan.FromMinutes(1);
    }

    private static DateTimeOffset ComputeIdleCutoff(DateTimeOffset now, TimeSpan idleTtl)
    {
        try
        {
            return now - idleTtl;
        }
        catch (ArgumentOutOfRangeException)
        {
            return DateTimeOffset.MinValue;
        }
    }

    private static DateTimeOffset ComputeNextPruneAt(DateTimeOffset now, TimeSpan idleTtl)
    {
        try
        {
            return now + ComputePruneInterval(idleTtl);
        }
        catch (ArgumentOutOfRangeException)
        {
            return DateTimeOffset.MaxValue;
        }
    }

    private static long ComputeNextPruneInMilliseconds(DateTimeOffset now, DateTimeOffset nextPruneAt)
    {
        if (nextPruneAt <= now)
            return 0;
        try
        {
            return CapDiagnosticMilliseconds((nextPruneAt - now).TotalMilliseconds);
        }
        catch (ArgumentOutOfRangeException)
        {
            return MaxDiagnosticIntervalMilliseconds;
        }
    }

    internal static long ComputeNextPruneInMillisecondsForTests(DateTimeOffset now, DateTimeOffset nextPruneAt) =>
        ComputeNextPruneInMilliseconds(now, nextPruneAt);

    private static long ComputeElapsedMilliseconds(DateTimeOffset now, DateTimeOffset since)
    {
        if (now <= since)
            return 0;
        try
        {
            return CapDiagnosticMilliseconds((now - since).TotalMilliseconds);
        }
        catch (ArgumentOutOfRangeException)
        {
            return MaxDiagnosticIntervalMilliseconds;
        }
    }

    internal static long ComputeElapsedMillisecondsForTests(DateTimeOffset now, DateTimeOffset since) =>
        ComputeElapsedMilliseconds(now, since);

    private static long CapDiagnosticMilliseconds(double milliseconds)
    {
        if (milliseconds <= 0)
            return 0;
        if (milliseconds >= MaxDiagnosticIntervalMilliseconds)
            return MaxDiagnosticIntervalMilliseconds;
        return (long)Math.Ceiling(milliseconds);
    }

    private sealed class TokenBucket
    {
        private double _tokens;
        private DateTimeOffset _lastUpdate;
        private DateTimeOffset _lastTouched;

        public TokenBucket(double initialTokens, DateTimeOffset createdAt)
        {
            _tokens = initialTokens;
            _lastUpdate = createdAt;
            _lastTouched = createdAt;
        }

        public DateTimeOffset LastTouched => _lastTouched;

        public bool WouldAllowAt(DateTimeOffset candidateTime, double refillRate, double capacity) =>
            ProjectTokens(candidateTime, refillRate, capacity) >= 1.0;

        public long? TryGetNextTokenDelayMilliseconds(DateTimeOffset now, double refillRate, double capacity)
        {
            if (capacity < 1.0 || refillRate <= 0)
                return null;
            var projectedTokens = ProjectTokens(now, refillRate, capacity);
            if (projectedTokens >= 1.0)
                return 1;
            return Math.Max(1, (long)Math.Ceiling(((1.0 - projectedTokens) / refillRate) * 1000.0));
        }

        private double ProjectTokens(DateTimeOffset candidateTime, double refillRate, double capacity)
        {
            var elapsedSeconds = (candidateTime - _lastUpdate).TotalSeconds;
            return elapsedSeconds > 0
                ? Math.Min(capacity, _tokens + elapsedSeconds * refillRate)
                : _tokens;
        }

        public RateLimiterDecision TryAcquire(DateTimeOffset now, double refillRate, double capacity)
        {
            // Defense in depth: the public surface gates on RateLimiterOptions.IsEnabled
            // (which requires refillRate > 0), so this guard only fires if a future caller
            // bypasses that gate. Without it, `deficit / refillRate` would silently become
            // ±Infinity, and `(long)Infinity` is implementation-defined in .NET.
            // 上位の IsEnabled で refillRate > 0 は保証されるが、将来 IsEnabled を経由しない
            // 経路が増えた場合に `deficit / refillRate` が ±Infinity になり (long) 化で
            // 実装依存の値になるのを防ぐための内部 guard。
            if (refillRate <= 0)
                throw new ArgumentOutOfRangeException(nameof(refillRate), "refillRate must be positive when TryAcquire is invoked.");

            var elapsedSeconds = (now - _lastUpdate).TotalSeconds;
            if (elapsedSeconds > 0)
            {
                _tokens = Math.Min(capacity, _tokens + elapsedSeconds * refillRate);
                _lastUpdate = now;
            }
            if (now > _lastTouched)
                _lastTouched = now;
            // When elapsedSeconds <= 0 (clock drift / repeated tick / backwards step) we
            // intentionally do NOT touch _lastUpdate. The bucket stays anchored to its
            // previous base, so the next forward tick computes elapsed against the older
            // anchor and refills correctly. The Math.Min clamp still caps at BurstCapacity
            // so even a large forward jump cannot grow the bucket beyond burst.
            // elapsedSeconds <= 0（時計逆行・同一 tick・ドリフト）の場合は _lastUpdate を
            // あえて更新せず、次回 forward tick で旧アンカーから経過時間を計算する。
            // Math.Min による burst clamp により、大きく前進しても burst を超える補充は起きない。

            if (_tokens >= 1.0)
            {
                _tokens -= 1.0;
                return RateLimiterDecision.Allow;
            }

            var deficit = 1.0 - _tokens;
            var seconds = deficit / refillRate;
            var retryAfterMs = (long)Math.Ceiling(seconds * 1000.0);
            if (retryAfterMs < 1)
                retryAfterMs = 1;
            return RateLimiterDecision.Deny(retryAfterMs);
        }
    }
}

internal readonly record struct RateLimiterBucketKey(string Tool, string Caller);

internal readonly record struct RateLimiterDiagnostics(
    int BucketCount,
    double BucketIdleTtlSeconds,
    int MaxBucketCount,
    int BucketLimitRejectionCount,
    long NextPruneInMs,
    long? LastPruneAgeMs,
    int LastPrunedBucketCount);

internal readonly record struct RateLimiterDecision(bool Allowed, long RetryAfterMs)
{
    public static RateLimiterDecision Allow { get; } = new(true, 0);
    public static RateLimiterDecision Deny(long retryAfterMs) => new(false, retryAfterMs);
}

/// <summary>
/// Configuration for the MCP <see cref="RateLimiter"/>. Disabled by default so single-user
/// stdio MCP sessions are unaffected; operators opt in by setting
/// `CDIDX_MCP_RATE_LIMIT_RPS` (and optionally `CDIDX_MCP_RATE_LIMIT_BURST`) on the server
/// process (#1560).
/// MCP <see cref="RateLimiter"/> の設定。既定では無効で stdio 単一ユーザーの MCP セッションには
/// 影響しない。運用側で `CDIDX_MCP_RATE_LIMIT_RPS`（必要なら `CDIDX_MCP_RATE_LIMIT_BURST`）を
/// MCP サーバープロセスに設定して opt-in する（#1560）。
/// </summary>
internal sealed class RateLimiterOptions
{
    internal static readonly TimeSpan DefaultBucketIdleTtl = TimeSpan.FromMinutes(15);
    internal const int DefaultMaxBucketCount = 4096;
    internal const string RpsEnvVar = "CDIDX_MCP_RATE_LIMIT_RPS";
    internal const string BurstEnvVar = "CDIDX_MCP_RATE_LIMIT_BURST";
    internal const string BucketIdleSecondsEnvVar = "CDIDX_MCP_RATE_LIMIT_BUCKET_IDLE_SECONDS";
    internal const double MaxRefillTokensPerSecond = 100.0;
    internal const double MaxBurstCapacity = 1000.0;

    public double RefillTokensPerSecond { get; init; }
    public double BurstCapacity { get; init; }
    public TimeSpan BucketIdleTtl { get; init; } = DefaultBucketIdleTtl;
    public int MaxBucketCount { get; init; } = DefaultMaxBucketCount;
    public bool IsEnabled => RefillTokensPerSecond > 0 && BurstCapacity > 0;

    public static RateLimiterOptions Disabled { get; } = new() { RefillTokensPerSecond = 0, BurstCapacity = 0, BucketIdleTtl = DefaultBucketIdleTtl, MaxBucketCount = DefaultMaxBucketCount };

    public static RateLimiterOptions FromEnvironment(Func<string, string?>? envReader = null, Action<string>? warningSink = null)
    {
        envReader ??= CdidxEnvironment.GetEnvironmentVariable;
        warningSink ??= CommandErrorWriter.WriteStderr;

        var rpsRaw = envReader(RpsEnvVar);
        if (string.IsNullOrWhiteSpace(rpsRaw))
            return Disabled;

        if (!TryParsePositiveDouble(rpsRaw, out var rps))
        {
            warningSink($"[cdidx-mcp] Ignoring invalid {RpsEnvVar}='{FormatEnvironmentValue(rpsRaw)}'. Expected a positive number (tokens per second). Rate limiting stays disabled.");
            return Disabled;
        }
        if (rps > MaxRefillTokensPerSecond)
        {
            warningSink($"[cdidx-mcp] Clamping {RpsEnvVar}='{FormatEnvironmentValue(rpsRaw)}' to maximum {MaxRefillTokensPerSecond.ToString(CultureInfo.InvariantCulture)} tokens per second.");
            rps = MaxRefillTokensPerSecond;
        }

        var burstRaw = envReader(BurstEnvVar);
        double burst;
        if (string.IsNullOrWhiteSpace(burstRaw))
        {
            // Default burst is max(rps, 1) so a 0.5/sec config still allows the first call
            // through and short bursts up to one second's worth of tokens.
            // 既定の burst は max(rps, 1)。0.5/sec のような低レートでも最初の 1 回は通し、
            // 1 秒分のバーストを許容する。
            burst = Math.Max(rps, 1.0);
        }
        else if (!TryParsePositiveDouble(burstRaw, out burst))
        {
            warningSink($"[cdidx-mcp] Ignoring invalid {BurstEnvVar}='{FormatEnvironmentValue(burstRaw)}'. Expected a positive number (bucket capacity). Falling back to default burst.");
            burst = Math.Max(rps, 1.0);
        }
        else if (burst > MaxBurstCapacity)
        {
            warningSink($"[cdidx-mcp] Clamping {BurstEnvVar}='{FormatEnvironmentValue(burstRaw)}' to maximum {MaxBurstCapacity.ToString(CultureInfo.InvariantCulture)} tokens.");
            burst = MaxBurstCapacity;
        }

        var bucketIdleTtl = DefaultBucketIdleTtl;
        var bucketIdleRaw = envReader(BucketIdleSecondsEnvVar);
        if (!string.IsNullOrWhiteSpace(bucketIdleRaw) && !TryParsePositiveTimeSpanSeconds(bucketIdleRaw, out bucketIdleTtl))
            warningSink($"[cdidx-mcp] Ignoring invalid {BucketIdleSecondsEnvVar}='{FormatEnvironmentValue(bucketIdleRaw)}'. Expected a positive finite number of seconds. Falling back to the default bucket idle TTL.");

        return new RateLimiterOptions { RefillTokensPerSecond = rps, BurstCapacity = burst, BucketIdleTtl = bucketIdleTtl, MaxBucketCount = DefaultMaxBucketCount };
    }

    private static string FormatEnvironmentValue(string value) => ConsoleUi.FormatBoundedValue(value);

    private static bool TryParsePositiveDouble(string raw, out double value)
    {
        if (double.TryParse(raw.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value)
            && double.IsFinite(value) && value > 0)
        {
            return true;
        }
        value = 0;
        return false;
    }

    private static bool TryParsePositiveTimeSpanSeconds(string raw, out TimeSpan value)
    {
        if (TryParsePositiveDouble(raw, out var seconds) && seconds <= TimeSpan.MaxValue.TotalSeconds)
        {
            value = TimeSpan.FromSeconds(seconds);
            return true;
        }

        value = DefaultBucketIdleTtl;
        return false;
    }
}
