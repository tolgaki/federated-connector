using System.Text.Json.Nodes;

namespace FederatedConnector.SmokeTest;

public sealed record SmokeOptions(Uri McpUrl, string? Token, string Query, bool SkipSearch, bool RequireResults);

/// <summary>
/// The smoke test: initialize, tools/list (assert title and readOnlyHint on every tool, and the fixed tool names),
/// list_collections, then search and fetch. Returns true when every check passes.
/// </summary>
public static class SmokeRunner
{
    private static readonly string[] RequiredTools = ["search", "fetch", "list_collections"];

    public static async Task<bool> RunAsync(HttpClient http, SmokeOptions options, TextWriter log, CancellationToken ct)
    {
        var client = new McpHttpClient(http, options.McpUrl, options.Token);
        var ok = true;

        async Task Check(string name, Func<Task<string>> step)
        {
            try
            {
                var detail = await step().ConfigureAwait(false);
                await log.WriteLineAsync($"PASS  {name}{(detail.Length > 0 ? " — " + detail : "")}").ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is SmokeException or HttpRequestException or TaskCanceledException or InvalidOperationException)
            {
                ok = false;
                await log.WriteLineAsync($"FAIL  {name} — {ex.Message}").ConfigureAwait(false);
            }
        }

        await Check("initialize", async () =>
        {
            var result = await client.RequestAsync("initialize", new JsonObject
            {
                ["protocolVersion"] = McpHttpClient.ProtocolVersion,
                ["capabilities"] = new JsonObject(),
                ["clientInfo"] = new JsonObject { ["name"] = "federated-connector-smoketest", ["version"] = "1.0.0" },
            }, ct).ConfigureAwait(false);
            await client.NotifyAsync("notifications/initialized", ct).ConfigureAwait(false);
            return $"{result["serverInfo"]?["name"]} {result["serverInfo"]?["version"]}, protocol {result["protocolVersion"]}";
        }).ConfigureAwait(false);
        if (!ok)
        {
            return false;
        }

        await Check("tools/list", async () =>
        {
            var result = await client.RequestAsync("tools/list", null, ct).ConfigureAwait(false);
            var tools = (result["tools"] as JsonArray ?? throw new SmokeException("no tools array")).OfType<JsonObject>().ToList();
            var names = tools.Select(t => t["name"]?.GetValue<string>()).ToList();
            foreach (var required in RequiredTools)
            {
                if (!names.Contains(required))
                {
                    throw new SmokeException($"missing tool '{required}'");
                }
            }

            foreach (var tool in tools)
            {
                var title = tool["title"]?.GetValue<string>() ?? tool["annotations"]?["title"]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(title))
                {
                    throw new SmokeException($"tool '{tool["name"]}' has no title");
                }

                if (tool["annotations"]?["readOnlyHint"]?.GetValue<bool>() != true)
                {
                    throw new SmokeException($"tool '{tool["name"]}' is not annotated readOnlyHint: true");
                }
            }

            return string.Join(", ", names);
        }).ConfigureAwait(false);

        await Check("list_collections", async () =>
        {
            var structured = await CallAsync(client, "list_collections", new JsonObject(), ct).ConfigureAwait(false);
            return $"{(structured["collections"] as JsonArray)?.Count ?? 0} collection(s)";
        }).ConfigureAwait(false);

        if (options.SkipSearch)
        {
            return ok;
        }

        string? firstId = null;
        await Check("search", async () =>
        {
            var structured = await CallAsync(client, "search", new JsonObject { ["query"] = options.Query, ["top"] = 3 }, ct).ConfigureAwait(false);
            var results = structured["results"] as JsonArray ?? throw new SmokeException("no results array");
            if (results.Count == 0 && options.RequireResults)
            {
                throw new SmokeException("no results (use a query the test user can see, or drop --require-results)");
            }

            firstId = results.Count > 0 ? results[0]?["id"]?.GetValue<string>() : null;
            return $"{results.Count} result(s)";
        }).ConfigureAwait(false);

        if (firstId is not null)
        {
            await Check("fetch", async () =>
            {
                var structured = await CallAsync(client, "fetch", new JsonObject { ["id"] = firstId }, ct).ConfigureAwait(false);
                var text = structured["text"]?.GetValue<string>() ?? throw new SmokeException("no text");
                return $"{text.Length} chars, truncated={structured["truncated"]}";
            }).ConfigureAwait(false);
        }
        else
        {
            await log.WriteLineAsync("SKIP  fetch — search returned no results").ConfigureAwait(false);
        }

        return ok;
    }

    private static async Task<JsonObject> CallAsync(McpHttpClient client, string tool, JsonObject args, CancellationToken ct)
    {
        var result = await client.RequestAsync("tools/call", new JsonObject { ["name"] = tool, ["arguments"] = args }, ct).ConfigureAwait(false);
        var structured = result["structuredContent"] as JsonObject;
        if (result["isError"]?.GetValue<bool>() == true)
        {
            throw new SmokeException($"tool error: {structured?["error"]?["category"]} — {structured?["error"]?["message"]}");
        }

        return structured ?? throw new SmokeException("no structuredContent");
    }
}
