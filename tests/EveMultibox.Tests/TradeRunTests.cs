using EveMultibox.Trade;

namespace EveMultibox.Tests;

public class TradeRunTests
{
    private const long Jita = 60003760, Amarr = 60008494, Dodixie = 60011866;
    private const int Ore = 1230;

    /// <summary>A route offering <paramref name="units"/> units: buy at <paramref name="buy"/>, net ~<paramref name="netEach"/> profit each.</summary>
    private static RouteCandidate Route(int type, double volume, long units, double buy, double netEach, long src = Jita, long dst = Amarr)
    {
        double seed = buy * units, revenue = (buy + netEach) * units;
        return new RouteCandidate(type, $"Item {type}", src, dst, units, volume, buy, seed, buy + netEach, revenue, 0, 0,
            revenue - seed, 0, 11, null, false, false);
    }

    private static TradeRun Run(double cargo = 33_000, double ore = 0, double cash = 1e12) => new(cargo, ore, cash, t => t == Ore);

    [Fact]
    public void SpaceIsMeasuredAgainstTheHoldBeforeTheRun()
    {
        var run = Run(cargo: 33_000);
        Assert.Null(run.TryAdd(Route(1, volume: 10, units: 2_000, buy: 100, netEach: 50))); // 20,000 m³
        Assert.Equal(13_000, run.CargoLeft, 6);

        // Wants 2,000 × 10 m³ but only 13,000 m³ are left: 1,300 units.
        Assert.Null(run.TryAdd(Route(2, volume: 10, units: 2_000, buy: 100, netEach: 50)));
        Assert.Equal(1_300, run.Items[1].Units);
        Assert.Equal(0, run.CargoLeft, 6);
        Assert.Equal(33_000, run.CargoUsed, 6);
    }

    [Fact]
    public void CashRunsOutToo()
    {
        var run = Run(cash: 150_000);
        Assert.Null(run.TryAdd(Route(1, 1, units: 1_000, buy: 100, netEach: 20)));   // 100,000 ISK
        Assert.Null(run.TryAdd(Route(2, 1, units: 1_000, buy: 100, netEach: 20)));   // only 500 affordable
        Assert.Equal(500, run.Items[1].Units);
        Assert.Equal(0, run.CashLeft, 6);
        Assert.NotNull(run.TryAdd(Route(3, 1, units: 10, buy: 100, netEach: 20))); // Nothing left.
    }

    [Fact]
    public void OneBuyStationAndOneSellStationPerRun()
    {
        var run = Run();
        Assert.Null(run.TryAdd(Route(1, 1, 10, 100, 50)));
        Assert.Contains("different", run.TryAdd(Route(2, 1, 10, 100, 50, dst: Dodixie)));
        Assert.Contains("already", run.TryAdd(Route(1, 1, 10, 100, 50)));
    }

    [Fact]
    public void OreGoesInTheMiningHold()
    {
        var run = Run(cargo: 100, ore: 5_000);
        Assert.Null(run.TryAdd(Route(Ore, 0.1, units: 100_000, buy: 10, netEach: 2)));
        Assert.Equal(50_000, run.Items[0].Units);   // 5,000 m³ of mining hold.
        Assert.Equal(100, run.CargoLeft, 6);          // Cargo untouched.
    }

    [Fact]
    public void RemovingAnItemFreesItsSpace()
    {
        var run = Run(cargo: 1_000);
        run.TryAdd(Route(1, 1, 600, 10, 5));
        run.Remove(run.Items[0]);
        Assert.Equal(1_000, run.CargoLeft, 6);
        Assert.Null(run.SourceStation);
    }

    [Fact]
    public void AutoFillPicksTheBestPairAndPacksIt()
    {
        var run = Run(cargo: 1_000, cash: 1e9);
        var candidates = new[]
        {
            // Jita → Amarr: two items that together fill the hold.
            Route(1, 1, 600, 100, 30),
            Route(2, 1, 600, 100, 25),
            // Jita → Dodixie: one great item, but alone it only uses 200 m³.
            Route(3, 1, 200, 100, 60, dst: Dodixie),
        };

        Assert.Equal(2, run.AutoFill(candidates));
        Assert.Equal(Amarr, run.DestStation);
        // The scaled-down second item pays EVE's 100 ISK minimum broker fee.
        Assert.Equal(600 * 30 + 400 * 25 - Fees.MinBrokerFeeIsk, run.Profit, 3);
        Assert.Equal(0, run.CargoLeft, 6);
    }

    [Fact]
    public void AutoFillTopsUpAnExistingRunOnItsOwnPair()
    {
        var run = Run(cargo: 1_000);
        run.TryAdd(Route(3, 1, 200, 100, 60, dst: Dodixie));
        run.AutoFill(new[] { Route(1, 1, 600, 100, 30), Route(4, 1, 900, 100, 10, dst: Dodixie) });
        Assert.All(run.Items, i => Assert.Equal(Dodixie, i.DestStation));
        Assert.Equal(800, run.Items.Single(i => i.TypeId == 4).Units);
    }
}
