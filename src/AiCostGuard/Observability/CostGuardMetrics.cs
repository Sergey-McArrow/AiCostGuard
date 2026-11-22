using System.Diagnostics;
using System.Diagnostics.Metrics;
using AiCostGuard.Options;

namespace AiCostGuard.Observability;

/// <summary>
/// Provides OpenTelemetry-compatible metrics for <see cref="CostGuardChatClient"/> using <see cref="System.Diagnostics.Metrics.Meter"/>.
/// </summary>
public static class CostGuardMetrics
{
    /// <summary>
    /// The meter name used to subscribe to CostGuard metrics in OpenTelemetry, Prometheus, or <c>dotnet-counters</c>.
    /// </summary>
    public const string MeterName = "AiCostGuard";

    /// <summary>
    /// The metric instrument name for total tokens consumed (<c>costguard.tokens.consumed</c>).
    /// </summary>
    public const string TokensConsumedInstrumentName = "costguard.tokens.consumed";

    /// <summary>
    /// The metric instrument name for cumulative USD monetary cost (<c>costguard.cost.usd</c>).
    /// </summary>
    public const string CostUsdInstrumentName = "costguard.cost.usd";

    /// <summary>
    /// The metric instrument name for fallback routing activations (<c>costguard.fallbacks.triggered</c>).
    /// </summary>
    public const string FallbacksTriggeredInstrumentName = "costguard.fallbacks.triggered";

    /// <summary>
    /// The metric instrument name for rejected requests (<c>costguard.requests.rejected</c>).
    /// </summary>
    public const string RequestsRejectedInstrumentName = "costguard.requests.rejected";

    private static readonly Meter _meter = new(MeterName, "0.1.1");

    private static readonly Counter<long> _tokensConsumed = _meter.CreateCounter<long>(
        TokensConsumedInstrumentName,
        unit: "{token}",
        description: "Total number of AI tokens consumed, tagged by scope, model, and token type.");

    private static readonly Counter<double> _costUsd = _meter.CreateCounter<double>(
        CostUsdInstrumentName,
        unit: "USD",
        description: "Cumulative monetary cost in USD for AI model usage.");

    private static readonly Counter<long> _fallbacksTriggered = _meter.CreateCounter<long>(
        FallbacksTriggeredInstrumentName,
        unit: "{fallback}",
        description: "Number of requests routed to the fallback chat client due to rate limit or budget exhaustion.");

    private static readonly Counter<long> _requestsRejected = _meter.CreateCounter<long>(
        RequestsRejectedInstrumentName,
        unit: "{request}",
        description: "Number of requests rejected with CostGuardLimitExceededException.");

    internal static void RecordUsage(
        string scopeKey,
        string? modelId,
        long inputTokens,
        long outputTokens,
        decimal costUsd)
    {
        string normalizedModel = string.IsNullOrWhiteSpace(modelId) ? "unknown" : modelId;

        if (_tokensConsumed.Enabled)
        {
            if (inputTokens > 0)
            {
                TagList inputTags = new()
                {
                    { "scope", scopeKey },
                    { "model", normalizedModel },
                    { "token.type", "input" },
                };
                _tokensConsumed.Add(inputTokens, inputTags);
            }

            if (outputTokens > 0)
            {
                TagList outputTags = new()
                {
                    { "scope", scopeKey },
                    { "model", normalizedModel },
                    { "token.type", "output" },
                };
                _tokensConsumed.Add(outputTokens, outputTags);
            }
        }

        if (_costUsd.Enabled && costUsd > 0m)
        {
            TagList costTags = new()
            {
                { "scope", scopeKey },
                { "model", normalizedModel },
            };
            _costUsd.Add((double)costUsd, costTags);
        }
    }

    internal static void RecordFallbackTriggered(string scopeKey, LimitExceededReason reason)
    {
        if (!_fallbacksTriggered.Enabled)
        {
            return;
        }

        TagList tags = new()
        {
            { "scope", scopeKey },
            { "reason", reason.ToString() },
        };
        _fallbacksTriggered.Add(1, tags);
    }

    internal static void RecordRequestRejected(string scopeKey, LimitExceededReason reason)
    {
        if (!_requestsRejected.Enabled)
        {
            return;
        }

        TagList tags = new()
        {
            { "scope", scopeKey },
            { "reason", reason.ToString() },
        };
        _requestsRejected.Add(1, tags);
    }
}
