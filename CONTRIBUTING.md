# Contributing

Thanks for your interest in improving the federated MCP connector blueprint. Bug reports, design feedback, new
providers and documentation fixes are all welcome.

By participating you agree to follow the [Code of Conduct](CODE_OF_CONDUCT.md). Please report security issues
privately as described in [SECURITY.md](SECURITY.md), not in public issues.

## Before you start

- For anything beyond a small fix, open an issue first so we can agree on the approach.
- Read the [design](docs/design/blueprint.md). Changes to the provider contract (`FederatedConnector.Abstractions`) or
  to the Copilot-facing tool surface need a design discussion, because every provider depends on them.
- Keep the split of responsibilities: the host owns everything Copilot sees and everything security-critical; a
  provider owns only how to search, read and list one kind of source.

## Development setup

1. Install the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).
2. Build and test:

   ```bash
   dotnet build
   dotnet test
   ```

   The tests use simulated services and need no cloud access or credentials.
3. Deployment scripts: check with `bash -n deploy/register-connector.sh` and, if you have PowerShell,
   `pwsh -c "[System.Management.Automation.Language.Parser]::ParseFile('deploy/register-connector.ps1',[ref]$null,[ref]$null)"`.
   Bicep: `az bicep build --file deploy/infra/core.bicep --stdout > /dev/null`.

## Pull requests

- Keep each pull request focused on one change, with tests.
- The build treats warnings as errors; keep it clean.
- New or changed providers must pass the conformance kit (`ProviderConformanceTests<T>`), including the two-user
  permission test.
- Never add tests that call live services by default. Opt-in live tests must read their configuration from the
  environment and be skipped when it is absent.
- Don't commit secrets, tenant or subscription ids, internal hostnames or personal paths. Use `contoso`/`example`
  placeholders.
- Refer to external systems generically (for example "cloud file store", "document management system") rather than by
  third-party product names.
- Update [README.md](README.md), the design doc and [CHANGELOG.md](CHANGELOG.md) (under *Unreleased*) when behaviour
  changes.
- Write commit messages in the imperative mood ("Add …", "Fix …") with a short summary line.

## Adding a provider

See [Writing a provider](README.md#writing-a-provider). A provider contribution should include the provider project,
its tests (including a conformance fixture over recorded or simulated traffic), and `deploy/providers/<id>/` metadata.

## License

By contributing, you agree that your contributions are licensed under the [MIT License](LICENSE).
