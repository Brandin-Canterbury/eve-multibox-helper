using System.Security.Cryptography;
using System.Text;

namespace EveMultibox.Esi;

/// <summary>PKCE for EVE SSO's native-app flow: a one-time verifier and its S256 challenge.</summary>
public static class Pkce
{
    public static (string Verifier, string Challenge) Create(byte[]? randomBytes = null)
    {
        randomBytes ??= RandomNumberGenerator.GetBytes(32);
        string verifier = Base64Url(randomBytes);
        return (verifier, ChallengeFor(verifier));
    }

    public static string ChallengeFor(string verifier) =>
        Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

    public static string Base64Url(byte[] data) =>
        Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static byte[] FromBase64Url(string s)
    {
        string b = s.Replace('-', '+').Replace('_', '/');
        b += (b.Length % 4) switch { 2 => "==", 3 => "=", _ => "" };
        return Convert.FromBase64String(b);
    }
}
