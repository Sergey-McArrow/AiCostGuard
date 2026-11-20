using AiCostGuard.Estimation;
using Microsoft.Extensions.AI;

namespace AiCostGuard.Tests;

public sealed class HeuristicTokenEstimatorTests
{
    [Fact]
    public void EstimateTokens_NullMessages_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() =>
            HeuristicTokenEstimator.Instance.EstimateTokens(null!));
    }

    [Fact]
    public void EstimateTokens_EmptyMessagesWithoutInstructions_ReturnsZero()
    {
        int tokens = HeuristicTokenEstimator.Instance.EstimateTokens([]);

        Assert.Equal(0, tokens);
    }

    [Theory]
    [InlineData("Hi", 5)]           // 4 overhead + ceil(2 / 4.0) = 5
    [InlineData("1234", 5)]         // 4 overhead + ceil(4 / 4.0) = 5
    [InlineData("12345", 6)]        // 4 overhead + ceil(5 / 4.0) = 6
    [InlineData("12345678", 6)]     // 4 overhead + ceil(8 / 4.0) = 6
    [InlineData("123456789", 7)]    // 4 overhead + ceil(9 / 4.0) = 7
    public void EstimateTokens_SingleMessage_UsesCeilingFourCharsPerTokenPlusOverhead(string text, int expectedTokens)
    {
        ChatMessage[] messages = [new(ChatRole.User, text)];

        int actual = HeuristicTokenEstimator.Instance.EstimateTokens(messages);

        Assert.Equal(expectedTokens, actual);
    }

    [Fact]
    public void EstimateTokens_MultipleMessagesAndInstructions_SumsAllComponentsDeterministically()
    {
        ChatMessage[] messages =
        [
            new(ChatRole.System, "12345678"), // 4 + 2 = 6
            new(ChatRole.User, "1234"),       // 4 + 1 = 5
        ];
        ChatOptions options = new()
        {
            Instructions = "1234567812345678", // 4 + 4 = 8
        };

        int actual = HeuristicTokenEstimator.Instance.EstimateTokens(messages, options);

        Assert.Equal(19, actual);
    }
}
