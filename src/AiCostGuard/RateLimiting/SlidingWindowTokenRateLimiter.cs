using System.Collections.Concurrent;

namespace AiCostGuard.RateLimiting;

/// <summary>
/// A thread-safe 1-minute sliding window implementation of <see cref="ITokenRateLimiter"/> driven by <see cref="TimeProvider"/>.
/// </summary>
/// <param name="timeProvider">An optional <see cref="TimeProvider"/> used for sliding window timestamps. Defaults to <see cref="TimeProvider.System"/>.</param>
public sealed class SlidingWindowTokenRateLimiter(TimeProvider? timeProvider = null) : ITokenRateLimiter
{
    private static readonly TimeSpan _windowDuration = TimeSpan.FromMinutes(1);

    private readonly ConcurrentDictionary<string, TenantWindow> _windows = new(StringComparer.Ordinal);
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    /// <inheritdoc />
    public ValueTask<TokenLeaseResult> TryAcquireAsync(
        string key,
        long estimatedTokens,
        long maxTokensPerMinute,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentOutOfRangeException.ThrowIfNegative(estimatedTokens);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(maxTokensPerMinute, 0);

        cancellationToken.ThrowIfCancellationRequested();

        DateTimeOffset now = _timeProvider.GetUtcNow();
        TenantWindow window = _windows.GetOrAdd(key, static _ => new TenantWindow());

        lock (window.SyncRoot)
        {
            EvictExpiredEntries(window, now);

            // Fast-fail if a single request is larger than the entire 1-minute quota.
            // RetryAfter is null so Wait mode knows waiting will never make this request fit.
            if (estimatedTokens > maxTokensPerMinute)
            {
                return ValueTask.FromResult(new TokenLeaseResult(false, window.CurrentTokens, null));
            }

            if (estimatedTokens <= maxTokensPerMinute - window.CurrentTokens)
            {
                if (estimatedTokens > 0)
                {
                    window.Entries.AddLast(new TokenEntry(now, estimatedTokens));
                    window.CurrentTokens += estimatedTokens;
                }

                return ValueTask.FromResult(new TokenLeaseResult(true, window.CurrentTokens, null));
            }

            TimeSpan retryAfter = CalculateRetryAfter(window, now, estimatedTokens, maxTokensPerMinute);
            return ValueTask.FromResult(new TokenLeaseResult(false, window.CurrentTokens, retryAfter));
        }
    }

    /// <inheritdoc />
    public ValueTask ReconcileAsync(
        string key,
        long reservedTokens,
        long actualTokens,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentOutOfRangeException.ThrowIfNegative(reservedTokens);
        ArgumentOutOfRangeException.ThrowIfNegative(actualTokens);

        if (!_windows.TryGetValue(key, out TenantWindow? window))
        {
            return ValueTask.CompletedTask;
        }

        long delta = actualTokens - reservedTokens;
        if (delta == 0)
        {
            return ValueTask.CompletedTask;
        }

        DateTimeOffset now = _timeProvider.GetUtcNow();

        lock (window.SyncRoot)
        {
            EvictExpiredEntries(window, now);

            if (delta > 0)
            {
                // Actual usage exceeded the estimate: record the additional consumed tokens.
                window.Entries.AddLast(new TokenEntry(now, delta));
                window.CurrentTokens = window.CurrentTokens > long.MaxValue - delta
                    ? long.MaxValue
                    : window.CurrentTokens + delta;
            }
            else
            {
                // Actual usage was lower than the estimate (or 0 on rollback): refund unused tokens FIFO from the oldest entries
                // so newer reservations retain their timestamps and cannot expire prematurely.
                long toRefund = Math.Min(-delta, window.CurrentTokens);
                window.CurrentTokens -= toRefund;

                while (toRefund > 0 && window.Entries.First is { } firstNode)
                {
                    TokenEntry entry = firstNode.Value;
                    if (entry.Tokens <= toRefund)
                    {
                        toRefund -= entry.Tokens;
                        window.Entries.RemoveFirst();
                    }
                    else
                    {
                        firstNode.Value = entry with { Tokens = entry.Tokens - toRefund };
                        toRefund = 0;
                    }
                }
            }
        }

        return ValueTask.CompletedTask;
    }

    private static void EvictExpiredEntries(TenantWindow window, DateTimeOffset now)
    {
        TimeSpan maxSubtractable = now - DateTimeOffset.MinValue;
        DateTimeOffset cutoff = _windowDuration >= maxSubtractable ? DateTimeOffset.MinValue : now - _windowDuration;

        while (window.Entries.First is { } firstNode && firstNode.Value.Timestamp <= cutoff)
        {
            window.CurrentTokens -= firstNode.Value.Tokens;
            window.Entries.RemoveFirst();
        }

        if (window.Entries.Count == 0 || window.CurrentTokens < 0)
        {
            window.CurrentTokens = 0;
        }
    }

    private static TimeSpan CalculateRetryAfter(
        TenantWindow window,
        DateTimeOffset now,
        long estimatedTokens,
        long maxTokensPerMinute)
    {
        Int128 tokensToFree = ((Int128)window.CurrentTokens + estimatedTokens) - maxTokensPerMinute;
        Int128 accumulated = 0;

        foreach (TokenEntry entry in window.Entries)
        {
            accumulated += entry.Tokens;
            if (accumulated >= tokensToFree)
            {
                TimeSpan maxAddable = DateTimeOffset.MaxValue - entry.Timestamp;
                DateTimeOffset expiresAt = _windowDuration >= maxAddable ? DateTimeOffset.MaxValue : entry.Timestamp + _windowDuration;
                return expiresAt > now ? expiresAt - now : TimeSpan.Zero;
            }
        }

        return _windowDuration;
    }

    private readonly record struct TokenEntry(DateTimeOffset Timestamp, long Tokens);

    private sealed class TenantWindow
    {
        public Lock SyncRoot { get; } = new();

        public LinkedList<TokenEntry> Entries { get; } = new();

        public long CurrentTokens { get; set; }
    }
}
