using AiCostGuard.DependencyInjection;
using AiCostGuard.Options;
using AiCostGuard.Pricing;
using AiCostGuard.RateLimiting;
using AiCostGuard.Storage;
using AiCostGuard.Tests.Fakes;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace AiCostGuard.Tests;

/// <summary>
/// Adversarial tests proving state corruption, financial accounting flaws, and mutable shared state bugs.
/// </summary>
public sealed class AdversarialStateCorruptionTests
{
    [Fact]
    public async Task MultiRuleSameWindowType_SoftDailyFallbackAndHardDailyThrow_MustNotDoubleCountSpend()
    {
        FakeTimeProvider fakeTime = new(new DateTimeOffset(2026, 5, 1, 12, 0, 0, TimeSpan.Zero));
        FakeChatClient primary = new(defaultModelId: "exact-model", responseText: "Primary", inputTokens: 0, outputTokens: 1_000);
        FakeChatClient fallback = new(defaultModelId: "llama3.2", responseText: "Fallback", inputTokens: 0, outputTokens: 1_000);
        InMemoryBudgetStore store = new(fakeTime);

        var softDaily = BudgetRule.FixedUtcDay(5.00m, LimitExceededAction.Fallback);
        var hardDaily = BudgetRule.FixedUtcDay(10.00m, LimitExceededAction.Throw);

        CostGuardOptions options = new()
        {
            EstimatedOutputTokensBuffer = 1_000,
            FallbackClient = fallback,
        };
        options.SetModelPricing("exact-model", 0m, 1_000m); // 1,000 tokens = $1.00 USD
        options.AddBudgetRule(softDaily);
        options.AddBudgetRule(hardDaily);

        using CostGuardChatClient client = new(primary, options, budgetStore: store, timeProvider: fakeTime);

        // Single $1.00 request must record $1.00 spend, NOT $2.00 due to BucketKey collision!
        await client.GetResponseAsync([]);

        decimal recordedSpend = await store.GetCurrentSpendAsync("default", softDaily);
        Assert.Equal(1.00m, recordedSpend);
    }

    [Fact]
    public async Task FixedUtcDay_InFlightRequestFailsAfterMidnight_MustNotEraseNextDaySpend()
    {
        FakeTimeProvider fakeTime = new(new DateTimeOffset(2026, 5, 1, 23, 59, 59, TimeSpan.Zero));
        InMemoryBudgetStore store = new(fakeTime);
        var dailyRule = BudgetRule.FixedUtcDay(10.00m, LimitExceededAction.Throw);

        // Day 1 (23:59:59 UTC): Request A reserves $1.00.
        BudgetReservation reqA = await store.TryReserveAsync("tenant-a", 1.00m, dailyRule);
        Assert.True(reqA.IsAllowed);

        // Clock crosses midnight into Day 2 (00:00:01 UTC).
        fakeTime.Advance(TimeSpan.FromSeconds(2));

        // Day 2 (00:00:01 UTC): Request B reserves and completes for $0.80.
        BudgetReservation reqB = await store.TryReserveAsync("tenant-a", 0.80m, dailyRule);
        Assert.True(reqB.IsAllowed);

        // Now Request A (which was reserved on Day 1) fails and rolls back ($1.00 -> $0.00).
        await store.ReconcileAsync("tenant-a", reservedCostUsd: 1.00m, actualCostUsd: 0m, dailyRule);

        // Expected safe behavior: Day 2 spend must still be $0.80 (Request B), NOT wiped out to $0.00 by Day 1's rollback!
        decimal day2Spend = await store.GetCurrentSpendAsync("tenant-a", dailyRule);
        Assert.Equal(0.80m, day2Spend);
    }

    [Fact]
    public async Task SlidingWindowTokenRateLimiter_OlderRequestRefundsAfterNewerRequest_MustNotEraseNewerEntryTimestamp()
    {
        FakeTimeProvider fakeTime = new(new DateTimeOffset(2026, 5, 1, 12, 0, 0, TimeSpan.Zero));
        SlidingWindowTokenRateLimiter limiter = new(fakeTime);

        // t=0s: Request A reserves 500 tokens.
        TokenLeaseResult reqA = await limiter.TryAcquireAsync("tenant-a", 500, maxTokensPerMinute: 1_000);
        Assert.True(reqA.IsAllowed);

        // t=30s: Request B reserves 500 tokens and consumes all 500 tokens (no reconcile delta).
        fakeTime.Advance(TimeSpan.FromSeconds(30));
        TokenLeaseResult reqB = await limiter.TryAcquireAsync("tenant-a", 500, maxTokensPerMinute: 1_000);
        Assert.True(reqB.IsAllowed);

        // t=40s: Request A (from t=0s) fails and refunds 500 tokens.
        fakeTime.Advance(TimeSpan.FromSeconds(10));
        await limiter.ReconcileAsync("tenant-a", reservedTokens: 500, actualTokens: 0);

        // t=61s: Request A's original timestamp (t=0s) has expired, but Request B (t=30s, 500 tokens) MUST still be active until t=90s!
        fakeTime.Advance(TimeSpan.FromSeconds(21));

        // Requesting 600 tokens at t=61s MUST be rejected (500 active + 600 = 1,100 > 1,000).
        TokenLeaseResult reqC = await limiter.TryAcquireAsync("tenant-a", 600, maxTokensPerMinute: 1_000);
        Assert.False(reqC.IsAllowed, "LIFO refund deleted Request B's newer (t=30s) entry instead of Request A's older (t=0s) entry, letting Request B's quota expire 30s early.");
    }

    [Fact]
    public async Task InMemoryBudgetStore_SlidingWindowOlderRequestRefundsAfterNewerRequest_MustNotEraseNewerEntryTimestamp()
    {
        FakeTimeProvider fakeTime = new(new DateTimeOffset(2026, 5, 1, 12, 0, 0, TimeSpan.Zero));
        InMemoryBudgetStore store = new(fakeTime);
        var rule = BudgetRule.Sliding(10.00m, TimeSpan.FromMinutes(1), LimitExceededAction.Throw);

        // t=0s: Request A reserves $5.00.
        await store.TryReserveAsync("tenant-a", 5.00m, rule);

        // t=30s: Request B reserves $5.00 (legitimate spend that should remain in window until t=90s).
        fakeTime.Advance(TimeSpan.FromSeconds(30));
        await store.TryReserveAsync("tenant-a", 5.00m, rule);

        // t=40s: Request A fails and refunds $5.00.
        fakeTime.Advance(TimeSpan.FromSeconds(10));
        await store.ReconcileAsync("tenant-a", reservedCostUsd: 5.00m, actualCostUsd: 0m, rule);

        // t=61s: Request B's $5.00 spend from t=30s MUST still be counted!
        fakeTime.Advance(TimeSpan.FromSeconds(21));
        decimal spendAt61s = await store.GetCurrentSpendAsync("tenant-a", rule);
        Assert.Equal(5.00m, spendAt61s);
    }

    [Fact]
    public async Task GetResponseAsync_WhenResponseModelIdDiffersFromDefaultPricing_MustBillUsingResponseModelPricing()
    {
        FakeTimeProvider fakeTime = new(new DateTimeOffset(2026, 5, 1, 12, 0, 0, TimeSpan.Zero));
        // Client metadata has no default model ID, but server responds with ModelId = "expensive-model".
        FakeChatClient primary = new(defaultModelId: null, inputTokens: 0, outputTokens: 1_000);
        InMemoryBudgetStore store = new(fakeTime);
        var rule = BudgetRule.FixedUtcDay(100.00m, LimitExceededAction.Throw);

        CostGuardOptions options = new()
        {
            EstimatedOutputTokensBuffer = 1_000,
            DefaultPricing = new ModelPricing(1m, 1m), // Cheap fallback estimate ($0.001)
        };
        options.SetModelPricing("expensive-model", 0m, 1_000m); // Actual model costs $1.00 per 1,000 output tokens
        options.AddBudgetRule(rule);

        // Inner client returns response.ModelId = "expensive-model" even though ChatOptions.ModelId was null.
        IChatClient modelReportingClient = new ModelOverrideChatClient(primary, "expensive-model");
        using CostGuardChatClient client = new(modelReportingClient, options, budgetStore: store, timeProvider: fakeTime);

        await client.GetResponseAsync([]);

        decimal billedSpend = await store.GetCurrentSpendAsync("default", rule);
        Assert.Equal(1.00m, billedSpend);
    }

    [Fact]
    public void UseCostGuard_MultiplePipelinesSharingOptionsFromDi_MustNotMutateSharedSingletonOptions()
    {
        ServiceCollection services = new();
        services.Configure<CostGuardOptions>(opt => opt.MaxTokensPerMinute = 5_000);

        using ServiceProvider sp = services.BuildServiceProvider();
        FakeChatClient inner1 = new();
        FakeChatClient inner2 = new();
        FakeChatClient fallback1 = new();
        FakeChatClient fallback2 = new();

        IChatClient pipeline1 = new ChatClientBuilder(inner1)
            .UseCostGuard(opt => opt.AddBudgetRule(5.00m, BudgetWindowType.FixedUtcDay, LimitExceededAction.Fallback), fallback1)
            .Build(sp);

        IChatClient pipeline2 = new ChatClientBuilder(inner2)
            .UseCostGuard(opt => opt.AddBudgetRule(20.00m, BudgetWindowType.FixedUtcMonth, LimitExceededAction.Throw), fallback2)
            .Build(sp);

        Assert.NotNull(pipeline1);
        Assert.NotNull(pipeline2);

        // Resolving IOptions<CostGuardOptions>.Value should NOT have been mutated with rules from both pipelines!
        CostGuardOptions sharedOptions = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<CostGuardOptions>>().Value;
        Assert.Empty(sharedOptions.BudgetRules);
        Assert.Null(sharedOptions.FallbackClient);
    }

    [Fact]
    public async Task CostGuardChatClient_MutatingBudgetRulesAfterConstruction_MustNotAffectRunningClient()
    {
        FakeTimeProvider fakeTime = new(new DateTimeOffset(2026, 5, 1, 12, 0, 0, TimeSpan.Zero));
        FakeChatClient primary = new(defaultModelId: "exact-model", inputTokens: 0, outputTokens: 1_000);
        InMemoryBudgetStore store = new(fakeTime);

        var rule = BudgetRule.FixedUtcDay(0.50m, LimitExceededAction.Throw);
        CostGuardOptions options = new()
        {
            EstimatedOutputTokensBuffer = 1_000,
        };
        options.SetModelPricing("exact-model", 0m, 1_000m); // 1,000 tokens = $1.00 (> $0.50 limit)
        options.AddBudgetRule(rule);

        using CostGuardChatClient client = new(primary, options, budgetStore: store, timeProvider: fakeTime);

        // Adversary clears the mutable BudgetRules list after construction!
        options.BudgetRules.Clear();

        // Expected safe behavior: the constructed client must have snapshotted its rules and still enforce the $0.50 cap.
        await Assert.ThrowsAsync<Exceptions.CostGuardLimitExceededException>(() => client.GetResponseAsync([]));
    }

    [Fact]
    public async Task GetStreamingResponseAsync_UsageContentWithNullInputTokens_MustFallBackToEstimatedInputLikeGetResponseAsync()
    {
        FakeTimeProvider fakeTime = new(new DateTimeOffset(2026, 5, 1, 12, 0, 0, TimeSpan.Zero));
        InMemoryBudgetStore store = new(fakeTime);
        var rule = BudgetRule.FixedUtcDay(100.00m, LimitExceededAction.Throw);

        // Stream emits UsageContent with OutputTokenCount = 100, but InputTokenCount = null (common in some providers).
        IChatClient partialUsageClient = new PartialUsageStreamingClient(outputTokens: 100);

        CostGuardOptions options = new()
        {
            EstimatedOutputTokensBuffer = 100,
        };
        options.SetModelPricing("exact-model", 1_000m, 0m); // Input costs $1.00 per 1,000 tokens; output is $0
        options.AddBudgetRule(rule);

        using CostGuardChatClient client = new(partialUsageClient, options, budgetStore: store, timeProvider: fakeTime);

        // Prompt has 4,000 chars -> ~1,004 estimated input tokens (~$1.004).
        ChatMessage[] prompt = [new(ChatRole.User, new string('a', 4_000))];
        await foreach (ChatResponseUpdate _ in client.GetStreamingResponseAsync(prompt, new ChatOptions { ModelId = "exact-model" }))
        {
        }

        // Expected safe behavior (consistent with GetResponseAsync): when InputTokenCount is null, fall back to estimated input tokens (> $1.00), NOT $0.00!
        decimal spend = await store.GetCurrentSpendAsync("default", rule);
        Assert.True(spend > 1.00m, $"Expected streaming spend > $1.00 when InputTokenCount is null, but got ${spend}.");
    }

    private sealed class ModelOverrideChatClient(IChatClient inner, string responseModelId) : DelegatingChatClient(inner)
    {
        public override async Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            ChatResponse response = await base.GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
            response.ModelId = responseModelId;
            return response;
        }
    }

    private sealed class PartialUsageStreamingClient(long outputTokens) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant, "hello") { ModelId = options?.ModelId };
            yield return new ChatResponseUpdate
            {
                Role = ChatRole.Assistant,
                ModelId = options?.ModelId,
                Contents =
                [
                    new UsageContent(new UsageDetails
                    {
                        InputTokenCount = null,
                        OutputTokenCount = outputTokens,
                        TotalTokenCount = null,
                    }),
                ],
            };
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
