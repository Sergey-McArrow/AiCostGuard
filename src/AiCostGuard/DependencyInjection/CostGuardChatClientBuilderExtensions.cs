using AiCostGuard.Estimation;
using AiCostGuard.Options;
using AiCostGuard.RateLimiting;
using AiCostGuard.Storage;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AiCostGuard.DependencyInjection;

/// <summary>
/// Provides extension methods for integrating <see cref="CostGuardChatClient"/> into a <see cref="ChatClientBuilder"/> pipeline.
/// </summary>
public static class CostGuardChatClientBuilderExtensions
{
    /// <summary>
    /// Adds <see cref="CostGuardChatClient"/> middleware to the chat client pipeline with the specified configuration delegate.
    /// </summary>
    /// <param name="builder">The <see cref="ChatClientBuilder"/> to configure.</param>
    /// <param name="configure">A delegate used to configure <see cref="CostGuardOptions"/>.</param>
    /// <param name="fallbackClient">An optional fallback <see cref="IChatClient"/> used when <see cref="LimitExceededAction.Fallback"/> is triggered.</param>
    /// <returns>The configured <see cref="ChatClientBuilder"/> for fluent chaining.</returns>
    public static ChatClientBuilder UseCostGuard(
        this ChatClientBuilder builder,
        Action<CostGuardOptions> configure,
        IChatClient? fallbackClient = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configure);

        return builder.Use((innerClient, services) =>
        {
            CostGuardOptions options = ResolveBaseOptions(services);
            configure(options);

            if (fallbackClient is not null)
            {
                options.FallbackClient = fallbackClient;
            }

            return CreateClient(innerClient, options, services);
        });
    }

    /// <summary>
    /// Adds <see cref="CostGuardChatClient"/> middleware to the chat client pipeline with a service-provider-aware configuration delegate and an optional fallback client factory.
    /// </summary>
    /// <param name="builder">The <see cref="ChatClientBuilder"/> to configure.</param>
    /// <param name="configure">A delegate that receives the <see cref="IServiceProvider"/> and <see cref="CostGuardOptions"/>.</param>
    /// <param name="fallbackClientFactory">An optional factory that resolves the fallback <see cref="IChatClient"/> from the <see cref="IServiceProvider"/>.</param>
    /// <returns>The configured <see cref="ChatClientBuilder"/> for fluent chaining.</returns>
    public static ChatClientBuilder UseCostGuard(
        this ChatClientBuilder builder,
        Action<IServiceProvider, CostGuardOptions> configure,
        Func<IServiceProvider, IChatClient>? fallbackClientFactory = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configure);

        return builder.Use((innerClient, services) =>
        {
            CostGuardOptions options = ResolveBaseOptions(services);
            configure(services, options);

            if (fallbackClientFactory is not null)
            {
                options.FallbackClient = fallbackClientFactory(services);
            }

            return CreateClient(innerClient, options, services);
        });
    }

    /// <summary>
    /// Adds <see cref="CostGuardChatClient"/> middleware to the chat client pipeline using a pre-configured <see cref="CostGuardOptions"/> instance.
    /// </summary>
    /// <param name="builder">The <see cref="ChatClientBuilder"/> to configure.</param>
    /// <param name="options">The <see cref="CostGuardOptions"/> instance to use.</param>
    /// <returns>The configured <see cref="ChatClientBuilder"/> for fluent chaining.</returns>
    public static ChatClientBuilder UseCostGuard(
        this ChatClientBuilder builder,
        CostGuardOptions options)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(options);

        return builder.Use((innerClient, services) => CreateClient(innerClient, options, services));
    }

    private static CostGuardOptions ResolveBaseOptions(IServiceProvider services) =>
        services.GetService<IOptions<CostGuardOptions>>()?.Value.Clone() ?? new CostGuardOptions();

    private static CostGuardChatClient CreateClient(
        IChatClient innerClient,
        CostGuardOptions options,
        IServiceProvider services)
    {
        TimeProvider timeProvider = services.GetService<TimeProvider>() ?? TimeProvider.System;
        ITokenEstimator tokenEstimator = services.GetService<ITokenEstimator>() ?? HeuristicTokenEstimator.Instance;
        ITokenRateLimiter rateLimiter = services.GetService<ITokenRateLimiter>() ?? new SlidingWindowTokenRateLimiter(timeProvider);
        IBudgetStore budgetStore = services.GetService<IBudgetStore>() ?? new InMemoryBudgetStore(timeProvider);
        ILogger<CostGuardChatClient>? logger = services.GetService<ILoggerFactory>()?.CreateLogger<CostGuardChatClient>()
            ?? services.GetService<ILogger<CostGuardChatClient>>();

        return new CostGuardChatClient(
            innerClient,
            options,
            tokenEstimator,
            rateLimiter,
            budgetStore,
            timeProvider,
            logger);
    }
}
