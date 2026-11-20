namespace AiCostGuard.Pricing;

/// <summary>
/// Represents the per-million-token pricing for an AI language model in US dollars (USD).
/// </summary>
public sealed record ModelPricing
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ModelPricing"/> record.
    /// </summary>
    /// <param name="inputCostPerMillionTokens">The cost in USD per 1,000,000 input (prompt) tokens.</param>
    /// <param name="outputCostPerMillionTokens">The cost in USD per 1,000,000 output (completion) tokens.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <paramref name="inputCostPerMillionTokens"/> or <paramref name="outputCostPerMillionTokens"/> is negative.
    /// </exception>
    public ModelPricing(decimal inputCostPerMillionTokens, decimal outputCostPerMillionTokens)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(inputCostPerMillionTokens);
        ArgumentOutOfRangeException.ThrowIfNegative(outputCostPerMillionTokens);

        InputCostPerMillionTokens = inputCostPerMillionTokens;
        OutputCostPerMillionTokens = outputCostPerMillionTokens;
    }

    /// <summary>
    /// Gets the cost in USD per 1,000,000 input (prompt) tokens.
    /// </summary>
    public decimal InputCostPerMillionTokens
    {
        get;
        init
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value, nameof(InputCostPerMillionTokens));
            field = value;
        }
    }

    /// <summary>
    /// Gets the cost in USD per 1,000,000 output (completion) tokens.
    /// </summary>
    public decimal OutputCostPerMillionTokens
    {
        get;
        init
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value, nameof(OutputCostPerMillionTokens));
            field = value;
        }
    }

    /// <summary>
    /// Calculates the total USD cost for the given input and output token counts.
    /// </summary>
    /// <param name="inputTokens">The number of input tokens consumed.</param>
    /// <param name="outputTokens">The number of output tokens consumed.</param>
    /// <returns>The calculated cost in USD.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <paramref name="inputTokens"/> or <paramref name="outputTokens"/> is negative.
    /// </exception>
    public decimal CalculateCost(long inputTokens, long outputTokens)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(inputTokens);
        ArgumentOutOfRangeException.ThrowIfNegative(outputTokens);

        return ((inputTokens * InputCostPerMillionTokens) + (outputTokens * OutputCostPerMillionTokens)) / 1_000_000m;
    }
}
