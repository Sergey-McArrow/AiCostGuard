using AiCostGuard.Exceptions;
using AiCostGuard.Options;
using AiCostGuard.RateLimiting;
using AiCostGuard.Storage;
using AiCostGuard.Tests.Fakes;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Time.Testing;

namespace AiCostGuard.Tests;

/// <summary>
/// Adversarial tests proving concurrency flaws, race conditions, and non-atomic reservation leaks.
/// </summary>
public sealed class AdversarialConcurrencyTests
{
    [Fact]
    public async Task AcquireBudgetRules_CancellationDuringSecondRule_MustRollBackTpmAndFirstRuleReservation()
    {
        FakeTimeProvider fakeTime = new(new DateTimeOffset(2026, 5, 1, 12, 0, 0, TimeSpan.Zero));
        FakeChatClient primary = new(defaultModelId: "exact-model", inputTokens: 0, outputTokens: 1_000);
        SlidingWindowTokenRateLimiter rateLimiter = new(fakeTime);
        InMemoryBudgetStore innerStore = new(fakeTime);

        var dailyRule = BudgetRule.FixedUtcDay(10.00m, LimitExceededAction.Throw);
        var monthlyRule = BudgetRule.FixedUtcMonth(100.00m, LimitExceededAction.Throw);

        using CancellationTokenSource cts = new();
        CancellingSecondReserveBudgetStore faultyStore = new(innerStore, cts);

        CostGuardOptions options = new()
        {
            MaxTokensPerMinute = 1_000,
            EstimatedOutputTokensBuffer = 1_000,
            OnRateLimitExceeded = LimitExceededAction.Throw,
        };
        options.SetModelPricing("exact-model", 0m, 1_000m); // 1,000 tokens = $1.00
        options.AddBudgetRule(dailyRule);
        options.AddBudgetRule(monthlyRule);

        using CostGuardChatClient client = new(
            primary,
            options,
            rateLimiter: rateLimiter,
            budgetStore: faultyStore,
            timeProvider: fakeTime);

        // Rule 0 (daily) reserves $1.00, then cancellation triggers before Rule 1 (monthly) completes.
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            client.GetResponseAsync([], cancellationToken: cts.Token));

        // Expected safe behavior: both the Rule 0 budget reservation AND the TPM reservation must be rolled back.
        decimal leakedDailySpend = await innerStore.GetCurrentSpendAsync("default", dailyRule);
        Assert.Equal(0m, leakedDailySpend);

        TokenLeaseResult probeTpm = await rateLimiter.TryAcquireAsync("default", 1_000, 1_000);
        Assert.True(probeTpm.IsAllowed, "TPM reservation leaked when multi-rule budget acquisition threw OperationCanceledException.");
    }

    [Fact]
    public async Task AcquireBudgetRules_ConcurrentMultiRuleProbe_MustNotFalselyRejectSecondRequest()
    {
        FakeTimeProvider fakeTime = new(new DateTimeOffset(2026, 5, 1, 12, 0, 0, TimeSpan.Zero));
        FakeChatClient primary = new(defaultModelId: "exact-model", inputTokens: 0, outputTokens: 1_000);
        InMemoryBudgetStore innerStore = new(fakeTime);

        // Rule 0 (daily) allows $1.00; Rule 1 (monthly) is already full ($1.00 / $1.00).
        // Request A will reserve $1.00 on Rule 0, pause before checking Rule 1 (which will fail and roll back Rule 0).
        // While Request A holds the phantom reservation on Rule 0, Request B (which only needs $0.50 on Rule 0? No, same tenant)
        // arrives — wait: to isolate Rule 0 phantom reservation from Rule 1, let Rule 0 be $1.00 and Rule 1 be $1.50:
        // Request A ($1.00) reserves Rule 0 ($1.00/$1.00), then fails Rule 1 (pre-spent $1.00 + $1.00 > $1.50) and rolls back.
        // Request B ($0.50) SHOULD succeed because Request A never actually passes Rule 1 ($0.50 fits in both $1.00 daily and $1.50 monthly).
        var dailyRule = BudgetRule.FixedUtcDay(1.00m, LimitExceededAction.Throw);
        var monthlyRule = BudgetRule.FixedUtcMonth(1.50m, LimitExceededAction.Throw);

        // Pre-spend $1.00 on monthly rule only (e.g., from previous days in the same month).
        await innerStore.TryReserveAsync("default", 1.00m, monthlyRule);

        TaskCompletionSource requestAReservedDaily = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource releaseRequestA = new(TaskCreationOptions.RunContinuationsAsynchronously);

        InterceptingBudgetStore interceptingStore = new(
            innerStore,
            onAfterReserve: (rule, reservation) =>
            {
                if (rule.WindowType == BudgetWindowType.FixedUtcDay && reservation.IsAllowed && !requestAReservedDaily.Task.IsCompleted)
                {
                    requestAReservedDaily.TrySetResult();
                    return releaseRequestA.Task;
                }

                return Task.CompletedTask;
            });

        CostGuardOptions options = new()
        {
            EstimatedOutputTokensBuffer = 1_000,
        };
        options.SetModelPricing("exact-model", 0m, 1_000m); // 1,000 tokens = $1.00, 500 tokens = $0.50
        options.AddBudgetRule(dailyRule);
        options.AddBudgetRule(monthlyRule);

        using CostGuardChatClient client = new(primary, options, budgetStore: interceptingStore, timeProvider: fakeTime);

        // Start Request A ($1.00): reserves daily ($1.00/$1.00), pauses before checking monthly (where it will fail).
        Task requestATask = client.GetResponseAsync([], new ChatOptions { MaxOutputTokens = 1_000 });
        await requestAReservedDaily.Task;

        // While Request A holds the uncommitted daily reservation, Request B ($0.50) arrives.
        // Request B fits in both daily ($0.50 <= $1.00) and monthly ($1.00 + $0.50 <= $1.50).
        Task<ChatResponse> requestBTask = client.GetResponseAsync([], new ChatOptions { MaxOutputTokens = 500 });

        // Release Request A so it fails monthly and rolls back daily.
        releaseRequestA.TrySetResult();

        await Assert.ThrowsAsync<CostGuardLimitExceededException>(() => requestATask);

        // Expected safe behavior: Request B must succeed rather than being falsely rejected by Request A's phantom reservation.
        ChatResponse responseB = await requestBTask;
        Assert.NotNull(responseB);
    }

    [Fact]
    public async Task BudgetWaitMode_WhenWaitingExceedsOneMinute_MustNotHoldOrExpireTpmReservation()
    {
        FakeTimeProvider fakeTime = new(new DateTimeOffset(2026, 5, 1, 12, 0, 0, TimeSpan.Zero));
        FakeChatClient primary = new(defaultModelId: "exact-model", inputTokens: 0, outputTokens: 1_000);
        SlidingWindowTokenRateLimiter rateLimiter = new(fakeTime);
        InMemoryBudgetStore store = new(fakeTime);

        // TPM = 1,000 tokens/min; Budget = $1.00 per 2-minute sliding window with Wait (MaxWaitTimeout = 3 min).
        var budgetRule = BudgetRule.Sliding(1.00m, TimeSpan.FromMinutes(2), LimitExceededAction.Wait);

        CostGuardOptions options = new()
        {
            MaxTokensPerMinute = 1_000,
            OnRateLimitExceeded = LimitExceededAction.Throw,
            EstimatedOutputTokensBuffer = 1_000,
            MaxWaitTimeout = TimeSpan.FromMinutes(3),
        };
        options.SetModelPricing("exact-model", 0m, 1_000m); // 1,000 tokens = $1.00
        options.AddBudgetRule(budgetRule);

        using CostGuardChatClient client = new(
            primary,
            options,
            rateLimiter: rateLimiter,
            budgetStore: store,
            timeProvider: fakeTime);

        // Pre-spend $1.00 on budget at t=0 (expires at t=120s). TPM is currently 0.
        await store.TryReserveAsync("default", 1.00m, budgetRule);

        // At t=0, Request A starts: it acquires TPM (1,000/1,000), then enters Budget Wait for 120 seconds!
        Task<ChatResponse> requestATask = client.GetResponseAsync([]);

        // Give Request A a moment to enter Task.Delay(2m)
        await Task.Yield();

        // Advance time by 10 seconds (t=10s): Request A is sleeping on budget, NOT executing an LLM call.
        fakeTime.Advance(TimeSpan.FromSeconds(10));

        // While Request A is blocked waiting for budget, TPM capacity should NOT be held hostage by a sleeping waiter.
        TokenLeaseResult otherCallerLease = await rateLimiter.TryAcquireAsync("default", 500, 1_000);

        // Advance past 2 minutes so Request A can finish cleanly even if assertion fails.
        fakeTime.Advance(TimeSpan.FromMinutes(2));
        await requestATask;

        Assert.True(otherCallerLease.IsAllowed, "Request sleeping in Budget Wait mode held TPM reservation hostage and starved other callers.");
    }

    [Fact]
    public async Task ReconcileReservations_WhenRateLimiterReconcileThrows_MustStillRollBackBudgetReservation()
    {
        FakeTimeProvider fakeTime = new(new DateTimeOffset(2026, 5, 1, 12, 0, 0, TimeSpan.Zero));
        FakeChatClient primary = new(defaultModelId: "exact-model", inputTokens: 0, outputTokens: 1_000)
        {
            ExceptionToThrow = new HttpRequestException("Downstream LLM failure"),
        };

        InMemoryBudgetStore store = new(fakeTime);
        ThrowingReconcileRateLimiter faultyLimiter = new();
        var rule = BudgetRule.FixedUtcDay(10.00m, LimitExceededAction.Throw);

        CostGuardOptions options = new()
        {
            MaxTokensPerMinute = 1_000,
            EstimatedOutputTokensBuffer = 1_000,
        };
        options.SetModelPricing("exact-model", 0m, 1_000m); // 1,000 tokens = $1.00
        options.AddBudgetRule(rule);

        using CostGuardChatClient client = new(
            primary,
            options,
            rateLimiter: faultyLimiter,
            budgetStore: store,
            timeProvider: fakeTime);

        await Assert.ThrowsAnyAsync<Exception>(() => client.GetResponseAsync([]));

        // Expected safe behavior: even if rateLimiter.ReconcileAsync throws, budgetStore.ReconcileAsync MUST still run so budget is not leaked.
        decimal leakedSpend = await store.GetCurrentSpendAsync("default", rule);
        Assert.Equal(0m, leakedSpend);
    }

    private sealed class CancellingSecondReserveBudgetStore(
        IBudgetStore inner,
        CancellationTokenSource cts) : IBudgetStore
    {
        private int _reserveCalls;

        public ValueTask<BudgetReservation> TryReserveAsync(string key, decimal estimatedCostUsd, BudgetRule rule, CancellationToken cancellationToken = default)
        {
            int call = Interlocked.Increment(ref _reserveCalls);
            if (call == 2)
            {
                cts.Cancel();
            }

            return inner.TryReserveAsync(key, estimatedCostUsd, rule, cancellationToken);
        }

        public ValueTask<BudgetReservation> TryReserveAsync(string key, decimal estimatedCostUsd, decimal maxBudgetUsd, TimeSpan window, CancellationToken cancellationToken = default) =>
            inner.TryReserveAsync(key, estimatedCostUsd, maxBudgetUsd, window, cancellationToken);

        public ValueTask ReconcileAsync(string key, decimal reservedCostUsd, decimal actualCostUsd, BudgetRule rule, CancellationToken cancellationToken = default) =>
            inner.ReconcileAsync(key, reservedCostUsd, actualCostUsd, rule, cancellationToken);

        public ValueTask ReconcileAsync(string key, decimal reservedCostUsd, decimal actualCostUsd, CancellationToken cancellationToken = default) =>
            inner.ReconcileAsync(key, reservedCostUsd, actualCostUsd, cancellationToken);

        public ValueTask<decimal> GetCurrentSpendAsync(string key, BudgetRule rule, CancellationToken cancellationToken = default) =>
            inner.GetCurrentSpendAsync(key, rule, cancellationToken);

        public ValueTask<decimal> GetCurrentSpendAsync(string key, TimeSpan window, CancellationToken cancellationToken = default) =>
            inner.GetCurrentSpendAsync(key, window, cancellationToken);
    }

    private sealed class InterceptingBudgetStore(
        IBudgetStore inner,
        Func<BudgetRule, BudgetReservation, Task> onAfterReserve) : IBudgetStore
    {
        public async ValueTask<BudgetReservation> TryReserveAsync(string key, decimal estimatedCostUsd, BudgetRule rule, CancellationToken cancellationToken = default)
        {
            BudgetReservation res = await inner.TryReserveAsync(key, estimatedCostUsd, rule, cancellationToken).ConfigureAwait(false);
            await onAfterReserve(rule, res).ConfigureAwait(false);
            return res;
        }

        public ValueTask<BudgetReservation> TryReserveAsync(string key, decimal estimatedCostUsd, decimal maxBudgetUsd, TimeSpan window, CancellationToken cancellationToken = default) =>
            inner.TryReserveAsync(key, estimatedCostUsd, maxBudgetUsd, window, cancellationToken);

        public ValueTask ReconcileAsync(string key, decimal reservedCostUsd, decimal actualCostUsd, BudgetRule rule, CancellationToken cancellationToken = default) =>
            inner.ReconcileAsync(key, reservedCostUsd, actualCostUsd, rule, cancellationToken);

        public ValueTask ReconcileAsync(string key, decimal reservedCostUsd, decimal actualCostUsd, CancellationToken cancellationToken = default) =>
            inner.ReconcileAsync(key, reservedCostUsd, actualCostUsd, cancellationToken);

        public ValueTask<decimal> GetCurrentSpendAsync(string key, BudgetRule rule, CancellationToken cancellationToken = default) =>
            inner.GetCurrentSpendAsync(key, rule, cancellationToken);

        public ValueTask<decimal> GetCurrentSpendAsync(string key, TimeSpan window, CancellationToken cancellationToken = default) =>
            inner.GetCurrentSpendAsync(key, window, cancellationToken);
    }

    private sealed class ThrowingReconcileRateLimiter : ITokenRateLimiter
    {
        public ValueTask<TokenLeaseResult> TryAcquireAsync(string key, long estimatedTokens, long maxTokensPerMinute, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new TokenLeaseResult(true, estimatedTokens, null));

        public ValueTask ReconcileAsync(string key, long reservedTokens, long actualTokens, CancellationToken cancellationToken = default) =>
            ValueTask.FromException(new InvalidOperationException("Simulated Redis/store failure during TPM reconcile."));
    }
}
