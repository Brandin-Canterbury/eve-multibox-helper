using System.IO;
using System.IO.Compression;
using System.Text;
using EveMultibox.StaticData;
using EveMultibox.Trade;

namespace EveMultibox.Tests;

public sealed class TradeFoundationTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "EveMultiboxTests", Guid.NewGuid().ToString("N"));

    public TradeFoundationTests() => Directory.CreateDirectory(_dir);

    // ---- fees and skills ----

    [Theory]
    [InlineData(0, 0.075)]
    [InlineData(1, 0.06675)]
    [InlineData(4, 0.042)]
    [InlineData(5, 0.03375)]
    [InlineData(9, 0.03375)] // Clamped to level 5.
    public void SalesTaxFallsElevenPercentPerAccountingLevel(int level, double expected) =>
        Assert.Equal(expected, Fees.SalesTax(level), 6);

    [Fact]
    public void BrokerFeeUsesSkillAndStandings()
    {
        Assert.Equal(0.03, Fees.BrokerFee(0, 0, 0), 6);
        Assert.Equal(0.015, Fees.BrokerFee(5, 0, 0), 6);
        Assert.Equal(0.015 - 0.0003 * 5 - 0.0002 * 7, Fees.BrokerFee(5, 5, 7), 6);
        Assert.Equal(0.033, Fees.BrokerFee(0, -10, 0), 6); // Bad standings cost more.
        Assert.Equal(100, Fees.BrokerFeeIsk(1000, 0.03)); // Minimum fee.
    }

    [Fact]
    public void SkillProfileReadsSkillsByNameAndStandingsByStationOwner()
    {
        var sde = Fixture();
        var levels = new Dictionary<int, int> { [16622] = 4, [3446] = 3, [3443] = 5, [3444] = 2, [16598] = 2 };
        var profile = SkillProfile.From(levels, sde, new Dictionary<int, double> { [1000035] = 2.0, [500001] = 4.0 });

        Assert.Equal(4, profile.Accounting);
        Assert.Equal(3, profile.BrokerRelations);
        Assert.Equal(5 + 4 * 5 + 8 * 2, profile.OrderSlots);
        Assert.Equal(5, profile.RemoteSellJumps);
        Assert.Null(profile.RemoteBuyJumps); // No Procurement.

        var jita44 = sde.Stations[60003760];
        Assert.Equal(Fees.BrokerFee(3, 4.0, 2.0), profile.BrokerFeeAt(jita44), 9);
    }

    [Theory]
    [InlineData(0, "station only")]
    [InlineData(1, "same system")]
    [InlineData(3, "10 jumps")]
    [InlineData(5, "whole region")]
    public void RemoteRangeText(int level, string expected) =>
        Assert.Equal(expected, SkillProfile.RangeText(SkillProfile.RangeJumps(level)));

    // ---- static data ----

    [Fact]
    public void BuildsFromSdeZipKeepingOnlyTradeData()
    {
        var sde = Fixture();
        Assert.Equal(3632936, sde.BuildNumber);
        Assert.Equal("Tritanium", sde.TypeName(34));
        Assert.Equal(3900, sde.Types[648].Capacity);
        Assert.Equal(20000, sde.Types[648].HaulVolume); // Packaged.
        Assert.False(sde.Types.ContainsKey(999)); // Unpublished.
        Assert.False(sde.Types.ContainsKey(998)); // Not on the market and not a skill.
        Assert.Equal(16622, sde.SkillId("accounting"));
        Assert.Equal("The Forge", sde.RegionName(10000002));
        Assert.Equal(new[] { 30000140, 30000144 }, sde.Systems[30000142].Neighbors);
        Assert.Equal(500001, sde.Stations[60003760].FactionId);
        Assert.Equal(30000142, sde.SystemByName("jita")!.Id);
    }

    [Fact]
    public void CacheRoundTrips()
    {
        var sde = Fixture();
        string path = Path.Combine(_dir, "cache.json.gz");
        SdeStore.SaveCache(path, sde);
        var loaded = SdeStore.LoadCache(path)!;
        Assert.Equal(sde.BuildNumber, loaded.BuildNumber);
        Assert.Equal(sde.Types[648], loaded.Types[648]);
        Assert.Equal(sde.Systems[30000142].Neighbors, loaded.Systems[30000142].Neighbors);
        Assert.Equal(sde.Stations[60003760], loaded.Stations[60003760]);
    }

    [Fact]
    public void ParsesLatestBuildRecord() =>
        Assert.Equal(3632936, SdeStore.ParseBuildNumber("""{"_key": "sde", "buildNumber": 3632936, "releaseDate": "2026-10-20T11:08:17Z"}""" + "\n"));

    [Theory]
    [InlineData(0.9459, 0.9)]
    [InlineData(0.45, 0.5)]
    [InlineData(0.04, 0.1)]   // Anything just above zero shows as 0.1.
    [InlineData(-0.3, -0.3)]
    public void DisplaySecurityRoundsLikeTheGame(double raw, double shown) =>
        Assert.Equal(shown, new SystemInfo(1, "X", raw, 1, Array.Empty<int>()).DisplaySecurity, 6);

    // ---- jumps ----

    [Fact]
    public void JumpGraphFindsSystemsWithinRadiusAndAvoidsLowSec()
    {
        // Jita - Perimeter - New Caldari - Lowsec - Beyond, plus Jita - Maurasi.
        var sde = Fixture();
        var graph = new JumpGraph(sde.Systems);

        var within1 = graph.Within(30000142, 1);
        Assert.Equal(new[] { 30000140, 30000142, 30000144 }, within1.Keys.Order());

        Assert.Equal(3, graph.Jumps(30000142, 30000001));
        Assert.Null(graph.Jumps(30000142, 30000002, minSecurity: 0.5)); // Only reachable through lowsec.
        Assert.Equal(4, graph.Jumps(30000142, 30000002));
        Assert.Equal(0, graph.Jumps(30000142, 30000142));
    }

    /// <summary>
    /// Parses a real downloaded SDE zip when EVE_SDE_ZIP points at one (skipped otherwise, as the file is ~100 MB).
    /// </summary>
    [Fact]
    public void ParsesRealSdeZipWhenAvailable()
    {
        string? path = Environment.GetEnvironmentVariable("EVE_SDE_ZIP");
        if (string.IsNullOrEmpty(path) || !System.IO.File.Exists(path)) return;

        using var zip = System.IO.File.OpenRead(path);
        var sde = SdeStore.Build(zip);
        Assert.Equal("Tritanium", sde.TypeName(34));
        Assert.Equal("Jita", sde.SystemName(30000142));
        Assert.Equal(30000142, sde.Stations[60003760].SystemId);
        Assert.NotNull(sde.SkillId("Accounting"));
        Assert.NotNull(sde.SkillId("Broker Relations"));
        Assert.True(new JumpGraph(sde.Systems).Jumps(30000142, 30002187) > 5); // Jita → Amarr is reachable, and far.
        Console.WriteLine($"Real SDE: {sde.Types.Count} types, {sde.Systems.Count} systems, {sde.Stations.Count} stations, build {sde.BuildNumber}");
    }

    /// <summary>A tiny SDE zip in the real JSON Lines format.</summary>
    private static SdeData Fixture()
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            void File(string name, params string[] lines)
            {
                using var w = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false));
                foreach (var l in lines) w.WriteLine(l);
            }

            File("_sde.jsonl", """{"_key": "sde", "buildNumber": 3632936, "releaseDate": "2026-10-20T11:08:17Z"}""");
            File("groups.jsonl",
                """{"_key": 18, "categoryID": 4, "name": {"en": "Mineral"}}""",
                """{"_key": 28, "categoryID": 6, "name": {"en": "Hauler"}}""",
                """{"_key": 274, "categoryID": 16, "name": {"en": "Trade"}}""",
                """{"_key": 999, "categoryID": 2, "name": {"en": "Celestial"}}""");
            File("types.jsonl",
                """{"_key": 34, "groupID": 18, "marketGroupID": 1857, "name": {"en": "Tritanium", "de": "Tritanium"}, "published": true, "volume": 0.01, "packagedVolume": 0.01}""",
                """{"_key": 648, "groupID": 28, "marketGroupID": 84, "name": {"en": "Badger"}, "published": true, "volume": 250000.0, "packagedVolume": 20000.0, "capacity": 3900.0}""",
                """{"_key": 16622, "groupID": 274, "name": {"en": "Accounting"}, "published": true, "volume": 0.01}""",
                """{"_key": 3446, "groupID": 274, "name": {"en": "Broker Relations"}, "published": true}""",
                """{"_key": 3443, "groupID": 274, "name": {"en": "Trade"}, "published": true}""",
                """{"_key": 3444, "groupID": 274, "name": {"en": "Retail"}, "published": true}""",
                """{"_key": 16598, "groupID": 274, "name": {"en": "Marketing"}, "published": true}""",
                """{"_key": 999, "groupID": 18, "marketGroupID": 1, "name": {"en": "Hidden"}, "published": false}""",
                """{"_key": 998, "groupID": 999, "name": {"en": "Sun"}, "published": true}""");
            File("mapRegions.jsonl", """{"_key": 10000002, "name": {"en": "The Forge"}}""");
            File("mapSolarSystems.jsonl",
                """{"_key": 30000142, "name": {"en": "Jita"}, "regionID": 10000002, "securityStatus": 0.9459}""",
                """{"_key": 30000144, "name": {"en": "Perimeter"}, "regionID": 10000002, "securityStatus": 0.9}""",
                """{"_key": 30000140, "name": {"en": "Maurasi"}, "regionID": 10000002, "securityStatus": 0.9}""",
                """{"_key": 30000145, "name": {"en": "New Caldari"}, "regionID": 10000002, "securityStatus": 1.0}""",
                """{"_key": 30000001, "name": {"en": "Lowsec"}, "regionID": 10000002, "securityStatus": 0.3}""",
                """{"_key": 30000002, "name": {"en": "Beyond"}, "regionID": 10000002, "securityStatus": 0.7}""");
            File("mapStargates.jsonl",
                Gate(1, 30000142, 30000144), Gate(2, 30000144, 30000142),
                Gate(3, 30000142, 30000140), Gate(4, 30000140, 30000142),
                Gate(5, 30000144, 30000145), Gate(6, 30000145, 30000144),
                Gate(7, 30000145, 30000001), Gate(8, 30000001, 30000145),
                Gate(9, 30000001, 30000002), Gate(10, 30000002, 30000001));
            File("npcCorporations.jsonl",
                """{"_key": 1000035, "factionID": 500001, "name": {"en": "Caldari Navy"}}""",
                """{"_key": 1000002, "name": {"en": "No Faction Corp"}}""");
            File("npcStations.jsonl",
                """{"_key": 60003760, "ownerID": 1000035, "solarSystemID": 30000142, "typeID": 1531}""");
        }
        ms.Position = 0;
        return SdeStore.Build(ms);
    }

    private static string Gate(int id, int from, int to) =>
        "{\"_key\": " + (50000000 + id) + ", \"solarSystemID\": " + from + ", \"destination\": {\"solarSystemID\": " + to + ", \"stargateID\": 1}}";

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }
}
