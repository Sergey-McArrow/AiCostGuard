using System.Collections.Concurrent;
using AiCostGuard.Options;

namespace AiCostGuard.Storage;

/// <summary>
/// An in-memory, thread-safe implementation of <see cref="IBudgetStore"/> that isolates state per tenant key and per budget rule window
/// (<see cref="BudgetWindowType.Sliding"/>, <see cref="BudgetWindowType.FixedUtcDay"/>, and <see cref="BudgetWindowType.FixedUtcMonth"/>),
/// using <see cref="TimeProvider"/> for deterministic window evaluation.
/// </summary>
/// <param name="timeProvider">An optional <see cref="TimeProvider"/> used to track budget windows. Defaults to <see cref="TimeProvider.System"/>.</param>
public sealed class InMemoryBudgetStore(TimeProvider? timeProvider = null) : IBudgetStore
{
    private readonly ConcurrentDictionary<BucketKey, BudgetBucket> _buckets = new();
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    /// <inheritdoc />
    public ValueTask<BudgetReservation> TryReserveAsync(
        string key,
        decimal estimatedCostUsd,
        BudgetRule rule,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentOutOfRangeException.ThrowIfNegative(estimatedCostUsd);
        ArgumentNullException.ThrowIfNull(rule);
        rule.Validate();

        cancellationToken.ThrowIfCancellationRequested();

        DateTimeOffset now = _timeProvider.GetUtcNow().ToUniversalTime();
        var bucketKey = BucketKey.FromRule(key, rule);
        BudgetBucket bucket = _buckets.GetOrAdd(bucketKey, static (k, startTime) => new BudgetBucket(k.WindowType, k.SlidingTicks, startTime), now);

        lock (bucket.SyncRoot)
        {
            EvictOrResetExpired(bucket, now);

            if (estimatedCostUsd > rule.MaxAmountUsd - bucket.SpentUsd)
            {
                TimeSpan retryAfter = CalculateRetryAfter(bucket, now, estimatedCostUsd, rule.MaxAmountUsd);
                return ValueTask.FromResult(new BudgetReservation(false, bucket.SpentUsd, retryAfter));
            }

            if (estimatedCostUsd > 0m)
            {
                if (bucket.WindowType == BudgetWindowType.Sliding)
                {
                    bucket.SlidingEntries.AddLast(new SpendEntry(now, estimatedCostUsd));
                }
                else
                {
                    bucket.AddPendingCalendarReservation(estimatedCostUsd);
                }

                bucket.SpentUsd += estimatedCostUsd;
            }

            return ValueTask.FromResult(new BudgetReservation(true, bucket.SpentUsd, null));
        }
    }

    /// <inheritdoc />
    public ValueTask<BudgetReservation> TryReserveAsync(
        string key,
        decimal estimatedCostUsd,
        decimal maxBudgetUsd,
        TimeSpan window,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentOutOfRangeException.ThrowIfNegative(estimatedCostUsd);
        ArgumentOutOfRangeException.ThrowIfNegative(maxBudgetUsd);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(window, TimeSpan.Zero);

        var rule = BudgetRule.Sliding(maxBudgetUsd, window);
        return TryReserveAsync(key, estimatedCostUsd, rule, cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask ReconcileAsync(
        string key,
        decimal reservedCostUsd,
        decimal actualCostUsd,
        BudgetRule rule,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentOutOfRangeException.ThrowIfNegative(reservedCostUsd);
        ArgumentOutOfRangeException.ThrowIfNegative(actualCostUsd);
        ArgumentNullException.ThrowIfNull(rule);
        rule.Validate();

        var bucketKey = BucketKey.FromRule(key, rule);
        if (!_buckets.TryGetValue(bucketKey, out BudgetBucket? bucket))
        {
            return ValueTask.CompletedTask;
        }

        ReconcileBucket(bucket, reservedCostUsd, actualCostUsd);
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask ReconcileAsync(
        string key,
        decimal reservedCostUsd,
        decimal actualCostUsd,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentOutOfRangeException.ThrowIfNegative(reservedCostUsd);
        ArgumentOutOfRangeException.ThrowIfNegative(actualCostUsd);

        foreach (KeyValuePair<BucketKey, BudgetBucket> pair in _buckets)
        {
            if (string.Equals(pair.Key.ScopeKey, key, StringComparison.Ordinal))
            {
                ReconcileBucket(pair.Value, reservedCostUsd, actualCostUsd);
            }
        }

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask<decimal> GetCurrentSpendAsync(
        string key,
        BudgetRule rule,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(rule);
        rule.Validate();

        cancellationToken.ThrowIfCancellationRequested();

        var bucketKey = BucketKey.FromRule(key, rule);
        if (!_buckets.TryGetValue(bucketKey, out BudgetBucket? bucket))
        {
            return ValueTask.FromResult(0m);
        }

        DateTimeOffset now = _timeProvider.GetUtcNow().ToUniversalTime();

        lock (bucket.SyncRoot)
        {
            EvictOrResetExpired(bucket, now);
            return ValueTask.FromResult(bucket.SpentUsd);
        }
    }

    /// <inheritdoc />
    public ValueTask<decimal> GetCurrentSpendAsync(
        string key,
        TimeSpan window,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(window, TimeSpan.Zero);

        var rule = BudgetRule.Sliding(0m, window);
        return GetCurrentSpendAsync(key, rule, cancellationToken);
    }

    private static DateTimeOffset GetCalendarPeriodStart(BudgetWindowType windowType, DateTimeOffset utcNow) =>
        windowType switch
        {
            BudgetWindowType.FixedUtcDay => new DateTimeOffset(utcNow.Year, utcNow.Month, utcNow.Day, 0, 0, 0, TimeSpan.Zero),
            BudgetWindowType.FixedUtcMonth => new DateTimeOffset(utcNow.Year, utcNow.Month, 1, 0, 0, 0, TimeSpan.Zero),
            _ => utcNow,
        };

    private static DateTimeOffset GetCalendarPeriodEnd(BudgetWindowType windowType, DateTimeOffset periodStart) =>
        windowType switch
        {
            BudgetWindowType.FixedUtcDay => periodStart <= DateTimeOffset.MaxValue.AddDays(-1) ? periodStart.AddDays(1) : DateTimeOffset.MaxValue,
            BudgetWindowType.FixedUtcMonth => periodStart <= DateTimeOffset.MaxValue.AddMonths(-1) ? periodStart.AddMonths(1) : DateTimeOffset.MaxValue,
            _ => periodStart,
        };

    private static void EvictOrResetExpired(BudgetBucket bucket, DateTimeOffset utcNow)
    {
        if (bucket.WindowType == BudgetWindowType.Sliding)
        {
            var window = TimeSpan.FromTicks(bucket.SlidingTicks);
            TimeSpan maxSubtractable = utcNow - DateTimeOffset.MinValue;
            DateTimeOffset cutoff = window >= maxSubtractable ? DateTimeOffset.MinValue : utcNow - window;

            while (bucket.SlidingEntries.First is { } firstNode && firstNode.Value.Timestamp <= cutoff)
            {
                bucket.SpentUsd -= firstNode.Value.AmountUsd;
                bucket.SlidingEntries.RemoveFirst();
            }

            if (bucket.SlidingEntries.Count == 0 || bucket.SpentUsd < 0m)
            {
                bucket.SpentUsd = 0m;
            }
        }
        else
        {
            DateTimeOffset currentPeriodStart = GetCalendarPeriodStart(bucket.WindowType, utcNow);
            if (currentPeriodStart > bucket.PeriodStart)
            {
                bucket.PeriodStart = currentPeriodStart;
                bucket.SpentUsd = 0m;
                bucket.PendingCalendarReservations.Clear();
            }
        }
    }

    private static TimeSpan CalculateRetryAfter(
        BudgetBucket bucket,
        DateTimeOffset utcNow,
        decimal estimatedCostUsd,
        decimal maxAmountUsd)
    {
        if (bucket.WindowType == BudgetWindowType.Sliding)
        {
            var window = TimeSpan.FromTicks(bucket.SlidingTicks);
            decimal amountToFree = estimatedCostUsd - (maxAmountUsd - bucket.SpentUsd);
            decimal accumulated = 0m;

            foreach (SpendEntry entry in bucket.SlidingEntries)
            {
                accumulated += entry.AmountUsd;
                if (accumulated >= amountToFree)
                {
                    TimeSpan maxAddable = DateTimeOffset.MaxValue - entry.Timestamp;
                    DateTimeOffset expiresAt = window >= maxAddable ? DateTimeOffset.MaxValue : entry.Timestamp + window;
                    return expiresAt > utcNow ? expiresAt - utcNow : TimeSpan.Zero;
                }
            }

            return window;
        }

        DateTimeOffset periodEnd = GetCalendarPeriodEnd(bucket.WindowType, bucket.PeriodStart);
        return periodEnd > utcNow ? periodEnd - utcNow : TimeSpan.Zero;
    }

    private void ReconcileBucket(BudgetBucket bucket, decimal reservedCostUsd, decimal actualCostUsd)
    {
        decimal delta = actualCostUsd - reservedCostUsd;
        DateTimeOffset now = _timeProvider.GetUtcNow().ToUniversalTime();

        lock (bucket.SyncRoot)
        {
            EvictOrResetExpired(bucket, now);

            if (bucket.WindowType == BudgetWindowType.Sliding)
            {
                if (delta == 0m)
                {
                    return;
                }

                if (delta > 0m)
                {
                    bucket.SlidingEntries.AddLast(new SpendEntry(now, delta));
                    bucket.SpentUsd += delta;
                }
                else
                {
                    // Refund FIFO from the oldest entries so newer entries preserve their timestamps.
                    decimal toRefund = Math.Min(-delta, bucket.SpentUsd);
                    bucket.SpentUsd -= toRefund;

                    while (toRefund > 0m && bucket.SlidingEntries.First is { } firstNode)
                    {
                        SpendEntry entry = firstNode.Value;
                        if (entry.AmountUsd <= toRefund)
                        {
                            toRefund -= entry.AmountUsd;
                            bucket.SlidingEntries.RemoveFirst();
                        }
                        else
                        {
                            firstNode.Value = entry with { AmountUsd = entry.AmountUsd - toRefund };
                            toRefund = 0m;
                        }
                    }
                }
            }
            else
            {
                if (reservedCostUsd > 0m)
                {
                    // If this reservation did not belong to the active calendar period (e.g. an in-flight request crossed UTC midnight),
                    // do not corrupt the new period's spend.
                    if (!bucket.TryConsumePendingCalendarReservation(reservedCostUsd))
                    {
                        return;
                    }
                }

                if (delta == 0m)
                {
                    return;
                }

                decimal updated = bucket.SpentUsd + delta;
                bucket.SpentUsd = updated < 0m ? 0m : updated;
            }
        }
    }

    private readonly record struct BucketKey(string ScopeKey, BudgetWindowType WindowType, long SlidingTicks)
    {
        public static BucketKey FromRule(string scopeKey, BudgetRule rule) =>
            new(
                scopeKey,
                rule.WindowType,
                rule.WindowType == BudgetWindowType.Sliding && rule.SlidingWindow.HasValue
                    ? rule.SlidingWindow.Value.Ticks
                    : 0L);
    }

    private readonly record struct SpendEntry(DateTimeOffset Timestamp, decimal AmountUsd);

    private sealed class BudgetBucket(BudgetWindowType windowType, long slidingTicks, DateTimeOffset initialUtcTime)
    {
        public Lock SyncRoot { get; } = new();

        public BudgetWindowType WindowType { get; } = windowType;

        public long SlidingTicks { get; } = slidingTicks;

        public DateTimeOffset PeriodStart { get; set; } = GetCalendarPeriodStart(windowType, initialUtcTime);

        public LinkedList<SpendEntry> SlidingEntries { get; } = new();

        public Dictionary<decimal, int> PendingCalendarReservations { get; } = [];

        public decimal SpentUsd { get; set; }

        public void AddPendingCalendarReservation(decimal amountUsd)
        {
            PendingCalendarReservations.TryGetValue(amountUsd, out int count);
            PendingCalendarReservations[amountUsd] = count + 1;
        }

        public bool TryConsumePendingCalendarReservation(decimal amountUsd)
        {
            if (!PendingCalendarReservations.TryGetValue(amountUsd, out int count) || count <= 0)
            {
                return false;
            }

            if (count == 1)
            {
                PendingCalendarReservations.Remove(amountUsd);
            }
            else
            {
                PendingCalendarReservations[amountUsd] = count - 1;
            }

            return true;
        }
    }
}
