namespace AiCostGuard.RateLimiting;

/// <summary>
/// Defines a thread-safe rate limiter that enforces tokens-per-minute (TPM) quotas per tenant or scope using a Reserve-then-Reconcile workflow.
/// </summary>
public interface ITokenRateLimiter
{
    /// <summary>
    /// Atomically attempts to acquire capacity for the estimated number of tokens within the current 1-minute window.
    /// </summary>
    /// <param name="key">The tenant or global scope key.</param>
    /// <param name="estimatedTokens">The estimated total tokens (input + output buffer) to reserve.</param>
    /// <param name="maxTokensPerMinute">The maximum tokens allowed per 1-minute sliding window.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>A <see cref="TokenLeaseResult"/> indicating whether the tokens were reserved and when to retry if throttled.</returns>
    ValueTask<TokenLeaseResult> TryAcquireAsync(
        string key,
        long estimatedTokens,
        long maxTokensPerMinute,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reconciles a previously reserved token estimate with the actual number of tokens consumed after an AI response completes or fails.
    /// </summary>
    /// <param name="key">The tenant or global scope key.</param>
    /// <param name="reservedTokens">The token count that was previously reserved in <see cref="TryAcquireAsync"/>.</param>
    /// <param name="actualTokens">The actual total tokens consumed (or <c>0</c> if the request was rolled back or failed).</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>A <see cref="ValueTask"/> representing the asynchronous reconciliation operation.</returns>
    ValueTask ReconcileAsync(
        string key,
        long reservedTokens,
        long actualTokens,
        CancellationToken cancellationToken = default);
}
