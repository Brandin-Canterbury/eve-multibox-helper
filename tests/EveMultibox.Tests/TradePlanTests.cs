using EveMultibox.StaticData;
using EveMultibox.Trade;
using EveMultibox.Widget;

namespace EveMultibox.Tests;

public class TradePlanTests
{
    private const long Jita = 60003760, Dodixie = 60011866, Amarr = 60008494;

    private static readonly SdeData Sde = new()
    {
        Types = { [2629] = new TypeInfo(2629, "Inferno Light Missile", 0.015, 0.015, 0, 384, 8, 920) },
        Systems =
        {
            [30000142] = new SystemInfo(30000142, "Jita", 0.95, 10000002, new[] { 30002659 }),
            [30002659] = new SystemInfo(30002659, "Dodixie", 0.87, 10000032, new[] { 30000142 }),
            [30002187] = new SystemInfo(30002187, "Amarr", 1.0, 10000043, Array.Empty<int>()),
        },
        Stations =
        {
            [Jita] = new StationInfo(Jita, 30000142, 1000035, 500001),
            [Dodixie] = new StationInfo(Dodixie, 30002659, 1000120, 500004),
            [Amarr] = new StationInfo(Amarr, 30002187, 1000086, 500003),
        },
    };

    private static RouteRow Route() =>
        new(new RouteCandidate(2629, "Inferno Light Missile", Jita, Dodixie, 10_000, 0.015, 100, 1_000_000, 199,
            1_990_000, 67_162.5, 29_850, 892_987.5, 0, 15, 170, false, false), Sde, id => $"Station {id}");

    private static CharacterState Char(long? station, int system = 30000142) =>
        new(1, "The Pheonix") { StationId = station, SystemId = system };

    private static IEnumerable<string> Titles(ActionPlan p) => p.Steps.Select(s => s.Title.Split(' ')[0]);

    [Fact]
    public void RouteFromTheBuyStationGoesBuyUndockTravelSell()
    {
        var plan = TradePlans.ForRoute(Route(), Char(Jita), Sde);
        Assert.Equal(new[] { "Buy", "Undock", "Travel", "List" }, Titles(plan));

        var buy = plan.Steps[0];
        Assert.Contains(buy.Copies, c => c.Value == "10000");
        Assert.Contains("break-even", buy.Instruction);

        var list = plan.Steps[^1];
        Assert.Contains(list.Copies, c => c.Value == "199.00");
    }

    [Fact]
    public void RouteFromElsewhereTravelsToTheBuyStationFirst()
    {
        var plan = TradePlans.ForRoute(Route(), Char(Amarr, 30002187), Sde);
        Assert.Equal(new[] { "Undock", "Travel", "Buy", "Undock", "Travel", "List" }, Titles(plan));

        var travel = plan.Steps[1];
        Assert.Equal(Jita, travel.DestinationId);
        Assert.Contains(GameKey.JumpOrDock, travel.Keys);
        Assert.False(travel.CompleteWhen!(new PlanContext(30000142, null, null)));   // In Jita, not docked yet.
        Assert.True(travel.CompleteWhen!(new PlanContext(30000142, Jita, null)));    // Docked: advance.
    }

    [Fact]
    public void RouteFromSpaceSkipsTheFirstUndock()
    {
        var plan = TradePlans.ForRoute(Route(), Char(null, 30002659), Sde);
        Assert.Equal("Travel", Titles(plan).First());
    }

    [Fact]
    public void UndockCompletesOnceInSpace()
    {
        var undock = TradePlans.ForRoute(Route(), Char(Jita), Sde).Steps[1];
        Assert.Contains(GameKey.Undock, undock.Keys);
        Assert.False(undock.CompleteWhen!(new PlanContext(30000142, Jita, null)));
        Assert.True(undock.CompleteWhen!(new PlanContext(30000142, null, null)));
    }

    [Fact]
    public void RunPlanBuysEverythingThenSellsEverythingInOneTrip()
    {
        var run = new TradeRun(33_000, 0, 1e9, _ => false);
        run.TryAdd(new RouteCandidate(2629, "Inferno Light Missile", Jita, Dodixie, 10_000, 0.015, 100, 1_000_000, 199,
            1_990_000, 0, 0, 990_000, 0, 15, null, false, false));
        run.TryAdd(new RouteCandidate(34, "Tritanium", Jita, Dodixie, 100_000, 0.01, 4, 400_000, 6,
            600_000, 0, 0, 200_000, 0, 15, null, false, false));

        var plan = TradePlans.ForRun(run, Char(Amarr, 30002187), Sde, id => $"Station {id}");
        Assert.Equal(new[] { "Undock", "Travel", "Buy", "Buy", "Undock", "Travel", "Sell", "Sell" }, Titles(plan));
        Assert.Equal(Jita, plan.Steps[1].DestinationId);
        Assert.Equal(Dodixie, plan.Steps[5].DestinationId);
        Assert.StartsWith("Buy 2/2: 100,000", plan.Steps[3].Title);
        Assert.Contains(plan.Steps[7].Copies, c => c.Value == "6.00");
    }

    private static SellRow Sale(AssetStack stack, long sellAt, bool instant) =>
        new(instant ? new SellOption(stack, "Inferno Light Missile", sellAt, true, stack.Quantity, 125, 125_000, 4_000, 0, 121_000, 9) : null,
            instant ? null : new SellOption(stack, "Inferno Light Missile", sellAt, false, stack.Quantity, 199, 199_000, 6_000, 3_000, 190_000, 9),
            Sde, id => $"Station {id}");

    [Fact]
    public void SellingCargoAtTheCurrentStationIsOneStep()
    {
        var row = Sale(new AssetStack(2629, 1_000, Jita, 30000142, InCurrentShip: true), Jita, instant: true);
        var plan = TradePlans.ForSale(row, Char(Jita), Sde);
        Assert.Equal(new[] { "Sell" }, Titles(plan));
    }

    [Fact]
    public void SellingHangarItemsElsewhereFetchesThemFirst()
    {
        // Items sit in Jita, you're docked in Amarr, best sale is in Dodixie.
        var row = Sale(new AssetStack(2629, 1_000, Jita, 30000142, InCurrentShip: false), Dodixie, instant: false);
        var plan = TradePlans.ForSale(row, Char(Amarr, 30002187), Sde);
        Assert.Equal(new[] { "Undock", "Travel", "Load", "Undock", "Travel", "List" }, Titles(plan));
        Assert.Equal(Jita, plan.Steps[1].DestinationId);
        Assert.Equal(Dodixie, plan.Steps[4].DestinationId);
    }

    [Fact]
    public void SellingHangarItemsWhereTheyAreSkipsTravel()
    {
        var row = Sale(new AssetStack(2629, 1_000, Jita, 30000142, InCurrentShip: false), Jita, instant: true);
        var plan = TradePlans.ForSale(row, Char(Jita), Sde);
        Assert.Equal(new[] { "Load", "Sell" }, Titles(plan));
    }

    [Fact]
    public void SoldPerWeekIsTheLastSevenDaysTotal()
    {
        var row = Route();
        Assert.Equal("…", row.WeeklyVolume);
        row.Daily = 1_234.5;
        Assert.Equal(8_641.5, row.Weekly);
        Assert.Equal("8,642", row.WeeklyVolume);
        Assert.Contains(row.PnlLines, l => l.Label == "Sold last week there" && l.Value == "8,642");
    }
}
