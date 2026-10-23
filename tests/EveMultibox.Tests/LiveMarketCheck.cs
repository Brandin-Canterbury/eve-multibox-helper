using System.Diagnostics;
using System.IO;
using EveMultibox.Esi;
using EveMultibox.StaticData;
using EveMultibox.Trade;
using Xunit.Abstractions;

namespace EveMultibox.Tests;

/// <summary>
/// Runs the hub-to-hub route search against live ESI market data. Only when EVE_LIVE=1 and
/// EVE_SDE_ZIP points at a static data zip; otherwise it does nothing (it downloads ~150 MB of orders).
/// </summary>
public class LiveMarketCheck
{
    private readonly ITestOutputHelper _out;

    public LiveMarketCheck(ITestOutputHelper output) => _out = output;

    [Fact]
    public async Task HubRoutesFromRealMarket()
    {
        string? zip = Environment.GetEnvironmentVariable("EVE_SDE_ZIP");
        if (Environment.GetEnvironmentVariable("EVE_LIVE") != "1" || zip is null || !File.Exists(zip)) return;

        SdeData sde;
        using (var f = File.OpenRead(zip)) sde = SdeStore.Build(f);

        var esi = new EsiClient();
        long[] hubs = { 60003760, 60008494, 60011866, 60004588, 60005686 };
        var regions = hubs.Select(h => sde.Systems[sde.Stations[h].SystemId].RegionId).Distinct();

        var sw = Stopwatch.StartNew();
        var orders = new List<MarketOrder>();
        foreach (int r in regions) orders.AddRange(await esi.GetAllPagesAsync<MarketOrder>($"markets/{r}/orders?order_type=all"));
        _out.WriteLine($"Fetched {orders.Count:N0} orders in {sw.Elapsed.TotalSeconds:N0}s");

        var market = new MarketSnapshot(orders, hubs.ToHashSet().Contains, DateTimeOffset.UtcNow);
        var averages = (await esi.GetAsync<List<MarketPrice>>("markets/prices")).Where(p => p.AveragePrice > 0).ToDictionary(p => p.TypeId, p => p.AveragePrice);
        var graph = new JumpGraph(sde.Systems);
        var trader = new TraderContext(30000142, 5_000, 0, 100_000_000, new SkillProfile(Accounting: 3, BrokerRelations: 3));

        sw.Restart();
        var options = new RouteOptions { MaxResults = 1500 };
        var candidates = RouteFinder.Find(market, hubs, sde, trader, (a, b) => graph.Jumps(a, b), averages, options);
        _out.WriteLine($"{candidates.Count} candidates in {sw.Elapsed.TotalSeconds:N1}s (from Jita, 5,000 m³, 100M ISK)");

        sw.Restart();
        async Task<double?> Daily(int region, int type, CancellationToken ct)
        {
            var days = await esi.GetAsync<List<MarketHistoryDay>>($"markets/{region}/history?type_id={type}", ct: ct);
            string since = DateTime.UtcNow.AddDays(-8).ToString("yyyy-MM-dd");
            return days.Where(d => string.CompareOrdinal(d.Date, since) > 0).Sum(d => (double)d.Volume) / 7;
        }
        var verified = await MarketService.FilterByDemandAsync(candidates, sde, Daily, 3, options.MinProfit, 100, null, CancellationToken.None);
        var routes = verified.Select(v => v.Route).ToList();
        _out.WriteLine($"Found {routes.Count} routes with real demand in {sw.Elapsed.TotalSeconds:N1}s");

        foreach (var r in routes.Take(12))
            _out.WriteLine($"{r.ItemName,-40} {sde.SystemName(sde.Stations[r.SourceStation].SystemId),-8} → {sde.SystemName(sde.Stations[r.DestStation].SystemId),-8} " +
                           $"{r.Units,9:N0} u  seed {r.SeedCost / 1e6,7:N2}M  profit {r.Profit / 1e6,6:N2}M  {r.TotalJumps,2} j  sell {r.SellPrice:N2}");

        Assert.NotEmpty(routes);
        Assert.All(routes, r => Assert.True(r.CargoUsed <= 5_000 + 1e-6));
        Assert.All(routes, r => Assert.True(r.SeedCost <= 100_000_000 + 1e-6));
    }
}
