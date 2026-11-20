using AiCostGuard.Exceptions;
using AiCostGuard.Options;
using AiCostGuard.Storage;
using AiCostGuard.Tests.Fakes;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Time.Testing;

namespace AiCostGuard.Tests;

public sealed class BudgetEnforcementTests
{
    [Fact]
    public async Task GetResponseAsync_SlidingBudgetWindow_BlocksWhenExhaustedAndResetsAfter24Hours()
    {
        FakeTimeProvider fakeTime = new(new DateTimeOffset(2026, 6, 15, 10, 0, 0, TimeSpan.Zero));
        // Configure custom pricing: $100 per 1M input, $100 per 1M output -> 1,000 tokens = $0.10 USD
        FakeChatClient primary = new(defaultModelId: "custom-model", inputTokens: 500, outputTokens: 500);
        InMemoryBudgetStore store = new(fakeTime);

        CostGuardOptions options = new()
        {
            EstimatedOutputTokensBuffer = 500,
        };
        options.SetModelPricing("custom-model", 100m, 100m);
        options.AddBudgetRule(maxUsd: 0.15m, slidingWindow: TimeSpan.FromHours(24), LimitExceededAction.Throw);

        using CostGuardChatClient client = new(primary, options, budgetStore: store, timeProvider: fakeTime);
        ChatMessage[] prompt = [new(ChatRole.User, "Hello")];

        // 1st call spends $0.10 USD (remaining: $0.05)
        await client.GetResponseAsync(prompt);
        Assert.Equal(0.10m, await store.GetCurrentSpendAsync("default", TimeSpan.FromHours(24)));

        // Advance 23 hours: 2nd call (~$0.0505 estimated) exceeds $0.15 limit
        fakeTime.Advance(TimeSpan.FromHours(23));

        CostGuardLimitExceededException ex = await Assert.ThrowsAsync<CostGuardLimitExceededException>(() =>
            client.GetResponseAsync(prompt));

        Assert.Equal(LimitExceededReason.BudgetExceeded, ex.Reason);
        Assert.Equal(0.10m, ex.CurrentUsage);
        Assert.Equal(0.15m, ex.Limit);
        Assert.Equal(TimeSpan.FromHours(1), ex.RetryAfter);

        // Advance 1 more hour (24h total): 1st spend expires from sliding window!
        fakeTime.Advance(TimeSpan.FromHours(1));

        await client.GetResponseAsync(prompt);
        Assert.Equal(2, primary.CallCount);
        Assert.Equal(0.10m, await store.GetCurrentSpendAsync("default", TimeSpan.FromHours(24)));
    }

    [Fact]
    public async Task GetResponseAsync_MultiRuleDailyFallbackAndMonthlyThrow_EnforcesBothAndPrioritizesThrowOverFallback()
    {
        // Start on May 30, 2026 at 20:00 UTC
        FakeTimeProvider fakeTime = new(new DateTimeOffset(2026, 5, 30, 20, 0, 0, TimeSpan.Zero));
        // 1,000 tokens = $1.00 USD ($1,000 per 1M tokens)
        FakeChatClient primary = new(defaultModelId: "pro-model", responseText: "Primary", inputTokens: 500, outputTokens: 500);
        FakeChatClient fallback = new(defaultModelId: "llama3.2", responseText: "Fallback", inputTokens: 500, outputTokens: 500);

        CostGuardOptions options = new()
        {
            EstimatedOutputTokensBuffer = 500,
            FallbackClient = fallback,
        };
        options.SetModelPricing("pro-model", 1_000m, 1_000m);
        // Rule 1: Soft daily budget of $1.50 -> Fallback
        options.AddBudgetRule(maxUsd: 1.50m, BudgetWindowType.FixedUtcDay, LimitExceededAction.Fallback);
        // Rule 2: Hard monthly cap of $2.50 -> Throw
        options.AddBudgetRule(maxUsd: 2.50m, BudgetWindowType.FixedUtcMonth, LimitExceededAction.Throw);

        using CostGuardChatClient client = new(primary, options, timeProvider: fakeTime);
        ChatMessage[] prompt = [new(ChatRole.User, "Test")];

        // Day 1, Call 1: costs $1.00 -> fits in Daily ($1.00 <= $1.50) and Monthly ($1.00 <= $2.50)
        ChatResponse r1 = await client.GetResponseAsync(prompt);
        Assert.Equal("Primary", r1.Text);

        // Day 1, Call 2: exceeds Daily ($1.00 + est > $1.50), fits in Monthly -> triggers Fallback!
        ChatResponse r2 = await client.GetResponseAsync(prompt);
        Assert.Equal("Fallback", r2.Text);
        Assert.Equal(1, fallback.CallCount);

        // Advance 5 hours to May 31, 2026 01:00 UTC (crosses midnight 00:00:00Z -> FixedUtcDay resets to $0!)
        fakeTime.Advance(TimeSpan.FromHours(5));

        // Day 2, Call 3: Daily is now $0, Monthly is $1.00 -> succeeds on Primary! (Monthly becomes $2.00)
        ChatResponse r3 = await client.GetResponseAsync(prompt);
        Assert.Equal("Primary", r3.Text);
        Assert.Equal(2, primary.CallCount);

        // Day 2, Call 4: exceeds BOTH Daily ($1.00 + est > $1.50 -> Fallback) AND Monthly ($2.00 + est > $2.50 -> Throw)
        // Because Throw has higher priority than Fallback, it MUST throw CostGuardLimitExceededException!
        CostGuardLimitExceededException ex = await Assert.ThrowsAsync<CostGuardLimitExceededException>(() =>
            client.GetResponseAsync(prompt));

        Assert.Equal(LimitExceededReason.BudgetExceeded, ex.Reason);
        Assert.Equal(2.50m, ex.Limit);

        // Advance to June 1, 2026 00:00:00Z (crosses month boundary -> both FixedUtcDay and FixedUtcMonth reset!)
        fakeTime.Advance(TimeSpan.FromHours(23));

        ChatResponse r5 = await client.GetResponseAsync(prompt);
        Assert.Equal("Primary", r5.Text);
        Assert.Equal(3, primary.CallCount);
    }

    [Fact]
    public async Task GetResponseAsync_DownstreamException_RollsBackReservedBudgetAndTpm()
    {
        FakeTimeProvider fakeTime = new(new DateTimeOffset(2026, 5, 1, 12, 0, 0, TimeSpan.Zero));
        FakeChatClient primary = new(defaultModelId: "gpt-4o")
        {
            ExceptionToThrow = new HttpRequestException("Simulated network failure"),
        };
        InMemoryBudgetStore store = new(fakeTime);
        var rule = BudgetRule.FixedUtcDay(5.00m);

        CostGuardOptions options = new()
        {
            MaxTokensPerMinute = 1000,
            EstimatedOutputTokensBuffer = 500,
        };
        options.AddBudgetRule(rule);

        using CostGuardChatClient client = new(primary, options, budgetStore: store, timeProvider: fakeTime);
        ChatMessage[] prompt = [new(ChatRole.User, "Hello")];

        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetResponseAsync(prompt));

        // Budget reservation must be rolled back to 0
        decimal currentSpend = await store.GetCurrentSpendAsync("default", rule);
        Assert.Equal(0m, currentSpend);

        // And once the network recovers, the quota is still available
        primary.ExceptionToThrow = null;
        await client.GetResponseAsync(prompt);
        Assert.Equal(2, primary.CallCount);
        Assert.True(await store.GetCurrentSpendAsync("default", rule) > 0m);
    }
}
