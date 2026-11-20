using AiCostGuard.Options;

namespace AiCostGuard.Storage;

/// <summary>
/// Defines a thread-safe store for tracking and enforcing monetary AI usage budgets across tenants or scopes,
/// supporting multiple simultaneous <see cref="BudgetRule"/> policies (<see cref="BudgetWindowType.Sliding"/>, <see cref="BudgetWindowType.FixedUtcDay"/>, and <see cref="BudgetWindowType.FixedUtcMonth"/>).
/// </summary>
public interface IBudgetStore
{
    /// <summary>
    /// Atomically checks whether the estimated cost fits within the specified <paramref name="rule"/> for the tenant scope <paramref name="key"/> and reserves the amount if allowed.
    /// </summary>
    /// <param name="key">The tenant or global scope key.</param>
    /// <param name="estimatedCostUsd">The estimated cost in USD to reserve ahead of the request.</param>
    /// <param name="rule">The budget rule specifying the limit and window type.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>A <see cref="BudgetReservation"/> describing whether the reservation succeeded and the current spend state.</returns>
    ValueTask<BudgetReservation> TryReserveAsync(
        string key,
        decimal estimatedCostUsd,
        BudgetRule rule,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Atomically checks whether the estimated cost fits within a sliding budget window for the specified scope key and reserves the amount if allowed.
    /// </summary>
    /// <param name="key">The tenant or global scope key.</param>
    /// <param name="estimatedCostUsd">The estimated cost in USD to reserve ahead of the request.</param>
    /// <param name="maxBudgetUsd">The maximum budget in USD allowed within <paramref name="window"/>.</param>
    /// <param name="window">The duration of the sliding budget tracking window.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>A <see cref="BudgetReservation"/> describing whether the reservation succeeded and the current spend state.</returns>
    ValueTask<BudgetReservation> TryReserveAsync(
        string key,
        decimal estimatedCostUsd,
        decimal maxBudgetUsd,
        TimeSpan window,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reconciles a previously reserved monetary estimate with the actual cost incurred for the specified <paramref name="rule"/> after an AI response completes or fails.
    /// </summary>
    /// <param name="key">The tenant or global scope key.</param>
    /// <param name="reservedCostUsd">The amount in USD that was previously reserved in <see cref="TryReserveAsync(string, decimal, BudgetRule, CancellationToken)"/>.</param>
    /// <param name="actualCostUsd">The actual cost in USD calculated from the response's token usage (or <c>0m</c> if the request failed or was rolled back).</param>
    /// <param name="rule">The budget rule against which the reservation was made.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>A <see cref="ValueTask"/> representing the asynchronous reconciliation operation.</returns>
    ValueTask ReconcileAsync(
        string key,
        decimal reservedCostUsd,
        decimal actualCostUsd,
        BudgetRule rule,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reconciles a previously reserved monetary estimate with the actual cost incurred for all sliding-window buckets of the specified scope key.
    /// </summary>
    /// <param name="key">The tenant or global scope key.</param>
    /// <param name="reservedCostUsd">The amount in USD that was previously reserved.</param>
    /// <param name="actualCostUsd">The actual cost in USD calculated from the response's token usage (or <c>0m</c> if the request failed).</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>A <see cref="ValueTask"/> representing the asynchronous reconciliation operation.</returns>
    ValueTask ReconcileAsync(
        string key,
        decimal reservedCostUsd,
        decimal actualCostUsd,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the current cumulative spend in USD for the specified scope key and <paramref name="rule"/> within the active window.
    /// </summary>
    /// <param name="key">The tenant or global scope key.</param>
    /// <param name="rule">The budget rule specifying the window type and duration.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>The current spend in USD within the active window.</returns>
    ValueTask<decimal> GetCurrentSpendAsync(
        string key,
        BudgetRule rule,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the current cumulative spend in USD for the specified scope key within a sliding window of duration <paramref name="window"/>.
    /// </summary>
    /// <param name="key">The tenant or global scope key.</param>
    /// <param name="window">The duration of the sliding budget tracking window.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>The current spend in USD within the active sliding window.</returns>
    ValueTask<decimal> GetCurrentSpendAsync(
        string key,
        TimeSpan window,
        CancellationToken cancellationToken = default);
}
