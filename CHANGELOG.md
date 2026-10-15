# Changelog

All notable changes to this project are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project adheres to
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added

- Design: [federated MCP connector blueprint](docs/design/blueprint.md).
- `FederatedConnector.Abstractions`: the provider contract (`IKnowledgeProvider`, `ProviderDescriptor`,
  `AuthRequirement`, `ProviderContext`, models, `ProviderException`, `ProviderRegistration`).
- `FederatedConnector.Host`: MCP Streamable HTTP server (stateless, `/mcp`) with the fixed `search`, `fetch` and
  `list_collections` tools and read-only annotations; Entra SSO + on-behalf-of, OAuth pass-through and None auth modes;
  signed, user-bound result handles with key rotation; timeouts, truncation, text normalisation, sanitised errors,
  per-user rate limiting and OpenTelemetry / Application Insights telemetry.
- `FederatedConnector.Providers.FoundryIq`: Foundry IQ knowledge base provider (retrieve-based search with fan-out,
  GA and preview fetch paths, allowlisted collections, Work IQ source exclusion).
- `FederatedConnector.ContentExtraction`: text, HTML and DOCX extraction.
- `FederatedConnector.Conformance`: provider conformance kit with an in-memory reference provider.
- `FederatedConnector.SmokeTest`: MCP smoke test console tool.
- Deployment: `register-connector.sh` / `.ps1`, `core.bicep`, Foundry IQ provider metadata and hooks, ATK templates,
  `agentConnectors` manifest template, Dockerfile.
- Repository documents, issue and pull request templates, and CI.
