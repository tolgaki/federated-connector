using System.Text.Json;
using FederatedConnector.Host.Tools;
using ModelContextProtocol.Protocol;

namespace FederatedConnector.Host.Mcp;

/// <summary>The only place that touches the MCP SDK: maps the host's tool catalog and results onto MCP types.</summary>
public static class McpWiring
{
    public const string Route = "/mcp";

    public static void AddConnectorMcp(this IServiceCollection services, string displayName)
    {
        services.AddMcpServer(o =>
            {
                o.ServerInfo = new Implementation
                {
                    Name = "federated-connector",
                    Title = displayName,
                    Version = typeof(McpWiring).Assembly.GetName().Version?.ToString(3) ?? "0.1.0",
                };
                o.ServerInstructions =
                    $"Read-only access to {displayName}. Use `search` to find documents, then `fetch` with a result id to read one.";
            })
            .WithHttpTransport(o => o.Stateless = true)
            .WithListToolsHandler((ctx, _) =>
            {
                var catalog = RequestServices(ctx.Services).GetRequiredService<ToolCatalog>();
                return ValueTask.FromResult(new ListToolsResult { Tools = [.. catalog.Tools.Select(ToMcpTool)] });
            })
            .WithCallToolHandler(async (ctx, ct) =>
            {
                var service = RequestServices(ctx.Services).GetRequiredService<ConnectorToolService>();
                var result = await service.InvokeAsync(ctx.Params?.Name ?? "", ctx.Params?.Arguments, ct).ConfigureAwait(false);
                return ToMcpResult(result);
            });
    }

    public static Tool ToMcpTool(ToolDefinition t) => new()
    {
        Name = t.Name,
        Title = t.Title,
        Description = t.Description,
        InputSchema = t.InputSchema,
        Annotations = new ToolAnnotations
        {
            Title = t.Title,
            ReadOnlyHint = t.Hints.ReadOnly,
            IdempotentHint = t.Hints.Idempotent,
            OpenWorldHint = t.Hints.OpenWorld,
            DestructiveHint = t.Hints.Destructive,
        },
    };

    public static CallToolResult ToMcpResult(ToolResult r) => new()
    {
        Content = [new TextContentBlock { Text = r.Text }],
        StructuredContent = JsonSerializer.SerializeToElement(r.Structured),
        IsError = r.IsError ? true : null,
    };

    // Resolve per-request services from the HTTP request scope so scoped dependencies (token acquisition, user accessor)
    // see the current caller, regardless of how the SDK scopes its own handlers.
    private static IServiceProvider RequestServices(IServiceProvider? sdkServices)
    {
        if (sdkServices is null)
        {
            throw new InvalidOperationException("MCP request has no service provider.");
        }

        return sdkServices.GetService<IHttpContextAccessor>()?.HttpContext?.RequestServices ?? sdkServices;
    }
}
