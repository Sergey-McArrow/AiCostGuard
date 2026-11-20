using System.Diagnostics.Metrics;
using AiCostGuard.DependencyInjection;
using AiCostGuard.Exceptions;
using AiCostGuard.Observability;
using AiCostGuard.Options;
using AiCostGuard.Tests.Fakes;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace AiCostGuard.Tests;

public sealed class ObservabilityAndDiTests
{
    [Fact]
    public async Task UseCostGuard_InChatClientBuilderPipeline_ResolvesDependenciesAndEnforcesPolicies()
    {
        FakeTimeProvider fakeTime = new(new DateTimeOffset(2026, 5, 1, 12, 0, 0, TimeSpan.Zero));
        FakeChatClient primary = new(defaultModelId: "gpt-4o", responseText: "Primary", inputTokens: 500, outputTokens: 500);
        FakeChatClient fallback = new(defaultModelId: "llama3.2", responseText: "Fallback", inputTokens: 50, outputTokens: 50);

        ServiceCollection services = new();
        services.AddSingleton<TimeProvider>(fakeTime);

        services.AddChatClient(primary)
            .UseCostGuard(options =>
            {
                options.MaxTokensPerRequest = 5_000;
                options.MaxTokensPerMinute = 1_200;
                options.EstimatedOutputTokensBuffer = 500;
                options.OnRateLimitExceeded = LimitExceededAction.Fallback;
                options.AddBudgetRule(maxUsd: 5.00m, BudgetWindowType.FixedUtcDay, LimitExceededAction.Fallback);
            }, fallbackClient: fallback);

        await using ServiceProvider provider = services.BuildServiceProvider();
        IChatClient client = provider.GetRequiredService<IChatClient>();

        ChatMessage[] prompt = [new(ChatRole.User, "Hello")];

        // 1st call goes to primary (1,000 actual tokens)
        ChatResponse r1 = await client.GetResponseAsync(prompt);
        Assert.Equal("Primary", r1.Text);

        // 2nd call exceeds 1,200 TPM -> routes to fallback
        ChatResponse r2 = await client.GetResponseAsync(prompt);
        Assert.Equal("Fallback", r2.Text);
    }

    [Fact]
    public async Task CostGuardMetrics_EmitsTokensCostFallbackAndRejectionCounters()
    {
        long tokensRecorded = 0;
        double costRecorded = 0d;
        long fallbacksRecorded = 0;
        long rejectionsRecorded = 0;

        using MeterListener listener = new();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Meter.Name == CostGuardMetrics.MeterName)
            {
                meterListener.EnableMeasurementEvents(instrument);
            }
        };

        listener.SetMeasurementEventCallback<long>((instrument, measurement, _, _) =>
        {
            switch (instrument.Name)
            {
                case CostGuardMetrics.TokensConsumedInstrumentName:
                    Interlocked.Add(ref tokensRecorded, measurement);
                    break;
                case CostGuardMetrics.FallbacksTriggeredInstrumentName:
                    Interlocked.Add(ref fallbacksRecorded, measurement);
                    break;
                case CostGuardMetrics.RequestsRejectedInstrumentName:
                    Interlocked.Add(ref rejectionsRecorded, measurement);
                    break;
            }
        });

        listener.SetMeasurementEventCallback<double>((instrument, measurement, _, _) =>
        {
            if (instrument.Name == CostGuardMetrics.CostUsdInstrumentName)
            {
                costRecorded += measurement;
            }
        });

        listener.Start();

        FakeChatClient primary = new(defaultModelId: "metric-model", inputTokens: 100, outputTokens: 200);
        FakeChatClient fallback = new(defaultModelId: "llama3.2", inputTokens: 10, outputTokens: 20);

        CostGuardOptions options = new()
        {
            MaxTokensPerRequest = 1_000,
            EstimatedOutputTokensBuffer = 200,
            FallbackClient = fallback,
        };
        options.SetModelPricing("metric-model", 100m, 100m); // 300 tokens = $0.03 USD
        options.AddBudgetRule(maxUsd: 0.04m, BudgetWindowType.FixedUtcDay, LimitExceededAction.Fallback);

        using CostGuardChatClient client = new(primary, options);

        // 1. Successful primary request: records 100 input + 200 output = 300 tokens and $0.03 USD
        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "First")]);

        // 2. Budget exceeded -> triggers Fallback metric
        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "Second")]);

        // 3. Oversized prompt (> 1,000 tokens) -> triggers Rejected metric
        string hugeText = new('a', 4_500);
        await Assert.ThrowsAsync<CostGuardLimitExceededException>(() =>
            client.GetResponseAsync([new ChatMessage(ChatRole.User, hugeText)]));

        Assert.True(tokensRecorded >= 300);
        Assert.True(costRecorded >= 0.03d);
        Assert.True(fallbacksRecorded >= 1);
        Assert.True(rejectionsRecorded >= 1);
    }
}
