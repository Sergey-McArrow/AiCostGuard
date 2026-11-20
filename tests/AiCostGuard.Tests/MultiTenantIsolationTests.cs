using AiCostGuard.Exceptions;
using AiCostGuard.Options;
using AiCostGuard.Tests.Fakes;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Time.Testing;

namespace AiCostGuard.Tests;

public sealed class MultiTenantIsolationTests
{
    [Fact]
    public async Task GetResponseAsync_TenantAExhaustsQuota_TenantBSucceedsIndependently()
    {
        FakeTimeProvider fakeTime = new(new DateTimeOffset(2026, 5, 1, 12, 0, 0, TimeSpan.Zero));
        FakeChatClient primary = new(defaultModelId: "custom-model", inputTokens: 400, outputTokens: 400); // 800 tokens = $0.80

        CostGuardOptions options = new()
        {
            MaxTokensPerMinute = 1000,
            EstimatedOutputTokensBuffer = 400,
            OnRateLimitExceeded = LimitExceededAction.Throw,
            KeySelector = chatOptions =>
                chatOptions?.AdditionalProperties?.TryGetValue("TenantId", out object? tenant) is true
                    ? tenant?.ToString() ?? "default"
                    : "default",
        };
        options.SetModelPricing("custom-model", 1_000m, 1_000m);
        options.AddBudgetRule(maxUsd: 1.00m, BudgetWindowType.FixedUtcDay, LimitExceededAction.Throw);

        using CostGuardChatClient client = new(primary, options, timeProvider: fakeTime);
        ChatMessage[] prompt = [new(ChatRole.User, "Hello")];

        ChatOptions tenantAOptions = new()
        {
            AdditionalProperties = new AdditionalPropertiesDictionary { ["TenantId"] = "tenant-a" },
        };
        ChatOptions tenantBOptions = new()
        {
            AdditionalProperties = new AdditionalPropertiesDictionary { ["TenantId"] = "tenant-b" },
        };

        // Tenant A makes 1st call (consumes 800 TPM and $0.80 USD)
        await client.GetResponseAsync(prompt, tenantAOptions);

        // Tenant A's 2nd call is blocked
        CostGuardLimitExceededException ex = await Assert.ThrowsAsync<CostGuardLimitExceededException>(() =>
            client.GetResponseAsync(prompt, tenantAOptions));
        Assert.Equal("tenant-a", ex.ScopeKey);

        // Tenant B is completely unaffected and succeeds
        ChatResponse tenantBResponse = await client.GetResponseAsync(prompt, tenantBOptions);
        Assert.NotNull(tenantBResponse);
        Assert.Equal(2, primary.CallCount);
    }
}
