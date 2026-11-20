using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using AiCostGuard.Estimation;
using AiCostGuard.Exceptions;
using AiCostGuard.Observability;
using AiCostGuard.Options;
using AiCostGuard.Pricing;
using AiCostGuard.RateLimiting;
using AiCostGuard.Storage;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AiCostGuard;

/// <summary>
/// A delegating <see cref="IChatClient"/> middleware that enforces per-request token guardrails,
/// token-aware rate limiting (TPM), multi-rule monetary budget caps (USD), automatic fallback routing,
/// and post-response token usage reconciliation.
/// </summary>
public sealed class CostGuardChatClient : DelegatingChatClient
{
    private static readonly TimeSpan _defaultWaitPollInterval = TimeSpan.FromMilliseconds(100);

    private readonly CostGuardOptions _options;
    private readonly List<BudgetWindowGroup> _budgetWindowGroups;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _multiRuleLocks = new(StringComparer.Ordinal);
    private readonly ITokenEstimator _tokenEstimator;
    private readonly ITokenRateLimiter _rateLimiter;
    private readonly IBudgetStore _budgetStore;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<CostGuardChatClient> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="CostGuardChatClient"/> class.
    /// </summary>
    /// <param name="innerClient">The underlying <see cref="IChatClient"/> to decorate.</param>
    /// <param name="options">The configuration options for rate limiting, budget enforcement, and fallback routing.</param>
    /// <param name="tokenEstimator">An optional token estimator. Defaults to <see cref="HeuristicTokenEstimator.Instance"/>.</param>
    /// <param name="rateLimiter">An optional token rate limiter. Defaults to a new <see cref="SlidingWindowTokenRateLimiter"/>.</param>
    /// <param name="budgetStore">An optional monetary budget store. Defaults to a new <see cref="InMemoryBudgetStore"/>.</param>
    /// <param name="timeProvider">An optional time provider. Defaults to <see cref="TimeProvider.System"/>.</param>
    /// <param name="logger">An optional logger instance. Defaults to <see cref="NullLogger{TCategoryName}.Instance"/>.</param>
    public CostGuardChatClient(
        IChatClient innerClient,
        CostGuardOptions options,
        ITokenEstimator? tokenEstimator = null,
        ITokenRateLimiter? rateLimiter = null,
        IBudgetStore? budgetStore = null,
        TimeProvider? timeProvider = null,
        ILogger<CostGuardChatClient>? logger = null)
        : base(innerClient)
    {
        ArgumentNullException.ThrowIfNull(options);
        ValidateOptions(options);

        _options = options;
        _budgetWindowGroups = GroupBudgetRulesByWindow(options.GetEffectiveBudgetRules());
        _timeProvider = timeProvider ?? TimeProvider.System;
        _tokenEstimator = tokenEstimator ?? HeuristicTokenEstimator.Instance;
        _rateLimiter = rateLimiter ?? new SlidingWindowTokenRateLimiter(_timeProvider);
        _budgetStore = budgetStore ?? new InMemoryBudgetStore(_timeProvider);
        _logger = logger ?? NullLogger<CostGuardChatClient>.Instance;
    }

    /// <inheritdoc />
    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);

        ExecutionPlan plan = await PrepareExecutionAsync(messages, options, cancellationToken).ConfigureAwait(false);

        if (plan.UseFallback)
        {
            IChatClient fallbackClient = GetRequiredFallbackClient(plan);
            return await fallbackClient.GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
        }

        ChatResponse? response = null;
        try
        {
            response = await base.GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
            return response;
        }
        finally
        {
            if (response is null)
            {
                // Request failed or was canceled before receiving a response: roll back reserved capacity.
                await ReconcileReservationsAsync(
                    plan,
                    modelId: plan.ModelId,
                    actualInputTokens: 0,
                    actualOutputTokens: 0,
                    actualTotalTokens: 0,
                    actualCostUsd: 0m,
                    recordTelemetry: false).ConfigureAwait(false);
            }
            else
            {
                long actualInput = Math.Max(0, response.Usage?.InputTokenCount ?? plan.EstimatedInputTokens);
                long actualOutput = Math.Max(0, response.Usage?.OutputTokenCount ?? plan.EstimatedOutputTokens);
                long actualTotal = Math.Max(0, response.Usage?.TotalTokenCount ?? SafeAddTokens(actualInput, actualOutput));

                string? resolvedModelId = !string.IsNullOrWhiteSpace(response.ModelId) ? response.ModelId : plan.ModelId;
                ModelPricing? pricing = ResolvePricing(resolvedModelId) ?? plan.Pricing;
                decimal actualCostUsd = pricing?.CalculateCost(actualInput, actualOutput) ?? 0m;

                await ReconcileReservationsAsync(
                    plan,
                    resolvedModelId,
                    actualInput,
                    actualOutput,
                    actualTotal,
                    actualCostUsd,
                    recordTelemetry: true).ConfigureAwait(false);
            }
        }
    }

    /// <inheritdoc />
    public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);

        return StreamCoreAsync(messages, options, cancellationToken);
    }

    private static void ValidateOptions(CostGuardOptions options)
    {
        if (options.MaxTokensPerRequest.HasValue)
        {
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.MaxTokensPerRequest.Value, 0, nameof(options.MaxTokensPerRequest));
        }

        if (options.MaxTokensPerMinute.HasValue)
        {
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.MaxTokensPerMinute.Value, 0, nameof(options.MaxTokensPerMinute));
        }

        if (options.MaxBudgetUsd.HasValue)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(options.MaxBudgetUsd.Value, nameof(options.MaxBudgetUsd));
        }

        foreach (BudgetRule? rule in options.BudgetRules)
        {
            if (rule is null)
            {
                throw new ArgumentException("BudgetRules cannot contain null entries.", nameof(options));
            }

            rule.Validate();
        }

        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.BudgetWindow, TimeSpan.Zero, nameof(options.BudgetWindow));
        ArgumentOutOfRangeException.ThrowIfNegative(options.EstimatedOutputTokensBuffer, nameof(options.EstimatedOutputTokensBuffer));
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaxWaitTimeout, TimeSpan.Zero, nameof(options.MaxWaitTimeout));
    }

    private static int GetActionPriority(LimitExceededAction action) =>
        action switch
        {
            LimitExceededAction.Throw => 3,
            LimitExceededAction.Fallback => 2,
            LimitExceededAction.Wait => 1,
            _ => 3,
        };

    private async IAsyncEnumerable<ChatResponseUpdate> StreamCoreAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ExecutionPlan plan = await PrepareExecutionAsync(messages, options, cancellationToken).ConfigureAwait(false);

        if (plan.UseFallback)
        {
            IChatClient fallbackClient = GetRequiredFallbackClient(plan);
            await foreach (ChatResponseUpdate fallbackUpdate in fallbackClient
                .GetStreamingResponseAsync(messages, options, cancellationToken)
                .WithCancellation(cancellationToken)
                .ConfigureAwait(false))
            {
                yield return fallbackUpdate;
            }

            yield break;
        }

        bool receivedAnyUpdate = false;
        bool hasUsageDetails = false;
        long? reportedInputTokens = null;
        long? reportedOutputTokens = null;
        long? reportedTotalTokens = null;
        long streamedTextCharacters = 0;
        string? detectedModelId = null;

        try
        {
            await foreach (ChatResponseUpdate update in base
                .GetStreamingResponseAsync(messages, options, cancellationToken)
                .WithCancellation(cancellationToken)
                .ConfigureAwait(false))
            {
                receivedAnyUpdate = true;

                if (detectedModelId is null && !string.IsNullOrEmpty(update.ModelId))
                {
                    detectedModelId = update.ModelId;
                }

                foreach (AIContent content in update.Contents)
                {
                    if (content is UsageContent usageContent && usageContent.Details is { } details)
                    {
                        hasUsageDetails = true;
                        if (details.InputTokenCount.HasValue)
                        {
                            reportedInputTokens = details.InputTokenCount.Value;
                        }

                        if (details.OutputTokenCount.HasValue)
                        {
                            reportedOutputTokens = details.OutputTokenCount.Value;
                        }

                        if (details.TotalTokenCount.HasValue)
                        {
                            reportedTotalTokens = details.TotalTokenCount.Value;
                        }
                    }
                    else if (content is TextContent textContent && !string.IsNullOrEmpty(textContent.Text))
                    {
                        streamedTextCharacters = SafeAddTokens(streamedTextCharacters, textContent.Text.Length);
                    }
                }

                yield return update;
            }
        }
        finally
        {
            string? resolvedModelId = !string.IsNullOrWhiteSpace(detectedModelId) ? detectedModelId : plan.ModelId;

            if (!receivedAnyUpdate && !hasUsageDetails)
            {
                // Stream threw or was canceled before producing any output: full rollback.
                await ReconcileReservationsAsync(
                    plan,
                    resolvedModelId,
                    actualInputTokens: 0,
                    actualOutputTokens: 0,
                    actualTotalTokens: 0,
                    actualCostUsd: 0m,
                    recordTelemetry: false).ConfigureAwait(false);
            }
            else
            {
                long actualInput;
                long actualOutput;
                long actualTotal;

                long estimatedStreamedOutput = streamedTextCharacters > 0
                    ? (streamedTextCharacters > long.MaxValue - 3 ? long.MaxValue / 4 : (streamedTextCharacters + 3) / 4)
                    : 0;

                if (hasUsageDetails)
                {
                    actualInput = Math.Max(0, reportedInputTokens ?? plan.EstimatedInputTokens);
                    actualOutput = Math.Max(0, reportedOutputTokens ?? estimatedStreamedOutput);
                    actualTotal = Math.Max(0, reportedTotalTokens is > 0 ? reportedTotalTokens.Value : SafeAddTokens(actualInput, actualOutput));
                }
                else
                {
                    // Provider did not emit UsageContent (or caller broke out of the stream early):
                    // estimate output tokens from the characters streamed so far (~4 chars/token).
                    actualInput = plan.EstimatedInputTokens;
                    actualOutput = estimatedStreamedOutput;
                    actualTotal = SafeAddTokens(actualInput, actualOutput);
                }

                ModelPricing? pricing = ResolvePricing(resolvedModelId) ?? plan.Pricing;
                decimal actualCostUsd = pricing?.CalculateCost(actualInput, actualOutput) ?? 0m;

                await ReconcileReservationsAsync(
                    plan,
                    resolvedModelId,
                    actualInput,
                    actualOutput,
                    actualTotal,
                    actualCostUsd,
                    recordTelemetry: true).ConfigureAwait(false);
            }
        }
    }

    private async ValueTask<ExecutionPlan> PrepareExecutionAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options,
        CancellationToken cancellationToken)
    {
        string rawScopeKey = _options.KeySelector?.Invoke(options) ?? CostGuardOptions.DefaultScopeKey;
        string scopeKey = string.IsNullOrWhiteSpace(rawScopeKey) ? CostGuardOptions.DefaultScopeKey : rawScopeKey;

        string? modelId = ResolveModelId(options);
        ModelPricing? pricing = ResolvePricing(modelId);

        long estimatedInput = Math.Max(0, _tokenEstimator.EstimateTokens(messages, options));
        long estimatedOutput = Math.Max(0, options?.MaxOutputTokens ?? _options.EstimatedOutputTokensBuffer);
        long estimatedTotal = SafeAddTokens(estimatedInput, estimatedOutput);
        decimal estimatedCostUsd = pricing?.CalculateCost(estimatedInput, estimatedOutput) ?? 0m;

        // 0. Check Per-Request Token Guardrail (MaxTokensPerRequest) — always Throw
        if (_options.MaxTokensPerRequest is { } maxPerRequest && estimatedTotal > maxPerRequest)
        {
            throw RejectRequest(
                LimitExceededReason.RequestTokenLimitExceeded,
                scopeKey,
                currentUsage: estimatedTotal,
                limit: maxPerRequest,
                retryAfter: null);
        }

        bool hasTokenReservation = false;
        long reservedTokens = 0;

        // 1. Check Token-Per-Minute (TPM) Rate Limit
        if (_options.MaxTokensPerMinute is { } maxTpm)
        {
            TokenLeaseResult lease = await AcquireTokenLeaseWithPolicyAsync(
                scopeKey,
                estimatedTotal,
                maxTpm,
                cancellationToken).ConfigureAwait(false);

            if (!lease.IsAllowed)
            {
                CostGuardLog.FallbackTriggered(_logger, LimitExceededReason.RateLimitExceeded, scopeKey, lease.CurrentTokens, maxTpm);
                CostGuardMetrics.RecordFallbackTriggered(scopeKey, LimitExceededReason.RateLimitExceeded);

                return ExecutionPlan.ForFallback(
                    scopeKey,
                    modelId,
                    LimitExceededReason.RateLimitExceeded,
                    lease.CurrentTokens,
                    maxTpm,
                    lease.RetryAfter);
            }

            hasTokenReservation = true;
            reservedTokens = estimatedTotal;
        }

        // 2. Check Monetary Budget Rules (USD)
        bool hasBudgetReservation = false;
        decimal reservedCostUsd = 0m;

        if (_budgetWindowGroups.Count > 0)
        {
            BudgetAcquisitionOutcome budgetOutcome = await AcquireBudgetRulesWithPolicyAsync(
                scopeKey,
                estimatedCostUsd,
                hasTokenReservation,
                reservedTokens,
                cancellationToken).ConfigureAwait(false);

            hasTokenReservation = budgetOutcome.HasTokenReservation;

            if (!budgetOutcome.IsAllowed)
            {
                // Roll back TPM reservation before switching to fallback!
                await RollbackTpmIfReservedAsync(scopeKey, hasTokenReservation, reservedTokens).ConfigureAwait(false);

                CostGuardLog.FallbackTriggered(
                    _logger,
                    LimitExceededReason.BudgetExceeded,
                    scopeKey,
                    budgetOutcome.ExceededUsage,
                    budgetOutcome.ExceededLimit);
                CostGuardMetrics.RecordFallbackTriggered(scopeKey, LimitExceededReason.BudgetExceeded);

                return ExecutionPlan.ForFallback(
                    scopeKey,
                    modelId,
                    LimitExceededReason.BudgetExceeded,
                    budgetOutcome.ExceededUsage,
                    budgetOutcome.ExceededLimit,
                    budgetOutcome.RetryAfter);
            }

            hasBudgetReservation = true;
            reservedCostUsd = estimatedCostUsd;

            // If TPM was released while waiting on a Budget Wait rule, re-acquire a fresh TPM lease now.
            if (!hasTokenReservation && _options.MaxTokensPerMinute is { } postWaitMaxTpm)
            {
                TokenLeaseResult lease;
                try
                {
                    lease = await AcquireTokenLeaseWithPolicyAsync(
                        scopeKey,
                        estimatedTotal,
                        postWaitMaxTpm,
                        cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    await RollbackBudgetGroupsAsync(scopeKey, reservedCostUsd, _budgetWindowGroups.Count).ConfigureAwait(false);
                    throw;
                }

                if (!lease.IsAllowed)
                {
                    await RollbackBudgetGroupsAsync(scopeKey, reservedCostUsd, _budgetWindowGroups.Count).ConfigureAwait(false);

                    CostGuardLog.FallbackTriggered(_logger, LimitExceededReason.RateLimitExceeded, scopeKey, lease.CurrentTokens, postWaitMaxTpm);
                    CostGuardMetrics.RecordFallbackTriggered(scopeKey, LimitExceededReason.RateLimitExceeded);

                    return ExecutionPlan.ForFallback(
                        scopeKey,
                        modelId,
                        LimitExceededReason.RateLimitExceeded,
                        lease.CurrentTokens,
                        postWaitMaxTpm,
                        lease.RetryAfter);
                }

                hasTokenReservation = true;
                reservedTokens = estimatedTotal;
            }
        }

        return new ExecutionPlan(
            ScopeKey: scopeKey,
            ModelId: modelId,
            Pricing: pricing,
            EstimatedInputTokens: estimatedInput,
            EstimatedOutputTokens: estimatedOutput,
            HasTokenReservation: hasTokenReservation,
            ReservedTokens: reservedTokens,
            HasBudgetReservation: hasBudgetReservation,
            ReservedCostUsd: reservedCostUsd,
            UseFallback: false,
            FallbackReason: default,
            ExceededUsage: 0m,
            ExceededLimit: 0m,
            RetryAfter: null);
    }

    private async ValueTask<TokenLeaseResult> AcquireTokenLeaseWithPolicyAsync(
        string scopeKey,
        long estimatedTotal,
        long maxTpm,
        CancellationToken cancellationToken)
    {
        TokenLeaseResult lease = await _rateLimiter
            .TryAcquireAsync(scopeKey, estimatedTotal, maxTpm, cancellationToken)
            .ConfigureAwait(false);

        if (lease.IsAllowed)
        {
            return lease;
        }

        switch (_options.OnRateLimitExceeded)
        {
            case LimitExceededAction.Throw:
                throw RejectRequest(
                    LimitExceededReason.RateLimitExceeded,
                    scopeKey,
                    lease.CurrentTokens,
                    maxTpm,
                    lease.RetryAfter);

            case LimitExceededAction.Fallback:
                EnsureFallbackClientConfigured(
                    LimitExceededReason.RateLimitExceeded,
                    scopeKey,
                    lease.CurrentTokens,
                    maxTpm,
                    lease.RetryAfter);
                return lease;

            case LimitExceededAction.Wait:
                // If RetryAfter is null, the single request exceeds the entire 1-minute window capacity.
                if (!lease.RetryAfter.HasValue)
                {
                    throw RejectRequest(
                        LimitExceededReason.RateLimitExceeded,
                        scopeKey,
                        lease.CurrentTokens,
                        maxTpm,
                        retryAfter: null,
                        message: $"Request requires {estimatedTotal} tokens, which exceeds the configured MaxTokensPerMinute ({maxTpm}) for scope '{scopeKey}'.");
                }

                DateTimeOffset deadline = SafeAddTimeout(_timeProvider.GetUtcNow(), _options.MaxWaitTimeout);

                while (!lease.IsAllowed)
                {
                    TimeSpan waitDuration = lease.RetryAfter is { } retry && retry > TimeSpan.Zero
                        ? retry
                        : _defaultWaitPollInterval;

                    DateTimeOffset now = _timeProvider.GetUtcNow();
                    if (SafeAddTimeout(now, waitDuration) > deadline)
                    {
                        throw RejectRequest(
                            LimitExceededReason.RateLimitExceeded,
                            scopeKey,
                            lease.CurrentTokens,
                            maxTpm,
                            lease.RetryAfter);
                    }

                    CostGuardLog.WaitingForCapacity(_logger, LimitExceededReason.RateLimitExceeded, scopeKey, waitDuration.TotalMilliseconds);
                    await Task.Delay(waitDuration, _timeProvider, cancellationToken).ConfigureAwait(false);

                    lease = await _rateLimiter
                        .TryAcquireAsync(scopeKey, estimatedTotal, maxTpm, cancellationToken)
                        .ConfigureAwait(false);
                }

                return lease;

            default:
                throw RejectRequest(
                    LimitExceededReason.RateLimitExceeded,
                    scopeKey,
                    lease.CurrentTokens,
                    maxTpm,
                    lease.RetryAfter);
        }
    }

    private async ValueTask<BudgetAcquisitionOutcome> AcquireBudgetRulesWithPolicyAsync(
        string scopeKey,
        decimal estimatedCostUsd,
        bool hasTokenReservation,
        long reservedTokens,
        CancellationToken cancellationToken)
    {
        DateTimeOffset? waitDeadline = null;
        SemaphoreSlim? scopeLock = _budgetWindowGroups.Count > 1
            ? _multiRuleLocks.GetOrAdd(scopeKey, static _ => new SemaphoreSlim(1, 1))
            : null;

        while (true)
        {
            int acquiredCount = 0;
            bool allSucceeded = true;

            BudgetRule? winningFailedRule = null;
            BudgetReservation winningFailedReservation = default;
            TimeSpan maxWaitRetryAfter = TimeSpan.Zero;

            bool lockHeld = false;
            try
            {
                if (scopeLock is not null)
                {
                    await scopeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
                    lockHeld = true;
                }

                for (int i = 0; i < _budgetWindowGroups.Count; i++)
                {
                    BudgetWindowGroup group = _budgetWindowGroups[i];
                    BudgetRule representativeRule = group.TightestRule;

                    BudgetReservation reservation = await _budgetStore
                        .TryReserveAsync(scopeKey, estimatedCostUsd, representativeRule, cancellationToken)
                        .ConfigureAwait(false);

                    if (reservation.IsAllowed)
                    {
                        if (allSucceeded)
                        {
                            acquiredCount = i + 1;
                        }
                        else
                        {
                            // A previous window group already failed, so immediately release this reservation;
                            // we only probed to check whether a higher-priority rule also applies.
                            await _budgetStore
                                .ReconcileAsync(scopeKey, estimatedCostUsd, actualCostUsd: 0m, representativeRule, CancellationToken.None)
                                .ConfigureAwait(false);
                        }
                    }
                    else
                    {
                        if (allSucceeded)
                        {
                            allSucceeded = false;
                            await RollbackBudgetGroupsAsync(scopeKey, estimatedCostUsd, acquiredCount).ConfigureAwait(false);
                            acquiredCount = 0;
                        }

                        if (reservation.RetryAfter is { } r && r > maxWaitRetryAfter)
                        {
                            maxWaitRetryAfter = r;
                        }

                        // Evaluate all rules sharing this window bucket against the returned CurrentSpendUsd
                        // so a looser rule with a higher-priority action (e.g. hard daily Throw over soft daily Fallback) still wins.
                        for (int j = 0; j < group.Rules.Count; j++)
                        {
                            BudgetRule candidate = group.Rules[j];
                            if (ReferenceEquals(candidate, representativeRule) ||
                                estimatedCostUsd > candidate.MaxAmountUsd - reservation.CurrentSpendUsd)
                            {
                                if (winningFailedRule is null ||
                                    GetActionPriority(candidate.OnExceeded) > GetActionPriority(winningFailedRule.OnExceeded))
                                {
                                    winningFailedRule = candidate;
                                    winningFailedReservation = reservation;
                                }
                            }
                        }
                    }
                }
            }
            catch
            {
                if (acquiredCount > 0)
                {
                    await RollbackBudgetGroupsAsync(scopeKey, estimatedCostUsd, acquiredCount).ConfigureAwait(false);
                }

                await RollbackTpmIfReservedAsync(scopeKey, hasTokenReservation, reservedTokens).ConfigureAwait(false);
                throw;
            }
            finally
            {
                if (lockHeld)
                {
                    scopeLock!.Release();
                }
            }

            if (allSucceeded)
            {
                return BudgetAcquisitionOutcome.Allowed(hasTokenReservation);
            }

            BudgetRule failedRule = winningFailedRule!;
            LimitExceededAction action = failedRule.OnExceeded;

            switch (action)
            {
                case LimitExceededAction.Throw:
                    await RollbackTpmIfReservedAsync(scopeKey, hasTokenReservation, reservedTokens).ConfigureAwait(false);
                    throw RejectRequest(
                        LimitExceededReason.BudgetExceeded,
                        scopeKey,
                        winningFailedReservation.CurrentSpendUsd,
                        failedRule.MaxAmountUsd,
                        winningFailedReservation.RetryAfter);

                case LimitExceededAction.Fallback:
                    if (_options.FallbackClient is null)
                    {
                        await RollbackTpmIfReservedAsync(scopeKey, hasTokenReservation, reservedTokens).ConfigureAwait(false);
                        EnsureFallbackClientConfigured(
                            LimitExceededReason.BudgetExceeded,
                            scopeKey,
                            winningFailedReservation.CurrentSpendUsd,
                            failedRule.MaxAmountUsd,
                            winningFailedReservation.RetryAfter);
                    }

                    return BudgetAcquisitionOutcome.Fallback(
                        hasTokenReservation,
                        winningFailedReservation.CurrentSpendUsd,
                        failedRule.MaxAmountUsd,
                        winningFailedReservation.RetryAfter);

                case LimitExceededAction.Wait:
                    if (estimatedCostUsd > failedRule.MaxAmountUsd)
                    {
                        await RollbackTpmIfReservedAsync(scopeKey, hasTokenReservation, reservedTokens).ConfigureAwait(false);
                        throw RejectRequest(
                            LimitExceededReason.BudgetExceeded,
                            scopeKey,
                            winningFailedReservation.CurrentSpendUsd,
                            failedRule.MaxAmountUsd,
                            retryAfter: null,
                            message: $"Request estimated cost (${estimatedCostUsd:F6} USD) exceeds the configured budget rule limit (${failedRule.MaxAmountUsd:F6} USD) for scope '{scopeKey}'.");
                    }

                    waitDeadline ??= SafeAddTimeout(_timeProvider.GetUtcNow(), _options.MaxWaitTimeout);
                    TimeSpan waitDuration = maxWaitRetryAfter > TimeSpan.Zero
                        ? maxWaitRetryAfter
                        : _defaultWaitPollInterval;

                    DateTimeOffset now = _timeProvider.GetUtcNow();
                    if (SafeAddTimeout(now, waitDuration) > waitDeadline.Value)
                    {
                        await RollbackTpmIfReservedAsync(scopeKey, hasTokenReservation, reservedTokens).ConfigureAwait(false);
                        throw RejectRequest(
                            LimitExceededReason.BudgetExceeded,
                            scopeKey,
                            winningFailedReservation.CurrentSpendUsd,
                            failedRule.MaxAmountUsd,
                            winningFailedReservation.RetryAfter);
                    }

                    // Release TPM reservation before sleeping so other callers are not starved and the TPM entry does not expire mid-wait.
                    if (hasTokenReservation)
                    {
                        await RollbackTpmIfReservedAsync(scopeKey, hasTokenReservation: true, reservedTokens).ConfigureAwait(false);
                        hasTokenReservation = false;
                    }

                    CostGuardLog.WaitingForCapacity(_logger, LimitExceededReason.BudgetExceeded, scopeKey, waitDuration.TotalMilliseconds);

                    await Task.Delay(waitDuration, _timeProvider, cancellationToken).ConfigureAwait(false);
                    break;

                default:
                    await RollbackTpmIfReservedAsync(scopeKey, hasTokenReservation, reservedTokens).ConfigureAwait(false);
                    throw RejectRequest(
                        LimitExceededReason.BudgetExceeded,
                        scopeKey,
                        winningFailedReservation.CurrentSpendUsd,
                        failedRule.MaxAmountUsd,
                        winningFailedReservation.RetryAfter);
            }
        }
    }

    private async ValueTask RollbackBudgetGroupsAsync(string scopeKey, decimal reservedCostUsd, int count)
    {
        for (int i = 0; i < count; i++)
        {
            await _budgetStore
                .ReconcileAsync(scopeKey, reservedCostUsd, actualCostUsd: 0m, _budgetWindowGroups[i].TightestRule, CancellationToken.None)
                .ConfigureAwait(false);
        }
    }

    private CostGuardLimitExceededException RejectRequest(
        LimitExceededReason reason,
        string scopeKey,
        decimal currentUsage,
        decimal limit,
        TimeSpan? retryAfter,
        string? message = null)
    {
        CostGuardLog.RequestRejected(_logger, reason, scopeKey, currentUsage, limit);
        CostGuardMetrics.RecordRequestRejected(scopeKey, reason);

        return new CostGuardLimitExceededException(
            reason,
            scopeKey,
            currentUsage,
            limit,
            retryAfter,
            message);
    }

    private async ValueTask RollbackTpmIfReservedAsync(string scopeKey, bool hasTokenReservation, long reservedTokens)
    {
        if (hasTokenReservation)
        {
            await _rateLimiter.ReconcileAsync(scopeKey, reservedTokens, actualTokens: 0, CancellationToken.None)
                .ConfigureAwait(false);
        }
    }

    private async ValueTask ReconcileReservationsAsync(
        ExecutionPlan plan,
        string? modelId,
        long actualInputTokens,
        long actualOutputTokens,
        long actualTotalTokens,
        decimal actualCostUsd,
        bool recordTelemetry)
    {
        List<Exception>? exceptions = null;

        // Always pass CancellationToken.None during finally reconciliation so an aborted request still frees capacity.
        if (plan.HasTokenReservation)
        {
            try
            {
                await _rateLimiter
                    .ReconcileAsync(plan.ScopeKey, plan.ReservedTokens, actualTotalTokens, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                exceptions ??= [];
                exceptions.Add(ex);
            }
        }

        if (plan.HasBudgetReservation)
        {
            for (int i = 0; i < _budgetWindowGroups.Count; i++)
            {
                try
                {
                    await _budgetStore
                        .ReconcileAsync(plan.ScopeKey, plan.ReservedCostUsd, actualCostUsd, _budgetWindowGroups[i].TightestRule, CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    exceptions ??= [];
                    exceptions.Add(ex);
                }
            }
        }

        if (recordTelemetry)
        {
            CostGuardLog.UsageReconciled(
                _logger,
                plan.ScopeKey,
                modelId,
                plan.ReservedTokens,
                actualTotalTokens,
                plan.ReservedCostUsd,
                actualCostUsd);

            CostGuardMetrics.RecordUsage(
                plan.ScopeKey,
                modelId,
                actualInputTokens,
                actualOutputTokens,
                actualCostUsd);
        }

        if (exceptions is not null)
        {
            throw exceptions.Count == 1 ? exceptions[0] : new AggregateException(exceptions);
        }
    }

    private IChatClient GetRequiredFallbackClient(ExecutionPlan plan)
    {
        EnsureFallbackClientConfigured(
            plan.FallbackReason,
            plan.ScopeKey,
            plan.ExceededUsage,
            plan.ExceededLimit,
            plan.RetryAfter);

        return _options.FallbackClient!;
    }

    private void EnsureFallbackClientConfigured(
        LimitExceededReason reason,
        string scopeKey,
        decimal currentUsage,
        decimal limit,
        TimeSpan? retryAfter)
    {
        if (_options.FallbackClient is null)
        {
            throw RejectRequest(
                reason,
                scopeKey,
                currentUsage,
                limit,
                retryAfter,
                message: $"CostGuard limit ({reason}) exceeded for scope '{scopeKey}', and LimitExceededAction.Fallback is configured without a FallbackClient.");
        }
    }

    private string? ResolveModelId(ChatOptions? options)
    {
        if (!string.IsNullOrWhiteSpace(options?.ModelId))
        {
            return options.ModelId;
        }

        ChatClientMetadata? metadata = InnerClient.GetService<ChatClientMetadata>();
        return metadata?.DefaultModelId;
    }

    private ModelPricing? ResolvePricing(string? modelId)
    {
        if (!string.IsNullOrWhiteSpace(modelId) &&
            _options.Pricing.TryGetValue(modelId, out ModelPricing? configuredPricing))
        {
            return configuredPricing;
        }

        return _options.DefaultPricing;
    }

    private static long SafeAddTokens(long a, long b) =>
        a > long.MaxValue - b ? long.MaxValue : a + b;

    private static DateTimeOffset SafeAddTimeout(DateTimeOffset now, TimeSpan timeout)
    {
        TimeSpan remaining = DateTimeOffset.MaxValue - now;
        return timeout >= remaining ? DateTimeOffset.MaxValue : now + timeout;
    }

    private static List<BudgetWindowGroup> GroupBudgetRulesByWindow(IReadOnlyList<BudgetRule> rules)
    {
        if (rules.Count == 0)
        {
            return [];
        }

        Dictionary<(BudgetWindowType WindowType, long SlidingTicks), List<BudgetRule>> byWindow = [];
        List<(BudgetWindowType WindowType, long SlidingTicks)> order = [];

        foreach (BudgetRule rule in rules)
        {
            long slidingTicks = rule.WindowType == BudgetWindowType.Sliding && rule.SlidingWindow.HasValue
                ? rule.SlidingWindow.Value.Ticks
                : 0L;
            (BudgetWindowType WindowType, long SlidingTicks) key = (rule.WindowType, slidingTicks);

            if (!byWindow.TryGetValue(key, out List<BudgetRule>? list))
            {
                list = [];
                byWindow[key] = list;
                order.Add(key);
            }

            list.Add(rule);
        }

        List<BudgetWindowGroup> groups = [];
        foreach ((BudgetWindowType WindowType, long SlidingTicks) key in order)
        {
            List<BudgetRule> list = byWindow[key];
            BudgetRule tightest = list[0];
            for (int i = 1; i < list.Count; i++)
            {
                if (list[i].MaxAmountUsd < tightest.MaxAmountUsd)
                {
                    tightest = list[i];
                }
            }

            groups.Add(new BudgetWindowGroup(tightest, list));
        }

        return groups;
    }

    private sealed record BudgetWindowGroup(BudgetRule TightestRule, IReadOnlyList<BudgetRule> Rules);

    private readonly record struct BudgetAcquisitionOutcome(
        bool IsAllowed,
        bool HasTokenReservation,
        decimal ExceededUsage,
        decimal ExceededLimit,
        TimeSpan? RetryAfter)
    {
        public static BudgetAcquisitionOutcome Allowed(bool hasTokenReservation) =>
            new(true, hasTokenReservation, 0m, 0m, null);

        public static BudgetAcquisitionOutcome Fallback(
            bool hasTokenReservation,
            decimal exceededUsage,
            decimal exceededLimit,
            TimeSpan? retryAfter) =>
            new(false, hasTokenReservation, exceededUsage, exceededLimit, retryAfter);
    }

    private readonly record struct ExecutionPlan(
        string ScopeKey,
        string? ModelId,
        ModelPricing? Pricing,
        long EstimatedInputTokens,
        long EstimatedOutputTokens,
        bool HasTokenReservation,
        long ReservedTokens,
        bool HasBudgetReservation,
        decimal ReservedCostUsd,
        bool UseFallback,
        LimitExceededReason FallbackReason,
        decimal ExceededUsage,
        decimal ExceededLimit,
        TimeSpan? RetryAfter)
    {
        public static ExecutionPlan ForFallback(
            string scopeKey,
            string? modelId,
            LimitExceededReason reason,
            decimal currentUsage,
            decimal limit,
            TimeSpan? retryAfter) =>
            new(
                ScopeKey: scopeKey,
                ModelId: modelId,
                Pricing: null,
                EstimatedInputTokens: 0,
                EstimatedOutputTokens: 0,
                HasTokenReservation: false,
                ReservedTokens: 0,
                HasBudgetReservation: false,
                ReservedCostUsd: 0m,
                UseFallback: true,
                FallbackReason: reason,
                ExceededUsage: currentUsage,
                ExceededLimit: limit,
                RetryAfter: retryAfter);
    }
}
