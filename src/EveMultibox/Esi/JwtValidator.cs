using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace EveMultibox.Esi;

/// <summary>A signing key from EVE SSO's JWKS (https://login.eveonline.com/oauth/jwks).</summary>
public sealed record Jwk(string Kid, string Kty, string? Alg, string? N, string? E, string? Crv, string? X, string? Y);

public sealed record SsoCharacter(long CharacterId, string Name, DateTimeOffset Expires, IReadOnlyList<string> Scopes);

public sealed class JwtValidationException : Exception
{
    public JwtValidationException(string message) : base(message)
    {
    }
}

/// <summary>
/// Checks an EVE SSO access token: signature (RS256 or ES256) against the published keys,
/// issuer, audience (our client ID and "EVE Online") and expiry, then reads out the character.
/// </summary>
public static class JwtValidator
{
    private static readonly string[] Issuers = { "login.eveonline.com", "https://login.eveonline.com" };

    public static SsoCharacter Validate(string token, IEnumerable<Jwk> keys, string clientId, DateTimeOffset now)
    {
        var parts = token.Split('.');
        if (parts.Length != 3) throw new JwtValidationException("Not a JWT.");

        using var header = JsonDocument.Parse(Pkce.FromBase64Url(parts[0]));
        string alg = header.RootElement.GetProperty("alg").GetString() ?? "";
        string? kid = header.RootElement.TryGetProperty("kid", out var k) ? k.GetString() : null;

        var key = keys.FirstOrDefault(j => j.Kid == kid && (j.Alg is null || j.Alg == alg))
                  ?? throw new JwtValidationException($"No SSO signing key matches kid '{kid}'.");

        byte[] signed = Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]);
        byte[] signature = Pkce.FromBase64Url(parts[2]);
        if (!VerifySignature(alg, key, signed, signature)) throw new JwtValidationException("Bad signature.");

        using var payload = JsonDocument.Parse(Pkce.FromBase64Url(parts[1]));
        var p = payload.RootElement;

        string iss = p.GetProperty("iss").GetString() ?? "";
        if (!Issuers.Contains(iss)) throw new JwtValidationException($"Unexpected issuer '{iss}'.");

        var audiences = p.GetProperty("aud").ValueKind == JsonValueKind.Array
            ? p.GetProperty("aud").EnumerateArray().Select(a => a.GetString()).ToList()
            : new List<string?> { p.GetProperty("aud").GetString() };
        if (!audiences.Contains(clientId) || !audiences.Contains("EVE Online"))
            throw new JwtValidationException("Token wasn't issued for this app.");

        var expires = DateTimeOffset.FromUnixTimeSeconds(p.GetProperty("exp").GetInt64());
        if (expires <= now) throw new JwtValidationException("Token has expired.");

        // sub looks like "CHARACTER:EVE:2112345678".
        string sub = p.GetProperty("sub").GetString() ?? "";
        var subParts = sub.Split(':');
        if (subParts.Length != 3 || subParts[0] != "CHARACTER" || !long.TryParse(subParts[2], out long characterId))
            throw new JwtValidationException($"Unexpected subject '{sub}'.");

        var scopes = !p.TryGetProperty("scp", out var scp) ? new List<string>()
            : scp.ValueKind == JsonValueKind.Array ? scp.EnumerateArray().Select(s => s.GetString() ?? "").ToList()
            : new List<string> { scp.GetString() ?? "" };

        return new SsoCharacter(characterId, p.GetProperty("name").GetString() ?? $"Character {characterId}", expires, scopes);
    }

    private static bool VerifySignature(string alg, Jwk key, byte[] data, byte[] signature)
    {
        switch (alg)
        {
            case "RS256" when key.Kty == "RSA":
                using (var rsa = RSA.Create())
                {
                    rsa.ImportParameters(new RSAParameters { Modulus = Pkce.FromBase64Url(key.N!), Exponent = Pkce.FromBase64Url(key.E!) });
                    return rsa.VerifyData(data, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                }
            case "ES256" when key.Kty == "EC":
                using (var ec = ECDsa.Create(new ECParameters
                       {
                           Curve = ECCurve.NamedCurves.nistP256,
                           Q = new ECPoint { X = Pkce.FromBase64Url(key.X!), Y = Pkce.FromBase64Url(key.Y!) },
                       }))
                {
                    return ec.VerifyData(data, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
                }
            default:
                throw new JwtValidationException($"Unsupported signing algorithm '{alg}'.");
        }
    }
}
