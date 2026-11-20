namespace AiCostGuard.Storage;

/// <summary>
/// Represents the result of attempting to reserve monetary budget in an <see cref="IBudgetStore"/>.
/// </summary>
public readonly record struct BudgetReservation
{
    /// <summary>
    /// Initializes a new instance of the <see cref="BudgetReservation"/> struct.
    /// </summary>
    /// <param name="isAllowed">Indicates whether the budget reservation succeeded within the configured limit.</param>
    /// <param name="currentSpendUsd">
    /// The current cumulative USD spend in the active window (including the reservation if <paramref name="isAllowed"/> is <see langword="true"/>).
    /// </param>
    /// <param name="retryAfter">
    /// When <paramref name="isAllowed"/> is <see langword="false"/>, indicates the remaining duration until the current budget window resets.
    /// </param>
    public BudgetReservation(bool isAllowed, decimal currentSpendUsd, TimeSpan? retryAfter = null)
    {
        IsAllowed = isAllowed;
        CurrentSpendUsd = currentSpendUsd;
        RetryAfter = retryAfter;
    }

    /// <summary>
    /// Gets a value indicating whether the budget reservation succeeded within the configured limit.
    /// </summary>
    public bool IsAllowed { get; init; }

    /// <summary>
    /// Gets the current cumulative USD spend in the active window.
    /// </summary>
    public decimal CurrentSpendUsd { get; init; }

    /// <summary>
    /// Gets the remaining duration until the current budget window resets, when <see cref="IsAllowed"/> is <see langword="false"/>.
    /// </summary>
    public TimeSpan? RetryAfter { get; init; }
}
