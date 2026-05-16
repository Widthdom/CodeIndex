using System.Globalization;

namespace CodeIndex.Mcp;

/// <summary>
/// Token-bucket rate limiter keyed by (tool, caller). MCP tool calls consume one token from
/// the bucket; over-quota callers receive a structured `-32000` JSON-RPC error including
/// `retry_after_ms` (issue #1560).
/// (tool, caller) ごとのトークンバケット型レート制限。MCP ツール呼び出しはバケットから 1 トークンを
/// 消費し、超過した呼び出しには `retry_after_ms` を含む `-32000` の JSON-RPC エラーを返す（#1560）。
/// </summary>
internal sealed class RateLimiter
{
    private readonly object _gate = new();
    private readonly Dictionary<string, TokenBucket> _buckets = new(StringComparer.Ordinal);
    private readonly RateLimiterOptions _options;
    private readonly Func<DateTimeOffset> _clock;

    public RateLimiter(RateLimiterOptions options, Func<DateTimeOffset>? clock = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public RateLimiterOptions Options => _options;

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
        var now = _clock();
        lock (_gate)
        {
            if (!_buckets.TryGetValue(key, out var bucket))
            {
                bucket = new TokenBucket(_options.BurstCapacity, now);
                _buckets[key] = bucket;
            }
            return bucket.TryAcquire(now, _options.RefillTokensPerSecond, _options.BurstCapacity);
        }
    }

    internal static string BuildKey(string tool, string caller) => $"{tool}|{caller}";

    private sealed class TokenBucket
    {
        private double _tokens;
        private DateTimeOffset _lastUpdate;

        public TokenBucket(double initialTokens, DateTimeOffset createdAt)
        {
            _tokens = initialTokens;
            _lastUpdate = createdAt;
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
    internal const string RpsEnvVar = "CDIDX_MCP_RATE_LIMIT_RPS";
    internal const string BurstEnvVar = "CDIDX_MCP_RATE_LIMIT_BURST";

    public double RefillTokensPerSecond { get; init; }
    public double BurstCapacity { get; init; }
    public bool IsEnabled => RefillTokensPerSecond > 0 && BurstCapacity > 0;

    public static RateLimiterOptions Disabled { get; } = new() { RefillTokensPerSecond = 0, BurstCapacity = 0 };

    public static RateLimiterOptions FromEnvironment(Func<string, string?>? envReader = null, Action<string>? warningSink = null)
    {
        envReader ??= Environment.GetEnvironmentVariable;
        warningSink ??= Console.Error.WriteLine;

        var rpsRaw = envReader(RpsEnvVar);
        if (string.IsNullOrWhiteSpace(rpsRaw))
            return Disabled;

        if (!TryParsePositiveDouble(rpsRaw, out var rps))
        {
            warningSink($"[cdidx-mcp] Ignoring invalid {RpsEnvVar}='{rpsRaw}'. Expected a positive number (tokens per second). Rate limiting stays disabled.");
            return Disabled;
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
            warningSink($"[cdidx-mcp] Ignoring invalid {BurstEnvVar}='{burstRaw}'. Expected a positive number (bucket capacity). Falling back to default burst.");
            burst = Math.Max(rps, 1.0);
        }

        return new RateLimiterOptions { RefillTokensPerSecond = rps, BurstCapacity = burst };
    }

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
}
