namespace EveMultibox.Trade;

/// <summary>
/// A planned multi-item trade run: several items bought at one station and sold at another, in
/// one trip. Space and ISK are measured against what you had before the run starts (nothing is
/// bought yet), and each item added takes from what's left.
/// </summary>
public sealed class TradeRun
{
    private readonly List<RouteCandidate> _items = new();
    private readonly Func<int, bool> _isOre;

    /// <param name="cargoSpace">Free cargo hold (m³) before the run.</param>
    /// <param name="oreSpace">Free mining hold (m³) before the run; 0 if the ship has none.</param>
    /// <param name="cash">ISK available to spend.</param>
    /// <param name="isOre">Whether an item type goes in the mining hold.</param>
    public TradeRun(double cargoSpace, double oreSpace, double cash, Func<int, bool> isOre)
    {
        CargoSpace = cargoSpace;
        OreSpace = oreSpace;
        Cash = cash;
        _isOre = isOre;
    }

    public double CargoSpace { get; }
    public double OreSpace { get; }
    public double Cash { get; }

    public IReadOnlyList<RouteCandidate> Items => _items;
    public long? SourceStation => _items.Count > 0 ? _items[0].SourceStation : null;
    public long? DestStation => _items.Count > 0 ? _items[0].DestStation : null;

    public double CargoUsed => _items.Where(i => !UsesOreHold(i.TypeId)).Sum(i => i.CargoUsed);
    public double OreUsed => _items.Where(i => UsesOreHold(i.TypeId)).Sum(i => i.CargoUsed);
    public double CashUsed => _items.Sum(i => i.SeedCost);
    public double Profit => _items.Sum(i => i.Profit);
    public double Revenue => _items.Sum(i => i.Revenue);

    public double CargoLeft => Math.Max(0, CargoSpace - CargoUsed);
    public double OreLeft => Math.Max(0, OreSpace - OreUsed);
    public double CashLeft => Math.Max(0, Cash - CashUsed);

    private bool UsesOreHold(int typeId) => OreSpace > 0 && _isOre(typeId);

    /// <summary>Why an item couldn't be added, or null if it was.</summary>
    public string? TryAdd(RouteCandidate route)
    {
        if (SourceStation is long src && (route.SourceStation != src || route.DestStation != DestStation))
            return "A run buys at one station and sells at one station. This item's route is different; clear the run to start another.";
        if (_items.Any(i => i.TypeId == route.TypeId))
            return $"{route.ItemName} is already in the run.";

        long units = UnitsThatFit(route);
        if (units <= 0) return $"No room left for {route.ItemName} (out of hold space or ISK).";

        var scaled = RouteFinder.Scale(route, units);
        if (scaled.Profit <= 0) return $"Only {units:N0} {route.ItemName} fit, which wouldn't make a profit after fees.";
        _items.Add(scaled);
        return null;
    }

    public void Remove(RouteCandidate item) => _items.Remove(item);

    public void Clear() => _items.Clear();

    /// <summary>The most units of this route that fit in the space and ISK left (never more than the route offered).</summary>
    public long UnitsThatFit(RouteCandidate route)
    {
        double space = UsesOreHold(route.TypeId) ? OreLeft : CargoLeft;
        long bySpace = route.UnitVolume > 0 ? (long)Math.Floor(space / route.UnitVolume + 1e-9) : route.Units;
        long byCash = route.AvgBuyPrice > 0 ? (long)Math.Floor(CashLeft / route.AvgBuyPrice + 1e-9) : route.Units;
        return Math.Min(route.Units, Math.Min(bySpace, byCash));
    }

    /// <summary>
    /// Fills the rest of the run, best value first. "Best" weighs profit against how much of the
    /// remaining hold and ISK an item uses, so cheap bulky items and pricey compact ones compete fairly.
    /// With an empty run it picks whichever buy → sell pair fills most profitably.
    /// </summary>
    public int AutoFill(IEnumerable<RouteCandidate> candidates)
    {
        var all = candidates.ToList();
        if (_items.Count == 0)
        {
            var bestPair = all.GroupBy(c => (c.SourceStation, c.DestStation))
                .Select(g => (Pair: g.Key, Profit: Simulate(g)))
                .OrderByDescending(p => p.Profit)
                .FirstOrDefault();
            if (bestPair.Profit <= 0) return 0;
            all = all.Where(c => (c.SourceStation, c.DestStation) == bestPair.Pair).ToList();
        }

        int added = 0;
        foreach (var route in Ranked(all.Where(c => c.SourceStation == (SourceStation ?? c.SourceStation)
                                                  && c.DestStation == (DestStation ?? c.DestStation))))
        {
            if (TryAdd(route) is null) added++;
        }
        return added;
    }

    private double Simulate(IEnumerable<RouteCandidate> routes)
    {
        var trial = new TradeRun(CargoSpace, OreSpace, Cash, _isOre);
        foreach (var r in Ranked(routes)) trial.TryAdd(r);
        return trial.Profit;
    }

    private IEnumerable<RouteCandidate> Ranked(IEnumerable<RouteCandidate> routes) =>
        routes.Where(r => r.Units > 0 && r.Profit > 0)
            .OrderByDescending(r =>
            {
                double perUnit = r.Profit / r.Units;
                double space = UsesOreHold(r.TypeId) ? OreSpace : CargoSpace;
                double share = (space > 0 ? r.UnitVolume / space : 0) + (Cash > 0 ? r.AvgBuyPrice / Cash : 0);
                return share > 0 ? perUnit / share : perUnit;
            });
}
