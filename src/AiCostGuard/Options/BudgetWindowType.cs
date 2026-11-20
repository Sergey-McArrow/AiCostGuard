namespace AiCostGuard.Options;

/// <summary>
/// Specifies the type of time window used to evaluate a <see cref="BudgetRule"/>.
/// </summary>
public enum BudgetWindowType
{
    /// <summary>
    /// A continuous sliding time window of duration <see cref="BudgetRule.SlidingWindow"/> (for example, the last 24 hours).
    /// </summary>
    Sliding = 0,

    /// <summary>
    /// A fixed calendar UTC day window that resets at <c>00:00:00Z</c> each day.
    /// </summary>
    FixedUtcDay = 1,

    /// <summary>
    /// A fixed calendar UTC month window that resets on the 1st of each month at <c>00:00:00Z</c>.
    /// </summary>
    FixedUtcMonth = 2,
}
