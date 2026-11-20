using AiCostGuard.Options;
using AiCostGuard.RateLimiting;
using AiCostGuard.Storage;
using AiCostGuard.Tests.Fakes;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Time.Testing;

namespace AiCostGuard.Tests;

public sealed class StreamingReconciliationTests
{
    [Fact]
    public async Task GetStreamingResponseAsync_UnderestimatedTokens_ReconcilesToActualUsageContent()
    {
        FakeTimeProvider fakeTime = new(new DateTimeOffset(2026, 5, 1, 12, 0, 0, TimeSpan.Zero));
        // Estimate will be ~105 tokens, but stream reports 200 input + 800 output = 1,000 actual tokens
        FakeChatClient primary = new(defaultModelId: "test-model", inputTokens: 200, outputTokens: 800);
        InMemoryBudgetStore store = new(fakeTime);
        var rule = BudgetRule.FixedUtcDay(10.00m);

        CostGuardOptions options = new()
        {
            EstimatedOutputTokensBuffer = 100,
        };
        // $100 per 1M input, $200 per 1M output -> 200*$100/1M + 800*$200/1M = $0.02 + $0.16 = $0.18 USD
        options.SetModelPricing("test-model", 100m, 200m);
        options.AddBudgetRule(rule);

        using CostGuardChatClient client = new(primary, options, budgetStore: store, timeProvider: fakeTime);

        List<ChatResponseUpdate> updates = [];
        await foreach (ChatResponseUpdate update in client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "Hi")]))
        {
            updates.Add(update);
        }

        Assert.NotEmpty(updates);
        decimal actualSpend = await store.GetCurrentSpendAsync("default", rule);
        Assert.Equal(0.18m, actualSpend);
    }

    [Fact]
    public async Task GetStreamingResponseAsync_OverestimatedBuffer_RefundsUnusedTokensToRateLimiter()
    {
        FakeTimeProvider fakeTime = new(new DateTimeOffset(2026, 5, 1, 12, 0, 0, TimeSpan.Zero));
        // Buffer reserves 800 output tokens, but actual response uses only 10 input + 5 output = 15 tokens!
        FakeChatClient primary = new(defaultModelId: "gpt-4o", inputTokens: 10, outputTokens: 5);
        SlidingWindowTokenRateLimiter limiter = new(fakeTime);

        CostGuardOptions options = new()
        {
            MaxTokensPerMinute = 1000,
            EstimatedOutputTokensBuffer = 800,
            OnRateLimitExceeded = LimitExceededAction.Throw,
        };

        using CostGuardChatClient client = new(primary, options, rateLimiter: limiter, timeProvider: fakeTime);
        ChatMessage[] prompt = [new(ChatRole.User, "Yes or no?")];

        // 1st streaming call reserves ~807 tokens, then refunds 792 tokens in finally (leaving 15 consumed)
        await foreach (ChatResponseUpdate _ in client.GetStreamingResponseAsync(prompt))
        {
        }

        // 2nd streaming call also needs ~807 tokens pre-flight: without the refund, 807 + 807 > 1000 would fail!
        // With the refund, 15 + 807 = 822 <= 1000, so it succeeds!
        await foreach (ChatResponseUpdate _ in client.GetStreamingResponseAsync(prompt))
        {
        }

        Assert.Equal(2, primary.StreamingCallCount);
    }

    [Fact]
    public async Task GetStreamingResponseAsync_EarlyBreakFromForeach_ExecutesFinallyAndRefundsUnusedBuffer()
    {
        FakeTimeProvider fakeTime = new(new DateTimeOffset(2026, 5, 1, 12, 0, 0, TimeSpan.Zero));
        FakeChatClient primary = new(defaultModelId: "gpt-4o")
        {
            StreamChunks = ["Chunk1", "Chunk2", "Chunk3"],
            EmitStreamingUsage = true,
            InputTokens = 10,
            OutputTokens = 500,
        };
        SlidingWindowTokenRateLimiter limiter = new(fakeTime);

        CostGuardOptions options = new()
        {
            MaxTokensPerMinute = 1000,
            EstimatedOutputTokensBuffer = 800,
            OnRateLimitExceeded = LimitExceededAction.Throw,
        };

        using CostGuardChatClient client = new(primary, options, rateLimiter: limiter, timeProvider: fakeTime);
        ChatMessage[] prompt = [new(ChatRole.User, "Hi")];

        // Caller breaks after receiving the very first chunk ("Chunk1" = 6 chars -> 2 output tokens)
        await foreach (ChatResponseUpdate _ in client.GetStreamingResponseAsync(prompt))
        {
            break;
        }

        // The finally block must have run and reconciled to ~7 tokens (5 input + 2 output), freeing capacity for the next 805-token reservation
        await foreach (ChatResponseUpdate _ in client.GetStreamingResponseAsync(prompt))
        {
            break;
        }

        Assert.Equal(2, primary.StreamingCallCount);
    }
}
