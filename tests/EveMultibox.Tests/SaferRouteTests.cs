using EveMultibox.Edge;
using EveMultibox.StaticData;
using EveMultibox.Trade;

namespace EveMultibox.Tests;

public class SaferRouteTests
{
    // A(1.0) - L(0.3) - B(1.0) is two jumps through low-sec; A - H1 - H2 - H3 - B is four in high-sec.
    // C(0.9) hangs off L only, so getting there can't avoid low-sec.
    private static Dictionary<int, SystemInfo> Systems() => new()
    {
        [1] = new SystemInfo(1, "A", 1.0, 1, [2, 3]),
        [2] = new SystemInfo(2, "L", 0.3, 1, [1, 6, 7]),
        [3] = new SystemInfo(3, "H1", 0.8, 1, [1, 4]),
        [4] = new SystemInfo(4, "H2", 0.7, 1, [3, 5]),
        [5] = new SystemInfo(5, "H3", 0.6, 1, [4, 6]),
        [6] = new SystemInfo(6, "B", 1.0, 1, [2, 5]),
        [7] = new SystemInfo(7, "C", 0.9, 1, [2]),
    };

    [Fact]
    public void Safer_routes_go_around_low_sec_when_they_can()
    {
        var graph = new JumpGraph(Systems());
        Assert.Equal(2, graph.Jumps(1, 6)); // the shortest route cuts through L
        Assert.Equal(new JumpGraph.RouteSummary(4, 0), graph.Safer(1, 6));
        Assert.Equal(new JumpGraph.RouteSummary(2, 1), graph.Safer(1, 7)); // no way round, so through as little as possible
        Assert.Equal(new JumpGraph.RouteSummary(0, 0), graph.Safer(1, 1));
        Assert.Null(graph.Safer(1, 99));
    }

    [Fact]
    public void Ties_are_not_flagged_but_being_beaten_is()
    {
        var sell = new CharacterOrder(1, 34, 1, 60003760, "station", false, 4.12, 100, 50, DateTime.UtcNow, 90);
        Assert.Equal(("Tied lowest", false), OrderStanding.Describe(sell, 4.12));
        Assert.True(OrderStanding.Describe(sell, 4.11).NeedsAttention);
        var buy = sell with { IsBuyOrder = true };
        Assert.Equal(("Tied highest", false), OrderStanding.Describe(buy, 4.12));
        Assert.True(OrderStanding.Describe(buy, 4.13).NeedsAttention);
    }

    [Fact]
    public void Destination_shows_the_safer_jumps_left()
    {
        var sde = new SdeData { Systems = { } };
        foreach (var (id, s) in Systems()) sde.Systems[id] = s;
        sde.Stations[60000001] = new StationInfo(60000001, 6, 1, null);
        var pilot = new CharacterState(9, "Pilot") { SystemId = 1 };

        var d = EdgeStateBuilder.Destination(pilot, 60000001, sde, new JumpGraph(sde.Systems), _ => "B I - Trade Hub", saferRoutes: true)!;
        Assert.Equal("B I - Trade Hub", d.Name);
        Assert.Equal("B", d.SystemName);
        Assert.Equal(4, d.Jumps);
        Assert.Equal(0, d.LowSecSystems);
        Assert.False(d.Arrived);

        var shortest = EdgeStateBuilder.Destination(pilot, 60000001, sde, new JumpGraph(sde.Systems), _ => "x", saferRoutes: false)!;
        Assert.Equal(2, shortest.Jumps);

        pilot.SystemId = 6;
        pilot.StationId = 60000001;
        Assert.True(EdgeStateBuilder.Destination(pilot, 60000001, sde, new JumpGraph(sde.Systems), _ => "x", true)!.Arrived);
        Assert.Null(EdgeStateBuilder.Destination(pilot, null, sde, null, _ => "x", true));
    }
}
