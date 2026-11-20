namespace AiCostGuard.Options;

/// <summary>
/// Defines the action to take when a rate limit or budget limit is exceeded.
/// </summary>
public enum LimitExceededAction
{
    /// <summary>
    /// Immediately throws a <see cref="Exceptions.CostGuardLimitExceededException"/>.
    /// </summary>
    Throw = 0,

    /// <summary>
    /// Asynchronously waits for capacity to become available within <see cref="CostGuardOptions.MaxWaitTimeout"/>.
    /// </summary>
    Wait = 1,

    /// <summary>
    /// Transparently routes the request to <see cref="CostGuardOptions.FallbackClient"/>.
    /// </summary>
    Fallback = 2,
}
