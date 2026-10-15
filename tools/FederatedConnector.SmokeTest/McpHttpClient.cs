using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace FederatedConnector.SmokeTest;

/// <summary>Minimal JSON-RPC client for MCP Streamable HTTP. Handles JSON and SSE responses and the session header.</summary>
public sealed class McpHttpClient(HttpClient http, Uri endpoint, string? bearerToken)
{
    public const string ProtocolVersion = "2025-06-18";

    private int _nextId;
    private string? _sessionId;
    private string? _negotiatedVersion;

    public async Task<JsonObject> RequestAsync(string method, JsonObject? parameters, CancellationToken ct)
    {
        var id = Interlocked.Increment(ref _nextId);
        var message = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method };
        if (parameters is not null)
        {
            message["params"] = parameters;
        }

        using var response = await PostAsync(message, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new SmokeException($"{method}: HTTP {(int)response.StatusCode} {response.ReasonPhrase}");
        }

        if (response.Headers.TryGetValues("Mcp-Session-Id", out var session))
        {
            _sessionId = session.FirstOrDefault();
        }

        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var reply = Parse(body, response.Content.Headers.ContentType?.MediaType, id)
            ?? throw new SmokeException($"{method}: no JSON-RPC response with id {id}.");
        if (reply["error"] is JsonObject error)
        {
            throw new SmokeException($"{method}: JSON-RPC error {error["code"]}: {error["message"]}");
        }

        var result = reply["result"] as JsonObject ?? throw new SmokeException($"{method}: response has no result.");
        if (method == "initialize")
        {
            _negotiatedVersion = result["protocolVersion"]?.GetValue<string>();
        }

        return result;
    }

    public async Task NotifyAsync(string method, CancellationToken ct)
    {
        var message = new JsonObject { ["jsonrpc"] = "2.0", ["method"] = method };
        using var response = await PostAsync(message, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new SmokeException($"{method}: HTTP {(int)response.StatusCode}");
        }
    }

    private Task<HttpResponseMessage> PostAsync(JsonObject message, CancellationToken ct)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(message.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        if (bearerToken is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
        }

        if (_sessionId is not null)
        {
            request.Headers.TryAddWithoutValidation("Mcp-Session-Id", _sessionId);
        }

        if (_negotiatedVersion is not null)
        {
            request.Headers.TryAddWithoutValidation("MCP-Protocol-Version", _negotiatedVersion);
        }

        return http.SendAsync(request, ct);
    }

    internal static JsonObject? Parse(string body, string? mediaType, int id)
    {
        IEnumerable<string> candidates = mediaType == "text/event-stream"
            ? body.Split('\n').Where(l => l.StartsWith("data:", StringComparison.Ordinal)).Select(l => l[5..].Trim())
            : [body];

        foreach (var json in candidates)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                continue;
            }

            JsonNode? node;
            try
            {
                node = JsonNode.Parse(json);
            }
            catch (JsonException)
            {
                continue;
            }

            if (node is JsonObject obj && obj["id"] is JsonValue v && v.TryGetValue<int>(out var got) && got == id)
            {
                return obj;
            }
        }

        return null;
    }
}

public sealed class SmokeException(string message) : Exception(message);
