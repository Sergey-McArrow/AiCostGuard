using Microsoft.Extensions.AI;

namespace AiCostGuard.Estimation;

/// <summary>
/// A fast, zero-allocation heuristic token estimator based on character length (~4 characters per token plus per-message framing overhead).
/// </summary>
public sealed class HeuristicTokenEstimator : ITokenEstimator
{
    private const int PerMessageOverheadTokens = 4;
    private const int CharactersPerToken = 4;

    /// <summary>
    /// Shared singleton instance of <see cref="HeuristicTokenEstimator"/>.
    /// </summary>
    public static HeuristicTokenEstimator Instance { get; } = new();

    /// <inheritdoc />
    public int EstimateTokens(IEnumerable<ChatMessage> messages, ChatOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(messages);

        long totalTokens = 0;
        bool hasAnyContent = false;

        if (!string.IsNullOrEmpty(options?.Instructions))
        {
            hasAnyContent = true;
            totalTokens += PerMessageOverheadTokens + EstimateTextTokens(options.Instructions.AsSpan());
            if (totalTokens >= int.MaxValue)
            {
                return int.MaxValue;
            }
        }

        foreach (ChatMessage? message in messages)
        {
            if (message is null)
            {
                continue;
            }

            hasAnyContent = true;
            totalTokens += PerMessageOverheadTokens;

            foreach (AIContent content in message.Contents)
            {
                if (content is TextContent textContent && !string.IsNullOrEmpty(textContent.Text))
                {
                    totalTokens += EstimateTextTokens(textContent.Text.AsSpan());
                    if (totalTokens >= int.MaxValue)
                    {
                        return int.MaxValue;
                    }
                }
            }

            if (totalTokens >= int.MaxValue)
            {
                return int.MaxValue;
            }
        }

        return hasAnyContent ? (int)Math.Max(1L, totalTokens) : 0;
    }

    private static long EstimateTextTokens(ReadOnlySpan<char> text)
    {
        if (text.IsEmpty)
        {
            return 0;
        }

        // Integer ceiling division using 64-bit arithmetic to avoid overflow on huge strings.
        return ((long)text.Length + CharactersPerToken - 1) / CharactersPerToken;
    }
}
