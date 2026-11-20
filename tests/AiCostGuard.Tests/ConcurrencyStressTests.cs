using AiCostGuard.Exceptions;
using AiCostGuard.Options;
using AiCostGuard.Storage;
using AiCostGuard.Tests.Fakes;
using Microsoft.Extensions.Time.Testing;

namespace AiCostGuard.Tests;

public sealed class ConcurrencyStressTests
{
    [Fact]
    public async Task GetResponseAsync_100ConcurrentRequestsUnderStrictBudget_NeverExceedsCapOrCorruptsState()
    {
        FakeTimeProvider fakeTime = new(new DateTimeOffset(2026, 5, 1, 12, 0, 0, TimeSpan.Zero));
        // Each request has 0 input buffer trick: we use MaxOutputTokens = 500 and a message of 496 tokens -> 500 + 500 = 1,000 tokens.
        // Simpler: 0 input tokens (empty prompt) + 1,000 MaxOutputTokens = 1,000 estimated tokens, and FakeChatClient reports 0 input + 1,000 output = 1,000 actual tokens.
        // At $50 per 1M output tokens, each request costs exactly $0.05 USD (both estimated and actual!).
        FakeChatClient primary = new(defaultModelId: "exact-model", inputTokens: 0, outputTokens: 1_000);
        InMemoryBudgetStore store = new(fakeTime);
        var rule = BudgetRule.FixedUtcDay(1.00m, LimitExceededAction.Throw); // Cap = $1.00 -> exactly 20 requests allowed!

        CostGuardOptions options = new()
        {
            EstimatedOutputTokensBuffer = 1_000,
        };
        options.SetModelPricing("exact-model", 0m, 50m);
        options.AddBudgetRule(rule);

        using CostGuardChatClient client = new(primary, options, budgetStore: store, timeProvider: fakeTime);

        int successCount = 0;
        int rejectedCount = 0;

        Task[] tasks =
        [
            .. Enumerable.Range(0, 100)
                .Select(async _ =>
                {
                    await Task.Yield();
                    try
                    {
                        await client.GetResponseAsync([]);
                        Interlocked.Increment(ref successCount);
                    }
                    catch (CostGuardLimitExceededException ex) when (ex.Reason == LimitExceededReason.BudgetExceeded)
                    {
                        Interlocked.Increment(ref rejectedCount);
                    }
                }),
        ];

        await Task.WhenAll(tasks);

        Assert.Equal(20, successCount);
        Assert.Equal(80, rejectedCount);
        Assert.Equal(20, primary.CallCount);

        decimal finalSpend = await store.GetCurrentSpendAsync("default", rule);
        Assert.Equal(1.00m, finalSpend);
    }

    [Fact]
    public async Task GetResponseAsync_100ConcurrentRequestsUnderStrictTpm_NeverExceedsMaxTokensPerMinute()
    {
        FakeTimeProvider fakeTime = new(new DateTimeOffset(2026, 5, 1, 12, 0, 0, TimeSpan.Zero));
        // Empty prompt (0 input) + 100 output buffer = 100 estimated tokens; actual = 100 tokens.
        // MaxTokensPerMinute = 2,500 -> exactly 25 requests allowed out of 100!
        FakeChatClient primary = new(defaultModelId: "gpt-4o", inputTokens: 0, outputTokens: 100);

        CostGuardOptions options = new()
        {
            MaxTokensPerMinute = 2_500,
            EstimatedOutputTokensBuffer = 100,
            OnRateLimitExceeded = LimitExceededAction.Throw,
        };

        using CostGuardChatClient client = new(primary, options, timeProvider: fakeTime);

        int successCount = 0;
        int rejectedCount = 0;

        Task[] tasks =
        [
            .. Enumerable.Range(0, 100)
                .Select(async _ =>
                {
                    await Task.Yield();
                    try
                    {
                        await client.GetResponseAsync([]);
                        Interlocked.Increment(ref successCount);
                    }
                    catch (CostGuardLimitExceededException ex) when (ex.Reason == LimitExceededReason.RateLimitExceeded)
                    {
                        Interlocked.Increment(ref rejectedCount);
                    }
                }),
        ];

        await Task.WhenAll(tasks);

        Assert.Equal(25, successCount);
        Assert.Equal(75, rejectedCount);
        Assert.Equal(25, primary.CallCount);
    }
}
