# Contributing to MigrationSentinel

Thank you for your interest in contributing to **MigrationSentinel**!

## Development Setup

1. Install the [.NET 8.0 SDK](https://dotnet.microsoft.com/download) (or newer).
2. Clone the repository and restore dependencies:
   ```bash
   git clone https://github.com/soghrazakeran4-bot/MigrationSentinel.git
   cd MigrationSentinel
   dotnet restore MigrationSentinel.sln
   ```
3. Build and run the full test suite:
   ```bash
   dotnet test MigrationSentinel.sln -c Release
   ```

## Adding a New Risk Rule

1. Implement `IRiskRule` in `src/MigrationSentinel.Core/Rules/BuiltIn/`.
2. Register the rule in `RuleCatalog.CreateDefaultRules()`.
3. Provide dialect-aware mitigation SQL (`DatabaseDialect.SqlServer` and `DatabaseDialect.PostgreSql`) whenever applicable.
4. Add both positive and negative (no-false-positive) xUnit tests in `tests/MigrationSentinel.Tests/`.
5. Document the new rule ID in `README.md`.

## Commit Convention

We follow [Conventional Commits](https://www.conventionalcommits.org/):
- `feat:` New rules, dialect support, CLI commands, or AI providers
- `fix:` Parser or rule accuracy bug fixes
- `docs:` Documentation and README improvements
- `test:` Additional unit or integration test coverage
- `ci:` GitHub Actions workflow and packaging updates
