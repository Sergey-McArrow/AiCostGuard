# Meai.CostGuard (`AiCostGuard`)

[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](./LICENSE)
[![.NET](https://img.shields.io/badge/.NET-10.0%2B-512BD4.svg)](https://dotnet.microsoft.com/)
[![Native AOT](https://img.shields.io/badge/Native%20AOT-Compatible-brightgreen.svg)](https://learn.microsoft.com/dotnet/core/deploying/native-aot/)

A lightweight, Native AOT-compatible **Token-Aware Rate Limiter, Cost Guard, and Automatic Fallback middleware** built on top of [`Microsoft.Extensions.AI`](https://learn.microsoft.com/dotnet/ai/microsoft-extensions-ai) (`IChatClient` / `DelegatingChatClient`).

---

## Why Meai.CostGuard?

Standard HTTP rate limiters count *requests per minute*, which fails for LLM workloads where a single prompt can consume 50 tokens or 100,000 tokens. **Meai.CostGuard** wraps any `IChatClient` to enforce **tokens-per-minute (TPM)** quotas and **USD spend budgets** per tenant, with automatic reconciliation against real provider `UsageDetails` (including streaming responses) and seamless fallback to a cheaper or local model (e.g., Ollama via `OllamaSharp`).

## Features

- **Per-Request Token Guardrail (`MaxTokensPerRequest`)** — rejects oversized prompts upfront before consuming rate-limit or budget capacity.
- **Token-Aware Rate Limiting (TPM)** — sliding-window token budgets using `TimeProvider` to prevent boundary bursts.
- **Multi-Rule USD Cost Guard (`BudgetRules`)** — combine multiple simultaneous rules (e.g., soft daily `Fallback` + hard monthly `Throw`) using `Sliding`, `FixedUtcDay`, or `FixedUtcMonth` windows with exact `decimal` precision.
- **Flexible Limit Actions** — `Throw` (`CostGuardLimitExceededException`), `Wait` (async backoff with `CancellationToken` & timeout), or `Fallback` (transparent routing to a fallback `IChatClient`).
- **Streaming Reconciliation** — pre-flight reservation + post-flight reconciliation from `UsageDetails` / `UsageContent` in both `GetResponseAsync` and `GetStreamingResponseAsync`.
- **Multi-Tenant Isolation** — pluggable `KeySelector` per user/tenant via `ChatOptions.AdditionalProperties`.
- **Minimal Dependencies & Native AOT** — lightweight dependencies and full `<IsAotCompatible>true</IsAotCompatible>` support.
- **Built-in Observability** — `System.Diagnostics.Metrics` (`Meter("AiCostGuard")`) + `[LoggerMessage]` source-generated logging.

## Quick Start

> **Note:** Package is currently in active development (`0.1.0-preview.1`).

```bash
dotnet add package Meai.CostGuard
```

```csharp
using AiCostGuard;
using AiCostGuard.Options;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

services.AddChatClient(primaryClient)
    .UseCostGuard(options =>
    {
        options.MaxTokensPerRequest = 16_000;
        options.MaxTokensPerMinute = 50_000;
        options.OnRateLimitExceeded = LimitExceededAction.Wait;

        options.AddBudgetRule(maxUsd: 5.00m, BudgetWindowType.FixedUtcDay, LimitExceededAction.Fallback);
        options.AddBudgetRule(maxUsd: 100.00m, BudgetWindowType.FixedUtcMonth, LimitExceededAction.Throw);
    }, fallbackClient: ollamaClient);
```

## Multi-Tenant Example

Isolate TPM and USD budget rules per tenant using `KeySelector`:

```csharp
services.AddChatClient(primaryClient)
    .UseCostGuard(options =>
    {
        options.MaxTokensPerRequest = 16_000;
        options.MaxTokensPerMinute = 50_000;
        options.AddBudgetRule(maxUsd: 2.00m, BudgetWindowType.FixedUtcDay, LimitExceededAction.Fallback);
        options.AddBudgetRule(maxUsd: 30.00m, BudgetWindowType.FixedUtcMonth, LimitExceededAction.Throw);
        options.KeySelector = chatOptions =>
            chatOptions?.AdditionalProperties?.TryGetValue("TenantId", out var tenant) == true
                ? tenant?.ToString() ?? "default"
                : "default";
    }, fallbackClient: ollamaClient);
```

## Extensibility

- **Custom Tokenizer (`ITokenEstimator`)**: Defaults to a zero-allocation `HeuristicTokenEstimator`. Plug in `Microsoft.ML.Tokenizers` for exact BPE token counting.
- **Distributed Storage (`IBudgetStore`)**: Defaults to a thread-safe `InMemoryBudgetStore`. Implement `IBudgetStore` over Redis or PostgreSQL for multi-instance deployments.
- **Custom Model Pricing (`ModelPricing`)**: Ships with common OpenAI, Anthropic, and Gemini pricing and supports per-model overrides via `CostGuardOptions.Pricing`.

## Observability

Subscribe to the `"AiCostGuard"` meter in OpenTelemetry / `dotnet-counters`:

| Metric | Type | Description |
| :--- | :--- | :--- |
| `costguard.tokens.consumed` | `Counter<long>` | Total input/output tokens reconciled |
| `costguard.cost.usd` | `Counter<double>` | Total reconciled cost in USD |
| `costguard.fallbacks.triggered` | `Counter<long>` | Number of requests routed to the fallback client |
| `costguard.requests.rejected` | `Counter<long>` | Number of requests rejected with `CostGuardLimitExceededException` |

## Requirements

- **.NET 10.0+** (forward-compatible with .NET 11+)

## Contributing

Contributions are welcome! Please read [CONTRIBUTING.md](./CONTRIBUTING.md) and our [Code of Conduct](./CODE_OF_CONDUCT.md) before opening a pull request.

## License

Licensed under the [MIT License](./LICENSE).
