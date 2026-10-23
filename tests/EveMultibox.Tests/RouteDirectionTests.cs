using System.IO;
using EveMultibox.StaticData;
using EveMultibox.Trade;

namespace EveMultibox.Tests;

/// <summary>
/// Regression: routes into Jita disappeared because only the overall top routes were kept, and
/// almost every high-profit route starts in Jita. Routes are now kept per direction.
/// </summary>
public sealed class RouteDirectionTests : IDisposable
{
    private const long Jita = 60003760, Amarr = 60008494;
    private const int Cheap = 1000, IntoJita = 999;

    private readonly SdeData _sde;
    private readonly MarketSnapshot _market;
    private readonly Dictionary<int, double> _averages = new();
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "EveMultiboxTests", Guid.NewGuid().ToString("N"));

    public RouteDirectionTests()
    {
        _sde = new SdeData
        {
            Stations =
            {
                [Jita] = new StationInfo(Jita, 30000142, 1000035, 500001),
                [Amarr] = new StationInfo(Amarr, 30002187, 1000086, 500003),
            },
        };
        var orders = new List<MarketOrder>();
        long id = 0;

        // 30 items that are much cheaper in Jita: big Jita → Amarr profits.
        for (int t = 0; t < 30; t++)
        {
            int type = Cheap + t + 1;
            _sde.Types[type] = new TypeInfo(type, $"Jita bargain {t}", 1, 1, 0, 1, 7, 1);
            _averages[type] = 1_500;
            orders.Add(new MarketOrder(++id, type, Jita, 30000142, false, 1_000, 1_000, 1, "region"));
            orders.Add(new MarketOrder(++id, type, Amarr, 30002187, false, 2_000, 10, 1, "region"));
        }

        // One item cheaper in Amarr: a modest Amarr → Jita profit.
        _sde.Types[IntoJita] = new TypeInfo(IntoJita, "Amarr bargain", 1, 1, 0, 1, 7, 1);
        _averages[IntoJita] = 120;
        orders.Add(new MarketOrder(++id, IntoJita, Amarr, 30002187, false, 100, 5_000, 1, "region"));
        orders.Add(new MarketOrder(++id, IntoJita, Jita, 30000142, false, 150, 10, 1, "region"));

        _market = new MarketSnapshot(orders, _sde.Stations.ContainsKey, DateTimeOffset.UtcNow);
    }

    private List<RouteCandidate> Find(RouteOptions options) =>
        RouteFinder.Find(_market, new[] { Jita, Amarr }, _sde, new TraderContext(30000142, 1e6, 0, 1e12, SkillProfile.None),
            (_, _) => 10, _averages, options with { MinProfit = 0 });

    [Fact]
    public void AnOverallTopCutLosesTheReturnDirection()
    {
        var top = Find(new RouteOptions { MaxResults = 20 });
        Assert.DoesNotContain(top, r => r.DestStation == Jita); // This was the bug.
    }

    [Fact]
    public void KeepingRoutesPerDirectionKeepsRoutesIntoJita()
    {
        var all = Find(new RouteOptions { MaxPerPair = 20, MaxResults = int.MaxValue });
        Assert.Equal(20, all.Count(r => r.SourceStation == Jita && r.DestStation == Amarr));
        var back = Assert.Single(all, r => r.SourceStation == Amarr && r.DestStation == Jita);
        Assert.Equal(IntoJita, back.TypeId);

        // And the From / To filter then finds it: "Any hub → Jita" and "Amarr → Jita".
        Assert.Single(all, r => RouteFilter.Matches(r, null, Jita));
        Assert.Single(all, r => RouteFilter.Matches(r, Amarr, Jita));
    }

    [Fact]
    public void SavedResultsKeepEveryCandidateAndTheChosenDirection()
    {
        var all = Find(new RouteOptions { MaxPerPair = 20, MaxResults = int.MaxValue });
        var store = new ResultsStore(Path.Combine(_dir, "trade-results.json"));
        var saved = new SearchResults(SearchMode.HubToHub, 8, 1, DateTimeOffset.UtcNow, "status", new List<SavedRoute>(), null,
            new Dictionary<long, string>(), 100, 0, 1e9, all, Amarr, Jita);
        store.Save(saved, null);

        var (routes, _) = store.Load();
        Assert.Equal(all.Count, routes!.Candidates!.Count);
        Assert.Equal((Amarr, Jita), (routes.FilterFrom!.Value, routes.FilterTo!.Value));
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }
}
