namespace FederatedConnector.Abstractions;

/// <summary>The fixed error categories the host exposes to Copilot.</summary>
public enum ProviderErrorKind
{
    /// <summary>The user's credential is missing, expired or rejected by the source.</summary>
    Auth,
    /// <summary>The user is authenticated but not allowed.</summary>
    Forbidden,
    /// <summary>The item does not exist, is not visible to the user, or the handle is invalid.</summary>
    NotFound,
    /// <summary>The source failed or is unreachable.</summary>
    Unavailable,
    /// <summary>The source did not answer in time.</summary>
    Timeout,
}

/// <summary>
/// Thrown by providers for expected failures. <see cref="Exception.Message"/> is for logs only; the host replaces it
/// with a fixed, sanitised message per <see cref="Kind"/>. Never put source response bodies or secrets in it.
/// </summary>
public sealed class ProviderException : Exception
{
    public ProviderException(ProviderErrorKind kind, string logDetail, Exception? inner = null)
        : base(logDetail, inner) => Kind = kind;

    public ProviderErrorKind Kind { get; }

    /// <summary>HTTP status returned by the source, when there was one (for telemetry).</summary>
    public int? SourceStatus { get; init; }

    public static ProviderException InvalidHandle(string why) => new(ProviderErrorKind.NotFound, "Invalid handle payload: " + why);
}
