using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EveMultibox.Esi;

namespace EveMultibox.Tests;

public sealed class EsiAuthTests : IDisposable
{
    private const string ClientId = "test-client-id";
    private static readonly DateTimeOffset Now = new(2026, 10, 23, 12, 0, 0, TimeSpan.Zero);

    private readonly RSA _rsa = RSA.Create(2048);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "EveMultiboxTests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void PkceMatchesRfc7636Example()
    {
        // RFC 7636 appendix B.
        Assert.Equal("E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM", Pkce.ChallengeFor("dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk"));

        var (verifier, challenge) = Pkce.Create();
        Assert.Equal(43, verifier.Length); // 32 bytes, base64url without padding.
        Assert.DoesNotContain('=', challenge);
    }

    [Fact]
    public void ValidTokenGivesCharacter()
    {
        var c = JwtValidator.Validate(MakeToken(), Keys(), ClientId, Now);
        Assert.Equal(2112345678, c.CharacterId);
        Assert.Equal("The Pheonix", c.Name);
        Assert.Contains("esi-location.read_location.v1", c.Scopes);
    }

    [Fact]
    public void AcceptsBothIssuerForms() =>
        Assert.Equal(2112345678, JwtValidator.Validate(MakeToken(iss: "https://login.eveonline.com"), Keys(), ClientId, Now).CharacterId);

    [Theory]
    [InlineData("other-app")]
    [InlineData(null)]
    public void RejectsTokenForAnotherApp(string? audience)
    {
        string token = MakeToken(aud: audience is null ? new[] { "EVE Online" } : new[] { audience, "EVE Online" });
        Assert.Throws<JwtValidationException>(() => JwtValidator.Validate(token, Keys(), ClientId, Now));
    }

    [Fact]
    public void RejectsExpiredToken() =>
        Assert.Throws<JwtValidationException>(() => JwtValidator.Validate(MakeToken(exp: Now.AddSeconds(-1)), Keys(), ClientId, Now));

    [Fact]
    public void RejectsWrongIssuer() =>
        Assert.Throws<JwtValidationException>(() => JwtValidator.Validate(MakeToken(iss: "evil.example.com"), Keys(), ClientId, Now));

    [Fact]
    public void RejectsTamperedPayload()
    {
        var parts = MakeToken().Split('.');
        string forged = Pkce.Base64Url(Encoding.UTF8.GetBytes(
            Encoding.UTF8.GetString(Pkce.FromBase64Url(parts[1])).Replace("2112345678", "2100000001")));
        Assert.Throws<JwtValidationException>(() => JwtValidator.Validate($"{parts[0]}.{forged}.{parts[2]}", Keys(), ClientId, Now));
    }

    [Fact]
    public void RejectsUnknownKey()
    {
        using var other = RSA.Create(2048);
        var p = other.ExportParameters(false);
        var keys = new[] { new Jwk("JWT-Signature-Key", "RSA", "RS256", Pkce.Base64Url(p.Modulus!), Pkce.Base64Url(p.Exponent!), null, null, null) };
        Assert.Throws<JwtValidationException>(() => JwtValidator.Validate(MakeToken(), keys, ClientId, Now));
    }

    [Fact]
    public void VerifiesEs256Tokens()
    {
        using var ec = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var q = ec.ExportParameters(false).Q;
        var keys = new[] { new Jwk("JWT-Signature-Key-ES", "EC", "ES256", null, null, "P-256", Pkce.Base64Url(q.X!), Pkce.Base64Url(q.Y!)) };

        string header = Pkce.Base64Url(JsonSerializer.SerializeToUtf8Bytes(new { alg = "ES256", kid = "JWT-Signature-Key-ES", typ = "JWT" }));
        string payload = Pkce.Base64Url(JsonSerializer.SerializeToUtf8Bytes(Payload("login.eveonline.com", new[] { ClientId, "EVE Online" }, Now.AddMinutes(20))));
        byte[] sig = ec.SignData(Encoding.ASCII.GetBytes(header + "." + payload), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);

        Assert.Equal("The Pheonix", JwtValidator.Validate($"{header}.{payload}.{Pkce.Base64Url(sig)}", keys, ClientId, Now).Name);
    }

    [Fact]
    public void TokenStoreEncryptsAndRoundTrips()
    {
        var store = new TokenStore(_dir);
        store.Save(new StoredToken(2112345678, "The Pheonix", "refresh-secret-123", ClientId));
        store.Save(new StoredToken(2112345679, "Kayla Rockbreaker", "refresh-secret-456", ClientId));

        // The refresh token must not be readable on disk.
        string raw = File.ReadAllText(Path.Combine(_dir, "2112345678.token"), Encoding.Latin1);
        Assert.DoesNotContain("refresh-secret-123", raw);

        Assert.Equal("refresh-secret-123", store.Load(2112345678)!.RefreshToken);
        Assert.Equal(2, store.LoadAll().Count());

        store.Delete(2112345678);
        Assert.Null(store.Load(2112345678));
        Assert.Single(store.LoadAll());
    }

    private Jwk[] Keys()
    {
        var p = _rsa.ExportParameters(false);
        return new[] { new Jwk("JWT-Signature-Key", "RSA", "RS256", Pkce.Base64Url(p.Modulus!), Pkce.Base64Url(p.Exponent!), null, null, null) };
    }

    private static object Payload(string iss, string[] aud, DateTimeOffset exp) => new
    {
        scp = new[] { "esi-location.read_location.v1", "esi-ui.write_waypoint.v1" },
        jti = "abc",
        kid = "JWT-Signature-Key",
        sub = "CHARACTER:EVE:2112345678",
        azp = ClientId,
        tenant = "tranquility",
        tier = "live",
        region = "world",
        aud,
        name = "The Pheonix",
        owner = "owner-hash",
        exp = exp.ToUnixTimeSeconds(),
        iat = Now.ToUnixTimeSeconds(),
        iss,
    };

    private string MakeToken(string iss = "login.eveonline.com", string[]? aud = null, DateTimeOffset? exp = null)
    {
        string header = Pkce.Base64Url(JsonSerializer.SerializeToUtf8Bytes(new { alg = "RS256", kid = "JWT-Signature-Key", typ = "JWT" }));
        string payload = Pkce.Base64Url(JsonSerializer.SerializeToUtf8Bytes(Payload(iss, aud ?? new[] { ClientId, "EVE Online" }, exp ?? Now.AddMinutes(20))));
        byte[] sig = _rsa.SignData(Encoding.ASCII.GetBytes(header + "." + payload), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return $"{header}.{payload}.{Pkce.Base64Url(sig)}";
    }

    public void Dispose()
    {
        _rsa.Dispose();
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }
}
