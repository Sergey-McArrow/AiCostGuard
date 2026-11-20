using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace AiCostGuard.Tests.Fakes;

/// <summary>
/// A lightweight, reflection-free fake <see cref="IChatClient"/> for deterministic unit and stress testing.
/// </summary>
/// <param name="defaultModelId">The default model ID reported by <see cref="ChatClientMetadata"/>.</param>
/// <param name="responseText">The default response text returned by <see cref="GetResponseAsync"/>.</param>
/// <param name="inputTokens">The default reported input token count in <see cref="UsageDetails"/>.</param>
/// <param name="outputTokens">The default reported output token count in <see cref="UsageDetails"/>.</param>
public sealed class FakeChatClient(
    string? defaultModelId = "gpt-4o",
    string responseText = "Fake response",
    long? inputTokens = 100,
    long? outputTokens = 50) : IChatClient
{
    private readonly ChatClientMetadata _metadata = new("FakeProvider", null, defaultModelId);
    private int _callCount;
    private int _streamingCallCount;

    /// <summary>
    /// Gets the total number of times <see cref="GetResponseAsync"/> was invoked.
    /// </summary>
    public int CallCount => Volatile.Read(ref _callCount);

    /// <summary>
    /// Gets the total number of times <see cref="GetStreamingResponseAsync"/> was invoked.
    /// </summary>
    public int StreamingCallCount => Volatile.Read(ref _streamingCallCount);

    /// <summary>
    /// Gets or sets the response text returned by <see cref="GetResponseAsync"/>.
    /// </summary>
    public string ResponseText { get; set; } = responseText;

    /// <summary>
    /// Gets or sets the input token count reported in <see cref="UsageDetails"/>, or <see langword="null"/> to omit usage details.
    /// </summary>
    public long? InputTokens { get; set; } = inputTokens;

    /// <summary>
    /// Gets or sets the output token count reported in <see cref="UsageDetails"/>, or <see langword="null"/> to omit usage details.
    /// </summary>
    public long? OutputTokens { get; set; } = outputTokens;

    /// <summary>
    /// Gets or sets an optional exception to throw when <see cref="GetResponseAsync"/> or <see cref="GetStreamingResponseAsync"/> is called.
    /// </summary>
    public Exception? ExceptionToThrow { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether <see cref="ExceptionToThrow"/> should be thrown mid-stream (after yielding the first chunk) rather than before yielding.
    /// </summary>
    public bool ThrowAfterFirstChunk { get; set; }

    /// <summary>
    /// Gets or sets the chunks yielded by <see cref="GetStreamingResponseAsync"/>.
    /// </summary>
    public IReadOnlyList<string> StreamChunks { get; set; } = ["Hello", ", ", "world!"];

    /// <summary>
    /// Gets or sets a value indicating whether <see cref="GetStreamingResponseAsync"/> emits a final <see cref="UsageContent"/> update.
    /// </summary>
    public bool EmitStreamingUsage { get; set; } = true;

    /// <inheritdoc />
    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Interlocked.Increment(ref _callCount);

        if (ExceptionToThrow is not null)
        {
            throw ExceptionToThrow;
        }

        ChatMessage reply = new(ChatRole.Assistant, ResponseText);
        ChatResponse response = new(reply)
        {
            ModelId = options?.ModelId ?? _metadata.DefaultModelId,
        };

        if (InputTokens.HasValue || OutputTokens.HasValue)
        {
            long input = InputTokens ?? 0;
            long output = OutputTokens ?? 0;
            response.Usage = new UsageDetails
            {
                InputTokenCount = input,
                OutputTokenCount = output,
                TotalTokenCount = input + output,
            };
        }

        return Task.FromResult(response);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Interlocked.Increment(ref _streamingCallCount);

        if (ExceptionToThrow is not null && !ThrowAfterFirstChunk)
        {
            throw ExceptionToThrow;
        }

        string? modelId = options?.ModelId ?? _metadata.DefaultModelId;

        for (int i = 0; i < StreamChunks.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();

            ChatResponseUpdate textUpdate = new(ChatRole.Assistant, StreamChunks[i])
            {
                ModelId = modelId,
            };
            yield return textUpdate;

            if (i == 0 && ExceptionToThrow is not null && ThrowAfterFirstChunk)
            {
                throw ExceptionToThrow;
            }
        }

        if (EmitStreamingUsage && (InputTokens.HasValue || OutputTokens.HasValue))
        {
            long input = InputTokens ?? 0;
            long output = OutputTokens ?? 0;
            UsageDetails usage = new()
            {
                InputTokenCount = input,
                OutputTokenCount = output,
                TotalTokenCount = input + output,
            };

            ChatResponseUpdate usageUpdate = new()
            {
                Role = ChatRole.Assistant,
                ModelId = modelId,
                Contents = [new UsageContent(usage)],
            };
            yield return usageUpdate;
        }
    }

    /// <inheritdoc />
    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);

        if (serviceKey is null && serviceType == typeof(ChatClientMetadata))
        {
            return _metadata;
        }

        if (serviceKey is null && serviceType.IsInstanceOfType(this))
        {
            return this;
        }

        return null;
    }

    /// <inheritdoc />
    public void Dispose()
    {
    }
}
