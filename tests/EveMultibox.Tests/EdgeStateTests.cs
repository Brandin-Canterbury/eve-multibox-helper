using System.IO;
using EveMultibox.Alerts;
using EveMultibox.Clients;
using EveMultibox.Edge;
using EveMultibox.StaticData;
using EveMultibox.Trade;

namespace EveMultibox.Tests;

public class EdgeStateTests
{
    private static SdeData Sde() => new()
    {
        Systems = { [30000142] = new SystemInfo(30000142, "Jita", 0.9459, 10000002, []) },
        Regions = { [10000002] = "The Forge" },
        Types = { [648] = new TypeInfo(648, "Badger", 0, 0, 3900, 28, 6, null) },
    };

    [Fact]
    public void Builds_clients_characters_and_alerts_for_the_edge()
    {
        var hauler = new EveClient(new IntPtr(1), 1) { CharacterName = "Phoenix Hauler" };
        var miner = new EveClient(new IntPtr(2), 2) { CharacterName = "Phoenix Miner" };
        var login = new EveClient(new IntPtr(3), 3);
        var character = new CharacterState(90000001, "Phoenix Hauler")
        {
            Online = true, SystemId = 30000142, StationId = 60003760, ShipTypeId = 648, ShipName = "Big Box",
            CargoCapacity = 3900, Wallet = 1_234_567.89,
        };
        var offline = new CharacterState(90000002, "Phoenix Alt") { Online = false };
        var rule = AlertRule.Defaults().First();
        var alert = new Alert(rule, "Phoenix Miner", "Warp scrambled by a pirate", new DateTime(2026, 10, 25, 12, 0, 0, DateTimeKind.Utc));

        var state = EdgeStateBuilder.Build(
            [hauler, miner, login], hauler, [character, offline], [alert], Sde(),
            id => id == 60003760 ? "Jita IV - Moon 4 - Caldari Navy Assembly Plant" : $"Station {id}",
            [], [], null);

        Assert.Equal("Phoenix Hauler", state.ActiveCharacter);
        Assert.Equal(3, state.Clients.Count);
        Assert.True(state.Clients[0].Active);
        Assert.Equal("Warp scrambled by a pirate", state.Clients[1].Alert?.Text);
        Assert.Null(state.Clients[2].Character); // the login screen

        var c = state.Characters[0];
        Assert.True(c.HasClient);
        Assert.True(c.Active);
        Assert.Equal("Jita", c.SystemName);
        Assert.Equal(0.9, c.Security);
        Assert.Equal("The Forge", c.Region);
        Assert.True(c.Docked);
        Assert.Equal("Jita IV - Moon 4 - Caldari Navy Assembly Plant", c.Location);
        Assert.Equal("Badger", c.ShipType);
        Assert.Equal("Big Box", c.ShipName);

        Assert.False(state.Characters[1].HasClient);
        Assert.Equal("Locating…", state.Characters[1].Location);
        Assert.Single(state.Alerts);
    }

    [Fact]
    public void Settings_get_a_random_key_once_and_keep_it()
    {
        string path = Path.Combine(Path.GetTempPath(), $"edge-api-{Guid.NewGuid():N}.json");
        try
        {
            var first = EdgeApiSettings.LoadOrCreate(path);
            Assert.Equal(48, first.Key.Length);
            Assert.Equal(first.Key, EdgeApiSettings.LoadOrCreate(path).Key);
            Assert.Equal(31416, first.Port);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
