namespace FederatedConnector.Host;

/// <summary>Configuration section <c>Connector</c>.</summary>
public sealed class ConnectorOptions
{
    public const string SectionName = "Connector";

    /// <summary>Human-readable connector name used in tool titles and descriptions, e.g. "Contoso Knowledge".</summary>
    public string DisplayName { get; set; } = "Knowledge";

    /// <summary>Name of the compiled-in provider to run, e.g. "FoundryIq". Exactly one per deployment.</summary>
    public string Provider { get; set; } = "";

    /// <summary>Entra tenants whose users may call the connector (EntraObo only). Empty means the AzureAd tenant only.</summary>
    public List<string> AllowedTenants { get; set; } = [];

    /// <summary>Accepted token audiences (EntraObo): the Developer Portal <c>api://auth-…</c> URI and the app URI / client id.</summary>
    public List<string> AcceptedAudiences { get; set; } = [];

    /// <summary>Delegated scope Copilot must present (EntraObo).</summary>
    public string RequiredScope { get; set; } = "access_as_user";

    /// <summary>Base64 HMAC-SHA256 key (at least 32 bytes) for result handles. Supplied from Key Vault.</summary>
    public string? HandleSigningKey { get; set; }

    /// <summary>Previous key, accepted for verification only, to allow rotation without breaking live handles.</summary>
    public string? PreviousHandleSigningKey { get; set; }

    public int HandleLifetimeMinutes { get; set; } = 60;

    /// <summary>Client id of the user-assigned managed identity. Null uses the default Azure credential chain.</summary>
    public string? ManagedIdentityClientId { get; set; }

    /// <summary>Key Vault holding source secrets, if the provider needs any.</summary>
    public Uri? KeyVaultUri { get; set; }

    public LimitsOptions Limits { get; set; } = new();
}

public sealed class LimitsOptions
{
    public int ToolTimeoutSeconds { get; set; } = 8;
    public int MaxFetchChars { get; set; } = 20_000;
    public int MaxSnippetChars { get; set; } = 500;
    public int MaxQueryChars { get; set; } = 1_000;
    public int RequestsPerMinutePerUser { get; set; } = 60;
}
