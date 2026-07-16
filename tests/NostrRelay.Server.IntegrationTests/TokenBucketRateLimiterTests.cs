using NostrRelay.Server.RateLimiting;

namespace NostrRelay.Server.IntegrationTests;

public class TokenBucketRateLimiterTests
{
    [Fact]
    public void TryConsume_AllowsUpToCapacityImmediately()
    {
        var limiter = new TokenBucketRateLimiter(capacity: 3, refillPeriod: TimeSpan.FromMinutes(1));

        Assert.True(limiter.TryConsume());
        Assert.True(limiter.TryConsume());
        Assert.True(limiter.TryConsume());
    }

    [Fact]
    public void TryConsume_RejectsOnceCapacityExhausted()
    {
        var limiter = new TokenBucketRateLimiter(capacity: 2, refillPeriod: TimeSpan.FromMinutes(1));

        Assert.True(limiter.TryConsume());
        Assert.True(limiter.TryConsume());
        Assert.False(limiter.TryConsume());
    }

    [Fact]
    public async Task TryConsume_RefillsOverTime()
    {
        // Fast refill period so the test doesn't need to wait long: capacity 1, refills
        // fully in 200ms, so waiting ~250ms should yield exactly one more token.
        var limiter = new TokenBucketRateLimiter(capacity: 1, refillPeriod: TimeSpan.FromMilliseconds(200));

        Assert.True(limiter.TryConsume());
        Assert.False(limiter.TryConsume());

        await Task.Delay(250);

        Assert.True(limiter.TryConsume());
    }

    [Fact]
    public async Task TryConsume_DoesNotAccumulateUnboundedCreditDuringIdlePeriod()
    {
        // Idle for far longer than several refill periods (10x), to confirm the bucket
        // caps at capacity rather than accumulating unbounded credit for unused time.
        var limiter = new TokenBucketRateLimiter(capacity: 2, refillPeriod: TimeSpan.FromMilliseconds(50));

        await Task.Delay(500);

        Assert.True(limiter.TryConsume());
        Assert.True(limiter.TryConsume());
        Assert.False(limiter.TryConsume());
    }
}
