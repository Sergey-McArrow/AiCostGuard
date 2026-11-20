namespace AiCostGuard.Options;

/// <summary>
/// Specifies the policy limit that caused a request to be throttled, rejected, or routed to a fallback client.
/// </summary>
public enum LimitExceededReason
{
    /// <summary>
    /// The tokens-per-minute (TPM) rate limit was exceeded.
    /// </summary>
    RateLimitExceeded = 0,

    /// <summary>
    /// One or more USD monetary budget rules (<see cref="CostGuardOptions.BudgetRules"/>) were exceeded.
    /// </summary>
    BudgetExceeded = 1,

    /// <summary>
    /// The single-request token guardrail (<see cref="CostGuardOptions.MaxTokensPerRequest"/>) was exceeded.
    /// </summary>
    RequestTokenLimitExceeded = 2,
}
