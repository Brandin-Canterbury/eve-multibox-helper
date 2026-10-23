using EveMultibox.Trade;

namespace EveMultibox.Tests;

/// <summary>Switching the "For" character re-sizes routes and the run for that character's hold and wallet.</summary>
public class CharacterSwitchTests
{
    private const long Jita = 60003760, Amarr = 60008494, Dodixie = 60011866;
    private static readonly long[] Hubs = { Jita, Amarr, Dodixie };

    private static MarketCache Cache(SearchMode mode = SearchMode.HubToHub, bool safe = false, long[]? stations = null) =>
        new(mode, safe, stations ?? Hubs, new MarketSnapshot(Array.Empty<MarketOrder>(), _ => true, DateTimeOffset.UtcNow), new());

    [Fact]
    public void HubMarketDataCoversAnyCharacter() =>
        Assert.True(Cache().Covers(SearchMode.HubToHub, false, new[] { Dodixie, Jita, Amarr })); // Order doesn't matter.

    [Fact]
    public void DifferentModeOrSafetyNeedsFreshData()
    {
        Assert.False(Cache().Covers(SearchMode.WithinJumps, false, Hubs));
        Assert.False(Cache().Covers(SearchMode.HubToHub, true, Hubs));
    }

    [Fact]
    public void RadiusSearchOnlyReusedWhenTheNewCharacterReachesTheSameStations()
    {
        var cache = Cache(SearchMode.WithinJumps, stations: new[] { Jita, Amarr });
        Assert.True(cache.Covers(SearchMode.WithinJumps, false, new[] { Amarr, Jita }));
        Assert.False(cache.Covers(SearchMode.WithinJumps, false, new[] { Jita }));            // Fewer stations in range.
        Assert.False(cache.Covers(SearchMode.WithinJumps, false, new[] { Jita, Dodixie }));   // Different stations.
    }

    private static RouteCandidate Route(long units, double volume, double buy) =>
        new(1, "Item", Jita, Amarr, units, volume, buy, units * buy, buy * 2, units * buy * 2, 0, 0, units * buy, 0, 11, null, false, false);

    [Fact]
    public void TheSameRouteFitsDifferentlyForEachCharactersHold()
    {
        // Kayla: 435 m³ cargo (mining ship). The Pheonix: 7,300 m³ cargo.
        var kayla = new TradeRun(435, 27_500, 1.44e9, _ => false);
        var pheonix = new TradeRun(7_300, 0, 1.44e9, _ => false);
        var route = Route(units: 10_000, volume: 1, buy: 100);

        Assert.Equal(435, kayla.UnitsThatFit(route));
        Assert.Equal(7_300, pheonix.UnitsThatFit(route));
    }

    [Fact]
    public void TheSameRouteFitsDifferentlyForEachCharactersWallet()
    {
        var rich = new TradeRun(1e6, 0, 1_000_000, _ => false);
        var poor = new TradeRun(1e6, 0, 50_000, _ => false);
        var route = Route(units: 10_000, volume: 0.01, buy: 100);

        Assert.Equal(10_000, rich.UnitsThatFit(route));
        Assert.Equal(500, poor.UnitsThatFit(route));
    }
}
