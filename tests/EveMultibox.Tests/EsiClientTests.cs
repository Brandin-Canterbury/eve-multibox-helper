using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using EveMultibox.Esi;

namespace EveMultibox.Tests;

public sealed class EsiClientTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "EveMultiboxTests", Guid.NewGuid().ToString("N"));
    private DateTimeOffset _now = new(2026, 10, 23, 12, 0, 0, TimeSpan.Zero);
    private readonly List<TimeSpan> _delays = new();

    private sealed class FakeHandler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, HttpResponseMessage> Respond { get; set; } = _ => new HttpResponseMessage(HttpStatusCode.NotFound);
        public List<HttpRequestMessage> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request);
            return Task.FromResult(Respond(request));
        }
    }

    private EsiClient Client(FakeHandler handler, bool disk = false) =>
        new(handler, disk ? _dir : null, () => _now, (d, _) => { _delays.Add(d); return Task.CompletedTask; });

    private HttpResponseMessage Json(string body, int maxAgeSeconds = 300, string? etag = null, int? pages = null)
    {
        var r = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        r.Content.Headers.Expires = _now.AddSeconds(maxAgeSeconds);
        if (etag is not null) r.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue(etag);
        if (pages is not null) r.Headers.Add("X-Pages", pages.ToString());
        return r;
    }

    private sealed record Thing(int TypeId, double Price);

    [Fact]
    public async Task SendsIdentifyingHeadersAndParsesSnakeCase()
    {
        var h = new FakeHandler { Respond = _ => Json("""{"type_id": 34, "price": 4.5}""") };
        var thing = await Client(h).GetAsync<Thing>("markets/test");

        Assert.Equal(new Thing(34, 4.5), thing);
        var req = Assert.Single(h.Requests);
        Assert.Equal("https://esi.evetech.net/markets/test", req.RequestUri!.ToString());
        Assert.Contains("EveMultiboxHelper", req.Headers.UserAgent.ToString());
        Assert.Equal(EsiClient.CompatibilityDate, req.Headers.GetValues("X-Compatibility-Date").Single());
    }

    [Fact]
    public async Task ServesFromCacheUntilExpiresThenRevalidatesWithEtag()
    {
        int calls = 0;
        var h = new FakeHandler();
        h.Respond = req =>
        {
            calls++;
            if (req.Headers.IfNoneMatch.Count > 0) return new HttpResponseMessage(HttpStatusCode.NotModified) { Content = new StringContent("") };
            return Json("""{"type_id": 1, "price": 2}""", maxAgeSeconds: 300, etag: "\"v1\"");
        };
        var esi = Client(h);

        await esi.GetAsync<Thing>("x");
        await esi.GetAsync<Thing>("x");
        Assert.Equal(1, calls); // Second read came from cache.

        _now = _now.AddMinutes(6);
        var again = await esi.GetAsync<Thing>("x");
        Assert.Equal(2, calls);
        Assert.Equal("\"v1\"", h.Requests[^1].Headers.IfNoneMatch.Single().ToString());
        Assert.Equal(new Thing(1, 2), again); // 304 reused the cached body.
    }

    [Fact]
    public async Task PublicResponsesPersistToDiskButPrivateOnesDont()
    {
        var h = new FakeHandler { Respond = _ => Json("""{"type_id": 1, "price": 2}""") };
        await Client(h, disk: true).GetAsync<Thing>("public/route");
        await Client(h, disk: true).GetAsync<Thing>("characters/1/wallet", accessToken: "tok");
        Assert.Single(Directory.GetFiles(_dir));

        // A fresh client reads the public one from disk without a request.
        h.Requests.Clear();
        await Client(h, disk: true).GetAsync<Thing>("public/route");
        Assert.Empty(h.Requests);
    }

    [Fact]
    public async Task FollowsXPages()
    {
        var h = new FakeHandler
        {
            Respond = req =>
            {
                string page = System.Web.HttpUtility.ParseQueryString(req.RequestUri!.Query)["page"]!;
                return Json($$"""[{"type_id": {{page}}, "price": 1}]""", pages: 3);
            },
        };
        var all = await Client(h).GetAllPagesAsync<Thing>("markets/10000002/orders?order_type=all");
        Assert.Equal(new[] { 1, 2, 3 }, all.Select(t => t.TypeId).Order());
        Assert.All(h.Requests, r => Assert.Contains("order_type=all&page=", r.RequestUri!.Query));
    }

    [Fact]
    public async Task WaitsOutRateLimitThenRetries()
    {
        int calls = 0;
        var h = new FakeHandler
        {
            Respond = _ =>
            {
                if (++calls == 1)
                {
                    var r = new HttpResponseMessage((HttpStatusCode)429) { Content = new StringContent("slow down") };
                    r.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(7));
                    return r;
                }
                return Json("""{"type_id": 1, "price": 2}""");
            },
        };
        await Client(h).GetAsync<Thing>("x");
        Assert.Equal(2, calls);
        Assert.Contains(TimeSpan.FromSeconds(7), _delays);
    }

    [Fact]
    public async Task SlowsDownWhenRateLimitIsLow()
    {
        var h = new FakeHandler
        {
            Respond = _ =>
            {
                var r = Json("""{"type_id": 1, "price": 2}""");
                r.Headers.Add("X-Ratelimit-Group", "market-order");
                r.Headers.Add("X-Ratelimit-Remaining", "12");
                return r;
            },
        };
        var esi = Client(h);
        await esi.GetAsync<Thing>("x");
        Assert.NotEmpty(_delays);
        Assert.Equal(12, esi.RateLimitRemaining["market-order"]);
    }

    [Fact]
    public async Task ErrorsSurfaceAsEsiException()
    {
        var h = new FakeHandler { Respond = _ => new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("""{"error":"token not valid for scope"}""") } };
        var ex = await Assert.ThrowsAsync<EsiException>(() => Client(h).GetAsync<Thing>("characters/1/assets", "tok"));
        Assert.Equal(HttpStatusCode.Forbidden, ex.Status);
        Assert.Contains("scope", ex.Message);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }
}
