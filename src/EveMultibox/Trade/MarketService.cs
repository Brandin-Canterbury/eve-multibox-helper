using EveMultibox.Esi;
using EveMultibox.StaticData;

namespace EveMultibox.Trade;

public enum SearchMode
{
    HubToHub,
    WithinJumps,
}

/// <summary>
/// Market data loaded for a route search, kept for the session so switching character can
/// re-size routes without downloading the orders again.
/// </summary>
public sealed record MarketCache(SearchMode Mode, bool Safe, IReadOnlyList<long> Stations, MarketSnapshot Market, Dictionary<int, double> Averages)
{
    /// <summary>
    /// True when this data covers exactly the stations a new search needs. Hub-to-hub always does;
    /// "within N jumps" only if the new character's range reaches the same stations.
    /// </summary>
    public bool Covers(SearchMode mode, bool safe, IEnumerable<long> stations) =>
        Mode == mode && Safe == safe && Stations.ToHashSet().SetEquals(stations);
}

/// <summary>One route with the destination's daily sales, as saved to disk.</summary>
public sealed record SavedRoute(RouteCandidate Route, double Daily);

/// <summary>One item's sell options, as saved to disk.</summary>
public sealed record SavedSale(SellOption? Now, SellOption? List);

/// <summary>
/// A finished search, saved so the Trade window can show it again after being closed, a restart or
/// a crash. Holds the raw results (not display rows) plus the station names they need.
/// </summary>
public sealed record SearchResults(
    SearchMode Mode, int Radius, long CharacterId, DateTimeOffset At, string Status,
    List<SavedRoute>? Routes, List<SavedSale>? Sales, Dictionary<long, string> StationNames,
    double FreeCargo = 0, double FreeOreHold = 0, double Wallet = 0);

/// <summary>Gathers what the route and sell finders need: stations, order books, prices, holds, assets, demand.</summary>
public sealed class MarketService
{
    private readonly TradeService _trade;
    private readonly Func<IReadOnlyList<long>> _hubs;
    private readonly Dictionary<(int Origin, bool Safe), Dictionary<int, int>> _distances = new();

    public MarketService(TradeService trade, Func<IReadOnlyList<long>> hubs)
    {
        _trade = trade;
        _hubs = hubs;
    }

    private EsiClient Esi => _trade.Esi;

    /// <summary>The NPC stations a search covers.</summary>
    public IReadOnlyList<long> StationsFor(SearchMode mode, int? currentSystem, int radius, bool avoidLowSec, SdeData sde)
    {
        if (mode == SearchMode.HubToHub) return _hubs().Where(sde.Stations.ContainsKey).ToList();
        if (currentSystem is null) return Array.Empty<long>();

        var systems = new JumpGraph(sde.Systems).Within(currentSystem.Value, radius, avoidLowSec ? 0.5 : null);
        return sde.Stations.Values.Where(s => systems.ContainsKey(s.SystemId)).Select(s => s.Id).ToList();
    }

    /// <summary>Jump counts, computed once per origin system and reused.</summary>
    public Func<int, int, int?> JumpCounter(SdeData sde, bool avoidLowSec)
    {
        var graph = new JumpGraph(sde.Systems);
        return (from, to) =>
        {
            if (from == to) return 0;
            var key = (from, avoidLowSec);
            if (!_distances.TryGetValue(key, out var dist))
                _distances[key] = dist = graph.Within(from, int.MaxValue, avoidLowSec ? 0.5 : null);
            return dist.TryGetValue(to, out int d) ? d : null;
        };
    }

    /// <summary>All orders in every region the stations are in. Large regions take a while the first time.</summary>
    public async Task<MarketSnapshot> LoadOrdersAsync(IReadOnlyCollection<long> stations, SdeData sde, IProgress<string> progress, CancellationToken ct)
    {
        var regions = stations
            .Select(s => sde.Stations[s].SystemId)
            .Select(sys => sde.Systems[sys].RegionId)
            .Distinct()
            .ToList();

        var orders = new List<MarketOrder>();
        int done = 0;
        foreach (int region in regions)
        {
            progress.Report($"Loading market orders: {sde.RegionName(region)} ({++done} of {regions.Count})…");
            orders.AddRange(await Esi.GetAllPagesAsync<MarketOrder>($"markets/{region}/orders?order_type=all", ct: ct));
        }

        var wanted = stations.ToHashSet();
        return new MarketSnapshot(orders, wanted.Contains, DateTimeOffset.UtcNow);
    }

    /// <summary>CCP's average price for every item, used to throw out absurd listings.</summary>
    public async Task<Dictionary<int, double>> AveragePricesAsync(CancellationToken ct)
    {
        var prices = await Esi.GetAsync<List<MarketPrice>>("markets/prices", ct: ct);
        return prices.Where(p => p.AveragePrice > 0).ToDictionary(p => p.TypeId, p => p.AveragePrice);
    }

    public async Task<List<EsiAsset>> AssetsAsync(CharacterState character, CancellationToken ct)
    {
        string token = await _trade.Auth.GetAccessTokenAsync(character.Id, ct);
        return await Esi.GetAllPagesAsync<EsiAsset>($"characters/{character.Id}/assets", token, ct);
    }

    /// <summary>Free hold space and ISK for a character right now (cargo contents come from assets, which ESI caches for up to an hour).</summary>
    public static TraderContext Trader(CharacterState c, IReadOnlyList<EsiAsset> assets, SdeData sde)
    {
        if (c.SystemId is null) throw new InvalidOperationException($"{c.Name}'s location isn't known yet.");
        var (usedCargo, usedOre) = c.ShipItemId is long ship ? SellFinder.UsedHold(assets, ship, sde) : (0, 0);
        return new TraderContext(c.SystemId.Value,
            Math.Max(0, c.CargoCapacity - usedCargo),
            Math.Max(0, c.OreHoldCapacity - usedOre),
            c.Wallet ?? 0,
            c.Skills);
    }

    /// <summary>
    /// Keeps routes whose destination actually sells that much: each route is cut down to what
    /// the destination region trades in <paramref name="daysToSell"/> days and re-scored. Checks
    /// candidates best-first until it has <paramref name="want"/> routes.
    /// </summary>
    public Task<List<(RouteCandidate Route, double Daily)>> WithDemandAsync(
        IReadOnlyList<RouteCandidate> candidates, SdeData sde, double daysToSell, double minProfit, int want,
        IProgress<string> progress, CancellationToken ct) =>
        FilterByDemandAsync(candidates, sde, DailyVolumeAsync, daysToSell, minProfit, want, progress, ct);

    /// <summary>The demand filter with a pluggable history lookup (region, type → daily units).</summary>
    public static async Task<List<(RouteCandidate Route, double Daily)>> FilterByDemandAsync(
        IReadOnlyList<RouteCandidate> candidates, SdeData sde, Func<int, int, CancellationToken, Task<double?>> dailyVolume,
        double daysToSell, double minProfit, int want, IProgress<string>? progress, CancellationToken ct)
    {
        var kept = new List<(RouteCandidate, double)>();
        var gate = new SemaphoreSlim(6);
        int checkedCount = 0;

        // Check in batches so the cheap early exit still works.
        foreach (var batch in candidates.Chunk(24))
        {
            var results = await Task.WhenAll(batch.Select(async r =>
            {
                await gate.WaitAsync(ct);
                try
                {
                    int region = sde.Systems[sde.Stations[r.DestStation].SystemId].RegionId;
                    return (Route: r, Daily: await dailyVolume(region, r.TypeId, ct));
                }
                finally
                {
                    gate.Release();
                }
            }));

            foreach (var (route, daily) in results)
            {
                if (daily is not double d || d <= 0) continue;
                var scaled = RouteFinder.Scale(route, (long)Math.Floor(d * daysToSell));
                if (scaled.Units > 0 && scaled.Profit >= minProfit) kept.Add((scaled, d));
            }

            checkedCount += batch.Length;
            progress?.Report($"Checking demand at destinations… {checkedCount} of {candidates.Count} checked, {kept.Count} good routes so far");
            if (kept.Count >= want) break;
        }

        return kept.OrderByDescending(k => k.Item1.Profit).Take(want).ToList();
    }

    private readonly Dictionary<(int, int), double?> _daily = new();

    /// <summary>Average daily units traded over the last week in a region (demand check for a destination).</summary>
    public async Task<double?> DailyVolumeAsync(int regionId, int typeId, CancellationToken ct)
    {
        lock (_daily)
            if (_daily.TryGetValue((regionId, typeId), out var known)) return known;
        double? result;
        try
        {
            var days = await Esi.GetAsync<List<MarketHistoryDay>>($"markets/{regionId}/history?type_id={typeId}", ct: ct);
            // Average over the last 7 calendar days. ESI leaves out days with no trades, so count those as zero.
            string since = DateTime.UtcNow.AddDays(-8).ToString("yyyy-MM-dd");
            result = days.Where(d => string.CompareOrdinal(d.Date, since) > 0).Sum(d => (double)d.Volume) / 7;
        }
        catch (EsiException)
        {
            result = null;
        }
        lock (_daily) _daily[(regionId, typeId)] = result;
        return result;
    }
}
