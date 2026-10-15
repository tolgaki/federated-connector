# Federated MCP connector blueprint: design

**Status:** Draft design, 15 Oct 2026. Nothing has been built or deployed. Standalone project; the repository is still to be named.
**Goal:** A reusable blueprint for bringing an external data source into Microsoft 365 Copilot and Work IQ as a **federated MCP connector**. A pluggable **provider** supplies the data-source side.
**First provider (the test case):** Foundry IQ knowledge bases. Later providers might be document management systems, cloud file stores, line-of-business systems, and others.
**Stack:** C# on .NET 10.

---

## 1. Background

### 1.1 Copilot federated connectors

A federated connector is a **remote MCP server with read-only tools** (`search`, `fetch`, `query`). Copilot calls it live at query time, and nothing is copied into Microsoft 365. Approved connectors answer in Copilot Chat, Excel, Cowork and Researcher.

**Requirements**
- A public HTTPS endpoint, with TLS 1.2 or later.
- Every tool needs a human-readable `title` and the annotation `readOnlyHint: true`.
- Access is scoped to the user. **Each connector has exactly one authentication method:**
  - **Microsoft Entra SSO:** Copilot sends a token for our Entra app.
  - **OAuth 2.0:** Copilot runs the sign-in with a third-party identity provider and sends that provider's token.
  - **None:** for public data.

**Registration routes**

| Route | How | Scriptable? |
|---|---|---|
| **A. Admin center** | Microsoft 365 admin center → Copilot → Connectors → Gallery → *Connect to MCP server*. You enter a display name, a base URL, and the SSO or OAuth registration ID from Teams Developer Portal. Then staged rollout. | Everything *except* the final admin-center step. I found no public API for that step. |
| **B. App package** | Manifest v1.27+ with `agentConnectors[].toolSource.remoteMcpServer { mcpServerUrl, authorization { type: OAuthPluginVault, referenceId } }`. Sideload it, or publish to the org catalogue. This is the same artefact ISVs submit through Partner Center. | End to end, using Microsoft 365 Agents Toolkit (ATK) actions `aadApp/create`, `oauth/register`, `teamsApp/zipAppPackage`, `teamsApp/extendToM365` and `teamsApp/publishAppPackage`. |

ATK's `oauth/register` action (schema v1.10) supports `identityProvider: MicrosoftEntra | Custom`. That covers both the Entra SSO and the third-party OAuth case, and it writes the `configurationId` that the manifest uses as its `referenceId`.

Sources:
- [Set up custom federated connectors](https://learn.microsoft.com/microsoft-365/copilot/connectors/set-up-custom-federated-connectors)
- [Submit a federated connector](https://github.com/MicrosoftDocs/copilot-connectors/blob/public/copilot-connectors/submit-federated-connector.md)
- [Agent connectors](https://learn.microsoft.com/microsoftteams/platform/m365-apps/agent-connectors)
- [`root.agentConnectors`](https://learn.microsoft.com/microsoft-365/extensibility/schema/root-agent-connectors?view=m365-app-1.30)
- ATK `yaml.schema.json` v1.10

### 1.2 Why every data source needs a provider-shaped server rather than a pass-through

A connector's tool surface has to fit Copilot: `search` returns citable hits, `fetch` reads a document, and every tool is annotated. A data source's native API, or even its own MCP endpoint, rarely fits that shape.

Foundry IQ is an example. Its built-in MCP endpoint has one tool and no fetch, and it expects a `search.azure.com` token. The blueprint therefore owns the Copilot-facing contract once, and each provider maps a source onto it.

---

## 2. Architecture

```
                      ┌──────────────────────── Connector host (shared, provider-agnostic) ───────────────────────┐
Copilot / Work IQ ──▶ │ MCP Streamable HTTP · tool schema & annotations · auth (validate / OBO / pass-through)    │
 (one auth method     │ signed result handles · limits & timeouts · error sanitising · telemetry · config         │
  per connector)      └───────────────────────────────────────┬────────────────────────────────────────────────────┘
                                                              │  IKnowledgeProvider (Abstractions package)
                       ┌──────────────────────────────────────┼───────────────────────────────┐
                       ▼                                      ▼                               ▼
               FoundryIq provider                     Cloud file store provider        Document management provider
               (Entra OBO + MI)                       (third-party OAuth pass-through) (own OAuth or Entra OBO)
```

**Rule of thumb:** the host owns everything Copilot sees and everything security-critical. A provider owns only how to search, read and list one kind of source, using the credentials the host hands it.

### 2.1 Who owns what

| Concern | Host | Provider |
|---|---|---|
| MCP transport, tool names, input schemas, `title`/`readOnlyHint` annotations | ✅ | |
| Tool *descriptions* (what the data is, so Copilot routes correctly) | Template | Supplies content description |
| Validating the inbound token, OBO exchange, user context | ✅ | Declares what it needs |
| Service credentials (managed identity, Key Vault secrets) | Supplies | Uses |
| Search, fetch and list-collections calls against the source | | ✅ |
| Enforcing permissions | Refuses to call without a user credential | Must query **as the user**, so the source trims results |
| Result handles: signing, expiry, binding to the user | ✅ | Supplies opaque payload; validates it on fetch |
| Truncation, text normalisation, error sanitising, timeouts, rate limits | ✅ | |
| Converting binary to text (PDF, DOCX) | Optional shared helper | Calls it when the source returns binary |
| Infrastructure and registration steps specific to the source | Generic script | `provider.json` + optional hook |

### 2.2 One provider per deployment

Each deployment runs **one** provider, chosen in configuration. Two sources means two deployments of the same host image, each registered as its own connector.

- **Why:** a connector has exactly one authentication method, and a source that signs users in with its own OAuth provider can't share one with Foundry IQ (Entra). It also keeps one gallery entry and one admin decision per source, which is how admins think about connectors.
- **Multiple collections *within* a source** are fine: several knowledge bases, several shared drives, several workspaces.
- Providers are **compiled in** and selected in configuration (`Connector:Provider = "FoundryIq"`). I rejected runtime plugin loading (`AssemblyLoadContext`). It adds risk to the trust boundary and to versioning, and buys nothing that rebuilding the image doesn't.

---

## 3. The provider contract

The contract lives in the package `FederatedConnector.Abstractions`, so a provider can be built in its own repository against a stable NuGet package. The sketch below is illustrative, not final.

```csharp
public interface IKnowledgeProvider
{
    ProviderDescriptor Descriptor { get; }   // id, display text, auth requirement, limits

    Task<IReadOnlyList<CollectionInfo>> ListCollectionsAsync(
        ProviderContext ctx, CancellationToken ct);

    Task<SearchResultSet> SearchAsync(
        SearchQuery query, ProviderContext ctx, CancellationToken ct);

    Task<DocumentContent> FetchAsync(
        HandlePayload handle, ProviderContext ctx, CancellationToken ct);
}

public sealed record ProviderDescriptor(
    string Id,                       // "foundry-iq"
    string ContentDescription,       // goes into tool descriptions for routing
    AuthRequirement Auth,
    string CollectionNoun);          // "knowledge base", "shared drive", "workspace"

public abstract record AuthRequirement
{
    // Copilot sends an Entra token for our app; host does OBO to these scopes.
    public sealed record EntraObo(IReadOnlyList<string> DownstreamScopes) : AuthRequirement;
    // Copilot holds the source's own OAuth token (Developer Portal OAuth registration); host forwards it.
    public sealed record OAuthPassthrough(Uri AuthorizeUrl, Uri TokenUrl, IReadOnlyList<string> Scopes,
                                          bool SupportsPkce) : AuthRequirement;
    public sealed record None : AuthRequirement;   // public data only
}

public sealed class ProviderContext
{
    public UserIdentity User { get; }                           // stable hashed id, tenant, display name if known
    public IUserTokenSource UserToken { get; }                  // OBO token or forwarded OAuth token
    public IServiceCredentialSource ServiceCredentials { get; } // managed identity, Key Vault
    public IContentExtractor Extractor { get; }                 // optional binary → text
    public IConfiguration Settings { get; }                     // the provider's own config section
}

public sealed record SearchQuery(string Text, string? Collection, int Top,
                                 IReadOnlyDictionary<string, string>? Filters);
public sealed record SearchHit(HandlePayload Handle, string Title, string Snippet, Uri? Url,
                               string? Collection, DocumentMetadata Metadata);
public sealed record DocumentContent(string Title, Uri? Url, string Text, string? MediaType,
                                     DocumentMetadata Metadata);
public sealed record DocumentMetadata(DateTimeOffset? LastModified, string? Author,
                                      string? SensitivityLabel,
                                      IReadOnlyDictionary<string, string>? Extra);
```

### 3.1 Provider obligations

These are checked by the conformance tests (§7).

1. **Read only.** No write calls to the source, ever.
2. **Query as the user.** Results must come from a call that carries the user's identity, so the source enforces its own permissions. Using a service identity alone to read user-restricted data is not allowed.
3. **Re-check access on fetch.** `fetch` must re-read the document as the user, not replay content captured at search time. The only exception is content that is explicitly public.
4. **Validate handle payloads.** Before using one, check that it points at the configured source (host or endpoint, collection allowlist). Never treat it as a URL to follow blindly.
5. **Keep payloads small and secret-free.** The host signs them, but doesn't encrypt them.
6. **Honour cancellation and timeouts.** Return partial results with a flag rather than hanging.

---

## 4. Copilot-facing tools (owned by the host)

Tool names and shapes are fixed across providers. The descriptions are filled in from the provider's descriptor and the connector's display name. For example: *"Search Contoso Knowledge (HR policies and engineering runbooks). Returns ranked results with ids; call `fetch` with an id to read the full document."*

| Tool | Title | Input | Output |
|---|---|---|---|
| `search` | Search {DisplayName} | `query` (required), `collection?`, `top` (1–20, default 8) | `results[]: { id, title, snippet, url, collection, metadata }` |
| `fetch` | Read {DisplayName} document | `id` (required) | `{ id, title, url, text, truncated, metadata }` |
| `list_collections` | List {DisplayName} {CollectionNoun}s | none | `collections[]: { name, description }` |

- **Annotations:** every tool has `readOnlyHint: true`, `idempotentHint: true`, `openWorldHint: false`.
- **Result format:** every result is returned as both `structuredContent` and a text block.
- **Names:** `search` and `fetch` follow the established connector convention, with result fields `id`, `title` and `url`.
- **Filters:** provider-specific filters (file type, document category) are a **v2** extension. A provider would declare a JSON schema fragment, and the host would merge it into `search`'s input schema. They're left out of v1 to keep the contract small.

### 4.1 Result handles

The `id` that Copilot sees has this form:

```
v1.<base64url({ p: providerId, c: collection, d: providerPayload, u: hash(tid+oid), exp })>.<HMAC-SHA256>
```

- **Signing:** the HMAC key is held in Key Vault, and expiry defaults to 1 hour.
- **Bound to the user:** a handle issued to one user is rejected for another.
- **Two checks:** the host checks the signature, expiry, user and provider ID. The provider then validates its own payload (§3.1 item 4).

---

## 5. Host design

| Concern | Choice |
|---|---|
| **Runtime** | C# on .NET 10 (long-term support), with ASP.NET Core minimal hosting. Use the standard runtime, not Native AOT, because the Azure SDK and Identity.Web aren't fully trim-safe. Ship as a chiselled `mcr.microsoft.com/dotnet/aspnet:10.0` container image. |
| **MCP** | `ModelContextProtocol.AspNetCore`: Streamable HTTP, stateless, single `/mcp` route. |
| **Inbound auth** | **EntraObo:** `Microsoft.Identity.Web` validates the JWT. It checks the audience (both the Developer Portal `api://auth-…` URI and the app URI), the tenant allowlist and the scope, and rejects app-only tokens; then it performs the OBO exchange with a cached token. **OAuthPassthrough:** require a bearer token and forward it. The source is the validator, and a 401 from the source becomes an MCP auth error. If the provider supplies an introspection or userinfo endpoint, call it once and cache the result briefly. **None:** no user context, so only providers that declare None can run this way. |
| **Service credentials** | A user-assigned managed identity, plus Key Vault for any source secrets. OBO uses a federated identity credential (managed identity → Entra app), so there's no client secret. |
| **Configuration** | `Connector:{ DisplayName, Provider, AllowedTenants, Limits }` and `Providers:{Id}:{…}`, which is the provider's own section. |
| **Errors** | MCP `isError` with a short, sanitised message. Never forward source response bodies. Error categories are fixed: auth, forbidden, not found, unavailable, timeout. |
| **Telemetry** | OpenTelemetry → Application Insights. Record tool, provider, collection, latency, result count, source status, and hashed tenant and user IDs. **Don't log query text or content by default.** |
| **Limits** | Per-user rate limit, an overall tool timeout (default 8 seconds), and fetch text capped at 20,000 characters by default. |

**Host invariant:** if the provider's auth requirement isn't `None` and no user credential is available, the host fails the call before it reaches the provider.

---

## 6. Reference provider: Foundry IQ

This is the first implementation and the test of the blueprint.

### 6.1 What Foundry IQ offers

- A **knowledge base** in Azure AI Search groups about 10 knowledge sources and answers through agentic retrieval.
- **The retrieve action** is `POST {search}/knowledgebases/{kb}/retrieve`. It returns `references[]` containing `docKey`, `sourceData` and, in preview, `citationUrl` and sensitivity labels.
- **API versions:** `2026-04-01` (GA) offers `intents` and extractive output only. `2026-08-01-preview` adds `messages`, answer synthesis, `citationUrl` and labels.
- **Built-in MCP endpoint:** one tool, `knowledge_base_retrieve`. It returns a JSON-in-text blob, with no references and no fetch. **The provider does not use it.**
- **Permissions:**
  - The knowledge bases are **assumed to be indexed with permissions already**. That is the knowledge-base owner's responsibility.
  - The user's `search.azure.com` token goes in the `x-ms-query-source-authorization` header, and the service credential goes in `Authorization`.
  - Without the user token, Search returns unfiltered results, and the host invariant (§5) prevents that.
- **`citationUrl`** is an authenticated GET into the index that returns the document's fields. Follow it verbatim, and send the same user token.

Source: [Query a knowledge base via API or MCP](https://learn.microsoft.com/azure/search/agentic-retrieval-how-to-retrieve)

### 6.2 Mapping onto the contract

| Contract | Foundry IQ |
|---|---|
| `Auth` | `EntraObo(["https://search.azure.com/user_impersonation"])` |
| Service credential | Managed identity with the **Search Index Data Reader** role |
| Collection | Allowlisted knowledge base (optionally narrowed to specific knowledge sources in config) |
| `ListCollections` | Allowlist from config, with descriptions read from the knowledge base definitions (cached for 10 minutes) |
| `Search` | Retrieve call with `outputMode: extractiveData` (configurable). With no collection specified, it fans out across the allowlisted knowledge bases in parallel. Each reference becomes a `SearchHit`: the title and snippet come from `sourceData`, the URL from `docUrl`/`blobUrl`, and the label from `sensitivityLabelInfo`. |
| Handle payload | `{ kb, ks, index, docKey, citationUrl? }` |
| Payload validation | The `citationUrl` host must equal the configured search endpoint, and the index must be on the allowlist. |
| `Fetch` | `GET citationUrl` (preview) using the managed-identity token plus the user's OBO token. For GA, fall back to `GET /indexes/{index}/docs/{docKey}`. For remote sources, return the stored snippet with `truncated: true` and say the full text isn't available. |
| Exclusions | Work IQ knowledge sources are excluded by default, because they would loop back into Copilot. |

**Verify before go-live:** that `GET {citationUrl}` with `x-ms-query-source-authorization` applies document-level trimming. If it doesn't, `fetch` must re-authorise by running a retrieve or search filtered to that `docKey` under the user's token.

### 6.3 How later providers would map (for validating the contract, not commitments)

| | Cloud file store (third-party sign-in) | Document management system |
|---|---|---|
| `Auth` | `OAuthPassthrough` (the store's OAuth, read-only scope); Copilot registration = OAuth 2.0 | `OAuthPassthrough` via the system's own identity provider, **or** `EntraObo` if the tenant federates it with Entra |
| Collection | Shared drive or personal drive | Workspace or folder |
| Search | The store's full-text search API | The system's search API |
| Fetch | File download or export → text extractor | Content download → text extractor |
| Permissions | Native: the call runs as the user | Native: the call runs as the user |

If both of these can be written against §3 without changing it, the contract is right.

---

## 7. Testing

- **Conformance kit** (`FederatedConnector.Conformance`): xUnit tests that any provider runs against a test fixture. It checks:
  - read-only tool annotations (via the host);
  - the handle round trip;
  - that fetch rejects a tampered or foreign payload;
  - that cancellation is honoured;
  - **the two-user permission test**: user A finds document X, user B doesn't, and B can't fetch it using A's handle.
- **Provider tests:** unit tests against recorded HTTP traffic, plus an opt-in live test with an environment-supplied tenant.
- **Smoke test** (`FederatedConnector.SmokeTest` console project): run `initialize` and `tools/list` (asserting `title` and `readOnlyHint` on every tool), then a `search` and `fetch` against the deployed URL. The registration script uses it too.

---

## 8. Repository layout

```
src/
  FederatedConnector.Abstractions/      contracts (NuGet package)
  FederatedConnector.Host/              MCP server, auth, handles, telemetry
  FederatedConnector.ContentExtraction/ optional binary → text helper
  Providers/
    FederatedConnector.Providers.FoundryIq/
tests/
  FederatedConnector.Conformance/
  FederatedConnector.Host.Tests/
  FederatedConnector.Providers.FoundryIq.Tests/
tools/
  FederatedConnector.SmokeTest/
deploy/
  register-connector.sh  register-connector.ps1
  infra/core.bicep                      Container App, managed identity, Key Vault, App Insights
  providers/foundry-iq/
    provider.json                       auth mode, required inputs, ATK oauth settings
    provision.sh                        e.g. assign Search Index Data Reader to the managed identity
    infra.bicep                         optional
  templates/manifest.json               agentConnectors template (route B)
```

---

## 9. Registration script

The script is `register-connector.sh` (with a PowerShell twin). It is **driven by the provider's `provider.json`** and is idempotent: each step is skipped if its output already exists in `.env.<env>`.

```
./register-connector.sh --env dev --provider foundry-iq --display-name "Contoso Knowledge" \
  --route package|admin  [provider-specific flags, e.g. --search-service, --knowledge-bases]
```

| Step | Action |
|---|---|
| **0. Preflight** | Check tool logins (`az`, `atk`). Check the provider's required inputs. Run the provider's preflight hook, if one exists. |
| **1. Core infrastructure** | Deploy `core.bicep`: Container App, managed identity, Key Vault with the HMAC key, App Insights. |
| **2. Provider provisioning** | Run `providers/<id>/provision.sh` and `infra.bicep`. For Foundry IQ, this assigns the managed identity the Search Index Data Reader role. |
| **3a. Auth: EntraObo** | Create the Entra app (`api://<appId>`, `access_as_user`, the provider's delegated downstream permission plus admin consent, the managed-identity federated credential, and the Teams redirect URI). Then run ATK `oauth/register` with `identityProvider: MicrosoftEntra`, and add the resulting `applicationIdUri` to the app and to the host's accepted audiences. |
| **3b. Auth: OAuthPassthrough** | Take the source's client ID and secret (they live with the source's identity provider, so this step can't create them). Then run ATK `oauth/register` with `identityProvider: Custom` and the provider's authorise and token URLs, scopes and PKCE setting. |
| **4. Deploy the host** | Push the image and configuration (`Connector:Provider`, provider section, accepted audiences). |
| **5. Smoke test** | Run `FederatedConnector.SmokeTest` against the URL. |
| **6A. Admin route** | Print the base URL and registration ID, and open the admin center. **This step is manual.** Then staged rollout. |
| **6B. Package route** | Render `manifest.json` with `agentConnectors` → zip → validate → `extendToM365` (dev) or `publishAppPackage` (org). |

`--destroy` reverses the steps.

---

## 10. Open questions

1. **Does the app-package route equal the admin route?** Does an app-package `agentConnectors` server surface across Copilot exactly like an admin-center federated connector? This only affects which ending the script uses by default.
2. **Admin-center API:** is there a supported API for "Create a new connector"?
3. **Copilot's latency budget** for federated calls. This sets the timeouts and whether answer synthesis is ever acceptable.
4. **`OAuthPassthrough` token validation:** is "the source validates the token" enough for certification, or do reviewers expect the server to validate it before calling the source?
5. **Foundry preview dependency:** `citationUrl` and sensitivity labels exist only in the preview API. Is the preview acceptable for the pilot?

---

## 11. Build order

1. Abstractions + host with the `EntraObo` path + Foundry IQ `search` (GA API), against a test knowledge base. Prove the round trip in MCP Inspector.
2. Handles + `fetch` (preview citation URL, plus the GA fallback) + `list_collections`.
3. Conformance kit, including the two-user permission test.
4. Registration script steps 0–5 and 6B, sideloaded to a test tenant.
5. **Contract check:** a minimal second provider spike against a source with its own OAuth sign-in, to prove `OAuthPassthrough` and binary extraction without touching the host. Fix the contract now if it needs changing, while only one real provider depends on it.
6. Admin-route rollout in the target tenant.
