using AiCostGuard.Exceptions;
using AiCostGuard.Options;
using AiCostGuard.Tests.Fakes;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Time.Testing;

namespace AiCostGuard.Tests;

public sealed class RateLimitingTests
{
    [Fact]
    public async Task GetResponseAsync_MaxTokensPerRequestExceeded_ThrowsImmediatelyWithRequestTokenLimitExceeded()
    {
        FakeChatClient primary = new();
        CostGuardOptions options = new()
        {
            MaxTokensPerRequest = 100,
            EstimatedOutputTokensBuffer = 200, // 200 + input > 100
        };
        using CostGuardChatClient client = new(primary, options);

        CostGuardLimitExceededException ex = await Assert.ThrowsAsync<CostGuardLimitExceededException>(() =>
            client.GetResponseAsync([new ChatMessage(ChatRole.User, "Hello world")]));

        Assert.Equal(LimitExceededReason.RequestTokenLimitExceeded, ex.Reason);
        Assert.Equal(100m, ex.Limit);
        Assert.True(ex.CurrentUsage > 200m);
        Assert.Null(ex.RetryAfter);
        Assert.Equal(0, primary.CallCount);
    }

    [Fact]
    public async Task GetResponseAsync_TpmLimitExceededWithThrow_ThrowsWithRetryAfterAndResetsAfterOneMinute()
    {
        FakeTimeProvider fakeTime = new(new DateTimeOffset(2026, 5, 1, 12, 0, 0, TimeSpan.Zero));
        FakeChatClient primary = new(inputTokens: 400, outputTokens: 200); // actual = 600 tokens per call
        CostGuardOptions options = new()
        {
            MaxTokensPerMinute = 1000,
            EstimatedOutputTokensBuffer = 500,
            OnRateLimitExceeded = LimitExceededAction.Throw,
        };
        using CostGuardChatClient client = new(primary, options, timeProvider: fakeTime);
        ChatMessage[] prompt = [new(ChatRole.User, "1234")]; // est = 5 + 500 = 505 tokens

        // 1st request succeeds and reconciles to 600 actual tokens
        await client.GetResponseAsync(prompt);
        Assert.Equal(1, primary.CallCount);

        // Advance 20 seconds (still within 1-minute window: 600 + 505 = 1105 > 1000)
        fakeTime.Advance(TimeSpan.FromSeconds(20));

        CostGuardLimitExceededException ex = await Assert.ThrowsAsync<CostGuardLimitExceededException>(() =>
            client.GetResponseAsync(prompt));

        Assert.Equal(LimitExceededReason.RateLimitExceeded, ex.Reason);
        Assert.Equal(600m, ex.CurrentUsage);
        Assert.Equal(1000m, ex.Limit);
        Assert.NotNull(ex.RetryAfter);
        Assert.Equal(TimeSpan.FromSeconds(40), ex.RetryAfter.Value);
        Assert.Equal(1, primary.CallCount);

        // Advance remaining 40 seconds so the 1st request expires from the 1-minute sliding window
        fakeTime.Advance(TimeSpan.FromSeconds(40));

        await client.GetResponseAsync(prompt);
        Assert.Equal(2, primary.CallCount);
    }

    [Fact]
    public async Task GetResponseAsync_TpmLimitExceededWithWait_WaitsForWindowAndSucceedsWhenTimeAdvances()
    {
        FakeTimeProvider fakeTime = new(new DateTimeOffset(2026, 5, 1, 12, 0, 0, TimeSpan.Zero));
        FakeChatClient primary = new(inputTokens: 500, outputTokens: 300); // actual = 800
        CostGuardOptions options = new()
        {
            MaxTokensPerMinute = 1000,
            EstimatedOutputTokensBuffer = 400,
            MaxWaitTimeout = TimeSpan.FromMinutes(2),
            OnRateLimitExceeded = LimitExceededAction.Wait,
        };
        using CostGuardChatClient client = new(primary, options, timeProvider: fakeTime);
        ChatMessage[] prompt = [new(ChatRole.User, "1234")]; // est = 405

        await client.GetResponseAsync(prompt);
        Assert.Equal(1, primary.CallCount);

        // Start 2nd request (800 + 405 > 1000): it will await Task.Delay(60s, fakeTime)
        Task<ChatResponse> waitingTask = client.GetResponseAsync(prompt);
        Assert.False(waitingTask.IsCompleted);

        // Advance fake clock by 1 minute — Task.Delay completes deterministically
        fakeTime.Advance(TimeSpan.FromMinutes(1));

        ChatResponse response = await waitingTask;
        Assert.NotNull(response);
        Assert.Equal(2, primary.CallCount);
    }

    [Fact]
    public async Task GetResponseAsync_TpmLimitExceededWithWait_ThrowsWhenRetryAfterExceedsMaxWaitTimeout()
    {
        FakeTimeProvider fakeTime = new(new DateTimeOffset(2026, 5, 1, 12, 0, 0, TimeSpan.Zero));
        FakeChatClient primary = new(inputTokens: 500, outputTokens: 300); // actual = 800
        CostGuardOptions options = new()
        {
            MaxTokensPerMinute = 1000,
            EstimatedOutputTokensBuffer = 400,
            MaxWaitTimeout = TimeSpan.FromSeconds(15), // RetryAfter is 60s > 15s
            OnRateLimitExceeded = LimitExceededAction.Wait,
        };
        using CostGuardChatClient client = new(primary, options, timeProvider: fakeTime);
        ChatMessage[] prompt = [new(ChatRole.User, "1234")];

        await client.GetResponseAsync(prompt);

        CostGuardLimitExceededException ex = await Assert.ThrowsAsync<CostGuardLimitExceededException>(() =>
            client.GetResponseAsync(prompt));

        Assert.Equal(LimitExceededReason.RateLimitExceeded, ex.Reason);
        Assert.Equal(1, primary.CallCount);
    }

    [Fact]
    public async Task GetResponseAsync_TpmLimitExceededWithFallback_RoutesToFallbackClient()
    {
        FakeTimeProvider fakeTime = new(new DateTimeOffset(2026, 5, 1, 12, 0, 0, TimeSpan.Zero));
        FakeChatClient primary = new(defaultModelId: "gpt-4o", responseText: "Primary", inputTokens: 600, outputTokens: 300);
        FakeChatClient fallback = new(defaultModelId: "llama3.2", responseText: "Fallback", inputTokens: 50, outputTokens: 50);

        CostGuardOptions options = new()
        {
            MaxTokensPerMinute = 1000,
            EstimatedOutputTokensBuffer = 400,
            OnRateLimitExceeded = LimitExceededAction.Fallback,
            FallbackClient = fallback,
        };
        using CostGuardChatClient client = new(primary, options, timeProvider: fakeTime);
        ChatMessage[] prompt = [new(ChatRole.User, "1234")];

        ChatResponse first = await client.GetResponseAsync(prompt);
        Assert.Equal("Primary", first.Text);
        Assert.Equal(1, primary.CallCount);
        Assert.Equal(0, fallback.CallCount);

        ChatResponse second = await client.GetResponseAsync(prompt);
        Assert.Equal("Fallback", second.Text);
        Assert.Equal(1, primary.CallCount);
        Assert.Equal(1, fallback.CallCount);
    }
}
