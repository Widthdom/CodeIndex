using CodeIndex.Mcp;

namespace CodeIndex.Tests;

/// <summary>
/// Tests for the per-(tool, caller) MCP rate limiter (#1560).
/// (tool, caller) 単位の MCP レート制限器のテスト（#1560）。
/// </summary>
public class RateLimiterTests
{
    private sealed class TestClock
    {
        public DateTimeOffset Now { get; set; } = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public DateTimeOffset Read() => Now;
    }

    [Fact]
    public void Disabled_AlwaysAllows()
    {
        var limiter = new RateLimiter(RateLimiterOptions.Disabled);
        for (var i = 0; i < 1000; i++)
        {
            var d = limiter.TryAcquire("search", "client-a");
            Assert.True(d.Allowed);
            Assert.Equal(0, d.RetryAfterMs);
        }
    }

    [Fact]
    public void Enabled_AllowsUpToBurstThenDenies()
    {
        var clock = new TestClock();
        var options = new RateLimiterOptions { RefillTokensPerSecond = 1.0, BurstCapacity = 3.0 };
        var limiter = new RateLimiter(options, clock.Read);

        // Three calls fit in the initial burst / 初期バーストで 3 回まで通る
        Assert.True(limiter.TryAcquire("search", "client-a").Allowed);
        Assert.True(limiter.TryAcquire("search", "client-a").Allowed);
        Assert.True(limiter.TryAcquire("search", "client-a").Allowed);

        var denied = limiter.TryAcquire("search", "client-a");
        Assert.False(denied.Allowed);
        Assert.True(denied.RetryAfterMs >= 1, $"retry_after_ms should be >= 1, got {denied.RetryAfterMs}");
    }

    [Fact]
    public void Refill_RestoresTokensOverTime()
    {
        var clock = new TestClock();
        var options = new RateLimiterOptions { RefillTokensPerSecond = 2.0, BurstCapacity = 1.0 };
        var limiter = new RateLimiter(options, clock.Read);

        Assert.True(limiter.TryAcquire("search", "client-a").Allowed);
        Assert.False(limiter.TryAcquire("search", "client-a").Allowed);

        // Advance 600 ms — at 2 tokens/sec that yields 1.2 tokens, enough for one more call.
        // 600ms 経過。2 token/sec で 1.2 token 補充され、もう 1 回通る。
        clock.Now = clock.Now.AddMilliseconds(600);
        Assert.True(limiter.TryAcquire("search", "client-a").Allowed);
        Assert.False(limiter.TryAcquire("search", "client-a").Allowed);
    }

    [Fact]
    public void DifferentCallers_HaveIndependentBuckets()
    {
        var clock = new TestClock();
        var options = new RateLimiterOptions { RefillTokensPerSecond = 1.0, BurstCapacity = 1.0 };
        var limiter = new RateLimiter(options, clock.Read);

        Assert.True(limiter.TryAcquire("search", "client-a").Allowed);
        Assert.False(limiter.TryAcquire("search", "client-a").Allowed);

        // client-b owns a separate bucket / client-b は別バケットを持つ
        Assert.True(limiter.TryAcquire("search", "client-b").Allowed);
        Assert.False(limiter.TryAcquire("search", "client-b").Allowed);
    }

    [Fact]
    public void DifferentTools_HaveIndependentBuckets()
    {
        var clock = new TestClock();
        var options = new RateLimiterOptions { RefillTokensPerSecond = 1.0, BurstCapacity = 1.0 };
        var limiter = new RateLimiter(options, clock.Read);

        Assert.True(limiter.TryAcquire("search", "client-a").Allowed);
        // Same caller but different tool keeps its own bucket / 別ツールは別バケット
        Assert.True(limiter.TryAcquire("definition", "client-a").Allowed);
        Assert.False(limiter.TryAcquire("search", "client-a").Allowed);
        Assert.False(limiter.TryAcquire("definition", "client-a").Allowed);
    }

    [Fact]
    public void RetryAfterMs_ApproximatesTimeUntilNextToken()
    {
        var clock = new TestClock();
        var options = new RateLimiterOptions { RefillTokensPerSecond = 4.0, BurstCapacity = 1.0 };
        var limiter = new RateLimiter(options, clock.Read);

        Assert.True(limiter.TryAcquire("search", "client-a").Allowed);
        var denied = limiter.TryAcquire("search", "client-a");
        Assert.False(denied.Allowed);
        // At 4 tokens/sec, one token takes ~250 ms. Allow a small ceiling slack.
        // 4 token/sec のとき次トークンまで ~250ms。Ceiling での若干の上振れを許容。
        Assert.InRange(denied.RetryAfterMs, 200, 260);
    }

    [Fact]
    public void Refill_ClampsAtBurstCapacityAfterLongIdle()
    {
        // After draining the bucket, a long idle gap could refill `elapsed * rps` tokens
        // (10 seconds * 1 token/sec = 10) — the Math.Min(capacity, ...) clamp must cap
        // refill at BurstCapacity so a starved limiter cannot become a 10x burst window.
        // バケット枯渇後の長い無アクセス区間で `elapsed * rps`（10 秒 × 1 token/sec = 10）が
        // 流入しても、Math.Min による burst clamp により BurstCapacity を超えないことを検証する。
        var clock = new TestClock();
        var options = new RateLimiterOptions { RefillTokensPerSecond = 1.0, BurstCapacity = 2.0 };
        var limiter = new RateLimiter(options, clock.Read);

        Assert.True(limiter.TryAcquire("search", "client-a").Allowed);
        Assert.True(limiter.TryAcquire("search", "client-a").Allowed);
        Assert.False(limiter.TryAcquire("search", "client-a").Allowed);

        // 10 seconds elapse — would refill 10 tokens without clamping / clamp 無しなら 10 補充。
        clock.Now = clock.Now.AddSeconds(10);

        Assert.True(limiter.TryAcquire("search", "client-a").Allowed);
        Assert.True(limiter.TryAcquire("search", "client-a").Allowed);
        // Third consecutive call would succeed if the clamp were ignored / clamp 無視時のみ成功する。
        Assert.False(limiter.TryAcquire("search", "client-a").Allowed);
    }

    [Fact]
    public void Clock_GoingBackwards_DoesNotOverRefill()
    {
        // If the injected clock returns a value earlier than (or equal to) the last update
        // — possible during NTP step / DST adjustments with wall-clock time — the bucket
        // must not silently refill, and the original anchor must be preserved so the next
        // forward tick computes elapsed from the older anchor (issue #1560 review).
        // NTP / DST 補正等で時計が逆行または同一 tick を返した場合に、勝手な補充が起きず
        // anchor が保持され、次の forward tick で正しく経過時間が算出されることを検証する。
        var clock = new TestClock();
        var options = new RateLimiterOptions { RefillTokensPerSecond = 1.0, BurstCapacity = 1.0 };
        var limiter = new RateLimiter(options, clock.Read);

        Assert.True(limiter.TryAcquire("search", "client-a").Allowed);
        Assert.False(limiter.TryAcquire("search", "client-a").Allowed);

        // Clock steps backwards — must not refill / 逆行: 補充禁止。
        clock.Now = clock.Now.AddMilliseconds(-500);
        Assert.False(limiter.TryAcquire("search", "client-a").Allowed);

        // Forward to one second after the original anchor — should refill exactly one
        // token (anchor was preserved at the original timestamp).
        // 元の anchor から 1 秒経過: ちょうど 1 トークン補充される（anchor が保持されている）。
        clock.Now = clock.Now.AddMilliseconds(1500);
        Assert.True(limiter.TryAcquire("search", "client-a").Allowed);
    }

    [Fact]
    public void FromEnvironment_NoVars_ReturnsDisabled()
    {
        var opts = RateLimiterOptions.FromEnvironment(_ => null, _ => { });
        Assert.False(opts.IsEnabled);
    }

    [Fact]
    public void FromEnvironment_OnlyRps_DefaultsBurst()
    {
        var opts = RateLimiterOptions.FromEnvironment(
            key => key == RateLimiterOptions.RpsEnvVar ? "5" : null,
            _ => { });
        Assert.True(opts.IsEnabled);
        Assert.Equal(5.0, opts.RefillTokensPerSecond);
        Assert.Equal(5.0, opts.BurstCapacity);
    }

    [Fact]
    public void FromEnvironment_LowRps_DefaultsBurstToAtLeastOne()
    {
        var opts = RateLimiterOptions.FromEnvironment(
            key => key == RateLimiterOptions.RpsEnvVar ? "0.25" : null,
            _ => { });
        Assert.True(opts.IsEnabled);
        Assert.Equal(0.25, opts.RefillTokensPerSecond);
        Assert.Equal(1.0, opts.BurstCapacity);
    }

    [Fact]
    public void FromEnvironment_ExplicitBurst_IsHonored()
    {
        var opts = RateLimiterOptions.FromEnvironment(
            key => key switch
            {
                RateLimiterOptions.RpsEnvVar => "2",
                RateLimiterOptions.BurstEnvVar => "20",
                _ => null,
            },
            _ => { });
        Assert.True(opts.IsEnabled);
        Assert.Equal(2.0, opts.RefillTokensPerSecond);
        Assert.Equal(20.0, opts.BurstCapacity);
    }

    [Fact]
    public void FromEnvironment_InvalidRps_WarnsAndDisables()
    {
        var warnings = new List<string>();
        var opts = RateLimiterOptions.FromEnvironment(
            key => key == RateLimiterOptions.RpsEnvVar ? "not-a-number" : null,
            warnings.Add);
        Assert.False(opts.IsEnabled);
        Assert.Single(warnings);
        Assert.Contains("CDIDX_MCP_RATE_LIMIT_RPS", warnings[0]);
    }

    [Fact]
    public void FromEnvironment_NegativeRps_WarnsAndDisables()
    {
        var warnings = new List<string>();
        var opts = RateLimiterOptions.FromEnvironment(
            key => key == RateLimiterOptions.RpsEnvVar ? "-1" : null,
            warnings.Add);
        Assert.False(opts.IsEnabled);
        Assert.Single(warnings);
    }

    [Fact]
    public void FromEnvironment_InvalidBurst_WarnsAndFallsBack()
    {
        var warnings = new List<string>();
        var opts = RateLimiterOptions.FromEnvironment(
            key => key switch
            {
                RateLimiterOptions.RpsEnvVar => "3",
                RateLimiterOptions.BurstEnvVar => "garbage",
                _ => null,
            },
            warnings.Add);
        Assert.True(opts.IsEnabled);
        Assert.Equal(3.0, opts.RefillTokensPerSecond);
        Assert.Equal(3.0, opts.BurstCapacity);
        Assert.Single(warnings);
        Assert.Contains("CDIDX_MCP_RATE_LIMIT_BURST", warnings[0]);
    }
}
