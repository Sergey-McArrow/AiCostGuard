using AiCostGuard.Pricing;
using Microsoft.Extensions.AI;

namespace AiCostGuard.Options;

/// <summary>
/// Configures per-request token guardrails, rate limiting (TPM), multi-rule monetary budgets (USD),
/// multi-tenancy, and fallback behavior for <see cref="CostGuardChatClient"/>.
/// </summary>
public sealed class CostGuardOptions
{
    /// <summary>
    /// Default global scope key used when <see cref="KeySelector"/> is not configured or returns <see langword="null"/>.
    /// </summary>
    public const string DefaultScopeKey = "default";

    /// <summary>
    /// Gets or sets the maximum number of estimated tokens (input + output buffer) allowed for a single request.
    /// When exceeded, a <see cref="Exceptions.CostGuardLimitExceededException"/> with <see cref="LimitExceededReason.RequestTokenLimitExceeded"/> is immediately thrown.
    /// When <see langword="null"/>, the per-request token guardrail is disabled.
    /// </summary>
    public long? MaxTokensPerRequest { get; set; }

    /// <summary>
    /// Gets or sets the maximum number of tokens allowed per 1-minute sliding window per tenant/scope.
    /// When <see langword="null"/>, token rate limiting (TPM) is disabled.
    /// </summary>
    public long? MaxTokensPerMinute { get; set; }

    /// <summary>
    /// Gets or sets the action to perform when <see cref="MaxTokensPerMinute"/> is exceeded.
    /// Defaults to <see cref="LimitExceededAction.Wait"/>.
    /// </summary>
    public LimitExceededAction OnRateLimitExceeded { get; set; } = LimitExceededAction.Wait;

    /// <summary>
    /// Gets the list of monetary budget rules evaluated simultaneously for each request (for example, a soft daily fallback rule and a hard monthly cap).
    /// </summary>
    public List<BudgetRule> BudgetRules { get; init; } = [];

    /// <summary>
    /// Gets or sets a single maximum monetary spend in USD allowed within <see cref="BudgetWindow"/> per tenant/scope.
    /// This is a convenience property that works alongside <see cref="BudgetRules"/> as a sliding-window rule with <see cref="OnBudgetExceeded"/>.
    /// When <see langword="null"/>, only rules in <see cref="BudgetRules"/> are evaluated.
    /// </summary>
    public decimal? MaxBudgetUsd { get; set; }

    /// <summary>
    /// Gets or sets the sliding time window over which <see cref="MaxBudgetUsd"/> is tracked.
    /// Defaults to 24 hours.
    /// </summary>
    public TimeSpan BudgetWindow { get; set; } = TimeSpan.FromHours(24);

    /// <summary>
    /// Gets or sets the action to perform when <see cref="MaxBudgetUsd"/> is exceeded.
    /// Defaults to <see cref="LimitExceededAction.Throw"/>.
    /// </summary>
    public LimitExceededAction OnBudgetExceeded { get; set; } = LimitExceededAction.Throw;

    /// <summary>
    /// Gets or sets the number of output tokens to reserve ahead of a request when <see cref="ChatOptions.MaxOutputTokens"/> is not specified.
    /// Defaults to 500 tokens.
    /// </summary>
    public int EstimatedOutputTokensBuffer { get; set; } = 500;

    /// <summary>
    /// Gets or sets the maximum duration to wait when <see cref="LimitExceededAction.Wait"/> is configured.
    /// Defaults to 30 seconds.
    /// </summary>
    public TimeSpan MaxWaitTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Gets or sets the fallback <see cref="IChatClient"/> used when <see cref="LimitExceededAction.Fallback"/> is triggered.
    /// </summary>
    public IChatClient? FallbackClient { get; set; }

    /// <summary>
    /// Gets or sets a delegate that extracts a tenant or user scope key from <see cref="ChatOptions"/>.
    /// When <see langword="null"/>, all requests share the <see cref="DefaultScopeKey"/> (<c>"default"</c>) scope.
    /// </summary>
    public Func<ChatOptions?, string>? KeySelector { get; set; }

    /// <summary>
    /// Gets the case-insensitive lookup table mapping model IDs to their <see cref="ModelPricing"/>.
    /// Initialized with <see cref="DefaultModelPricing.CreateDefaultMap"/>.
    /// </summary>
    public Dictionary<string, ModelPricing> Pricing { get; init; } = DefaultModelPricing.CreateDefaultMap();

    /// <summary>
    /// Gets or sets the fallback pricing to apply when a model ID is not found in <see cref="Pricing"/>.
    /// When <see langword="null"/>, unknown models incur zero monetary cost.
    /// </summary>
    public ModelPricing? DefaultPricing { get; set; }

    /// <summary>
    /// Adds a fixed calendar window monetary budget rule (<see cref="BudgetWindowType.FixedUtcDay"/> or <see cref="BudgetWindowType.FixedUtcMonth"/>) to <see cref="BudgetRules"/>.
    /// </summary>
    /// <param name="maxUsd">The maximum spend in USD allowed within the calendar window.</param>
    /// <param name="windowType">The fixed calendar window type (<see cref="BudgetWindowType.FixedUtcDay"/> or <see cref="BudgetWindowType.FixedUtcMonth"/>).</param>
    /// <param name="onExceeded">The action to take when this budget rule is exceeded.</param>
    /// <returns>The current <see cref="CostGuardOptions"/> instance for fluent chaining.</returns>
    public CostGuardOptions AddBudgetRule(
        decimal maxUsd,
        BudgetWindowType windowType,
        LimitExceededAction onExceeded = LimitExceededAction.Throw)
    {
        BudgetRules.Add(new BudgetRule(maxUsd, windowType, onExceeded));
        return this;
    }

    /// <summary>
    /// Adds a sliding-window monetary budget rule (<see cref="BudgetWindowType.Sliding"/>) to <see cref="BudgetRules"/>.
    /// </summary>
    /// <param name="maxUsd">The maximum spend in USD allowed within <paramref name="slidingWindow"/>.</param>
    /// <param name="slidingWindow">The sliding window duration.</param>
    /// <param name="onExceeded">The action to take when this budget rule is exceeded.</param>
    /// <returns>The current <see cref="CostGuardOptions"/> instance for fluent chaining.</returns>
    public CostGuardOptions AddBudgetRule(
        decimal maxUsd,
        TimeSpan slidingWindow,
        LimitExceededAction onExceeded = LimitExceededAction.Throw)
    {
        BudgetRules.Add(new BudgetRule(maxUsd, BudgetWindowType.Sliding, onExceeded, slidingWindow));
        return this;
    }

    /// <summary>
    /// Adds a pre-constructed <see cref="BudgetRule"/> to <see cref="BudgetRules"/>.
    /// </summary>
    /// <param name="rule">The budget rule to add.</param>
    /// <returns>The current <see cref="CostGuardOptions"/> instance for fluent chaining.</returns>
    public CostGuardOptions AddBudgetRule(BudgetRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);

        BudgetRules.Add(rule);
        return this;
    }

    /// <summary>
    /// Configures or overrides the pricing for a specific model identifier in <see cref="Pricing"/>.
    /// </summary>
    /// <param name="modelId">The model identifier (matched case-insensitively).</param>
    /// <param name="inputCostPerMillionTokens">The cost in USD per 1,000,000 input tokens.</param>
    /// <param name="outputCostPerMillionTokens">The cost in USD per 1,000,000 output tokens.</param>
    /// <returns>The current <see cref="CostGuardOptions"/> instance for fluent chaining.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="modelId"/> is null or whitespace.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when either cost is negative.</exception>
    public CostGuardOptions SetModelPricing(
        string modelId,
        decimal inputCostPerMillionTokens,
        decimal outputCostPerMillionTokens)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);

        Pricing[modelId] = new ModelPricing(inputCostPerMillionTokens, outputCostPerMillionTokens);
        return this;
    }

    internal CostGuardOptions Clone() =>
        new()
        {
            MaxTokensPerRequest = MaxTokensPerRequest,
            MaxTokensPerMinute = MaxTokensPerMinute,
            OnRateLimitExceeded = OnRateLimitExceeded,
            BudgetRules = [.. BudgetRules],
            MaxBudgetUsd = MaxBudgetUsd,
            BudgetWindow = BudgetWindow,
            OnBudgetExceeded = OnBudgetExceeded,
            EstimatedOutputTokensBuffer = EstimatedOutputTokensBuffer,
            MaxWaitTimeout = MaxWaitTimeout,
            FallbackClient = FallbackClient,
            KeySelector = KeySelector,
            Pricing = new Dictionary<string, ModelPricing>(Pricing, StringComparer.OrdinalIgnoreCase),
            DefaultPricing = DefaultPricing,
        };

    internal IReadOnlyList<BudgetRule> GetEffectiveBudgetRules()
    {
        if (!MaxBudgetUsd.HasValue)
        {
            return [.. BudgetRules];
        }

        BudgetRule legacyRule = new(
            MaxBudgetUsd.Value,
            BudgetWindowType.Sliding,
            OnBudgetExceeded,
            BudgetWindow);

        if (BudgetRules.Count == 0)
        {
            return [legacyRule];
        }

        return [.. BudgetRules, legacyRule];
    }
}
