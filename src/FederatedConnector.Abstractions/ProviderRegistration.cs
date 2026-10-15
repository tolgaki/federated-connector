using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace FederatedConnector.Abstractions;

/// <summary>
/// How a compiled-in provider plugs into the host. The host selects one registration per deployment by
/// <see cref="Name"/> (configuration key <c>Connector:Provider</c>) and passes it the section <c>Providers:{Name}</c>.
/// </summary>
/// <param name="Name">Configuration name, e.g. "FoundryIq".</param>
/// <param name="Describe">Builds the descriptor from the provider's configuration section. Called at startup,
/// before the container is built, so the host can configure inbound authentication.</param>
/// <param name="Register">Registers the provider's <see cref="IKnowledgeProvider"/> and its dependencies.</param>
public sealed record ProviderRegistration(
    string Name,
    Func<IConfiguration, ProviderDescriptor> Describe,
    Action<IServiceCollection, IConfiguration> Register);
