using AiCostGuard.Estimation;
using AiCostGuard.Exceptions;
using AiCostGuard.Options;
using AiCostGuard.Pricing;
using AiCostGuard.RateLimiting;
using AiCostGuard.Storage;
using AiCostGuard.Tests.Fakes;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Time.Testing;

namespace AiCostGuard.Tests;

/// <summary>
/// Adversarial tests proving integer/decimal overflows, extreme boundary crashes, and validation bypasses.
/// </summary>
public sealed class AdversarialBoundaryAndOverflowTests
{
    [Fact]
    public void HeuristicTokenEstimator_MassiveMessageEnumerable_MustNotOverflowIntToOneToken()
    {
        // A single 10M-character string (~20 MB RAM) repeated 1,000 times = 2.5 billion tokens (> int.MaxValue).
        // Because the same ChatMessage instance is yielded repeatedly, memory usage stays tiny (~20 MB).
        ChatMessage largeMessage = new(ChatRole.User, new string('a', 10_000_000));
        IEnumerable<ChatMessage> massivePrompt = Enumerable.Repeat(largeMessage, 1_000);

        int estimated = HeuristicTokenEstimator.Instance.EstimateTokens(massivePrompt);

        // Expected safe behavior: must saturate at int.MaxValue (or throw OverflowException), NEVER wrap around to negative and return 1!
        Assert.Equal(int.MaxValue, estimated);
    }

    [Fact]
    public async Task SlidingWindowTokenRateLimiter_NearLongMaxValue_MustNotOverflowAndBypassRateLimit()
    {
        FakeTimeProvider fakeTime = new(new DateTimeOffset(2026, 5, 1, 12, 0, 0, TimeSpan.Zero));
        SlidingWindowTokenRateLimiter limiter = new(fakeTime);

        const long MaxTpm = long.MaxValue;

        // 1st request takes almost the entire quota (only 5 tokens remain).
        TokenLeaseResult first = await limiter.TryAcquireAsync("tenant-a", long.MaxValue - 5, MaxTpm);
        Assert.True(first.IsAllowed);

        // 2nd request asks for 100 tokens (exceeds remaining 5 tokens, and CurrentTokens + 100 overflows long!).
        TokenLeaseResult second = await limiter.TryAcquireAsync("tenant-a", 100, MaxTpm);

        // Expected safe behavior: MUST be rejected and CurrentTokens must NEVER become negative!
        Assert.False(second.IsAllowed, "Signed 64-bit integer overflow allowed request past MaxTokensPerMinute!");
        Assert.True(second.CurrentTokens >= 0, $"CurrentTokens overflowed to negative value: {second.CurrentTokens}");
    }

    [Fact]
    public async Task InMemoryBudgetStore_SlidingWindowTimeSpanMaxValue_MustNotCrashWithArgumentOutOfRangeException()
    {
        FakeTimeProvider fakeTime = new(new DateTimeOffset(2026, 5, 1, 12, 0, 0, TimeSpan.Zero));
        InMemoryBudgetStore store = new(fakeTime);

        // BudgetRule.Sliding accepts TimeSpan.MaxValue because TimeSpan.MaxValue > TimeSpan.Zero.
        var rule = BudgetRule.Sliding(10.00m, TimeSpan.MaxValue, LimitExceededAction.Throw);

        // Expected safe behavior: either BudgetRule rejects unrepresentable window or store clamps cutoff to DateTimeOffset.MinValue instead of crashing.
        BudgetReservation reservation = await store.TryReserveAsync("default", 1.00m, rule);
        Assert.True(reservation.IsAllowed);
    }

    [Fact]
    public async Task CostGuardChatClient_MaxWaitTimeoutTimeSpanMaxValue_MustNotCrashWithArgumentOutOfRangeException()
    {
        FakeTimeProvider fakeTime = new(new DateTimeOffset(2026, 5, 1, 12, 0, 0, TimeSpan.Zero));
        FakeChatClient primary = new(defaultModelId: "gpt-4o", inputTokens: 0, outputTokens: 100);

        CostGuardOptions options = new()
        {
            MaxTokensPerMinute = 100,
            EstimatedOutputTokensBuffer = 100,
            OnRateLimitExceeded = LimitExceededAction.Wait,
            MaxWaitTimeout = TimeSpan.MaxValue, // Accepted by ValidateOptions!
        };

        using CostGuardChatClient client = new(primary, options, timeProvider: fakeTime);

        // Exhaust the 100 TPM quota.
        await client.GetResponseAsync([]);

        // Next request enters Wait mode and computes `now + MaxWaitTimeout` -> crashes with ArgumentOutOfRangeException!
        // Expected safe behavior: Wait mode clamps deadline to DateTimeOffset.MaxValue and completes once the window resets.
        Task<ChatResponse> waitingTask = client.GetResponseAsync([]);
        fakeTime.Advance(TimeSpan.FromMinutes(1));

        ChatResponse response = await waitingTask;
        Assert.NotNull(response);
    }

    [Fact]
    public void BudgetRule_UndefinedBudgetWindowTypeEnum_MustThrowArgumentOutOfRangeException()
    {
        // Passing an undefined enum value currently succeeds in BudgetRule and silently resets SpentUsd = 0 on every tick in InMemoryBudgetStore!
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new BudgetRule(10.00m, (BudgetWindowType)999, LimitExceededAction.Throw));
    }

    [Fact]
    public async Task BudgetRule_WithExpressionMutation_MustNotBypassValidationAndDisableBudgetCap()
    {
        FakeTimeProvider fakeTime = new(new DateTimeOffset(2026, 5, 1, 12, 0, 0, TimeSpan.Zero));
        InMemoryBudgetStore store = new(fakeTime);

        // Mutating a valid FixedUtcDay rule into a Sliding rule with SlidingWindow = null via record `with` expression!
        BudgetRule corruptedRule = BudgetRule.FixedUtcDay(1.00m) with
        {
            WindowType = BudgetWindowType.Sliding,
            SlidingWindow = null,
        };

        // Expected safe behavior: must throw ArgumentException instead of treating SlidingTicks = 0 and silently disabling the budget!
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await store.TryReserveAsync("default", 0.90m, corruptedRule));
    }

    [Fact]
    public void ModelPricing_WithExpressionNegativeCost_MustNotProduceNegativeCost()
    {
        // Expected safe behavior: mutating InputCostPerMillionTokens to negative via `with` must throw immediately.
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ModelPricing(1.00m, 1.00m) with
            {
                InputCostPerMillionTokens = -500m,
            });
    }

    [Fact]
    public async Task GetResponseAsync_UsageInputPlusOutputOverflow_MustNotClampedToZeroFreeTokens()
    {
        FakeTimeProvider fakeTime = new(new DateTimeOffset(2026, 5, 1, 12, 0, 0, TimeSpan.Zero));
        SlidingWindowTokenRateLimiter limiter = new(fakeTime);
        IChatClient overflowUsageClient = new OverflowUsageChatClient();

        CostGuardOptions options = new()
        {
            MaxTokensPerMinute = 1_000,
            EstimatedOutputTokensBuffer = 100,
            OnRateLimitExceeded = LimitExceededAction.Throw,
        };

        using CostGuardChatClient client = new(overflowUsageClient, options, rateLimiter: limiter, timeProvider: fakeTime);

        // Provider reports InputTokenCount = long.MaxValue, OutputTokenCount = 100, TotalTokenCount = null.
        // actualInput + actualOutput overflows long to negative, Math.Max(0, ...) clamps actualTotal to 0 -> refunds the reservation!
        await client.GetResponseAsync([]);

        // Expected safe behavior: the quota must be exhausted, NOT refunded to 0!
        TokenLeaseResult next = await limiter.TryAcquireAsync("default", 100, 1_000);
        Assert.False(next.IsAllowed, "Overflow in actualInput + actualOutput clamped actualTotal to 0 and refunded the rate-limit reservation!");
    }

    private sealed class OverflowUsageChatClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            ChatResponse response = new(new ChatMessage(ChatRole.Assistant, "ok"))
            {
                ModelId = "gpt-4o",
                Usage = new UsageDetails
                {
                    InputTokenCount = long.MaxValue,
                    OutputTokenCount = 100,
                    TotalTokenCount = null,
                },
            };
            return Task.FromResult(response);
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
