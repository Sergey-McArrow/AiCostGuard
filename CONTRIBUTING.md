# Contributing to Meai.CostGuard

Thank you for considering contributing to **Meai.CostGuard** (`AiCostGuard`)!

## Prerequisites

- [.NET 10.0 SDK](https://dotnet.microsoft.com/download) or newer (`.NET 11.0 SDK` supported)
- Optional: [Ollama](https://ollama.com/) running locally (`http://localhost:11434`) to run the live fallback sample with `OllamaSharp`

## Building & Testing Locally

```bash
# Restore and build all projects
dotnet build

# Run all unit, time-travel, and concurrency tests
dotnet test

# Pack the NuGet package locally
dotnet pack src/AiCostGuard/AiCostGuard.csproj -c Release -o ./artifacts
```

## Engineering Standards

This repository enforces strict compilation and code quality rules via `Directory.Build.props` and `.editorconfig`:

1. **Zero Warnings Policy**: `<TreatWarningsAsErrors>true</TreatWarningsAsErrors>` is enabled across the solution.
2. **XML Documentation**: Every `public` type, method, property, and enum value in `src/AiCostGuard` must have clear English XML documentation (`/// <summary>...</summary>`).
3. **Native AOT Compatibility**: `<IsAotCompatible>true</IsAotCompatible>` is enabled. Do not introduce runtime reflection, dynamic proxies, or reflection-based serialization.
4. **Deterministic Time**: Always inject and use `TimeProvider` — never call `DateTime.UtcNow`, `DateTime.Now`, or `DateTimeOffset.UtcNow` directly.
5. **Financial Precision**: Always use `decimal` for currency/pricing calculations (never `double` or `float`).
6. **Minimal Core Dependencies**: Do not add heavy third-party dependencies to `src/AiCostGuard`. Keep it limited to `Microsoft.Extensions.AI.Abstractions`, `Microsoft.Extensions.Options`, `Microsoft.Extensions.Logging.Abstractions`, and BCL.
7. **High-Performance Logging**: Use `[LoggerMessage]` source-generated logging methods.

## Pull Request Checklist

- [ ] Code follows `.editorconfig` and C# naming conventions.
- [ ] All new public APIs include XML documentation.
- [ ] Unit tests are added/updated (using `FakeTimeProvider` where time windows are involved).
- [ ] `dotnet build` and `dotnet test` pass with **0 warnings and 0 errors**.
- [ ] Commit messages are concise, clear, and written in English (e.g., Conventional Commits: `feat:`, `fix:`, `docs:`, `test:`, `chore:`).
