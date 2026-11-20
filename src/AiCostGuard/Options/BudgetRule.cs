namespace AiCostGuard.Options;

/// <summary>
/// Defines a monetary spend budget rule in US dollars (USD) over a sliding or fixed calendar UTC window.
/// </summary>
public sealed record BudgetRule
{
    /// <summary>
    /// Initializes a new instance of the <see cref="BudgetRule"/> record for a fixed calendar window (<see cref="BudgetWindowType.FixedUtcDay"/> or <see cref="BudgetWindowType.FixedUtcMonth"/>)
    /// or a sliding window when <paramref name="slidingWindow"/> is provided.
    /// </summary>
    /// <param name="maxAmountUsd">The maximum monetary spend in USD allowed within the window.</param>
    /// <param name="windowType">The type of budget tracking window.</param>
    /// <param name="onExceeded">The action to take when this budget rule is exceeded.</param>
    /// <param name="slidingWindow">The window duration when <paramref name="windowType"/> is <see cref="BudgetWindowType.Sliding"/>; otherwise <see langword="null"/>.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <paramref name="maxAmountUsd"/> is negative, or when <paramref name="slidingWindow"/> is not positive for <see cref="BudgetWindowType.Sliding"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="windowType"/> is <see cref="BudgetWindowType.Sliding"/> and <paramref name="slidingWindow"/> is <see langword="null"/>,
    /// or when a fixed calendar window is specified together with a non-null <paramref name="slidingWindow"/>.
    /// </exception>
    public BudgetRule(
        decimal maxAmountUsd,
        BudgetWindowType windowType,
        LimitExceededAction onExceeded = LimitExceededAction.Throw,
        TimeSpan? slidingWindow = null)
    {
        ValidateCore(maxAmountUsd, windowType, onExceeded, slidingWindow);

        MaxAmountUsd = maxAmountUsd;
        WindowType = windowType;
        OnExceeded = onExceeded;
        SlidingWindow = slidingWindow;
    }

    internal void Validate() =>
        ValidateCore(MaxAmountUsd, WindowType, OnExceeded, SlidingWindow);

    private static void ValidateCore(
        decimal maxAmountUsd,
        BudgetWindowType windowType,
        LimitExceededAction onExceeded,
        TimeSpan? slidingWindow)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxAmountUsd);

        if (!Enum.IsDefined(windowType))
        {
            throw new ArgumentOutOfRangeException(nameof(windowType), windowType, "Unsupported BudgetWindowType value.");
        }

        if (!Enum.IsDefined(onExceeded))
        {
            throw new ArgumentOutOfRangeException(nameof(onExceeded), onExceeded, "Unsupported LimitExceededAction value.");
        }

        if (windowType == BudgetWindowType.Sliding)
        {
            if (!slidingWindow.HasValue)
            {
                throw new ArgumentException("A slidingWindow duration must be specified when windowType is BudgetWindowType.Sliding.", nameof(slidingWindow));
            }

            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(slidingWindow.Value, TimeSpan.Zero, nameof(slidingWindow));
        }
        else if (slidingWindow.HasValue)
        {
            throw new ArgumentException($"slidingWindow must be null when windowType is {windowType}.", nameof(slidingWindow));
        }
    }

    /// <summary>
    /// Gets the maximum monetary spend in USD allowed within the window.
    /// </summary>
    public decimal MaxAmountUsd { get; init; }

    /// <summary>
    /// Gets the type of time window (<see cref="BudgetWindowType.Sliding"/>, <see cref="BudgetWindowType.FixedUtcDay"/>, or <see cref="BudgetWindowType.FixedUtcMonth"/>).
    /// </summary>
    public BudgetWindowType WindowType { get; init; }

    /// <summary>
    /// Gets the action to perform when this budget rule is exceeded.
    /// </summary>
    public LimitExceededAction OnExceeded { get; init; }

    /// <summary>
    /// Gets the sliding window duration when <see cref="WindowType"/> is <see cref="BudgetWindowType.Sliding"/>, or <see langword="null"/> for fixed calendar windows.
    /// </summary>
    public TimeSpan? SlidingWindow { get; init; }

    /// <summary>
    /// Creates a sliding-window <see cref="BudgetRule"/>.
    /// </summary>
    /// <param name="maxAmountUsd">The maximum spend in USD allowed within <paramref name="window"/>.</param>
    /// <param name="window">The sliding window duration.</param>
    /// <param name="onExceeded">The action to take when the budget rule is exceeded.</param>
    /// <returns>A configured <see cref="BudgetRule"/>.</returns>
    public static BudgetRule Sliding(decimal maxAmountUsd, TimeSpan window, LimitExceededAction onExceeded = LimitExceededAction.Throw) =>
        new(maxAmountUsd, BudgetWindowType.Sliding, onExceeded, window);

    /// <summary>
    /// Creates a fixed UTC daily <see cref="BudgetRule"/> (resets at <c>00:00:00Z</c>).
    /// </summary>
    /// <param name="maxAmountUsd">The maximum daily spend in USD.</param>
    /// <param name="onExceeded">The action to take when the daily budget is exceeded.</param>
    /// <returns>A configured <see cref="BudgetRule"/>.</returns>
    public static BudgetRule FixedUtcDay(decimal maxAmountUsd, LimitExceededAction onExceeded = LimitExceededAction.Throw) =>
        new(maxAmountUsd, BudgetWindowType.FixedUtcDay, onExceeded);

    /// <summary>
    /// Creates a fixed UTC monthly <see cref="BudgetRule"/> (resets on the 1st of each month at <c>00:00:00Z</c>).
    /// </summary>
    /// <param name="maxAmountUsd">The maximum monthly spend in USD.</param>
    /// <param name="onExceeded">The action to take when the monthly budget is exceeded.</param>
    /// <returns>A configured <see cref="BudgetRule"/>.</returns>
    public static BudgetRule FixedUtcMonth(decimal maxAmountUsd, LimitExceededAction onExceeded = LimitExceededAction.Throw) =>
        new(maxAmountUsd, BudgetWindowType.FixedUtcMonth, onExceeded);
}
