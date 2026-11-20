namespace AiCostGuard.RateLimiting;

/// <summary>
/// Represents the result of attempting to acquire token capacity from an <see cref="ITokenRateLimiter"/>.
/// </summary>
public readonly record struct TokenLeaseResult
{
    /// <summary>
    /// Initializes a new instance of the <see cref="TokenLeaseResult"/> struct.
    /// </summary>
    /// <param name="isAllowed">Indicates whether the requested token capacity was acquired within the configured rate limit.</param>
    /// <param name="currentTokens">
    /// The total number of tokens consumed in the current 1-minute window (including the acquired tokens if <paramref name="isAllowed"/> is <see langword="true"/>).
    /// </param>
    /// <param name="retryAfter">
    /// When <paramref name="isAllowed"/> is <see langword="false"/>, indicates the duration until enough tokens expire from the sliding window,
    /// or <see langword="null"/> if the single request exceeds the total window capacity and can never succeed by waiting.
    /// </param>
    public TokenLeaseResult(bool isAllowed, long currentTokens, TimeSpan? retryAfter = null)
    {
        IsAllowed = isAllowed;
        CurrentTokens = currentTokens;
        RetryAfter = retryAfter;
    }

    /// <summary>
    /// Gets a value indicating whether the requested token capacity was acquired within the configured rate limit.
    /// </summary>
    public bool IsAllowed { get; init; }

    /// <summary>
    /// Gets the total number of tokens consumed in the current 1-minute window.
    /// </summary>
    public long CurrentTokens { get; init; }

    /// <summary>
    /// Gets the duration until enough tokens expire from the sliding window, or <see langword="null"/> when not applicable.
    /// </summary>
    public TimeSpan? RetryAfter { get; init; }
}
