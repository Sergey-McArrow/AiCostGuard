namespace AiCostGuard.Pricing;

/// <summary>
/// Provides built-in default pricing tables for current commercial and local language models (in USD per 1,000,000 tokens).
/// Prices reflect standard list rates as of November 2026 and can be overridden via <see cref="Options.CostGuardOptions.Pricing"/>
/// or <see cref="Options.CostGuardOptions.SetModelPricing"/>.
/// </summary>
public static class DefaultModelPricing
{
    /// <summary>
    /// Creates a new case-insensitive dictionary pre-populated with default model pricing in USD per 1,000,000 tokens.
    /// </summary>
    /// <returns>A mutable dictionary mapping model identifiers to their <see cref="ModelPricing"/>.</returns>
    public static Dictionary<string, ModelPricing> CreateDefaultMap() =>
        new(StringComparer.OrdinalIgnoreCase)
        {
            // OpenAI GPT-6 generation (standard rates for <= 272K input tokens)
            ["gpt-6.2-astra"] = new ModelPricing(8.00m, 40.00m),
            ["gpt-6-astra"] = new ModelPricing(8.00m, 40.00m),
            ["gpt-6.2-sol"] = new ModelPricing(2.00m, 10.00m),
            ["gpt-6.1-sol"] = new ModelPricing(2.00m, 10.00m),
            ["gpt-6-sol"] = new ModelPricing(2.00m, 10.00m),
            ["gpt-6.1-terra"] = new ModelPricing(1.00m, 5.00m),
            ["gpt-6-luna"] = new ModelPricing(0.10m, 0.50m),

            // OpenAI GPT-5, Reasoning (o3 / o4) & GPT-4 legacy models
            ["gpt-5"] = new ModelPricing(1.25m, 10.00m),
            ["gpt-5-mini"] = new ModelPricing(0.25m, 2.00m),
            ["gpt-5-nano"] = new ModelPricing(0.05m, 0.40m),
            ["gpt-5-pro"] = new ModelPricing(15.00m, 120.00m),
            ["o3"] = new ModelPricing(2.00m, 8.00m),
            ["o3-mini"] = new ModelPricing(1.10m, 4.40m),
            ["o4-mini"] = new ModelPricing(1.10m, 4.40m),
            ["gpt-4.1"] = new ModelPricing(2.00m, 8.00m),
            ["gpt-4.1-mini"] = new ModelPricing(0.40m, 1.60m),
            ["gpt-4.1-nano"] = new ModelPricing(0.10m, 0.40m),
            ["gpt-4o"] = new ModelPricing(2.50m, 10.00m),
            ["gpt-4o-mini"] = new ModelPricing(0.15m, 0.60m),

            // Anthropic Claude 5.5 / 5.2 generation (standard list rates)
            ["claude-fable-5-2"] = new ModelPricing(8.00m, 40.00m),
            ["claude-mythos-5-2"] = new ModelPricing(8.00m, 40.00m),
            ["claude-opus-5-5"] = new ModelPricing(4.00m, 20.00m),
            ["claude-sonnet-5-5"] = new ModelPricing(2.00m, 10.00m),
            ["claude-haiku-5-5"] = new ModelPricing(0.10m, 0.50m),

            // Anthropic Claude 4.5 / 4 / 3.x legacy models
            ["claude-sonnet-4-5"] = new ModelPricing(3.00m, 15.00m),
            ["claude-opus-4-5"] = new ModelPricing(5.00m, 25.00m),
            ["claude-haiku-4-5"] = new ModelPricing(1.00m, 5.00m),
            ["claude-sonnet-4-0"] = new ModelPricing(3.00m, 15.00m),
            ["claude-opus-4-0"] = new ModelPricing(15.00m, 75.00m),
            ["claude-3-7-sonnet"] = new ModelPricing(3.00m, 15.00m),
            ["claude-3-5-sonnet"] = new ModelPricing(3.00m, 15.00m),
            ["claude-3-5-sonnet-latest"] = new ModelPricing(3.00m, 15.00m),
            ["claude-3-5-haiku"] = new ModelPricing(0.80m, 4.00m),

            // Google Gemini 4 generation (global standard rates; Carbon standard rate effective Dec 1, 2026)
            ["gemini-4-carbon"] = new ModelPricing(4.00m, 20.00m),
            ["gemini-4-neon"] = new ModelPricing(3.00m, 15.00m),
            ["gemini-4-hydrogen"] = new ModelPricing(1.50m, 7.50m),

            // Google Gemini 3.x & 2.5 legacy models
            ["gemini-3.8-flash"] = new ModelPricing(0.75m, 3.75m),
            ["gemini-3.6-flash"] = new ModelPricing(0.75m, 3.75m),
            ["gemini-3.5-pro"] = new ModelPricing(2.00m, 12.00m),
            ["gemini-3.5-flash"] = new ModelPricing(0.30m, 2.50m),
            ["gemini-3.5-flash-lite"] = new ModelPricing(0.10m, 0.40m),
            ["gemini-3.1-pro"] = new ModelPricing(2.00m, 12.00m),
            ["gemini-3.0-pro"] = new ModelPricing(2.00m, 12.00m),
            ["gemini-3.0-flash"] = new ModelPricing(0.30m, 2.50m),
            ["gemini-2.5-pro"] = new ModelPricing(1.25m, 10.00m),
            ["gemini-2.5-flash"] = new ModelPricing(0.30m, 2.50m),
            ["gemini-2.5-flash-lite"] = new ModelPricing(0.10m, 0.40m),
            ["gemini-2.0-flash"] = new ModelPricing(0.10m, 0.40m),

            // Local Ollama models (free / self-hosted: $0.00 USD)
            ["qwen3.6:latest"] = new ModelPricing(0.00m, 0.00m),
            ["qwen3.6"] = new ModelPricing(0.00m, 0.00m),
            ["qwen2.5"] = new ModelPricing(0.00m, 0.00m),
            ["deepseek-r1:14b"] = new ModelPricing(0.00m, 0.00m),
            ["deepseek-r1"] = new ModelPricing(0.00m, 0.00m),
            ["llama3.1:8b"] = new ModelPricing(0.00m, 0.00m),
            ["llama3.1"] = new ModelPricing(0.00m, 0.00m),
            ["llama3.2"] = new ModelPricing(0.00m, 0.00m),
        };
}
