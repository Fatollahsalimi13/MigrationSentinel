# Security Policy

## Supported Versions

| Version | Supported |
| :--- | :---: |
| `1.0.x` | ✅ |

## Reporting a Vulnerability

If you discover a security vulnerability in **MigrationSentinel** (for example, command injection in Git diff integration, secret leakage in AI review telemetry, or parser denial-of-service on crafted SQL/C# inputs):

1. **Do not** open a public issue with exploit details.
2. Report it privately via GitHub Security Advisories (**Security → Advisories → Report a vulnerability**).
3. Include reproduction steps, affected version, and suggested remediation if available.

We aim to acknowledge reports within 48 hours and release a patched version promptly.

## Security Design Principles

- **Zero shell interpolation:** Git commands are invoked via `ProcessStartInfo.ArgumentList` to prevent shell injection.
- **Opt-in AI telemetry:** Migration source code is never sent to external APIs unless `--ai openai` is explicitly enabled; local `--ai ollama` keeps all schema data completely offline.
- **No credential persistence:** API keys are read strictly from runtime flags or environment variables and are never written to reports or disk.
