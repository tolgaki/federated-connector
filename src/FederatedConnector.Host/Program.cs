using FederatedConnector.Host;
using FederatedConnector.Providers.FoundryIq;

var builder = WebApplication.CreateBuilder(args);

// Providers are compiled in and selected by Connector:Provider. Add new providers to this list.
builder.AddConnector([FoundryIqProvider.Registration]);

var app = builder.Build();
app.MapConnector();
app.Run();

public partial class Program;
