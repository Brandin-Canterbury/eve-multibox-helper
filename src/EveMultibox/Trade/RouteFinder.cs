using EveMultibox.StaticData;

namespace EveMultibox.Trade;

/// <summary>Everything about the trader that limits a route: hold space, ISK, skills, and where they are.</summary>
public sealed record TraderContext(
    int CurrentSystemId,
    double FreeCargo,
    double FreeOreHold,
    double Wallet,
    SkillProfile Skills)
{
    /// <summary>Free m³ for this item: the mining hold for ore when the ship has one, otherwise the cargo hold.</summary>
    public double FreeSpaceFor(TypeInfo type) => type.IsOre && FreeOreHold > 0 ? FreeOreHold : FreeCargo;
}

public sealed record RouteOptions
{
    /// <summary>The listing price is the destination's lowest sell order minus this.</summary>
    public double Undercut { get; init; } = 1;

    /// <summary>Skip destinations priced above this multiple of CCP's average price (catches 300M-ISK Tritanium).</summary>
    public double MaxOverAverage { get; init; } = 2.5;

    /// <summary>
    /// Items with no CCP average price are skipped: on real data they're mostly rarely-traded
    /// collectibles whose "lowest sell" is a wishful listing nobody will ever pay.
    /// </summary>
    public bool RequireAveragePrice { get; init; } = true;

    /// <summary>Resale above this markup on the buy price is treated as a fake listing (2 = +200%).</summary>
    public double MaxMarkup { get; init; } = 2.0;

    public double MinProfit { get; init; } = 100_000;
    public int MaxResults { get; init; } = 200;

    /// <summary>Per item, only try this many cheapest sources and priciest destinations (keeps big searches fast).</summary>
    public int CandidatesPerSide { get; init; } = 4;
}

public sealed record RouteCandidate(
    int TypeId,
    string ItemName,
    long SourceStation,
    long DestStation,
    long Units,
    double UnitVolume,
    double AvgBuyPrice,
    double SeedCost,
    double SellPrice,
    double Revenue,
    double SalesTax,
    double BrokerFee,
    double Profit,
    int JumpsToSource,
    int JumpsBetween,
    double? InstantSellPrice,
    bool LimitedByCargo,
    bool LimitedByWallet)
{
    public int TotalJumps => JumpsToSource + JumpsBetween;
    public double ProfitPerJump => Profit / Math.Max(1, TotalJumps);
    public double Margin => SeedCost > 0 ? Profit / SeedCost : 0;
    public double CargoUsed => Units * UnitVolume;
}

/// <summary>
/// Finds buy-here-sell-there opportunities. Buys walk the source's sell orders cheapest first, as
/// long as each unit still makes money; the sale is listed at the destination's lowest sell
/// order minus 1 ISK. Everything is capped by free hold space and wallet, and fees come off
/// using the trader's skills and standings.
/// </summary>
public static class RouteFinder
{
    public static List<RouteCandidate> Find(
        MarketSnapshot market,
        IReadOnlyCollection<long> stations,
        SdeData sde,
        TraderContext trader,
        Func<int, int, int?> jumps,
        IReadOnlyDictionary<int, double> averagePrices,
        RouteOptions? options = null)
    {
        options ??= new RouteOptions();
        double salesTax = trader.Skills.SalesTax;
        var results = new List<RouteCandidate>();

        var types = stations.SelectMany(market.TypesAt).ToHashSet();
        foreach (int typeId in types)
        {
            if (!sde.Types.TryGetValue(typeId, out var type) || type.HaulVolume <= 0) continue;
            double space = trader.FreeSpaceFor(type);
            long maxUnitsBySpace = (long)Math.Floor(space / type.HaulVolume);
            if (maxUnitsBySpace <= 0) continue;
            double? average = averagePrices.TryGetValue(typeId, out double avg) && avg > 0 ? avg : null;
            if (average is null && options.RequireAveragePrice) continue;

            // Destinations: where we'd list it, priciest first.
            var dests = stations
                .Select(s => (Station: s, Lowest: market.LowestSell(s, typeId)))
                .Where(d => d.Lowest is double p && p - options.Undercut > 0
                            && (average is null || p <= average * options.MaxOverAverage))
                .OrderByDescending(d => d.Lowest)
                .Take(options.CandidatesPerSide)
                .ToList();
            if (dests.Count == 0) continue;

            // Sources: where it's cheapest to buy.
            var sources = stations
                .Where(s => market.LowestSell(s, typeId) is not null)
                .OrderBy(s => market.LowestSell(s, typeId))
                .Take(options.CandidatesPerSide)
                .ToList();

            foreach (var (dest, lowest) in dests)
            {
                if (!sde.Stations.TryGetValue(dest, out var destInfo)) continue;
                double sellPrice = lowest!.Value - options.Undercut;
                double brokerRate = trader.Skills.BrokerFeeAt(destInfo);
                double netPerUnit = sellPrice * (1 - salesTax - brokerRate);

                foreach (long source in sources)
                {
                    if (source == dest || !sde.Stations.TryGetValue(source, out var srcInfo)) continue;
                    var fill = Fill(market.Sells(source, typeId), netPerUnit, maxUnitsBySpace, trader.Wallet);
                    if (fill.Units <= 0) continue;
                    if (sellPrice > fill.Cost / fill.Units * (1 + options.MaxMarkup)) continue;

                    double revenue = fill.Units * sellPrice;
                    double tax = revenue * salesTax;
                    double broker = Fees.BrokerFeeIsk(revenue, brokerRate);
                    double profit = revenue - tax - broker - fill.Cost;
                    if (profit < options.MinProfit) continue;

                    int? toSource = jumps(trader.CurrentSystemId, srcInfo.SystemId);
                    int? between = jumps(srcInfo.SystemId, destInfo.SystemId);
                    if (toSource is null || between is null) continue;

                    results.Add(new RouteCandidate(typeId, type.Name, source, dest, fill.Units, type.HaulVolume,
                        fill.Cost / fill.Units, fill.Cost, sellPrice, revenue, tax, broker, profit,
                        toSource.Value, between.Value, market.HighestBuy(dest, typeId),
                        fill.Units == maxUnitsBySpace, fill.LimitedByWallet));
                }
            }
        }

        return results.OrderByDescending(r => r.Profit).Take(options.MaxResults).ToList();
    }

    /// <summary>
    /// The same route with fewer units (e.g. only what the destination sells in a few days).
    /// Cost uses the plan's average price, which is slightly pessimistic since the cheapest units are bought first.
    /// </summary>
    public static RouteCandidate Scale(RouteCandidate r, long units)
    {
        if (units >= r.Units) return r;
        double brokerRate = r.Revenue > 0 ? r.BrokerFee / r.Revenue : 0;
        double taxRate = r.Revenue > 0 ? r.SalesTax / r.Revenue : 0;
        double seed = r.AvgBuyPrice * units;
        double revenue = r.SellPrice * units;
        double tax = revenue * taxRate;
        double broker = Fees.BrokerFeeIsk(revenue, brokerRate);
        return r with
        {
            Units = units,
            SeedCost = seed,
            Revenue = revenue,
            SalesTax = tax,
            BrokerFee = broker,
            Profit = revenue - tax - broker - seed,
            LimitedByCargo = false,
            LimitedByWallet = false,
        };
    }

    internal readonly record struct FillResult(long Units, double Cost, bool LimitedByWallet);

    /// <summary>
    /// Buys from sell orders (cheapest first) while each unit costs less than it nets on resale,
    /// stopping when hold space or ISK runs out.
    /// </summary>
    internal static FillResult Fill(IReadOnlyList<MarketOrder> sellOrders, double netPerUnit, long maxUnits, double wallet)
    {
        long units = 0;
        double cost = 0;
        bool walletLimited = false;
        foreach (var order in sellOrders)
        {
            if (order.Price >= netPerUnit || units >= maxUnits) break;
            long take = Math.Min(order.VolumeRemain, maxUnits - units);
            long affordable = (long)Math.Floor((wallet - cost) / order.Price);
            if (affordable < take)
            {
                take = affordable;
                walletLimited = true;
            }
            if (take <= 0) break;
            units += take;
            cost += take * order.Price;
            if (walletLimited) break;
        }
        return new FillResult(units, cost, walletLimited);
    }
}
