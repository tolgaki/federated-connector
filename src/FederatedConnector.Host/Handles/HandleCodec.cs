using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using FederatedConnector.Abstractions;

namespace FederatedConnector.Host.Handles;

public enum HandleDecodeStatus
{
    Valid,
    Malformed,
    BadSignature,
    Expired,
    WrongUser,
    WrongProvider,
}

/// <summary>
/// Encodes and verifies the result ids Copilot sees:
/// <c>v1.&lt;base64url({ p, c, d, u, exp })&gt;.&lt;base64url(HMAC-SHA256)&gt;</c>.
/// Handles are signed, not encrypted; they are bound to the provider id and to the user.
/// </summary>
public sealed class HandleCodec
{
    public const int MaxHandleLength = 4096;
    private const string Version = "v1";

    private readonly byte[] _key;
    private readonly byte[]? _previousKey;
    private readonly TimeProvider _time;
    private readonly TimeSpan _lifetime;

    public HandleCodec(byte[] key, byte[]? previousKey, TimeProvider time, TimeSpan lifetime)
    {
        if (key is null || key.Length < 32)
        {
            throw new ArgumentException("Handle signing key must be at least 32 bytes.", nameof(key));
        }

        _key = key;
        _previousKey = previousKey is { Length: >= 32 } ? previousKey : null;
        _time = time;
        _lifetime = lifetime;
    }

    public string Encode(string providerId, HandlePayload payload, UserIdentity user)
    {
        var body = new HandleBody
        {
            P = providerId,
            C = payload.Collection,
            D = new Dictionary<string, string>(payload.Data),
            U = user.HashedId,
            Exp = _time.GetUtcNow().Add(_lifetime).ToUnixTimeSeconds(),
        };
        var json = JsonSerializer.SerializeToUtf8Bytes(body, HandleJson.Compact.HandleBody);
        var encoded = Base64Url.EncodeToString(json);
        var signed = Version + "." + encoded;
        return signed + "." + Base64Url.EncodeToString(Sign(_key, signed));
    }

    public HandleDecodeStatus TryDecode(string id, string providerId, UserIdentity user, out HandlePayload? payload)
    {
        payload = null;
        if (string.IsNullOrEmpty(id) || id.Length > MaxHandleLength)
        {
            return HandleDecodeStatus.Malformed;
        }

        var parts = id.Split('.');
        if (parts.Length != 3 || parts[0] != Version)
        {
            return HandleDecodeStatus.Malformed;
        }

        byte[] signature;
        byte[] json;
        try
        {
            signature = Base64Url.DecodeFromChars(parts[2]);
            json = Base64Url.DecodeFromChars(parts[1]);
        }
        catch (FormatException)
        {
            return HandleDecodeStatus.Malformed;
        }

        var signed = parts[0] + "." + parts[1];
        if (!Verify(_key, signed, signature) && (_previousKey is null || !Verify(_previousKey, signed, signature)))
        {
            return HandleDecodeStatus.BadSignature;
        }

        HandleBody? body;
        try
        {
            body = JsonSerializer.Deserialize(json, HandleJson.Compact.HandleBody);
        }
        catch (JsonException)
        {
            return HandleDecodeStatus.Malformed;
        }

        if (body?.P is null || body.U is null || body.D is null)
        {
            return HandleDecodeStatus.Malformed;
        }

        if (body.Exp <= _time.GetUtcNow().ToUnixTimeSeconds())
        {
            return HandleDecodeStatus.Expired;
        }

        if (!FixedTimeEquals(body.U, user.HashedId))
        {
            return HandleDecodeStatus.WrongUser;
        }

        if (!string.Equals(body.P, providerId, StringComparison.Ordinal))
        {
            return HandleDecodeStatus.WrongProvider;
        }

        payload = new HandlePayload(body.C, body.D);
        return HandleDecodeStatus.Valid;
    }

    private static byte[] Sign(byte[] key, string data) => HMACSHA256.HashData(key, Encoding.ASCII.GetBytes(data));

    private static bool Verify(byte[] key, string data, byte[] signature) =>
        CryptographicOperations.FixedTimeEquals(Sign(key, data), signature);

    private static bool FixedTimeEquals(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));

    internal sealed class HandleBody
    {
        [JsonPropertyName("p")] public string? P { get; set; }
        [JsonPropertyName("c")] public string? C { get; set; }
        [JsonPropertyName("d")] public Dictionary<string, string>? D { get; set; }
        [JsonPropertyName("u")] public string? U { get; set; }
        [JsonPropertyName("exp")] public long Exp { get; set; }
    }
}

[JsonSerializable(typeof(HandleCodec.HandleBody))]
internal sealed partial class HandleJson : JsonSerializerContext
{
    // Non-ASCII stays as UTF-8 instead of 6-byte \uXXXX escapes, so handles for non-English content stay small.
    // Safe here: the JSON is base64url-encoded and never embedded in HTML or script.
    private static readonly JsonSerializerOptions Relaxed = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static HandleJson Compact { get; } = new(Relaxed);
}
