using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using EveMultibox.Config;

namespace EveMultibox.Esi;

public sealed class SignInRequiredException : Exception
{
    public SignInRequiredException(string message) : base(message)
    {
    }
}

/// <summary>
/// EVE SSO sign-in with the native PKCE flow. The user signs in on CCP's own page in their
/// browser; this app only ever sees the resulting tokens, never a password. Refresh tokens are
/// rotated on every refresh and kept encrypted in <see cref="TokenStore"/>.
/// </summary>
public sealed class EsiAuth
{
    public const string AuthorizeUrl = "https://login.eveonline.com/v2/oauth/authorize/";
    public const string TokenUrl = "https://login.eveonline.com/v2/oauth/token";
    public const string JwksUrl = "https://login.eveonline.com/oauth/jwks";

    /// <summary>Everything the trade module needs, requested once (changing scopes later forces a re-login).</summary>
    public static readonly string[] Scopes =
    {
        "esi-location.read_location.v1",
        "esi-location.read_ship_type.v1",
        "esi-location.read_online.v1",
        "esi-assets.read_assets.v1",
        "esi-skills.read_skills.v1",
        "esi-characters.read_standings.v1",
        "esi-wallet.read_character_wallet.v1",
        "esi-ui.write_waypoint.v1",
        "esi-markets.read_character_orders.v1",
    };

    private sealed record TokenResponse(
        [property: JsonPropertyName("access_token")] string AccessToken,
        [property: JsonPropertyName("expires_in")] int ExpiresIn,
        [property: JsonPropertyName("refresh_token")] string RefreshToken);

    private sealed record JwksDocument([property: JsonPropertyName("keys")] List<JwkJson> Keys);

    private sealed record JwkJson(
        [property: JsonPropertyName("kid")] string Kid,
        [property: JsonPropertyName("kty")] string Kty,
        [property: JsonPropertyName("alg")] string? Alg,
        [property: JsonPropertyName("n")] string? N,
        [property: JsonPropertyName("e")] string? E,
        [property: JsonPropertyName("crv")] string? Crv,
        [property: JsonPropertyName("x")] string? X,
        [property: JsonPropertyName("y")] string? Y);

    private readonly HttpClient _http;
    private readonly TokenStore _store;
    private readonly Func<TradeSettings> _settings;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<long, (string Token, DateTimeOffset Expires)> _access = new();
    private readonly SemaphoreSlim _refreshLock = new(1);
    private List<Jwk>? _jwks;
    private DateTimeOffset _jwksFetched;

    public EsiAuth(TokenStore store, Func<TradeSettings> settings, HttpMessageHandler? handler = null)
    {
        _store = store;
        _settings = settings;
        _http = new HttpClient(handler ?? new HttpClientHandler()) { Timeout = TimeSpan.FromSeconds(30) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(EsiClient.UserAgent);
    }

    public event Action? CharactersChanged;

    private string ClientId => _settings().ClientId?.Trim() is { Length: > 0 } id
        ? id
        : throw new SignInRequiredException("Add your EVE app's Client ID in ☰ → Settings → Trade first.");

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_settings().ClientId);

    /// <summary>Characters signed in with the current Client ID.</summary>
    public IReadOnlyList<StoredToken> Characters =>
        IsConfigured ? _store.LoadAll().Where(t => t.ClientId == ClientId).OrderBy(t => t.Name).ToList() : new List<StoredToken>();

    public string BuildAuthorizeUrl(string challenge, string state)
    {
        var s = _settings();
        return AuthorizeUrl +
               "?response_type=code" +
               "&redirect_uri=" + Uri.EscapeDataString(s.CallbackUrl) +
               "&client_id=" + Uri.EscapeDataString(ClientId) +
               "&scope=" + Uri.EscapeDataString(string.Join(' ', Scopes)) +
               "&code_challenge=" + challenge +
               "&code_challenge_method=S256" +
               "&state=" + state;
    }

    /// <summary>Opens CCP's sign-in page and waits (up to 5 minutes) for the browser to come back.</summary>
    public async Task<SsoCharacter> SignInAsync(CancellationToken ct = default)
    {
        string clientId = ClientId;
        var (verifier, challenge) = Pkce.Create();
        string state = Pkce.Base64Url(RandomNumberGenerator.GetBytes(16));

        using var listener = new HttpListener();
        listener.Prefixes.Add(_settings().CallbackUrl);
        try
        {
            listener.Start();
        }
        catch (HttpListenerException ex)
        {
            throw new SignInRequiredException($"Couldn't listen on {_settings().CallbackUrl} ({ex.Message}). Try another callback port in settings, and update it on your EVE app too.");
        }

        Process.Start(new ProcessStartInfo(BuildAuthorizeUrl(challenge, state)) { UseShellExecute = true });

        HttpListenerContext context;
        try
        {
            context = await listener.GetContextAsync().WaitAsync(TimeSpan.FromMinutes(5), ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            throw new SignInRequiredException("Sign-in timed out. Try again.");
        }

        string? code = context.Request.QueryString["code"];
        bool ok = code is not null && context.Request.QueryString["state"] == state;
        await RespondAsync(context.Response, ok).ConfigureAwait(false);
        if (!ok) throw new SignInRequiredException("Sign-in was cancelled or didn't match this request.");

        var tokens = await PostTokenAsync(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code!,
            ["client_id"] = clientId,
            ["code_verifier"] = verifier,
        }, ct).ConfigureAwait(false);

        var character = await ValidateAsync(tokens.AccessToken, clientId, ct).ConfigureAwait(false);
        _store.Save(new StoredToken(character.CharacterId, character.Name, tokens.RefreshToken, clientId));
        _access[character.CharacterId] = (tokens.AccessToken, character.Expires);
        CharactersChanged?.Invoke();
        return character;
    }

    /// <summary>A valid access token for the character, refreshing it if it's about to expire.</summary>
    public async Task<string> GetAccessTokenAsync(long characterId, CancellationToken ct = default)
    {
        if (_access.TryGetValue(characterId, out var cached) && cached.Expires > DateTimeOffset.UtcNow.AddMinutes(1))
            return cached.Token;

        await _refreshLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_access.TryGetValue(characterId, out cached) && cached.Expires > DateTimeOffset.UtcNow.AddMinutes(1))
                return cached.Token;

            var stored = _store.Load(characterId) ?? throw new SignInRequiredException("That character isn't signed in.");
            TokenResponse tokens;
            try
            {
                tokens = await PostTokenAsync(new Dictionary<string, string>
                {
                    ["grant_type"] = "refresh_token",
                    ["refresh_token"] = stored.RefreshToken,
                    ["client_id"] = stored.ClientId,
                }, ct).ConfigureAwait(false);
            }
            catch (EsiException ex) when (ex.Status is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized)
            {
                // Revoked, expired, or invalidated by a scope change: the user has to sign in again.
                SignOut(characterId);
                throw new SignInRequiredException($"{stored.Name} needs to sign in again.");
            }

            var character = await ValidateAsync(tokens.AccessToken, stored.ClientId, ct).ConfigureAwait(false);
            // Native apps get a new refresh token each time; the old one stops working.
            _store.Save(stored with { RefreshToken = tokens.RefreshToken, Name = character.Name });
            _access[characterId] = (tokens.AccessToken, character.Expires);
            return tokens.AccessToken;
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    public void SignOut(long characterId)
    {
        _store.Delete(characterId);
        _access.TryRemove(characterId, out _);
        CharactersChanged?.Invoke();
    }

    private async Task<TokenResponse> PostTokenAsync(Dictionary<string, string> form, CancellationToken ct)
    {
        using var response = await _http.PostAsync(TokenUrl, new FormUrlEncodedContent(form), ct).ConfigureAwait(false);
        string body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw new EsiException(response.StatusCode, "SSO token", body);
        return JsonSerializer.Deserialize<TokenResponse>(body) ?? throw new EsiException(response.StatusCode, "SSO token", "empty");
    }

    private async Task<SsoCharacter> ValidateAsync(string accessToken, string clientId, CancellationToken ct)
    {
        try
        {
            return JwtValidator.Validate(accessToken, await GetJwksAsync(false, ct).ConfigureAwait(false), clientId, DateTimeOffset.UtcNow);
        }
        catch (JwtValidationException) when (DateTimeOffset.UtcNow - _jwksFetched > TimeSpan.FromSeconds(30))
        {
            // CCP may have rotated keys since we last fetched them; refetch once and retry.
            return JwtValidator.Validate(accessToken, await GetJwksAsync(true, ct).ConfigureAwait(false), clientId, DateTimeOffset.UtcNow);
        }
    }

    private async Task<List<Jwk>> GetJwksAsync(bool force, CancellationToken ct)
    {
        if (!force && _jwks is not null && DateTimeOffset.UtcNow - _jwksFetched < TimeSpan.FromHours(6)) return _jwks;
        string json = await _http.GetStringAsync(JwksUrl, ct).ConfigureAwait(false);
        var doc = JsonSerializer.Deserialize<JwksDocument>(json) ?? new JwksDocument(new());
        _jwks = doc.Keys.Select(k => new Jwk(k.Kid, k.Kty, k.Alg, k.N, k.E, k.Crv, k.X, k.Y)).ToList();
        _jwksFetched = DateTimeOffset.UtcNow;
        return _jwks;
    }

    private static async Task RespondAsync(HttpListenerResponse response, bool ok)
    {
        string message = ok
            ? "Signed in. You can close this tab and go back to EVE Multibox Helper."
            : "Sign-in didn't complete. Close this tab and try again from the helper.";
        byte[] html = Encoding.UTF8.GetBytes(
            "<!doctype html><meta charset=utf-8><title>EVE Multibox Helper</title>" +
            "<body style=\"background:#1e1e22;color:#dddde4;font:16px Segoe UI,sans-serif;display:grid;place-items:center;height:90vh\">" +
            $"<p>{WebUtility.HtmlEncode(message)}</p></body>");
        response.ContentType = "text/html; charset=utf-8";
        response.ContentLength64 = html.Length;
        await response.OutputStream.WriteAsync(html).ConfigureAwait(false);
        response.Close();
    }
}
