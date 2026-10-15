using FederatedConnector.Abstractions;
using FederatedConnector.Host.Auth;
using FederatedConnector.Host.Handles;

namespace FederatedConnector.Host.Tests;

public sealed class ManualTime(DateTimeOffset start) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = start;

    public override DateTimeOffset GetUtcNow() => Now;
}

public sealed class HandleCodecTests
{
    private static readonly byte[] Key = Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();
    private static readonly byte[] OtherKey = Enumerable.Range(100, 32).Select(i => (byte)i).ToArray();
    private static readonly UserIdentity Alice = new(UserHash.FromEntra("t1", "alice"), "t1");
    private static readonly UserIdentity Bob = new(UserHash.FromEntra("t1", "bob"), "t1");
    private static readonly HandlePayload Payload = new("kb1", new Dictionary<string, string> { ["docKey"] = "42", ["index"] = "idx" });

    private readonly ManualTime _time = new(new DateTimeOffset(2026, 10, 15, 12, 0, 0, TimeSpan.Zero));

    private HandleCodec Codec(byte[]? key = null, byte[]? previous = null) => new(key ?? Key, previous, _time, TimeSpan.FromHours(1));

    [Fact]
    public void Round_trip_returns_the_payload()
    {
        var codec = Codec();
        var id = codec.Encode("foundry-iq", Payload, Alice);

        Assert.StartsWith("v1.", id);
        Assert.Equal(HandleDecodeStatus.Valid, codec.TryDecode(id, "foundry-iq", Alice, out var decoded));
        Assert.Equal("kb1", decoded!.Collection);
        Assert.Equal(Payload.Data, decoded.Data);
    }

    [Fact]
    public void Handle_is_bound_to_the_user()
    {
        var codec = Codec();
        var id = codec.Encode("foundry-iq", Payload, Alice);
        Assert.Equal(HandleDecodeStatus.WrongUser, codec.TryDecode(id, "foundry-iq", Bob, out var decoded));
        Assert.Null(decoded);
    }

    [Fact]
    public void Handle_is_bound_to_the_provider()
    {
        var codec = Codec();
        var id = codec.Encode("foundry-iq", Payload, Alice);
        Assert.Equal(HandleDecodeStatus.WrongProvider, codec.TryDecode(id, "other", Alice, out _));
    }

    [Fact]
    public void Handle_expires()
    {
        var codec = Codec();
        var id = codec.Encode("foundry-iq", Payload, Alice);
        _time.Now = _time.Now.AddMinutes(59);
        Assert.Equal(HandleDecodeStatus.Valid, codec.TryDecode(id, "foundry-iq", Alice, out _));
        _time.Now = _time.Now.AddMinutes(2);
        Assert.Equal(HandleDecodeStatus.Expired, codec.TryDecode(id, "foundry-iq", Alice, out _));
    }

    [Fact]
    public void Tampered_body_fails_signature()
    {
        var codec = Codec();
        var id = codec.Encode("foundry-iq", Payload, Alice);
        var parts = id.Split('.');
        var forged = Codec().Encode("foundry-iq", Payload with { Data = new Dictionary<string, string> { ["docKey"] = "43" } }, Alice).Split('.')[1];
        Assert.Equal(HandleDecodeStatus.BadSignature, codec.TryDecode($"{parts[0]}.{forged}.{parts[2]}", "foundry-iq", Alice, out _));
    }

    [Fact]
    public void Handle_signed_with_another_key_is_rejected()
    {
        var id = Codec(OtherKey).Encode("foundry-iq", Payload, Alice);
        Assert.Equal(HandleDecodeStatus.BadSignature, Codec().TryDecode(id, "foundry-iq", Alice, out _));
    }

    [Fact]
    public void Previous_key_is_accepted_for_rotation()
    {
        var id = Codec(OtherKey).Encode("foundry-iq", Payload, Alice);
        Assert.Equal(HandleDecodeStatus.Valid, Codec(Key, OtherKey).TryDecode(id, "foundry-iq", Alice, out _));
    }

    [Theory]
    [InlineData("")]
    [InlineData("v1")]
    [InlineData("v1.abc")]
    [InlineData("v2.abc.def")]
    [InlineData("v1.!!!.???")]
    [InlineData("https://evil.example.com/doc")]
    public void Malformed_handles_are_rejected(string id)
    {
        Assert.Equal(HandleDecodeStatus.Malformed, Codec().TryDecode(id, "foundry-iq", Alice, out _));
    }

    [Fact]
    public void Oversized_handle_is_rejected()
    {
        Assert.Equal(HandleDecodeStatus.Malformed, Codec().TryDecode("v1." + new string('a', 5000) + ".x", "foundry-iq", Alice, out _));
    }

    [Fact]
    public void Short_key_is_refused()
    {
        Assert.Throws<ArgumentException>(() => new HandleCodec(new byte[16], null, TimeProvider.System, TimeSpan.FromHours(1)));
    }

    [Fact]
    public void User_hash_is_stable_and_case_insensitive()
    {
        Assert.Equal(UserHash.FromEntra("T1", "ALICE"), UserHash.FromEntra("t1", "alice"));
        Assert.NotEqual(UserHash.FromEntra("t1", "alice"), UserHash.FromEntra("t2", "alice"));
    }
}
