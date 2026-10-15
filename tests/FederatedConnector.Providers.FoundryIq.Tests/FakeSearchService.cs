using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace FederatedConnector.Providers.FoundryIq.Tests;

/// <summary>
/// Simulated Azure AI Search data plane for Foundry IQ: knowledge base and knowledge source definitions, the
/// retrieve action, index definitions, key-filtered document search and citation GETs. Trims documents by the user
/// token in <c>x-ms-query-source-authorization</c> exactly where real Search would, and records every request.
/// </summary>
public sealed class FakeSearchService : HttpMessageHandler
{
    public static readonly Uri Endpoint = new("https://contoso-search.search.windows.net/");
    public const string ServiceToken = "svc-token";
    public const string AliceToken = "user-token-alice";
    public const string BobToken = "user-token-bob";

    private readonly Dictionary<string, string> _users = new() { [AliceToken] = "alice", [BobToken] = "bob" };

    public sealed record Doc(string Key, string Title, string Content, string? Url = null, IReadOnlySet<string>? AllowedUsers = null, string? Label = null)
    {
        public bool VisibleTo(string? user) => AllowedUsers is null || (user is not null && AllowedUsers.Contains(user));
    }

    public sealed record RecordedRequest(string Method, string Path, string Query, string? Authorization, string? UserToken, string? Body);

    public Dictionary<string, (string Description, string[] Sources)> KnowledgeBases { get; } = new()
    {
        ["hr-kb"] = ("HR policies and benefits", ["hr-ks"]),
        ["eng-kb"] = ("Engineering runbooks", ["eng-ks", "wiq-ks", "web-ks"]),
    };

    public Dictionary<string, (string Kind, string? Index)> KnowledgeSources { get; } = new()
    {
        ["hr-ks"] = ("searchIndex", "hr-index"),
        ["eng-ks"] = ("azureBlob", "eng-index"),
        ["wiq-ks"] = ("workIQ", null),
        ["web-ks"] = ("web", null),
    };

    public Dictionary<string, List<Doc>> Indexes { get; } = new()
    {
        ["hr-index"] =
        [
            new("hr-1", "Parental leave policy", "Employees receive 20 weeks of parental leave. This policy applies to all staff.", "https://intranet.contoso.com/hr/leave"),
            new("hr-2", "Executive compensation policy", "Confidential compensation bands for executives.", null, new HashSet<string> { "alice" }, "Highly Confidential"),
            new("hr'3", "Travel policy (O'Brien edition)", "Book travel through the portal. Policy owner: O'Brien."),
        ],
        ["eng-index"] =
        [
            new("eng-1", "Deployment runbook", "How to deploy the service; rollback policy and on-call steps.", "https://contoso.blob.core.windows.net/docs/deploy.md"),
        ],
    };

    /// <summary>When false (default), citation GETs do not trim — simulating the unverified risk in design §6.2.</summary>
    public bool CitationGetTrims { get; set; }

    /// <summary>Failure injection: return a response for matching requests instead of simulating.</summary>
    public Func<HttpRequestMessage, HttpResponseMessage?>? Override { get; set; }

    public TimeSpan Delay { get; set; }

    public List<RecordedRequest> Requests { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
        var userToken = request.Headers.TryGetValues("x-ms-query-source-authorization", out var v) ? v.Single() : null;
        lock (Requests)
        {
            Requests.Add(new RecordedRequest(
                request.Method.Method,
                Uri.UnescapeDataString(request.RequestUri!.AbsolutePath),
                request.RequestUri.Query,
                request.Headers.Authorization?.ToString(),
                userToken,
                body));
        }

        if (Delay > TimeSpan.Zero)
        {
            await Task.Delay(Delay, ct);
        }

        if (Override?.Invoke(request) is { } overridden)
        {
            return overridden;
        }

        if (request.RequestUri!.Host != Endpoint.Host)
        {
            return Status(HttpStatusCode.BadGateway);
        }

        if (request.Headers.Authorization?.ToString() != "Bearer " + ServiceToken)
        {
            return Status(HttpStatusCode.Unauthorized, "{\"error\":{\"message\":\"internal detail: key=abc123\"}}");
        }

        string? user = null;
        if (userToken is not null && !_users.TryGetValue(userToken, out user))
        {
            return Status(HttpStatusCode.Unauthorized);
        }

        var segments = Uri.UnescapeDataString(request.RequestUri.AbsolutePath).Trim('/').Split('/');
        var preview = request.RequestUri.Query.Contains("preview", StringComparison.Ordinal);
        return (request.Method.Method, segments) switch
        {
            ("GET", ["knowledgebases", var kb]) => KnowledgeBase(kb),
            ("GET", ["knowledgesources", var ks]) => KnowledgeSource(ks),
            ("POST", ["knowledgebases", var kb, "retrieve"]) => Retrieve(kb, body!, user, preview),
            ("GET", ["indexes", var index]) => IndexDefinition(index),
            ("POST", ["indexes", var index, "docs", "search"]) => DocsSearch(index, body!, user),
            ("GET", ["indexes", var index, "docs", var key]) => CitationGet(index, key, user),
            _ => Status(HttpStatusCode.NotFound),
        };
    }

    private HttpResponseMessage KnowledgeBase(string kb) =>
        KnowledgeBases.TryGetValue(kb, out var k)
            ? Json(new JsonObject
            {
                ["name"] = kb,
                ["description"] = k.Description,
                ["knowledgeSources"] = new JsonArray(k.Sources.Select(s => (JsonNode)new JsonObject { ["name"] = s }).ToArray()),
            })
            : Status(HttpStatusCode.NotFound);

    private HttpResponseMessage KnowledgeSource(string ks)
    {
        if (!KnowledgeSources.TryGetValue(ks, out var s))
        {
            return Status(HttpStatusCode.NotFound);
        }

        var obj = new JsonObject { ["name"] = ks, ["kind"] = s.Kind };
        if (s.Kind == "searchIndex")
        {
            obj["searchIndexParameters"] = new JsonObject { ["searchIndexName"] = s.Index };
        }
        else if (s.Index is not null)
        {
            obj["azureBlobParameters"] = new JsonObject { ["createdResources"] = new JsonObject { ["index"] = s.Index } };
        }

        return Json(obj);
    }

    private HttpResponseMessage Retrieve(string kb, string body, string? user, bool preview)
    {
        if (!KnowledgeBases.TryGetValue(kb, out var k))
        {
            return Status(HttpStatusCode.NotFound);
        }

        var request = JsonNode.Parse(body)!;
        var text = request["intents"]![0]!["search"]!.GetValue<string>();
        var terms = text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(t => t.Length >= 3).Select(t => t.ToLowerInvariant()).ToArray();
        var requested = request["knowledgeSourceParams"]!.AsArray().Select(p => p!["knowledgeSourceName"]!.GetValue<string>()).ToHashSet();

        var activity = new JsonArray();
        var references = new JsonArray();
        var activityId = 0;
        var referenceId = 0;
        foreach (var ksName in k.Sources)
        {
            var ks = KnowledgeSources[ksName];
            // Simulate a misconfigured knowledge base that still answers from a Work IQ source.
            if (!requested.Contains(ksName) && ks.Kind != "workIQ")
            {
                continue;
            }

            activityId++;
            activity.Add(new JsonObject { ["id"] = activityId, ["type"] = ks.Kind, ["knowledgeSourceName"] = ksName });
            if (ks.Kind == "workIQ")
            {
                references.Add(new JsonObject { ["type"] = "workIQ", ["id"] = (referenceId++).ToString(), ["activitySource"] = activityId, ["title"] = "Loop-back email", ["sourceData"] = new JsonObject { ["content"] = "policy email" } });
                continue;
            }

            if (ks.Kind == "web")
            {
                if (terms.Any(t => "public policy page".Contains(t, StringComparison.Ordinal)))
                {
                    references.Add(new JsonObject
                    {
                        ["type"] = "web",
                        ["id"] = (referenceId++).ToString(),
                        ["activitySource"] = activityId,
                        ["url"] = "https://www.example.org/public-policy",
                        ["title"] = "Public policy page",
                        ["sourceData"] = new JsonObject { ["snippet"] = "A public page about policy." },
                        ["rerankerScore"] = 1.0,
                    });
                }

                continue;
            }

            foreach (var doc in Indexes[ks.Index!].Where(d => user is null || d.VisibleTo(user)))
            {
                var hay = (doc.Title + " " + doc.Content).ToLowerInvariant();
                var score = terms.Count(t => hay.Contains(t, StringComparison.Ordinal));
                if (score == 0)
                {
                    continue;
                }

                var reference = new JsonObject
                {
                    ["type"] = ks.Kind,
                    ["id"] = (referenceId++).ToString(),
                    ["activitySource"] = activityId,
                    ["docKey"] = doc.Key,
                    ["rerankerScore"] = 2.0 + score,
                    ["sourceData"] = new JsonObject { ["title"] = doc.Title, ["content"] = doc.Content, ["url"] = doc.Url },
                };
                if (preview)
                {
                    reference["citationUrl"] = new Uri(Endpoint, $"indexes/{ks.Index}/docs/{Uri.EscapeDataString(doc.Key)}?api-version=2026-08-01-preview").AbsoluteUri;
                    if (doc.Label is not null)
                    {
                        reference["sensitivityLabelInfo"] = new JsonObject { ["displayName"] = doc.Label };
                    }
                }

                references.Add(reference);
            }
        }

        return Json(new JsonObject
        {
            ["response"] = new JsonArray(new JsonObject { ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "[]" }) }),
            ["activity"] = activity,
            ["references"] = references,
        });
    }

    private HttpResponseMessage IndexDefinition(string index) =>
        Indexes.ContainsKey(index)
            ? Json(new JsonObject
            {
                ["name"] = index,
                ["fields"] = new JsonArray(
                    new JsonObject { ["name"] = "id", ["type"] = "Edm.String", ["key"] = true },
                    new JsonObject { ["name"] = "title", ["type"] = "Edm.String", ["key"] = false },
                    new JsonObject { ["name"] = "content", ["type"] = "Edm.String", ["key"] = false }),
            })
            : Status(HttpStatusCode.NotFound);

    private HttpResponseMessage DocsSearch(string index, string body, string? user)
    {
        if (!Indexes.TryGetValue(index, out var docs))
        {
            return Status(HttpStatusCode.NotFound);
        }

        var request = JsonNode.Parse(body)!;
        var filter = request["filter"]?.GetValue<string>() ?? "";
        const string prefix = "id eq '";
        if (!filter.StartsWith(prefix, StringComparison.Ordinal) || !filter.EndsWith('\''))
        {
            return Status(HttpStatusCode.BadRequest);
        }

        var key = filter[prefix.Length..^1].Replace("''", "'", StringComparison.Ordinal);
        var match = docs.Where(d => d.Key == key && (user is null || d.VisibleTo(user))).Select(DocJson);
        return Json(new JsonObject { ["value"] = new JsonArray(match.Select(m => (JsonNode)m).ToArray()) });
    }

    private HttpResponseMessage CitationGet(string index, string key, string? user)
    {
        var doc = Indexes.TryGetValue(index, out var docs) ? docs.FirstOrDefault(d => d.Key == key) : null;
        if (doc is null || (CitationGetTrims && !doc.VisibleTo(user)))
        {
            return Status(HttpStatusCode.NotFound);
        }

        return Json(DocJson(doc));
    }

    private static JsonObject DocJson(Doc d) => new()
    {
        ["@search.score"] = 1.0,
        ["id"] = d.Key,
        ["title"] = d.Title,
        ["content"] = d.Content,
        ["url"] = d.Url,
        ["metadata_storage_last_modified"] = "2026-09-01T10:00:00Z",
        ["author"] = "Contoso HR",
    };

    private static HttpResponseMessage Json(JsonNode node) =>
        new(HttpStatusCode.OK) { Content = new StringContent(node.ToJsonString(), Encoding.UTF8, "application/json") };

    public static HttpResponseMessage Status(HttpStatusCode code, string? body = null) =>
        new(code) { Content = new StringContent(body ?? "{\"error\":{\"message\":\"upstream detail\"}}", Encoding.UTF8, "application/json") };
}

public sealed class FakeHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
}
