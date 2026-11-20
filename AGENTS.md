# Repository Guidelines (`Meai.CostGuard` / `AiCostGuard`)

When working with any files in this repository (`.cs`, `.csproj`, `.slnx`, `.editorconfig`, `Directory.Build.props`, tests, or samples):

1. **Activate the project skill** `.agents/skills/dotnet-csharp-pro/SKILL.md` and strictly follow its MCP-first workflow (`microsoft-learn`, `nuget`, `csharp-dev-tools`), engineering principles, and .NET 10+ / Native AOT standards.
2. **Follow `kb-autonomy`** when `.knowledge/` is present (`start-session` before work, `end-session` after work).
3. **Keep public repository artifacts in English** (source code, XML docs, `README.md`, `CONTRIBUTING.md`, commit messages).
4. **Verify before claiming completion**: `dotnet build` and `dotnet test` must pass with `0 Warning(s), 0 Error(s)`.
