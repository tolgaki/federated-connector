using FederatedConnector.SmokeTest;

// Usage: FederatedConnector.SmokeTest --url https://host/mcp [--token <bearer>] [--query "text"] [--skip-search] [--require-results]
// The token may also come from the CONNECTOR_TOKEN environment variable (preferred: keeps it out of shell history).
string? url = null, token = Environment.GetEnvironmentVariable("CONNECTOR_TOKEN"), query = "policy";
bool skipSearch = false, requireResults = false;
var timeoutSeconds = 60;

for (var i = 0; i < args.Length; i++)
{
    string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} needs a value");
    switch (args[i])
    {
        case "--url": url = Next(); break;
        case "--token": token = Next(); break;
        case "--query": query = Next(); break;
        case "--skip-search": skipSearch = true; break;
        case "--require-results": requireResults = true; break;
        case "--timeout": timeoutSeconds = int.Parse(Next(), System.Globalization.CultureInfo.InvariantCulture); break;
        case "-h" or "--help":
            Console.WriteLine("FederatedConnector.SmokeTest --url <https://host/mcp> [--token <bearer>] [--query <text>] [--skip-search] [--require-results] [--timeout <s>]");
            return 0;
        default:
            Console.Error.WriteLine($"Unknown argument {args[i]}");
            return 2;
    }
}

if (url is null || !Uri.TryCreate(url, UriKind.Absolute, out var mcpUrl))
{
    Console.Error.WriteLine("--url is required (the connector's /mcp endpoint).");
    return 2;
}

if (mcpUrl.AbsolutePath is "/" or "")
{
    mcpUrl = new Uri(mcpUrl, "/mcp");
}

if (token is null && !skipSearch)
{
    Console.Error.WriteLine("No token (--token or CONNECTOR_TOKEN): search and fetch will fail unless the connector uses auth mode None.");
}

using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(timeoutSeconds) };
using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds * 2));
Console.WriteLine($"Smoke testing {mcpUrl}");
var ok = await SmokeRunner.RunAsync(http, new SmokeOptions(mcpUrl, token, query, skipSearch, requireResults), Console.Out, cts.Token);
Console.WriteLine(ok ? "Smoke test passed." : "Smoke test FAILED.");
return ok ? 0 : 1;
