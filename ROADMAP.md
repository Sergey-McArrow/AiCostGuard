# Meai.CostGuard (`AiCostGuard`) Roadmap

This document outlines the current capabilities of `Meai.CostGuard` and planned enhancements for upcoming releases. Community contributions and feedback are very welcome — see [CONTRIBUTING.md](./CONTRIBUTING.md) for guidelines on opening an issue or pull request.

---

## `v0.1.0` (Current Release)

- [x] `CostGuardChatClient` (`DelegatingChatClient`) supporting both `GetResponseAsync` and `GetStreamingResponseAsync`.
- [x] Per-request token guardrail (`MaxTokensPerRequest`).
- [x] 1-minute sliding-window TPM rate limiter (`SlidingWindowTokenRateLimiter`) with `Throw`, `Wait`, and `Fallback`.
- [x] Multi-rule USD monetary budgets (`BudgetRules`) supporting `Sliding`, `FixedUtcDay`, and `FixedUtcMonth` windows with `decimal` precision.
- [x] Pre-flight reservation and post-flight reconciliation (`UsageDetails` / `UsageContent`, including early stream termination in `finally`).
- [x] Multi-tenant isolation via `KeySelector`.
- [x] OpenTelemetry-compatible metrics (`Meter("AiCostGuard")`) and zero-allocation `[LoggerMessage]` logging.
- [x] Native AOT compatibility (`IsAotCompatible=true`) and embedded Source Link symbols.

---

## `v0.2.0` — Scalability & Memory Hygiene (Planned)

- [ ] **Inactive Tenant Eviction (TTL)**: Add configurable sliding expiration / idle cleanup for tenant buckets in `InMemoryBudgetStore`, `SlidingWindowTokenRateLimiter`, and internal per-tenant locks to safely support high-cardinality ephemeral keys.
- [ ] **Configuration Binding & Startup Validation**: Support binding `CostGuardOptions.Pricing` from `IConfiguration` (`appsettings.json`) and add `IValidateOptions<CostGuardOptions>` for fail-fast validation during host startup.
- [ ] **Tool & Multimodal Estimation**: Account for serialized `ChatOptions.Tools` definitions and non-text `AIContent` in `HeuristicTokenEstimator`.

---

## Future / Community Wanted (`v0.3.0+`)

Interested in contributing? These extension packages and features are ideal for community PRs:

- [ ] **Distributed Stores (`Meai.CostGuard.Redis` / `Meai.CostGuard.PostgreSQL`)**: Production-ready implementations of `IBudgetStore` and `ITokenRateLimiter` using atomic Lua scripts / transactions for multi-instance (Kubernetes) deployments.
- [ ] **Exact BPE Tokenizer (`Meai.CostGuard.Tiktoken`)**: Optional `ITokenEstimator` package backed by `Microsoft.ML.Tokenizers` for exact pre-flight token counting.
- [ ] **Per-Tenant Policy Overrides**: Dynamic per-tenant limits and custom pricing resolved asynchronously from a database or feature-flag provider.
- [ ] **Prompt-Caching & Batch Pricing Modifiers**: Optional support for discounted cached input tokens (`CachedInputTokenCount`) and batch API pricing tiers.
