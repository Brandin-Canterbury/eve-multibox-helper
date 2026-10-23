using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace EveMultibox.Esi;

public sealed class EsiException : Exception
{
    public EsiException(HttpStatusCode status, string path, string? body)
        : base($"ESI {(int)status} for {path}: {Trim(body)}")
    {
        Status = status;
    }

    public HttpStatusCode Status { get; }

    private static string Trim(string? s) => s is null ? "" : s.Length > 300 ? s[..300] + "…" : s;
}

/// <summary>
/// Talks to ESI (https://esi.evetech.net). Every response is cached by URL using ESI's own
/// Expires and ETag headers, so repeat requests are free until the data changes. It follows
/// X-Pages pagination and backs off on rate and error limits instead of ever hitting them hard.
/// </summary>
public sealed class EsiClient
{
    public const string BaseUrl = "https://esi.evetech.net/";

    /// <summary>ESI versions the whole API by date. This is the date the helper was built and tested against.</summary>
    public const string CompatibilityDate = "2026-10-20";

    public const string UserAgent = "EveMultiboxHelper/0.3 (personal desktop tool)";

    internal static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
    };

    private const int LowRateLimitTokens = 30;
    private const int LowErrorLimit = 10;

    private readonly HttpClient _http;
    private readonly string? _cacheDir;
    private readonly Func<DateTimeOffset> _clock;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly ConcurrentDictionary<string, CacheEntry> _memory = new();
    private readonly SemaphoreSlim _parallel = new(4);

    /// <param name="handler">Swap in a fake for tests.</param>
    /// <param name="cacheDir">Where cached responses live; null keeps them in memory only.</param>
    public EsiClient(HttpMessageHandler? handler = null, string? cacheDir = null,
        Func<DateTimeOffset>? clock = null, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _http = new HttpClient(handler ?? new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All })
        {
            BaseAddress = new Uri(BaseUrl),
            Timeout = TimeSpan.FromSeconds(60),
        };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        _http.DefaultRequestHeaders.Add("X-Compatibility-Date", CompatibilityDate);
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        _cacheDir = cacheDir;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _delay = delay ?? Task.Delay;
        if (_cacheDir is not null) Directory.CreateDirectory(_cacheDir);
    }

    public static string DefaultCacheDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EveMultibox", "esi-cache");

    /// <summary>Requests left in each rate-limit group, as last reported by ESI.</summary>
    public ConcurrentDictionary<string, int> RateLimitRemaining { get; } = new();

    public async Task<T> GetAsync<T>(string path, string? accessToken = null, CancellationToken ct = default)
    {
        var (body, _) = await GetRawAsync(path, accessToken, ct).ConfigureAwait(false);
        return JsonSerializer.Deserialize<T>(body, Json) ?? throw new EsiException(HttpStatusCode.OK, path, "empty body");
    }

    /// <summary>Fetches every page of a paged route (page 1 says how many there are in X-Pages).</summary>
    public async Task<List<T>> GetAllPagesAsync<T>(string path, string? accessToken = null, CancellationToken ct = default)
    {
        string sep = path.Contains('?') ? "&" : "?";
        var (first, pages) = await GetRawAsync($"{path}{sep}page=1", accessToken, ct).ConfigureAwait(false);
        var result = JsonSerializer.Deserialize<List<T>>(first, Json) ?? new List<T>();
        if (pages <= 1) return result;

        var rest = await Task.WhenAll(Enumerable.Range(2, pages - 1).Select(async page =>
        {
            await _parallel.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var (body, _) = await GetRawAsync($"{path}{sep}page={page}", accessToken, ct).ConfigureAwait(false);
                return JsonSerializer.Deserialize<List<T>>(body, Json) ?? new List<T>();
            }
            finally
            {
                _parallel.Release();
            }
        })).ConfigureAwait(false);

        foreach (var list in rest) result.AddRange(list);
        return result;
    }

    public async Task<TResult> PostAsync<TBody, TResult>(string path, TBody body, string? accessToken = null, CancellationToken ct = default)
    {
        string json = JsonSerializer.Serialize(body, Json);
        string response = await SendAsync(HttpMethod.Post, path, accessToken, new StringContent(json, Encoding.UTF8, "application/json"), ct).ConfigureAwait(false);
        return JsonSerializer.Deserialize<TResult>(response, Json)!;
    }

    /// <summary>POST with no body or response (e.g. setting an autopilot waypoint).</summary>
    public Task PostAsync(string path, string accessToken, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Post, path, accessToken, null, ct);

    private async Task<string> SendAsync(HttpMethod method, string path, string? accessToken, HttpContent? content, CancellationToken ct)
    {
        for (int attempt = 0; ; attempt++)
        {
            using var request = new HttpRequestMessage(method, path) { Content = content };
            if (accessToken is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            await ObserveLimitsAsync(response, ct).ConfigureAwait(false);
            string text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            if (response.IsSuccessStatusCode) return text;
            if (await ShouldRetryAsync(response, attempt, ct).ConfigureAwait(false)) continue;
            throw new EsiException(response.StatusCode, path, text);
        }
    }

    /// <returns>Body and X-Pages (1 if absent).</returns>
    /// <summary>
    /// When ESI's copy of an already-fetched response expires, i.e. when asking again could give
    /// newer data. E.g. a character's orders refresh about every 20 minutes.
    /// </summary>
    public DateTimeOffset? ExpiresOf(string path, string? accessToken = null) =>
        _memory.TryGetValue(CacheKey(path, accessToken), out var entry) ? entry.Expires : null;

    internal async Task<(string Body, int Pages)> GetRawAsync(string path, string? accessToken, CancellationToken ct)
    {
        string key = CacheKey(path, accessToken);
        var cached = Load(key);
        if (cached is not null && cached.Expires > _clock()) return (cached.Body, cached.Pages);

        for (int attempt = 0; ; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            if (accessToken is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            if (cached?.ETag is not null) request.Headers.TryAddWithoutValidation("If-None-Match", cached.ETag);

            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            await ObserveLimitsAsync(response, ct).ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.NotModified && cached is not null)
            {
                cached = cached with { Expires = ExpiresOf(response) };
                Store(key, cached);
                return (cached.Body, cached.Pages);
            }

            if (response.IsSuccessStatusCode)
            {
                string body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                int pages = response.Headers.TryGetValues("X-Pages", out var p) && int.TryParse(p.FirstOrDefault(), out int n) ? n : 1;
                string? etag = response.Headers.ETag?.ToString();
                Store(key, new CacheEntry(body, pages, etag, ExpiresOf(response)));
                return (body, pages);
            }

            if (await ShouldRetryAsync(response, attempt, ct).ConfigureAwait(false)) continue;
            throw new EsiException(response.StatusCode, path, await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        }
    }

    /// <summary>Waits out 429/420 and retries transient 5xx a couple of times.</summary>
    private async Task<bool> ShouldRetryAsync(HttpResponseMessage response, int attempt, CancellationToken ct)
    {
        if (attempt >= 3) return false;
        int status = (int)response.StatusCode;

        if (status == 429)
        {
            var wait = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(5);
            await _delay(wait, ct).ConfigureAwait(false);
            return true;
        }
        if (status == 420)
        {
            await _delay(TimeSpan.FromSeconds(HeaderInt(response, "X-ESI-Error-Limit-Reset") ?? 60), ct).ConfigureAwait(false);
            return true;
        }
        if (status is 502 or 503 or 504)
        {
            await _delay(TimeSpan.FromSeconds(2 << attempt), ct).ConfigureAwait(false);
            return true;
        }
        return false;
    }

    /// <summary>Slows down before a limit is reached rather than after.</summary>
    private async Task ObserveLimitsAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.Headers.TryGetValues("X-Ratelimit-Group", out var g) && HeaderInt(response, "X-Ratelimit-Remaining") is int remaining)
        {
            RateLimitRemaining[g.First()] = remaining;
            if (remaining < LowRateLimitTokens) await _delay(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
        }
        if (HeaderInt(response, "X-ESI-Error-Limit-Remain") is int errorsLeft && errorsLeft < LowErrorLimit)
            await _delay(TimeSpan.FromSeconds(HeaderInt(response, "X-ESI-Error-Limit-Reset") ?? 30), ct).ConfigureAwait(false);
    }

    private static int? HeaderInt(HttpResponseMessage r, string name) =>
        r.Headers.TryGetValues(name, out var v) && int.TryParse(v.FirstOrDefault(), out int n) ? n : null;

    private DateTimeOffset ExpiresOf(HttpResponseMessage r) =>
        r.Content.Headers.Expires ?? r.Headers.Date?.AddSeconds(60) ?? _clock().AddSeconds(60);

    // ---- cache ----

    internal sealed record CacheEntry(string Body, int Pages, string? ETag, DateTimeOffset Expires);

    // Character routes already carry the character ID in the path, so the path is a unique key.
    // They're prefixed so they are kept in memory only: private data never goes to the disk cache.
    private const string AuthPrefix = "auth|";

    private static string CacheKey(string path, string? accessToken) =>
        accessToken is null ? path : AuthPrefix + path;

    private CacheEntry? Load(string key)
    {
        if (_memory.TryGetValue(key, out var hit)) return hit;
        if (_cacheDir is null || key.StartsWith(AuthPrefix, StringComparison.Ordinal)) return null;
        string file = FileFor(key);
        if (!File.Exists(file)) return null;
        try
        {
            var entry = JsonSerializer.Deserialize<CacheEntry>(File.ReadAllText(file));
            if (entry is not null) _memory[key] = entry;
            return entry;
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            return null;
        }
    }

    private void Store(string key, CacheEntry entry)
    {
        _memory[key] = entry;
        if (_cacheDir is null || key.StartsWith(AuthPrefix, StringComparison.Ordinal)) return;
        // Order books are huge (The Forge is ~400 pages) and expire in minutes: memory only.
        if (key.Contains("/orders", StringComparison.Ordinal)) return;
        try
        {
            File.WriteAllText(FileFor(key), JsonSerializer.Serialize(entry));
        }
        catch (IOException)
        {
            // The disk cache is only an optimisation.
        }
    }

    private string FileFor(string key) =>
        Path.Combine(_cacheDir!, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..32] + ".json");
}
