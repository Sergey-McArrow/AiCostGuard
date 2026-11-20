using System.Runtime.CompilerServices;
using AiCostGuard.DependencyInjection;
using AiCostGuard.Options;
using AiCostGuard.Storage;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OllamaSharp;

namespace AiCostGuard.Sample;

/// <summary>
/// Demonstrates <c>Meai.CostGuard</c> in action: Primary cloud model -> streaming token reconciliation -> automatic local Ollama fallback via <c>OllamaSharp</c>.
/// </summary>
internal static class Program
{
    public static async Task Main(string[] args)
    {
        Console.WriteLine("=================================================================");
        Console.WriteLine("  Meai.CostGuard — Interactive Demo (Primary -> Ollama Fallback)  ");
        Console.WriteLine("=================================================================");
        Console.WriteLine();

        Uri ollamaUri = new(Environment.GetEnvironmentVariable("OLLAMA_URI") ?? "http://localhost:11434");
        string ollamaModel = Environment.GetEnvironmentVariable("OLLAMA_MODEL") ?? "llama3.1:8b";
        bool forceMockFallback = args.Contains("--mock-fallback", StringComparer.OrdinalIgnoreCase);

        // 1. Configure the fallback client using OllamaSharp (OllamaApiClient : IChatClient),
        // wrapped in a resilient decorator so the demo also runs out-of-the-box if local Ollama is offline.
        using OllamaApiClient rawOllamaClient = new(ollamaUri, ollamaModel);
        using IChatClient fallbackClient = new ResilientOllamaFallbackClient(rawOllamaClient, ollamaUri, ollamaModel, forceMockFallback);

        // 2. Configure the simulated commercial cloud Primary client (e.g., gpt-5: $1.25 / 1M input, $10.00 / 1M output).
        // Each simulated response reports 1,000 input + 1,000 output tokens = $0.01125 USD per request.
        using IChatClient primaryClient = new SimulatedCloudChatClient("gpt-5");

        // 3. Build the Microsoft.Extensions.AI pipeline via Dependency Injection.
        // Daily budget of $0.0225 USD allows Requests #1 and #2 ($0.01125 each = $0.0225 total) on Primary,
        // and routes Request #3 to local Ollama fallback via OllamaSharp!
        var dailyFallbackRule = BudgetRule.FixedUtcDay(maxAmountUsd: 0.0225m, LimitExceededAction.Fallback);
        var monthlyHardCapRule = BudgetRule.FixedUtcMonth(maxAmountUsd: 10.00m, LimitExceededAction.Throw);

        ServiceCollection services = new();
        services.AddLogging(logging =>
        {
            logging.AddSimpleConsole(console =>
            {
                console.SingleLine = true;
                console.TimestampFormat = "HH:mm:ss ";
            });
            logging.SetMinimumLevel(LogLevel.Information);
        });
        services.AddSingleton<IBudgetStore, InMemoryBudgetStore>();

        services.AddChatClient(primaryClient)
            .UseCostGuard(options =>
            {
                options.MaxTokensPerRequest = 16_000;
                options.MaxTokensPerMinute = 50_000;
                options.EstimatedOutputTokensBuffer = 800;
                options.OnRateLimitExceeded = LimitExceededAction.Wait;

                options.AddBudgetRule(dailyFallbackRule);
                options.AddBudgetRule(monthlyHardCapRule);
            }, fallbackClient: fallbackClient);

        await using ServiceProvider provider = services.BuildServiceProvider();
        IChatClient chatClient = provider.GetRequiredService<IChatClient>();
        IBudgetStore budgetStore = provider.GetRequiredService<IBudgetStore>();

        string[] prompts =
        [
            "1. Summarize why token-aware rate limiting is better than request-based limiting for LLMs.",
            "2. Explain how streaming token reconciliation works in Microsoft.Extensions.AI.",
            "3. In one short sentence, explain why falling back to a local LLM in C# saves cloud API costs.",
        ];

        for (int i = 0; i < prompts.Length; i++)
        {
            decimal spendBefore = await budgetStore.GetCurrentSpendAsync(CostGuardOptions.DefaultScopeKey, dailyFallbackRule);
            Console.WriteLine($"--- Request #{i + 1} (Daily Spend Before: ${spendBefore:F5} / ${dailyFallbackRule.MaxAmountUsd:F5} USD) ---");
            Console.WriteLine($"User: {prompts[i]}");

            if (i == 0)
            {
                // Request #1: standard unary GetResponseAsync
                ChatResponse response = await chatClient.GetResponseAsync([new ChatMessage(ChatRole.User, prompts[i])]);
                Console.WriteLine($"Assistant [{response.ModelId}]: {response.Text}");
            }
            else
            {
                // Requests #2 and #3: streaming GetStreamingResponseAsync
                string? activeModelId = null;
                await foreach (ChatResponseUpdate update in chatClient.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, prompts[i])]))
                {
                    if (activeModelId is null)
                    {
                        activeModelId = update.ModelId ?? "streaming";
                        Console.Write($"Assistant [{activeModelId} (streaming)]: ");
                    }

                    Console.Write(update.Text);
                }

                Console.WriteLine();
            }

            decimal spendAfter = await budgetStore.GetCurrentSpendAsync(CostGuardOptions.DefaultScopeKey, dailyFallbackRule);
            Console.WriteLine($"-> Daily Spend After Reconciliation: ${spendAfter:F5} USD");
            Console.WriteLine();
        }
    }
}

/// <summary>
/// Simulates a commercial cloud LLM (such as <c>gpt-5</c>) that reports realistic token usage (<see cref="UsageDetails"/> / <see cref="UsageContent"/>).
/// </summary>
internal sealed class SimulatedCloudChatClient(string modelId) : IChatClient
{
    private readonly ChatClientMetadata _metadata = new("SimulatedCloudProvider", null, modelId);

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ChatMessage reply = new(
            ChatRole.Assistant,
            $"[Cloud Primary {modelId}] Token-aware rate limiting accounts for prompt and completion token weights rather than raw HTTP request counts.");

        ChatResponse response = new(reply)
        {
            ModelId = modelId,
            Usage = new UsageDetails
            {
                InputTokenCount = 1_000,
                OutputTokenCount = 1_000,
                TotalTokenCount = 2_000,
            },
        };

        return Task.FromResult(response);
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        string[] chunks =
        [
            $"[Cloud Primary {modelId}] ",
            "CostGuard reserves estimated tokens pre-flight ",
            "and reconciles actual UsageContent inside the finally block.",
        ];

        foreach (string chunk in chunks)
        {
            await Task.Delay(25, cancellationToken);
            yield return new ChatResponseUpdate(ChatRole.Assistant, chunk)
            {
                ModelId = modelId,
            };
        }

        UsageDetails usage = new()
        {
            InputTokenCount = 1_000,
            OutputTokenCount = 1_000,
            TotalTokenCount = 2_000,
        };

        yield return new ChatResponseUpdate
        {
            Role = ChatRole.Assistant,
            ModelId = modelId,
            Contents = [new UsageContent(usage)],
        };
    }

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceKey is null && serviceType == typeof(ChatClientMetadata) ? _metadata : null;

    public void Dispose()
    {
    }
}

/// <summary>
/// Wraps a real <see cref="OllamaApiClient"/> (<see cref="IChatClient"/>) from <c>OllamaSharp</c> and gracefully falls back to a simulated local response
/// when <c>--mock-fallback</c> is passed or when the local Ollama daemon on <c>localhost:11434</c> is not running.
/// </summary>
internal sealed class ResilientOllamaFallbackClient(
    OllamaApiClient ollamaClient,
    Uri ollamaUri,
    string modelId,
    bool forceMock) : IChatClient
{
    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        if (!forceMock)
        {
            try
            {
                return await ((IChatClient)ollamaClient).GetResponseAsync(messages, options, cancellationToken);
            }
            catch (HttpRequestException)
            {
                // Local Ollama daemon is not running; fall through to offline simulation.
            }
        }

        return new ChatResponse(new ChatMessage(
            ChatRole.Assistant,
            $"[OllamaSharp Fallback ({modelId} @ {ollamaUri})] Handled locally at $0.00 USD cost!"))
        {
            ModelId = modelId,
        };
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (!forceMock)
        {
            IAsyncEnumerator<ChatResponseUpdate> enumerator = ((IChatClient)ollamaClient)
                .GetStreamingResponseAsync(messages, options, cancellationToken)
                .GetAsyncEnumerator(cancellationToken);

            try
            {
                bool streamedAny = false;
                while (true)
                {
                    bool hasNext;
                    try
                    {
                        hasNext = await enumerator.MoveNextAsync();
                    }
                    catch (HttpRequestException)
                    {
                        break;
                    }

                    if (!hasNext)
                    {
                        if (streamedAny)
                        {
                            yield break;
                        }

                        break;
                    }

                    streamedAny = true;
                    yield return enumerator.Current;
                }
            }
            finally
            {
                await enumerator.DisposeAsync();
            }
        }

        string[] fallbackChunks =
        [
            $"[OllamaSharp Fallback ({modelId} @ {ollamaUri})] ",
            "Daily cloud budget reached! ",
            "This response is served by the fallback client at $0.00 USD.",
        ];

        foreach (string chunk in fallbackChunks)
        {
            await Task.Delay(25, cancellationToken);
            yield return new ChatResponseUpdate(ChatRole.Assistant, chunk)
            {
                ModelId = modelId,
            };
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        ((IChatClient)ollamaClient).GetService(serviceType, serviceKey);

    public void Dispose()
    {
    }
}
