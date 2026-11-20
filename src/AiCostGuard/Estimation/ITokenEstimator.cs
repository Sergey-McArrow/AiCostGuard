using Microsoft.Extensions.AI;

namespace AiCostGuard.Estimation;

/// <summary>
/// Estimates the number of input tokens required for a sequence of chat messages before sending a request to an AI model.
/// </summary>
public interface ITokenEstimator
{
    /// <summary>
    /// Estimates the input token count for the specified messages and optional chat configuration.
    /// </summary>
    /// <param name="messages">The conversation messages to estimate.</param>
    /// <param name="options">Optional chat options (such as system instructions) to include in the estimate.</param>
    /// <returns>The estimated number of input tokens (always non-negative).</returns>
    int EstimateTokens(IEnumerable<ChatMessage> messages, ChatOptions? options = null);
}
