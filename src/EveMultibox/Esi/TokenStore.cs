using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace EveMultibox.Esi;

public sealed record StoredToken(long CharacterId, string Name, string RefreshToken, string ClientId);

/// <summary>
/// Refresh tokens, one file per character, encrypted with Windows DPAPI for the current user.
/// Only this Windows account can decrypt them.
/// </summary>
public sealed class TokenStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("EveMultibox.TokenStore.v1");

    private readonly string _dir;

    public TokenStore(string dir)
    {
        _dir = dir;
        Directory.CreateDirectory(_dir);
    }

    public static string DefaultDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "EveMultibox", "tokens");

    public void Save(StoredToken token)
    {
        byte[] plain = JsonSerializer.SerializeToUtf8Bytes(token);
        byte[] cipher = ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser);
        string file = FileFor(token.CharacterId);
        File.WriteAllBytes(file + ".tmp", cipher);
        File.Move(file + ".tmp", file, overwrite: true);
    }

    public StoredToken? Load(long characterId)
    {
        string file = FileFor(characterId);
        if (!File.Exists(file)) return null;
        try
        {
            byte[] plain = ProtectedData.Unprotect(File.ReadAllBytes(file), Entropy, DataProtectionScope.CurrentUser);
            return JsonSerializer.Deserialize<StoredToken>(plain);
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException or IOException)
        {
            return null;
        }
    }

    public IEnumerable<StoredToken> LoadAll() =>
        Directory.EnumerateFiles(_dir, "*.token")
            .Select(f => long.TryParse(Path.GetFileNameWithoutExtension(f), out long id) ? Load(id) : null)
            .OfType<StoredToken>();

    public void Delete(long characterId)
    {
        string file = FileFor(characterId);
        if (File.Exists(file)) File.Delete(file);
    }

    private string FileFor(long characterId) => Path.Combine(_dir, $"{characterId}.token");
}
