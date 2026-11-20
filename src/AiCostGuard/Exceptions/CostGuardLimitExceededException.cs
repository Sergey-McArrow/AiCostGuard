using AiCostGuard.Options;

namespace AiCostGuard.Exceptions;

/// <summary>
/// The exception that is thrown when a token rate limit (TPM) or monetary budget limit (USD) is exceeded.
/// </summary>
public sealed class CostGuardLimitExceededException : InvalidOperationException
{
    /// <summary>
    /// Initializes a new instance of the <see cref="CostGuardLimitExceededException"/> class.
    /// </summary>
    public CostGuardLimitExceededException()
        : this(LimitExceededReason.RateLimitExceeded, CostGuardOptions.DefaultScopeKey, 0m, 0m)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="CostGuardLimitExceededException"/> class with a specified error message.
    /// </summary>
    /// <param name="message">The message that describes the error.</param>
    public CostGuardLimitExceededException(string? message)
        : base(message)
    {
        ScopeKey = CostGuardOptions.DefaultScopeKey;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="CostGuardLimitExceededException"/> class with a specified error message and a reference to the inner exception that is the cause of this exception.
    /// </summary>
    /// <param name="message">The error message that explains the reason for the exception.</param>
    /// <param name="innerException">The exception that is the cause of the current exception.</param>
    public CostGuardLimitExceededException(string? message, Exception? innerException)
        : base(message, innerException)
    {
        ScopeKey = CostGuardOptions.DefaultScopeKey;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="CostGuardLimitExceededException"/> class.
    /// </summary>
    /// <param name="reason">The policy reason why the limit was exceeded.</param>
    /// <param name="scopeKey">The tenant or global scope key associated with the request.</param>
    /// <param name="currentUsage">The current usage value (tokens or USD) before or including the rejected request.</param>
    /// <param name="limit">The configured maximum limit (tokens per minute or USD budget).</param>
    /// <param name="retryAfter">An optional duration indicating when capacity is expected to become available.</param>
    /// <param name="message">An optional custom error message.</param>
    public CostGuardLimitExceededException(
        LimitExceededReason reason,
        string scopeKey,
        decimal currentUsage,
        decimal limit,
        TimeSpan? retryAfter = null,
        string? message = null)
        : base(message ?? BuildDefaultMessage(reason, scopeKey, currentUsage, limit, retryAfter))
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scopeKey);

        Reason = reason;
        ScopeKey = scopeKey;
        CurrentUsage = currentUsage;
        Limit = limit;
        RetryAfter = retryAfter;
    }

    /// <summary>
    /// Gets the policy reason that triggered the exception.
    /// </summary>
    public LimitExceededReason Reason { get; }

    /// <summary>
    /// Gets the tenant or global scope key for which the limit was exceeded.
    /// </summary>
    public string ScopeKey { get; }

    /// <summary>
    /// Gets the current usage (tokens in the current minute or USD spent in the current budget window).
    /// </summary>
    public decimal CurrentUsage { get; }

    /// <summary>
    /// Gets the configured limit (maximum tokens per minute or maximum budget in USD).
    /// </summary>
    public decimal Limit { get; }

    /// <summary>
    /// Gets the estimated duration until capacity becomes available, if known.
    /// </summary>
    public TimeSpan? RetryAfter { get; }

    private static string BuildDefaultMessage(
        LimitExceededReason reason,
        string scopeKey,
        decimal currentUsage,
        decimal limit,
        TimeSpan? retryAfter)
    {
        string retrySuffix = retryAfter.HasValue
            ? $" Retry after {retryAfter.Value.TotalSeconds:F1}s."
            : string.Empty;

        return reason switch
        {
            LimitExceededReason.RequestTokenLimitExceeded =>
                $"Single-request token limit exceeded for scope '{scopeKey}'. Estimated tokens: {currentUsage}, limit: {limit} tokens per request.",
            LimitExceededReason.RateLimitExceeded =>
                $"Token rate limit exceeded for scope '{scopeKey}'. Current usage: {currentUsage} tokens, limit: {limit} TPM.{retrySuffix}",
            LimitExceededReason.BudgetExceeded =>
                $"Monetary budget exceeded for scope '{scopeKey}'. Current spend: ${currentUsage:F6} USD, limit: ${limit:F6} USD.{retrySuffix}",
            _ =>
                $"CostGuard limit ({reason}) exceeded for scope '{scopeKey}'. Current: {currentUsage}, limit: {limit}.{retrySuffix}",
        };
    }
}
