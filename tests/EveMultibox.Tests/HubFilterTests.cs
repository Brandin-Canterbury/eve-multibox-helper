using EveMultibox.Trade;

namespace EveMultibox.Tests;

/// <summary>From / To hub filters for hub-to-hub routes, e.g. the trip back after a Jita → Amarr run.</summary>
public class HubFilterTests
{
    private const long Jita = 60003760, Amarr = 60008494, Dodixie = 60011866;

    private static RouteCandidate Route(int type, long from, long to, double netEach = 50) =>
        new(type, $"Item {type}", from, to, 100, 1, 100, 10_000, 100 + netEach, (100 + netEach) * 100, 0, 0, netEach * 100, 0, 10, null, false, false);

    private static readonly RouteCandidate[] All =
    {
        Route(1, Jita, Amarr), Route(2, Amarr, Jita), Route(3, Amarr, Jita), Route(4, Jita, Dodixie), Route(5, Dodixie, Amarr),
    };

    [Fact]
    public void NoFilterKeepsEverything() =>
        Assert.Equal(All.Length, All.Count(r => RouteFilter.Matches(r, null, null)));

    [Fact]
    public void FromAndToPickOneDirection() =>
        Assert.Equal(new[] { 2, 3 }, All.Where(r => RouteFilter.Matches(r, Amarr, Jita)).Select(r => r.TypeId));

    [Fact]
    public void OnlyFromOrOnlyTo()
    {
        Assert.Equal(new[] { 1, 4 }, All.Where(r => RouteFilter.Matches(r, Jita, null)).Select(r => r.TypeId));
        Assert.Equal(new[] { 1, 5 }, All.Where(r => RouteFilter.Matches(r, null, Amarr)).Select(r => r.TypeId));
    }

    [Fact]
    public void AutoFillOnTheFilteredListPacksTheReturnTrip()
    {
        // The Jita → Amarr item is the most profitable overall, but the filter shows only Amarr → Jita.
        var all = new[] { Route(1, Jita, Amarr, netEach: 500), Route(2, Amarr, Jita), Route(3, Amarr, Jita, netEach: 30) };
        var visible = all.Where(r => RouteFilter.Matches(r, Amarr, Jita));

        var run = new TradeRun(1_000, 0, 1e9, _ => false);
        run.AutoFill(visible);

        Assert.Equal(Amarr, run.SourceStation);
        Assert.Equal(Jita, run.DestStation);
        Assert.Equal(new[] { 2, 3 }, run.Items.Select(i => i.TypeId).Order());
    }
}
