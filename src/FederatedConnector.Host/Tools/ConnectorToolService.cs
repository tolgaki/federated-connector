using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using FederatedConnector.Abstractions;
using FederatedConnector.Host.Auth;
using FederatedConnector.Host.Handles;
using FederatedConnector.Host.Telemetry;
using Microsoft.Extensions.Options;

namespace FederatedConnector.Host.Tools;

/// <summary>Result of a tool call: structured content plus the same content as a JSON text block.</summary>
public sealed record ToolResult(JsonObject Structured, bool IsError)
{
    public string Text => Structured.ToJsonString();
}

/// <summary>The provider's configuration section, handed to the provider in <see cref="ProviderContext.Settings"/>.</summary>
public sealed record ProviderSettings(IConfiguration Section);

/// <summary>
/// Executes the fixed tools against the configured provider. Owns argument validation, the host auth invariant,
/// handle signing and verification, timeouts, truncation, text normalisation, error sanitising and telemetry.
/// Independent of the MCP SDK so it can be unit-tested directly.
/// </summary>
public sealed class ConnectorToolService(
    IKnowledgeProvider provider,
    IRequestUserAccessor users,
    HandleCodec handles,
    IOptions<ConnectorOptions> options,
    IServiceCredentialSource serviceCredentials,
    IContentExtractor extractor,
    ProviderSettings settings,
    ConnectorTelemetry telemetry,
    ToolCatalog catalog,
    ILogger<ConnectorToolService> logger)
{
    private readonly ConnectorOptions _options = options.Value;

    public async Task<ToolResult> InvokeAsync(string name, IDictionary<string, JsonElement>? args, CancellationToken ct)
    {
        using var activity = ConnectorTelemetry.ActivitySource.StartActivity("tool " + name);
        var started = Stopwatch.GetTimestamp();
        var descriptor = provider.Descriptor;
        string? collection = null;
        int? resultCount = null;
        int? sourceStatus = null;
        string outcome = "ok";
        UserIdentity? identity = null;

        try
        {
            if (catalog.Find(name) is null)
            {
                outcome = "invalid_request";
                return Error("invalid_request", $"Unknown tool '{Sanitise(name)}'.");
            }

            // The overall timeout covers user resolution (for example a userinfo call) as well as the provider.
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(_options.Limits.ToolTimeoutSeconds));

            try
            {
                // Host invariant: if the provider needs a user credential and none is available, fail before the provider.
                var user = await users.GetCurrentAsync(timeout.Token).ConfigureAwait(false);
                if (descriptor.Auth is not AuthRequirement.None && (user is null || !user.Token.IsAvailable || user.Identity.IsAnonymous))
                {
                    outcome = "auth";
                    return Error(ProviderErrorKind.Auth);
                }

                user ??= new RequestUser(UserIdentity.Anonymous, NoUserTokenSource.Instance);
                identity = user.Identity;
                var ctx = new ProviderContext(user.Identity, user.Token, serviceCredentials, extractor, settings.Section);

                switch (name)
                {
                    case ToolCatalog.Search:
                    {
                        if (!TryParseSearch(args, out var query, out var error))
                        {
                            outcome = "invalid_request";
                            return Error("invalid_request", error);
                        }

                        collection = query.Collection;
                        var result = await provider.SearchAsync(query, ctx, timeout.Token).ConfigureAwait(false);
                        resultCount = Math.Min(result.Hits.Count, query.Top);
                        if (result.Partial)
                        {
                            outcome = "partial";
                        }

                        return new ToolResult(SearchJson(result, query.Top, descriptor.Id, user.Identity), false);
                    }

                    case ToolCatalog.Fetch:
                    {
                        var id = GetString(args, "id");
                        if (string.IsNullOrWhiteSpace(id))
                        {
                            outcome = "invalid_request";
                            return Error("invalid_request", "The 'id' argument is required.");
                        }

                        var status = handles.TryDecode(id, descriptor.Id, user.Identity, out var payload);
                        if (status != HandleDecodeStatus.Valid || payload is null)
                        {
                            logger.LogInformation("Rejected handle: {Reason}.", status);
                            outcome = "not_found";
                            return Error(ProviderErrorKind.NotFound);
                        }

                        collection = payload.Collection;
                        var doc = await provider.FetchAsync(payload, ctx, timeout.Token).ConfigureAwait(false);
                        resultCount = 1;
                        return new ToolResult(FetchJson(id, doc), false);
                    }

                    default:
                    {
                        var collections = await provider.ListCollectionsAsync(ctx, timeout.Token).ConfigureAwait(false);
                        resultCount = collections.Count;
                        var array = new JsonArray();
                        foreach (var c in collections)
                        {
                            array.Add(new JsonObject
                            {
                                ["name"] = c.Name,
                                ["description"] = TextNormalizer.Normalise(c.Description),
                            });
                        }

                        return new ToolResult(new JsonObject { ["collections"] = array }, false);
                    }
                }
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                outcome = "timeout";
                return Error(ProviderErrorKind.Timeout);
            }
        }
        catch (ProviderException ex)
        {
            outcome = Category(ex.Kind);
            sourceStatus = ex.SourceStatus;
            logger.LogWarning("Provider error {Category} (source status {Status}): {Detail}", outcome, ex.SourceStatus, ex.Message);
            return Error(ex.Kind);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            outcome = "cancelled";
            throw;
        }
        catch (Exception ex)
        {
            outcome = "unavailable";
            logger.LogError("Unexpected {ExceptionType} in tool {Tool}.", ex.GetType().FullName, name);
            return Error(ProviderErrorKind.Unavailable);
        }
        finally
        {
            activity?.SetStatus(outcome is "ok" or "partial" ? ActivityStatusCode.Ok : ActivityStatusCode.Error);
            telemetry.Record(new ToolCallRecord(
                name,
                descriptor.Id,
                collection,
                outcome,
                Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                resultCount,
                sourceStatus,
                UserHash.ForTelemetry(identity?.TenantId),
                UserHash.ForTelemetry(identity?.HashedId)));
        }
    }

    private bool TryParseSearch(IDictionary<string, JsonElement>? args, out SearchQuery query, out string error)
    {
        query = null!;
        error = "";
        var text = GetString(args, "query")?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            error = "The 'query' argument is required.";
            return false;
        }

        if (text.Length > _options.Limits.MaxQueryChars)
        {
            text = text[.._options.Limits.MaxQueryChars];
        }

        var collection = GetString(args, "collection")?.Trim();
        var top = ToolCatalog.DefaultTop;
        if (args is not null && args.TryGetValue("top", out var t) && t.ValueKind == JsonValueKind.Number && t.TryGetInt32(out var n))
        {
            top = Math.Clamp(n, ToolCatalog.MinTop, ToolCatalog.MaxTop);
        }

        query = new SearchQuery(text, string.IsNullOrEmpty(collection) ? null : collection, top);
        return true;
    }

    private JsonObject SearchJson(SearchResultSet result, int top, string providerId, UserIdentity user)
    {
        var array = new JsonArray();
        var partial = result.Partial;
        foreach (var hit in result.Hits.Take(top))
        {
            var payload = hit.Handle.Collection is null && hit.Collection is not null
                ? hit.Handle with { Collection = hit.Collection }
                : hit.Handle;
            var id = handles.Encode(providerId, payload, user);
            if (id.Length > HandleCodec.MaxHandleLength)
            {
                // Never hand out an id that fetch would reject.
                logger.LogWarning("Dropped a hit whose handle exceeds {Max} characters; the provider payload is too large.", HandleCodec.MaxHandleLength);
                partial = true;
                continue;
            }

            var (snippet, _) = TextNormalizer.Truncate(TextNormalizer.Normalise(hit.Snippet), _options.Limits.MaxSnippetChars);
            array.Add(new JsonObject
            {
                ["id"] = id,
                ["title"] = TextNormalizer.Normalise(hit.Title),
                ["snippet"] = snippet,
                ["url"] = SafeUrl(hit.Url),
                ["collection"] = hit.Collection,
                ["metadata"] = MetadataJson(hit.Metadata),
            });
        }

        var obj = new JsonObject { ["results"] = array };
        if (partial)
        {
            obj["partial"] = true;
        }

        return obj;
    }

    private JsonObject FetchJson(string id, DocumentContent doc)
    {
        var (text, truncated) = TextNormalizer.Truncate(TextNormalizer.Normalise(doc.Text), _options.Limits.MaxFetchChars);
        return new JsonObject
        {
            ["id"] = id,
            ["title"] = TextNormalizer.Normalise(doc.Title),
            ["url"] = SafeUrl(doc.Url),
            ["text"] = text,
            ["truncated"] = truncated || doc.Truncated,
            ["metadata"] = MetadataJson(doc.Metadata),
        };
    }

    private static string? SafeUrl(Uri? url) =>
        url is { IsAbsoluteUri: true } && (url.Scheme == Uri.UriSchemeHttps || url.Scheme == Uri.UriSchemeHttp) ? url.AbsoluteUri : null;

    private static JsonObject MetadataJson(DocumentMetadata m)
    {
        var obj = new JsonObject();
        if (m.LastModified is { } lm)
        {
            obj["lastModified"] = lm.ToString("O");
        }

        if (!string.IsNullOrEmpty(m.Author))
        {
            obj["author"] = m.Author;
        }

        if (!string.IsNullOrEmpty(m.SensitivityLabel))
        {
            obj["sensitivityLabel"] = m.SensitivityLabel;
        }

        if (m.Extra is not null)
        {
            foreach (var (k, v) in m.Extra)
            {
                if (!obj.ContainsKey(k))
                {
                    obj[k] = v;
                }
            }
        }

        return obj;
    }

    private static string? GetString(IDictionary<string, JsonElement>? args, string key) =>
        args is not null && args.TryGetValue(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    public static string Category(ProviderErrorKind kind) => kind switch
    {
        ProviderErrorKind.Auth => "auth",
        ProviderErrorKind.Forbidden => "forbidden",
        ProviderErrorKind.NotFound => "not_found",
        ProviderErrorKind.Timeout => "timeout",
        _ => "unavailable",
    };

    private ToolResult Error(ProviderErrorKind kind)
    {
        var name = _options.DisplayName;
        var message = kind switch
        {
            ProviderErrorKind.Auth => $"The connector could not verify your access to {name}. Sign in again and retry.",
            ProviderErrorKind.Forbidden => "You do not have access to this item.",
            ProviderErrorKind.NotFound => "The item was not found or is no longer available. Run search again to get a fresh id.",
            ProviderErrorKind.Timeout => $"{name} did not respond in time. Try a narrower query.",
            _ => $"{name} is temporarily unavailable. Try again later.",
        };
        return Error(Category(kind), message);
    }

    private static ToolResult Error(string category, string message) =>
        new(new JsonObject { ["error"] = new JsonObject { ["category"] = category, ["message"] = message } }, true);

    private static string Sanitise(string value) =>
        new(value.Where(c => char.IsLetterOrDigit(c) || c is '_' or '-').Take(64).ToArray());
}
