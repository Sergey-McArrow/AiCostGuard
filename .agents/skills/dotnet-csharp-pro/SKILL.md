---
name: dotnet-csharp-pro
description: >-
  Strict C# / .NET 10+ engineering skill for Meai.CostGuard (AiCostGuard).
  AUTO-INVOKE whenever reading, creating, editing, testing, or reviewing any
  .cs, .csproj, .slnx, .editorconfig, or Directory.Build.props file in this
  repository. Enforces MCP-first verification (microsoft-learn, nuget,
  csharp-dev-tools), clean software engineering principles, and production-grade
  .NET 10+ / Native AOT standards.
---

# .NET & C# Pro Engineering Skill (`Meai.CostGuard`)

> [!IMPORTANT]
> This skill is **mandatory** for all code, test, build configuration, and dependency changes in `AiCostGuard`.

---

## 1. Mandatory MCP-First Workflow

**Never guess APIs, method signatures, package versions, or compiler diagnostics.** Always verify using the configured MCP servers before writing or modifying code:

1. **`microsoft-learn` MCP** — call **before** using non-trivial .NET / MEAI APIs:
   - `Microsoft.Extensions.AI` (`IChatClient`, `DelegatingChatClient`, `ChatResponseUpdate`, `UsageContent`, `ChatClientBuilder`)
   - `TimeProvider` and `FakeTimeProvider` (`Microsoft.Extensions.TimeProvider.Testing`)
   - `System.Diagnostics.Metrics` (`Meter`, `Counter<T>`, `TagList`)
   - `[LoggerMessage]` source generator and Native AOT analyzers (`IsAotCompatible`)
   - Modern .NET 10 / C# 14 features (`System.Threading.Lock`, `field` keyword, `ReadOnlySpan<T>`).
2. **`nuget` MCP (`NuGet.Mcp.Server`)** — call **before** adding or upgrading any NuGet package:
   - Verify the latest stable version on `nuget.org`.
   - Confirm `net10.0` compatibility and minimal transitive dependencies.
   - Check deprecation status (**never** use the deprecated `Microsoft.Extensions.AI.Ollama`; use **`OllamaSharp`** instead).
3. **`csharp-dev-tools` MCP (`csdt-mcp`)** — use during implementation and refactoring:
   - Inspect symbols, navigate `AiCostGuard.slnx`, and check Roslyn diagnostics and warnings early.

> If an MCP server is temporarily unavailable in the current session, state it explicitly and verify via official docs or CLI (`dotnet build`, `dotnet package search`).

---

## 2. General Software Engineering Principles

1. **SOLID & Clean Architecture**:
   - **Single Responsibility**: one cohesive responsibility per class; one public type per file.
   - **Dependency Inversion**: core logic depends on abstractions (`ITokenEstimator`, `IBudgetStore`, `ITokenRateLimiter`, `TimeProvider`), not concrete implementations.
   - **Open/Closed**: extensible for custom stores (e.g., Redis `IBudgetStore`) and tokenizers (e.g., `Microsoft.ML.Tokenizers`) without modifying `CostGuardChatClient`.
2. **KISS & YAGNI**:
   - Keep `src/AiCostGuard` dependencies minimal (`Microsoft.Extensions.AI.Abstractions`, `Microsoft.Extensions.Options`, `Microsoft.Extensions.Logging.Abstractions`, and BCL only).
   - Avoid speculative abstractions or premature complexity outside the agreed architecture.
3. **Fail-Fast Argument Validation**:
   - Validate all public method arguments upfront using `ArgumentNullException.ThrowIfNull`, `ArgumentOutOfRangeException.ThrowIfNegative` / `ThrowIfLessThanOrEqual`, and `ArgumentException.ThrowIfNullOrWhiteSpace`.
4. **Atomicity & State Invariants**:
   - Under high concurrency, budget and TPM counters must never become negative or exceed configured limits due to race conditions.
   - Always roll back reservations on budget rejection (after TPM reservation), downstream exceptions, `CancellationToken` cancellation, or early stream termination (`break`).
5. **TDD & Verification (Evidence Before Assertions)**:
   - Every behavior and edge case must be covered by fast, deterministic `xUnit` tests using `FakeTimeProvider`.
   - Never claim a task is complete until `dotnet build` and `dotnet test` pass with **`0 Warning(s), 0 Error(s)`**.

---

## 3. Strict C# / .NET 10+ Standards

### Target Frameworks, Compiler & Native AOT
- **Library Target**: `src/AiCostGuard` targets **`net10.0`** (forward-compatible with `.NET 10 LTS` and `.NET 11+`).
- **Tests & Samples**: target `net11.0` (or `net10.0` in CI when .NET 10 runtime is installed).
- **Strict Compiler Settings** (`Directory.Build.props` + `.editorconfig`):
  - `LangVersion=latest`, `Nullable=enable`, `ImplicitUsings=enable`, `TreatWarningsAsErrors=true`, `EnforceCodeStyleInBuild=true`.
- **Public API Documentation**: `GenerateDocumentationFile=true` — **every** `public` type, method, property, and enum member must have clear English XML documentation (`/// <summary>...</summary>`, `<param>`, `<returns>`, `<exception>`).
- **Native AOT (`IsAotCompatible=true`)**:
  - **No runtime reflection** (`Assembly.GetTypes`, dynamic proxies, reflection-based serialization).
  - Logging **must** use source-generated `[LoggerMessage]` in `internal static partial class` (no string-interpolated `logger.LogWarning($"...")`).
  - Tests must use a lightweight `FakeChatClient : IChatClient` instead of reflection-based mocking libraries.

### Types, Money, Time & Performance
- **Currency**: **always `decimal`** (suffix `m`, e.g., `5.00m`). Never use `double` or `float` for financial calculations (cast to `double` only at the `Meter.CreateCounter<double>` boundary).
- **Time**: **always `TimeProvider`** (`_timeProvider.GetUtcNow()`, `Task.Delay(delay, _timeProvider, ct)`). **Strictly forbidden** to call `DateTime.UtcNow`, `DateTime.Now`, or `DateTimeOffset.UtcNow` directly.
- **Type Modifiers & DTOs**:
  - Mark classes `sealed` by default unless designed for inheritance.
  - Use `public sealed record` for immutable reference models (`ModelPricing`) and `public readonly record struct` for small stack-allocated return values (`BudgetReservation`, `TokenLeaseResult`).
- **Low-Allocation Hot Paths**:
  - In `HeuristicTokenEstimator`, iterate over `string` / `ReadOnlySpan<char>` directly — never call `string.Split(...)`.
  - In `CostGuardMetrics`, pass metric dimensions using `TagList`.
  - Use `ValueTask` / `ValueTask<T>` for frequently synchronous contract methods (`IBudgetStore`, `ITokenRateLimiter`).

### Concurrency, Async & Streaming
- **Synchronization**:
  - Use .NET 9+/10+ **`System.Threading.Lock`** (`private readonly Lock _sync = new();` with `lock (_sync)`) for synchronous per-tenant critical sections.
  - Use `SemaphoreSlim(1, 1)` with `try / finally { Release(); }` only when `await` is required inside a critical section (never `lock` across `await`).
  - Isolate per-tenant state using `ConcurrentDictionary<string, ...>` so tenants never block each other.
- **Cancellation**: propagate `CancellationToken` to all async calls; use `.ConfigureAwait(false)` in library code.
- **Streaming (`GetStreamingResponseAsync`)**:
  - Always decorate `CancellationToken` with `[EnumeratorCancellation]`.
  - Perform token/cost reconciliation and metric recording inside a **`finally`** block so early `break`, cancellation, and exceptions never leak reservations.

### Naming & Sample Standards
- `PascalCase` for namespaces, types, methods, properties, constants.
- `I`-prefix for interfaces (`IBudgetStore`, `ITokenEstimator`).
- `_camelCase` for private fields (`_options`, `_timeProvider`).
- `Async` suffix for all asynchronous methods.
- File-scoped namespaces (`namespace AiCostGuard.Storage;`).
- All source code, comments, XML docs, commit messages, and public docs (`README.md`, `CONTRIBUTING.md`) must be in **English**.
- In `samples/AiCostGuard.Sample`, use **`OllamaSharp`** (`OllamaApiClient : IChatClient`) for local Ollama fallback — **never** the deprecated `Microsoft.Extensions.AI.Ollama`.

---

## 4. Pre-Completion Verification Checklist

1. [ ] Verified APIs and package versions via `microsoft-learn` / `nuget` MCP.
2. [ ] Checked Roslyn diagnostics via `csharp-dev-tools` MCP (when available).
3. [ ] All public members have English XML docs (`/// <summary>`).
4. [ ] Zero direct `DateTime.UtcNow` calls, zero `double` for money, zero runtime reflection.
5. [ ] `dotnet build` succeeds with **`0 Warning(s), 0 Error(s)`**.
6. [ ] `dotnet test` succeeds with **`Failed: 0`**.
