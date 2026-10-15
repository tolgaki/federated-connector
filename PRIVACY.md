# Privacy statement — TEMPLATE

> [!IMPORTANT]
> **This is a template, not a privacy statement.** It is not legal text for this project and makes no commitments on
> anyone's behalf. Connector registration asks for a privacy statement URL. If you deploy a connector built from this
> blueprint, **you** (the deployer) must complete this template, have it reviewed by your own legal or privacy advisers,
> publish it at a stable URL, and pass that URL to the registration script (`--privacy-url`).
>
> Replace every `[bracketed]` item, delete sections that don't apply, and delete this notice.

**[Connector name]** — operated by **[Organisation legal name]**
Last updated: **[date]**

## What this connector does

[Connector name] lets Microsoft 365 Copilot search and read **[describe the data source and content]** on behalf of
signed-in users in **[organisation / tenants]**. It is a read-only service: it does not create, change or delete data in
the source, and it does not copy source content into Microsoft 365.

## Data we process

When a user asks Copilot a question that Copilot routes to this connector, the connector receives and processes:

- **Authentication data:** an access token that identifies the user [and their tenant]. [Describe: Entra ID token and
  on-behalf-of exchange / the data source's own OAuth token.]
- **Request data:** the search query text, optional collection name, and result identifiers.
- **Source content:** search results and document text returned by **[data source]**, which the connector passes back
  to Copilot for that user only.

The connector itself does not store queries or document content. [Adjust if your deployment differs.]

## Telemetry and logs

The connector records operational telemetry in **[Azure Application Insights / other]**: tool name, provider,
collection name, outcome, latency, result counts, source status codes, and **hashed** tenant and user identifiers.
By default it does **not** record query text or document content. Telemetry is retained for **[N days]**.
[Describe any changes you made to the defaults.]

## Where data is processed

The connector runs in **[cloud provider / region]**. [Describe data residency, sub-processors, and transfers.]

## Access and permissions

The connector queries the data source **as the signed-in user**, so the source applies its own permissions. Users only
receive content they are already allowed to access in **[data source]**.

## Your rights

[Describe how users can exercise data-protection rights applicable to you, for example access, correction, deletion
and objection, and how requests are handled.]

## Contact

[Privacy contact, address and email.]

## Changes

[How changes to this statement are communicated.]
