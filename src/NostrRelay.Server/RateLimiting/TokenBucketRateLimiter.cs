namespace NostrRelay.Server.RateLimiting;

/// <summary>
/// Simple token bucket (Section 4.3: "Rate limit per connection... using a token
/// bucket"). Refills continuously based on elapsed wall-clock time on each
/// <see cref="TryConsume"/> call, rather than on a fixed timer tick, so accuracy doesn't
/// depend on how often the bucket happens to be checked.
///
/// One instance per connection, created fresh in <c>NostrConnectionHandler.HandleAsync</c>
/// (per-connection state, not shared, same lifecycle as that connection's outbound
/// channel).
/// </summary>
public sealed class TokenBucketRateLimiter
{
    private readonly double _capacity;
    private readonly double _refillPerSecond;
    private readonly Lock _lock = new();
    private double _tokens;
    private DateTime _lastRefill;

    /// <param name="capacity">Maximum tokens the bucket can hold, also the burst limit.</param>
    /// <param name="refillPeriod">Time to refill from empty to <paramref name="capacity"/>
    /// at a steady rate, e.g. one minute for a "per minute" limit.</param>
    public TokenBucketRateLimiter(int capacity, TimeSpan refillPeriod)
    {
        _capacity = capacity;
        _refillPerSecond = capacity / refillPeriod.TotalSeconds;
        _tokens = capacity;
        _lastRefill = DateTime.UtcNow;
    }

    /// <summary>Attempts to consume one token. Returns false (no token consumed) if the
    /// bucket is empty.</summary>
    public bool TryConsume()
    {
        lock (_lock)
        {
            Refill();

            if (_tokens < 1)
                return false;

            _tokens -= 1;
            return true;
        }
    }

    private void Refill()
    {
        DateTime now = DateTime.UtcNow;
        var elapsedSeconds = (now - _lastRefill).TotalSeconds;

        _tokens = Math.Min(_capacity, _tokens + elapsedSeconds * _refillPerSecond);
        _lastRefill = now;
    }
}
