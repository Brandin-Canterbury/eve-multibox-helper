using EveMultibox.StaticData;
using EveMultibox.Trade;

namespace EveMultibox.Tests;

public class MarketEngineTests
{
    private const long Jita = 60003760, Dodixie = 60011866, Amarr = 60008494;
    private const int JitaSys = 30000142, DodixieSys = 30002659, AmarrSys = 30002187;
    private const int Inferno = 2629, Veldspar = 1230;

    private static readonly SdeData Sde = new()
    {
        Types = new()
        {
            [Inferno] = new TypeInfo(Inferno, "Inferno Light Missile", 0.015, 0.015, 0, 384, 8, 920),
            [Veldspar] = new TypeInfo(Veldspar, "Veldspar", 0.1, 0.1, 0, 462, TypeInfo.AsteroidCategory, 518),
            [34] = new TypeInfo(34, "Tritanium", 0.01, 0.01, 0, 18, 4, 1857),
        },
        Stations = new()
        {
            [Jita] = new StationInfo(Jita, JitaSys, 1000035, 500001),
            [Dodixie] = new StationInfo(Dodixie, DodixieSys, 1000120, 500004),
            [Amarr] = new StationInfo(Amarr, AmarrSys, 1000086, 500003),
        },
    };

    private static readonly long[] Hubs = { Jita, Dodixie, Amarr };

    // Jita→Dodixie 15 jumps, Jita→Amarr 9, Dodixie→Amarr 20; you're in Jita.
    private static int? Jumps(int a, int b) => (a, b) switch
    {
        _ when a == b => 0,
        (JitaSys, DodixieSys) or (DodixieSys, JitaSys) => 15,
        (JitaSys, AmarrSys) or (AmarrSys, JitaSys) => 9,
        _ => 20,
    };

    private static long _orderId;

    private static MarketOrder Sell(long station, int type, double price, long volume) =>
        new(++_orderId, type, station, Sde.Stations[station].SystemId, false, price, volume, 1, "region");

    private static MarketOrder Buy(long station, int type, double price, long volume, int minVolume = 1) =>
        new(++_orderId, type, station, Sde.Stations[station].SystemId, true, price, volume, minVolume, "station");

    private static MarketSnapshot Market(params MarketOrder[] orders) =>
        new(orders, Sde.Stations.ContainsKey, DateTimeOffset.UtcNow);

    private static TraderContext Trader(double cargo = 10_000, double ore = 0, double wallet = 1e12, SkillProfile? skills = null) =>
        new(JitaSys, cargo, ore, wallet, skills ?? new SkillProfile(Accounting: 5, BrokerRelations: 5));

    // Typical CCP averages for the test items (routes need one to sanity-check listings).
    private static readonly Dictionary<int, double> NoAverages = new() { [Inferno] = 150, [Veldspar] = 20, [34] = 4 };

    [Fact]
    public void UsersExampleListsAtLowestSellMinusOneIsk()
    {
        // Jita sells 10k Inferno at 100; Dodixie's cheapest is 200. List at 199.
        var market = Market(Sell(Jita, Inferno, 100, 10_000), Sell(Dodixie, Inferno, 200, 500));
        var r = RouteFinder.Find(market, Hubs, Sde, Trader(), Jumps, NoAverages).Single();

        Assert.Equal((Jita, Dodixie), (r.SourceStation, r.DestStation));
        Assert.Equal(199, r.SellPrice);
        Assert.Equal(10_000, r.Units);
        Assert.Equal(1_000_000, r.SeedCost);

        var p = new SkillProfile(Accounting: 5, BrokerRelations: 5);
        double revenue = 199 * 10_000;
        double expected = revenue - revenue * p.SalesTax - revenue * p.BrokerFeeAt(Sde.Stations[Dodixie]) - 1_000_000;
        Assert.Equal(expected, r.Profit, 3);
        Assert.Equal(0, r.JumpsToSource);
        Assert.Equal(15, r.JumpsBetween);
    }

    [Fact]
    public void CargoSpaceCapsUnits()
    {
        // 30 m³ free / 0.015 m³ each = 2,000 missiles.
        var market = Market(Sell(Jita, Inferno, 100, 10_000), Sell(Dodixie, Inferno, 200, 500));
        var r = RouteFinder.Find(market, Hubs, Sde, Trader(cargo: 30), Jumps, NoAverages).Single();
        Assert.Equal(2_000, r.Units);
        Assert.True(r.LimitedByCargo);
    }

    [Fact]
    public void ExistingCargoIsAlreadyTakenIntoAccount()
    {
        // The caller passes free space, so 10k missiles already aboard (150 m³ of 180) leave room for 2,000 more.
        var market = Market(Sell(Jita, Inferno, 100, 10_000), Sell(Dodixie, Inferno, 200, 500));
        var r = RouteFinder.Find(market, Hubs, Sde, Trader(cargo: 180 - 150), Jumps, NoAverages).Single();
        Assert.Equal(2_000, r.Units);
    }

    [Fact]
    public void WalletCapsUnits()
    {
        var market = Market(Sell(Jita, Inferno, 100, 10_000), Sell(Dodixie, Inferno, 200, 500));
        var r = RouteFinder.Find(market, Hubs, Sde, Trader(wallet: 250_000), Jumps, NoAverages).Single();
        Assert.Equal(2_500, r.Units);
        Assert.True(r.LimitedByWallet);
    }

    [Fact]
    public void OreUsesTheMiningHold()
    {
        var market = Market(Sell(Jita, Veldspar, 10, 1_000_000), Sell(Dodixie, Veldspar, 26, 1000));
        var opts = new RouteOptions { MinProfit = 0 };

        var withHold = RouteFinder.Find(market, Hubs, Sde, Trader(cargo: 100, ore: 5_000), Jumps, NoAverages, opts).Single();
        Assert.Equal(50_000, withHold.Units); // 5,000 m³ / 0.1

        var noHold = RouteFinder.Find(market, Hubs, Sde, Trader(cargo: 100, ore: 0), Jumps, NoAverages, opts).Single();
        Assert.Equal(1_000, noHold.Units);    // Falls back to cargo.
    }

    [Fact]
    public void WalksDepthOnlyWhileProfitable()
    {
        // Net resale ≈ 199 × (1 − tax − broker) ≈ 189; the 150 and 180 orders are worth buying, 195 isn't.
        var market = Market(
            Sell(Jita, Inferno, 150, 1_000), Sell(Jita, Inferno, 180, 1_000), Sell(Jita, Inferno, 195, 5_000),
            Sell(Dodixie, Inferno, 200, 10));
        var r = RouteFinder.Find(market, Hubs, Sde, Trader(), Jumps, NoAverages, new RouteOptions { MinProfit = 0 }).Single();
        Assert.Equal(2_000, r.Units);
        Assert.Equal(150 * 1_000 + 180 * 1_000, r.SeedCost);
    }

    [Fact]
    public void IgnoresAbsurdlyOverpricedDestinations()
    {
        // Someone lists Tritanium at 300M in Amarr; the real price is ~4 ISK.
        var market = Market(Sell(Jita, 34, 3.9, 1_000_000), Sell(Amarr, 34, 300_000_000, 1), Sell(Dodixie, 34, 4.5, 1_000));
        var averages = new Dictionary<int, double> { [34] = 4.0 };
        var results = RouteFinder.Find(market, Hubs, Sde, Trader(), Jumps, averages, new RouteOptions { MinProfit = 0, Undercut = 0.01 });
        Assert.DoesNotContain(results, r => r.DestStation == Amarr);
    }

    [Fact]
    public void SkipsItemsWithNoAveragePrice()
    {
        // Real data: rare collectibles with no CCP average and a wishful 3.7B listing.
        var market = Market(Sell(Jita, Inferno, 100, 10), Sell(Dodixie, Inferno, 3_770_000_000, 1));
        Assert.Empty(RouteFinder.Find(market, Hubs, Sde, Trader(), Jumps, new Dictionary<int, double>()));
    }

    [Fact]
    public void SkipsAbsurdMarkups()
    {
        // Within 2.5× the average, but +400% on what you'd pay: treat as a fake listing.
        var market = Market(Sell(Jita, Inferno, 70, 10_000), Sell(Dodixie, Inferno, 351, 5));
        var averages = new Dictionary<int, double> { [Inferno] = 150 };
        Assert.Empty(RouteFinder.Find(market, Hubs, Sde, Trader(), Jumps, averages, new RouteOptions { MinProfit = 0 }));
    }

    [Fact]
    public void NoRouteWhenFeesEatTheMargin()
    {
        var market = Market(Sell(Jita, Inferno, 100, 10_000), Sell(Dodixie, Inferno, 103, 10));
        Assert.Empty(RouteFinder.Find(market, Hubs, Sde, Trader(skills: SkillProfile.None), Jumps, NoAverages, new RouteOptions { MinProfit = 0 }));
    }

    [Fact]
    public void SortsByProfitAndReportsInstantSellAlternative()
    {
        var market = Market(
            Sell(Jita, Inferno, 100, 10_000), Sell(Dodixie, Inferno, 200, 10), Buy(Dodixie, Inferno, 170, 5_000),
            Sell(Jita, 34, 3, 1_000_000), Sell(Amarr, 34, 3.5, 1_000));
        var results = RouteFinder.Find(market, Hubs, Sde, Trader(), Jumps, NoAverages, new RouteOptions { MinProfit = 0, Undercut = 0.01 });
        Assert.Equal(Inferno, results[0].TypeId);
        Assert.Equal(170, results[0].InstantSellPrice);
        Assert.True(results.Zip(results.Skip(1)).All(p => p.First.Profit >= p.Second.Profit));
    }

    // ---- selling what you own ----

    [Fact]
    public void SellFinderPicksBestNowAndBestListing()
    {
        var stack = new AssetStack(Inferno, 1_000, Jita, JitaSys, false);
        var market = Market(
            Buy(Jita, Inferno, 120, 400), Buy(Jita, Inferno, 110, 10_000),
            Buy(Amarr, Inferno, 125, 1_000),
            Sell(Dodixie, Inferno, 200, 50));
        var skills = new SkillProfile(Accounting: 5, BrokerRelations: 5);

        var (now, list) = SellFinder.Find(market, Hubs, Sde, skills, new[] { stack }, Jumps, NoAverages).Single();

        // Amarr buys all 1,000 at 125 (beats Jita's 400 @120 + 600 @110).
        Assert.Equal(Amarr, now!.Station);
        Assert.Equal(125_000 * (1 - skills.SalesTax), now.Net, 3);
        Assert.Equal(9, now.Jumps);

        Assert.Equal(Dodixie, list!.Station);
        Assert.Equal(199, list.UnitPrice);
    }

    [Fact]
    public void SellIntoBuysRespectsMinimumVolume()
    {
        var buys = new[] { Buy(Jita, Inferno, 200, 10_000, minVolume: 5_000), Buy(Jita, Inferno, 100, 10_000) };
        Assert.Equal((1_000L, 100_000.0), SellFinder.SellIntoBuys(buys, 1_000));
        Assert.Equal((6_000L, 1_200_000.0), SellFinder.SellIntoBuys(buys, 6_000));
    }

    [Fact]
    public void ResolvesAssetLocationsThroughContainersAndShips()
    {
        const long ship = 1_000_000_001, can = 1_000_000_002;
        var assets = new List<EsiAsset>
        {
            new(ship, 648, 1, Jita, "station", "Hangar", true, null),                    // The ship itself (assembled).
            new(10, Inferno, 5_000, ship, "item", "Cargo", false, null),                 // In the ship's cargo.
            new(11, Inferno, 2_000, Jita, "station", "Hangar", false, null),             // Loose in the hangar.
            new(can, 3467, 1, Jita, "station", "Hangar", true, null),                    // A container.
            new(12, Inferno, 300, can, "item", "Unlocked", false, null),                 // Inside the container.
            new(13, 34, 50, ship, "item", "HiSlot0", false, null),                        // Fitted: ignored.
            new(14, Veldspar, 9_000, ship, "item", "SpecializedOreHold", false, null),   // Ore hold.
        };

        var stacks = SellFinder.ResolveStacks(assets, ship, JitaSys, Jita, Sde);
        Assert.Equal(5_000, stacks.Single(s => s.TypeId == Inferno && s.InCurrentShip).Quantity);
        Assert.Equal(2_300, stacks.Single(s => s.TypeId == Inferno && !s.InCurrentShip).Quantity);
        Assert.All(stacks, s => Assert.Equal(Jita, s.StationId));
        Assert.DoesNotContain(stacks, s => s.TypeId == 34);

        var (cargo, ore) = SellFinder.UsedHold(assets, ship, Sde);
        Assert.Equal(5_000 * 0.015, cargo, 6); // Only cargo counts; the fitted module doesn't take cargo space.
        Assert.Equal(900, ore, 6);
    }
}
