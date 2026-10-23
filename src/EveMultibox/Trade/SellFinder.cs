using EveMultibox.StaticData;

namespace EveMultibox.Trade;

/// <summary>A pile of one item at one place.</summary>
public sealed record AssetStack(int TypeId, long Quantity, long? StationId, int? SystemId, bool InCurrentShip);

public sealed record SellOption(
    AssetStack Stack,
    string ItemName,
    long Station,
    bool Instant,
    long Units,
    double UnitPrice,
    double Gross,
    double SalesTax,
    double BrokerFee,
    double Net,
    int Jumps)
{
    public string HowText => Instant ? "Sell now to buy orders" : "List as sell order";
}

/// <summary>Best place to sell each thing you own: straight into buy orders, or listed just under the cheapest seller.</summary>
public static class SellFinder
{
    private static readonly HashSet<string> HoldFlags = new(StringComparer.Ordinal)
    {
        "Cargo", "SpecializedOreHold", "SpecializedAsteroidHold", "SpecializedMineralHold", "FleetHangar",
    };

    /// <summary>
    /// Turns ESI's asset list (items inside items inside stations) into stacks per place. Fitted
    /// modules, assembled ships, blueprint copies and anything not on the market are left out.
    /// </summary>
    public static List<AssetStack> ResolveStacks(IReadOnlyList<EsiAsset> assets, long? currentShipItemId,
        int? currentSystemId, long? currentStationId, SdeData sde)
    {
        var byItem = assets.ToDictionary(a => a.ItemId);

        // Walk up through containers and ships until we reach a station or solar system.
        (long? Station, int? System, bool InShip) Place(EsiAsset a)
        {
            bool inShip = false;
            var current = a;
            for (int depth = 0; depth < 10; depth++)
            {
                if (current.LocationId == currentShipItemId) inShip = true;
                if (current.LocationType == "station") return (current.LocationId, sde.Stations.GetValueOrDefault(current.LocationId)?.SystemId, inShip);
                if (current.LocationType == "solar_system") return (null, (int)current.LocationId, inShip);
                if (!byItem.TryGetValue(current.LocationId, out var parent))
                {
                    // The current ship isn't listed inside a station while you're flying it.
                    return inShip ? (currentStationId, currentSystemId, true) : (null, null, false);
                }
                current = parent;
            }
            return (null, null, inShip);
        }

        return assets
            .Where(a => !a.IsSingleton && a.IsBlueprintCopy != true && a.Quantity > 0)
            .Where(a => sde.Types.TryGetValue(a.TypeId, out var t) && t.MarketGroupId is not null)
            .Where(a => a.LocationType != "item" || HoldFlags.Contains(a.LocationFlag) || a.LocationFlag == "Hangar" || a.LocationFlag == "Unlocked" || a.LocationFlag == "Locked")
            .Select(a => (Asset: a, Where: Place(a)))
            .GroupBy(x => (x.Asset.TypeId, x.Where.Station, x.Where.System, x.Where.InShip))
            .Select(g => new AssetStack(g.Key.TypeId, g.Sum(x => x.Asset.Quantity), g.Key.Station, g.Key.System, g.Key.InShip))
            .ToList();
    }

    /// <summary>m³ already used in the current ship's cargo and ore holds.</summary>
    public static (double Cargo, double Ore) UsedHold(IReadOnlyList<EsiAsset> assets, long shipItemId, SdeData sde)
    {
        double cargo = 0, ore = 0;
        foreach (var a in assets.Where(a => a.LocationId == shipItemId))
        {
            double volume = (sde.Types.TryGetValue(a.TypeId, out var t) ? (a.IsSingleton ? t.Volume : t.HaulVolume) : 0) * a.Quantity;
            if (a.LocationFlag is "SpecializedOreHold" or "SpecializedAsteroidHold") ore += volume;
            else if (a.LocationFlag == "Cargo") cargo += volume;
        }
        return (cargo, ore);
    }

    /// <summary>Best option per stack (by net ISK after tax and fees), searching the given stations.</summary>
    public static List<(SellOption? SellNow, SellOption? List)> Find(
        MarketSnapshot market,
        IReadOnlyCollection<long> stations,
        SdeData sde,
        SkillProfile skills,
        IReadOnlyList<AssetStack> stacks,
        Func<int, int, int?> jumps,
        IReadOnlyDictionary<int, double> averagePrices,
        RouteOptions? options = null)
    {
        options ??= new RouteOptions();
        var results = new List<(SellOption?, SellOption?)>();

        foreach (var stack in stacks)
        {
            string name = sde.TypeName(stack.TypeId);
            SellOption? bestNow = null, bestList = null;
            double? average = averagePrices.TryGetValue(stack.TypeId, out double avg) && avg > 0 ? avg : null;

            foreach (long station in stations)
            {
                if (!sde.Stations.TryGetValue(station, out var info)) continue;
                int? j = stack.SystemId is int from ? jumps(from, info.SystemId) : null;
                if (j is null) continue;

                // Straight into buy orders: no broker fee.
                var (units, gross) = SellIntoBuys(market.Buys(station, stack.TypeId), stack.Quantity);
                if (units > 0)
                {
                    double tax = gross * skills.SalesTax;
                    var option = new SellOption(stack, name, station, true, units, gross / units, gross, tax, 0, gross - tax, j.Value);
                    if (Better(option, bestNow)) bestNow = option;
                }

                // Listed just under the cheapest seller.
                // Listing needs a sane reference price, or a wishful listing would set it.
                if (market.LowestSell(station, stack.TypeId) is double lowest && lowest - options.Undercut > 0
                    && (average is null ? !options.RequireAveragePrice : lowest <= average * options.MaxOverAverage))
                {
                    double price = lowest - options.Undercut;
                    double listGross = price * stack.Quantity;
                    double tax = listGross * skills.SalesTax;
                    double broker = Fees.BrokerFeeIsk(listGross, skills.BrokerFeeAt(info));
                    var option = new SellOption(stack, name, station, false, stack.Quantity, price, listGross, tax, broker, listGross - tax - broker, j.Value);
                    if (Better(option, bestList)) bestList = option;
                }
            }
            results.Add((bestNow, bestList));
        }

        return results
            .OrderByDescending(r => Math.Max(r.Item1?.Net ?? 0, r.Item2?.Net ?? 0))
            .ToList();
    }

    private static bool Better(SellOption candidate, SellOption? current) =>
        current is null || candidate.Net > current.Net || (candidate.Net == current.Net && candidate.Jumps < current.Jumps);

    /// <summary>Sells into buy orders, highest first, respecting each order's minimum volume.</summary>
    internal static (long Units, double Gross) SellIntoBuys(IReadOnlyList<MarketOrder> buys, long quantity)
    {
        long left = quantity, sold = 0;
        double gross = 0;
        foreach (var order in buys)
        {
            if (left <= 0) break;
            long take = Math.Min(left, order.VolumeRemain);
            if (take < order.MinVolume) continue;
            sold += take;
            left -= take;
            gross += take * order.Price;
        }
        return (sold, gross);
    }
}
