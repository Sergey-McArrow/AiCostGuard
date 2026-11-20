using AiCostGuard.Options;
using Microsoft.Extensions.Logging;

namespace AiCostGuard.Observability;

/// <summary>
/// High-performance, Native AOT-compatible source-generated logging methods for <see cref="CostGuardChatClient"/>.
/// </summary>
internal static partial class CostGuardLog
{
    [LoggerMessage(
        EventId = 1001,
        Level = LogLevel.Warning,
        Message = "CostGuard limit ({Reason}) exceeded for scope '{ScopeKey}' (current: {CurrentUsage}, limit: {Limit}). Routing request to fallback client.")]
    public static partial void FallbackTriggered(
        ILogger logger,
        LimitExceededReason reason,
        string scopeKey,
        decimal currentUsage,
        decimal limit);

    [LoggerMessage(
        EventId = 1002,
        Level = LogLevel.Warning,
        Message = "CostGuard limit ({Reason}) exceeded for scope '{ScopeKey}' (current: {CurrentUsage}, limit: {Limit}). Request rejected.")]
    public static partial void RequestRejected(
        ILogger logger,
        LimitExceededReason reason,
        string scopeKey,
        decimal currentUsage,
        decimal limit);

    [LoggerMessage(
        EventId = 1003,
        Level = LogLevel.Debug,
        Message = "CostGuard limit ({Reason}) reached for scope '{ScopeKey}'. Waiting {WaitDurationMs:F0} ms for capacity.")]
    public static partial void WaitingForCapacity(
        ILogger logger,
        LimitExceededReason reason,
        string scopeKey,
        double waitDurationMs);

    [LoggerMessage(
        EventId = 1004,
        Level = LogLevel.Debug,
        Message = "CostGuard reconciled usage for scope '{ScopeKey}' (model: '{ModelId}'): tokens reserved={ReservedTokens}, actual={ActualTokens}; cost reserved=${ReservedCostUsd:F6}, actual=${ActualCostUsd:F6} USD.")]
    public static partial void UsageReconciled(
        ILogger logger,
        string scopeKey,
        string? modelId,
        long reservedTokens,
        long actualTokens,
        decimal reservedCostUsd,
        decimal actualCostUsd);
}
