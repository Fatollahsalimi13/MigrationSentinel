# Changelog

All notable changes to **MigrationSentinel** will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [1.0.0] - 2026-11-18

### Added
- **Core Parsing Engine (`MigrationSentinel.Core`)**:
  - Roslyn C# AST parser (`EfCoreMigrationParser`) for Entity Framework Core `Migration` classes with recursive analysis of raw SQL inside `migrationBuilder.Sql(...)`.
  - Dialect-aware SQL Lexer & AST Parser (`SqlLexer`, `SqlMigrationParser`) for SQL Server (T-SQL) and PostgreSQL DDL/DML, supporting multi-clause `ALTER TABLE`, dollar-quoted strings, nested block comments, and `sp_rename`.
  - Automatic SQL dialect detection (`DialectDetector`).
  - 14 deterministic DBA risk rules (`MS001`–`MS014`) covering data loss, exclusive table locks, breaking zero-downtime contracts, cascading deletes, raw SQL auditing, and missing `Down()` rollbacks.
  - Composite 0–100 risk scoring and automatic dialect-specific Expand-and-Contract mitigation SQL generation.
- **Infrastructure (`MigrationSentinel.Infrastructure`)**:
  - Recursive file scanner ignoring build artifacts, `.Designer.cs`, and `ModelSnapshot.cs`.
  - Git diff analyzer (`diff --base`, `diff --staged`) for Pull Request & pre-commit gating.
  - Optional AI migration review layer supporting OpenAI / Codex-compatible APIs and local Ollama models.
  - Standards-compliant **SARIF v2.1.0**, Markdown PR report, and JSON report formatters.
  - Workspace `.migrationsentinel.json` configuration support.
- **CLI Global Tool (`MigrationSentinel`)**:
  - Packable `.NET` global tool (`migration-sentinel`) built on `Spectre.Console` with `scan`, `diff`, `rules`, and `init` commands.
- **Quality & CI/CD**:
  - 22 xUnit + FluentAssertions unit and integration tests.
  - Cross-platform GitHub Actions CI/CD workflow (`ubuntu-latest`, `windows-latest`) with automated NuGet tool packaging, smoke testing, SARIF upload, and GitHub Release publishing.
