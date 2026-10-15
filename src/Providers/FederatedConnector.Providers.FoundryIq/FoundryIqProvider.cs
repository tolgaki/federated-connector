using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using FederatedConnector.Abstractions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FederatedConnector.Providers.FoundryIq;

/// <summary>
/// Reference provider: Foundry IQ knowledge bases in Azure AI Search, queried with the knowledge base retrieve
/// action under the user's identity. Does not use the knowledge base's built-in MCP endpoint.
/// </summary>
public sealed class FoundryIqProvider : IKnowledgeProvider
{
    public const string ProviderId = "foundry-iq";
    public const string HttpClientName = "foundry-iq";
    public const string DefaultUserScope = "https://search.azure.com/user_impersonation";

    // Handle payload keys. Short to keep handles small.
    internal const string KbKey = "kb";
    internal const string KsKey = "ks";
    internal const string IndexKey = "index";
    internal const string DocKeyKey = "docKey";
    internal const string CitationKey = "citationUrl";
    internal const string ExcerptKey = "x";
    internal const string TitleKey = "t";
    internal const string UrlKey = "u";

    private readonly FoundryIqOptions _options;
    private readonly IHttpClientFactory _httpClients;
    private readonly FoundryIqMetadata _metadata;
    private readonly ILogger<FoundryIqProvider> _logger;

    public FoundryIqProvider(IOptions<FoundryIqOptions> options, IHttpClientFactory httpClients, IMemoryCache cache, ILogger<FoundryIqProvider> logger)
    {
        _options = options.Value;
        _httpClients = httpClients;
        _metadata = new FoundryIqMetadata(_options, cache);
        _logger = logger;
        Descriptor = Describe(_options.ContentDescription);
    }

    /// <summary>Compiled-in registration, selected with <c>Connector:Provider = "FoundryIq"</c>.</summary>
    public static ProviderRegistration Registration { get; } = new(
        "FoundryIq",
        section => Describe(section["ContentDescription"] ?? new FoundryIqOptions().ContentDescription),
        Register);

    public ProviderDescriptor Descriptor { get; }

    private static ProviderDescriptor Describe(string contentDescription) =>
        new(ProviderId, contentDescription, new AuthRequirement.EntraObo([DefaultUserScope]), "knowledge base");

    private static void Register(IServiceCollection services, IConfiguration section)
    {
        services.AddOptions<FoundryIqOptions>()
            .Bind(section)
            .Validate(o => o.Endpoint is { Scheme: "https" }, "Providers:FoundryIq:Endpoint must be an https URL.")
            .Validate(o => o.KnowledgeBases.Count > 0 && o.KnowledgeBases.All(k => !string.IsNullOrWhiteSpace(k.Name)),
                "Providers:FoundryIq:KnowledgeBases must list at least one knowledge base.")
            .ValidateOnStart();
        services.AddMemoryCache();
        services.AddHttpClient(HttpClientName, c => c.Timeout = TimeSpan.FromSeconds(30));
        services.AddSingleton<IKnowledgeProvider, FoundryIqProvider>();
    }

    private SearchServiceClient Client() => new(_httpClients.CreateClient(HttpClientName), _options);

    public async Task<IReadOnlyList<CollectionInfo>> ListCollectionsAsync(ProviderContext ctx, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var client = Client();
        var tasks = _options.KnowledgeBases.Select(async kb =>
        {
            try
            {
                var info = await _metadata.GetAsync(kb, client, ctx, ct).ConfigureAwait(false);
                return new CollectionInfo(kb.Name, info.Description);
            }
            catch (ProviderException ex)
            {
                // A missing definition shouldn't hide the rest of the allowlist.
                _logger.LogWarning("Could not read knowledge base definition ({Kind}).", ex.Kind);
                return new CollectionInfo(kb.Name, kb.Description);
            }
        });
        return await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    public async Task<SearchResultSet> SearchAsync(SearchQuery query, ProviderContext ctx, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        RequireUser(ctx);

        IReadOnlyList<KnowledgeBaseOptions> targets;
        if (query.Collection is not null)
        {
            var kb = _metadata.Allowlisted(query.Collection)
                ?? throw new ProviderException(ProviderErrorKind.NotFound, "Collection is not allowlisted.");
            targets = [kb];
        }
        else
        {
            targets = _options.KnowledgeBases;
        }

        var client = Client();
        var outcomes = await Task.WhenAll(targets.Select(kb => SearchOneAsync(kb, query, client, ctx, ct))).ConfigureAwait(false);

        var errors = outcomes.Where(o => o.Error is not null).Select(o => o.Error!).ToList();
        if (errors.Count == outcomes.Length)
        {
            // Nothing succeeded: surface the most meaningful failure.
            if (ct.IsCancellationRequested)
            {
                ct.ThrowIfCancellationRequested();
            }

            throw errors.FirstOrDefault(e => e.Kind is ProviderErrorKind.Auth or ProviderErrorKind.Forbidden) ?? errors[0];
        }

        var hits = outcomes
            .SelectMany(o => o.Hits)
            .GroupBy(h => h.Dedupe)
            .Select(g => g.OrderByDescending(h => h.Score).First())
            .OrderByDescending(h => h.Score)
            .Take(query.Top)
            .Select(h => h.Hit)
            .ToList();
        return new SearchResultSet(hits, Partial: errors.Count > 0);
    }

    private async Task<KbOutcome> SearchOneAsync(KnowledgeBaseOptions kb, SearchQuery query, SearchServiceClient client, ProviderContext ctx, CancellationToken ct)
    {
        try
        {
            var info = await _metadata.GetAsync(kb, client, ctx, ct).ConfigureAwait(false);
            if (info.Sources.Count == 0)
            {
                return new KbOutcome([], null);
            }

            var body = new JsonObject
            {
                ["intents"] = new JsonArray(new JsonObject { ["type"] = "semantic", ["search"] = query.Text }),
                ["knowledgeSourceParams"] = new JsonArray(info.Sources.Select(s => (JsonNode)new JsonObject
                {
                    ["knowledgeSourceName"] = s.Name,
                    ["kind"] = s.Kind,
                    ["includeReferences"] = true,
                    ["includeReferenceSourceData"] = true,
                }).ToArray()),
                ["outputMode"] = _options.OutputMode,
            };

            using var doc = await client.RetrieveAsync(kb.Name, body, ctx, ct).ConfigureAwait(false);
            return new KbOutcome(MapReferences(doc.RootElement, info), null);
        }
        catch (ProviderException ex)
        {
            _logger.LogWarning("Retrieve failed for a knowledge base ({Kind}, status {Status}).", ex.Kind, ex.SourceStatus);
            return new KbOutcome([], ex);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Obligation 6: return what we have, flagged partial, rather than hanging.
            return new KbOutcome([], new ProviderException(ProviderErrorKind.Timeout, "Retrieve cancelled."));
        }
    }

    internal IReadOnlyList<RankedHit> MapReferences(JsonElement root, KnowledgeBaseInfo kb)
    {
        var activitySources = Json.Array(root, "activity")
            .Where(a => a.TryGetProperty("id", out _))
            .Select(a => (Id: a.GetProperty("id").ToString(), Ks: Json.String(a, "knowledgeSourceName")))
            .Where(a => a.Ks is not null)
            .GroupBy(a => a.Id)
            .ToDictionary(g => g.Key, g => g.First().Ks!);

        var results = new List<RankedHit>();
        var position = 0;
        foreach (var r in Json.Array(root, "references"))
        {
            position++;
            var ksName = Json.String(r, "knowledgeSourceName")
                ?? (r.TryGetProperty("activitySource", out var a) && activitySources.TryGetValue(a.ToString(), out var n) ? n : null)
                ?? (kb.Sources.Count == 1 ? kb.Sources[0].Name : null);
            var ks = kb.Source(ksName);
            var type = Json.String(r, "type");
            if (ks is null || (type is not null && _options.ExcludedKnowledgeSourceKinds.Contains(type, StringComparer.OrdinalIgnoreCase)))
            {
                continue; // unknown or excluded source: never surface it
            }

            var data = r.TryGetProperty("sourceData", out var sd) ? sd : default;
            var docKey = Json.String(r, "docKey");
            var title = Json.FirstText(data, _options.TitleFields) ?? Json.String(r, "title") ?? docKey ?? "Untitled";
            var snippet = Clip(Json.FirstText(data, _options.ContentFields, " … ") ?? "", _options.MaxSnippetChars);
            var url = HttpUrl(Json.String(r, "docUrl") ?? Json.String(r, "blobUrl") ?? Json.String(r, "url") ?? Json.FirstText(data, _options.UrlFields));
            var metadata = new DocumentMetadata(
                LastModified(data),
                Json.FirstText(data, _options.AuthorFields),
                Label(r));

            var payload = new Dictionary<string, string> { [KbKey] = kb.Name, [KsKey] = ks.Name };
            string dedupe;
            if (ks.IsIndexed)
            {
                if (string.IsNullOrEmpty(docKey))
                {
                    continue;
                }

                payload[IndexKey] = ks.Index!;
                payload[DocKeyKey] = docKey;
                if (_options.UsePreviewApi && Json.String(r, "citationUrl") is { } citation)
                {
                    payload[CitationKey] = citation;
                }

                dedupe = ks.Index + "|" + docKey;
            }
            else
            {
                payload[ExcerptKey] = Clip(snippet, _options.MaxPayloadExcerptChars);
                payload[TitleKey] = Clip(title, 200);
                if (url is not null)
                {
                    payload[UrlKey] = url.AbsoluteUri;
                }

                dedupe = ks.Name + "|" + (url?.AbsoluteUri ?? title);
            }

            var score = r.TryGetProperty("rerankerScore", out var s) && s.ValueKind == JsonValueKind.Number ? s.GetDouble() : -position;
            var hit = new SearchHit(new HandlePayload(kb.Name, payload), title, snippet, url, kb.Name, metadata);
            results.Add(new RankedHit(hit, score, kb.Name + "|" + dedupe));
        }

        return results;
    }

    public async Task<DocumentContent> FetchAsync(HandlePayload handle, ProviderContext ctx, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        RequireUser(ctx);

        // Obligation 4: validate the payload against the configured source before using any of it.
        var kbName = handle.Get(KbKey);
        var kb = _metadata.Allowlisted(kbName) ?? throw ProviderException.InvalidHandle("knowledge base not allowlisted");
        if (handle.Collection is not null && handle.Collection != kb.Name)
        {
            throw ProviderException.InvalidHandle("collection mismatch");
        }

        var client = Client();
        var info = await _metadata.GetAsync(kb, client, ctx, ct).ConfigureAwait(false);
        var ks = info.Source(handle.Get(KsKey)) ?? throw ProviderException.InvalidHandle("knowledge source not allowed");

        if (!ks.IsIndexed)
        {
            // Remote sources cannot be re-read by key; return the excerpt captured at search time.
            var excerpt = handle.Get(ExcerptKey) ?? "";
            return new DocumentContent(
                handle.Get(TitleKey) ?? "Untitled",
                HttpUrl(handle.Get(UrlKey)),
                excerpt + "\n\n[The full text of this item is not available through this connector; this is the excerpt returned by search.]",
                "text/plain",
                DocumentMetadata.Empty,
                Truncated: true);
        }

        var index = handle.Get(IndexKey);
        var docKey = handle.Get(DocKeyKey);
        if (index is null || !string.Equals(index, ks.Index, StringComparison.Ordinal))
        {
            throw ProviderException.InvalidHandle("index not allowed");
        }

        if (string.IsNullOrEmpty(docKey) || docKey.Length > 1024)
        {
            throw ProviderException.InvalidHandle("bad document key");
        }

        var keyField = await _metadata.GetKeyFieldAsync(ks, client, ctx, ct).ConfigureAwait(false);
        var citation = handle.Get(CitationKey);
        if (_options.UsePreviewApi && citation is not null)
        {
            var citationUrl = ValidateCitationUrl(citation, index);
            if (_options.VerifyCitationUrlTrimming)
            {
                await LookupAsUserAsync(client, index, keyField, docKey, select: keyField, ctx, ct).ConfigureAwait(false);
            }

            using var cited = await client.GetCitationAsync(citationUrl, ctx, ct).ConfigureAwait(false);
            return ToDocument(cited.RootElement, docKey);
        }

        // GA path: a search filtered to the key, under the user's token, is both the read and the access check.
        var fields = await LookupAsUserAsync(client, index, keyField, docKey, select: null, ctx, ct).ConfigureAwait(false);
        return ToDocument(fields, docKey);
    }

    private async Task<JsonElement> LookupAsUserAsync(SearchServiceClient client, string index, string keyField, string docKey, string? select, ProviderContext ctx, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["search"] = "*",
            ["filter"] = $"{keyField} eq '{docKey.Replace("'", "''", StringComparison.Ordinal)}'",
            ["top"] = 1,
        };
        if (select is not null)
        {
            body["select"] = select;
        }

        using var doc = await client.SearchDocumentsAsync(index, body, ctx, ct).ConfigureAwait(false);
        var first = Json.Array(doc.RootElement, "value").FirstOrDefault();
        if (first.ValueKind != JsonValueKind.Object)
        {
            // Not present, or trimmed for this user: indistinguishable by design.
            throw new ProviderException(ProviderErrorKind.NotFound, "Document not visible to the user.");
        }

        return first.Clone();
    }

    private Uri ValidateCitationUrl(string value, string index)
    {
        var endpoint = _options.Endpoint!;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var url) ||
            url.Scheme != Uri.UriSchemeHttps ||
            !string.IsNullOrEmpty(url.UserInfo) ||
            !string.Equals(url.Host, endpoint.Host, StringComparison.OrdinalIgnoreCase) ||
            url.Port != endpoint.Port ||
            !Uri.UnescapeDataString(url.AbsolutePath).Contains(index, StringComparison.Ordinal))
        {
            throw ProviderException.InvalidHandle("citationUrl does not point at the configured search endpoint");
        }

        return url;
    }

    private DocumentContent ToDocument(JsonElement fields, string docKey)
    {
        var title = Json.FirstText(fields, _options.TitleFields) ?? docKey;
        var text = Json.FirstText(fields, _options.ContentFields);
        if (text is null)
        {
            var known = new HashSet<string>(_options.TitleFields.Concat(_options.UrlFields), StringComparer.OrdinalIgnoreCase);
            text = string.Join("\n", fields.EnumerateObject()
                .Where(p => p.Value.ValueKind == JsonValueKind.String && !p.Name.StartsWith('@') && !known.Contains(p.Name))
                .Select(p => $"{p.Name}: {p.Value.GetString()}"));
        }

        return new DocumentContent(
            title,
            HttpUrl(Json.FirstText(fields, _options.UrlFields)),
            text,
            "text/plain",
            new DocumentMetadata(LastModified(fields), Json.FirstText(fields, _options.AuthorFields)));
    }

    private static void RequireUser(ProviderContext ctx)
    {
        if (!ctx.UserToken.IsAvailable)
        {
            throw new ProviderException(ProviderErrorKind.Auth, "Foundry IQ requires a user credential.");
        }
    }

    private DateTimeOffset? LastModified(JsonElement data) =>
        Json.FirstText(data, _options.LastModifiedFields) is { } s &&
        DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d) ? d : null;

    private static string? Label(JsonElement reference)
    {
        if (!reference.TryGetProperty("sensitivityLabelInfo", out var l))
        {
            return null;
        }

        return l.ValueKind == JsonValueKind.String ? l.GetString() : Json.String(l, "displayName") ?? Json.String(l, "name");
    }

    private static Uri? HttpUrl(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttps || u.Scheme == Uri.UriSchemeHttp) ? u : null;

    private static string Clip(string value, int max) => value.Length <= max ? value : value[..max];

    internal sealed record RankedHit(SearchHit Hit, double Score, string Dedupe);

    private sealed record KbOutcome(IReadOnlyList<RankedHit> Hits, ProviderException? Error);
}
