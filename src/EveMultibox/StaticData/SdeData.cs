namespace EveMultibox.StaticData;

public sealed record TypeInfo(int Id, string Name, double Volume, double PackagedVolume, double Capacity, int GroupId, int CategoryId, int? MarketGroupId,
    double OreHold = 0)
{
    public const int SkillCategory = 16;
    public const int ShipCategory = 6;
    public const int AsteroidCategory = 25;

    /// <summary>Ore goes in a ship's mining hold when it has one.</summary>
    public bool IsOre => CategoryId == AsteroidCategory;

    /// <summary>Volume when hauled packaged (ships and some modules are much smaller packaged).</summary>
    public double HaulVolume => PackagedVolume > 0 ? PackagedVolume : Volume;
}

public sealed record SystemInfo(int Id, string Name, double Security, int RegionId, int[] Neighbors)
{
    /// <summary>Security as shown in game: rounded to one decimal, with anything just above 0 shown as 0.1.</summary>
    public double DisplaySecurity => Security > 0 && Security < 0.05 ? 0.1 : Math.Round(Security, 1, MidpointRounding.AwayFromZero);
}

public sealed record StationInfo(long Id, int SystemId, int OwnerCorpId, int? FactionId);

/// <summary>The slice of CCP's static data export the trade module uses.</summary>
public sealed class SdeData
{
    public int BuildNumber { get; init; }
    public Dictionary<int, TypeInfo> Types { get; init; } = new();
    public Dictionary<int, SystemInfo> Systems { get; init; } = new();
    public Dictionary<long, StationInfo> Stations { get; init; } = new();
    public Dictionary<int, string> Regions { get; init; } = new();

    private Dictionary<string, int>? _skillIds;
    private Dictionary<string, int>? _systemIds;

    public int? SkillId(string name)
    {
        _skillIds ??= Types.Values.Where(t => t.CategoryId == TypeInfo.SkillCategory)
            .GroupBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Id, StringComparer.OrdinalIgnoreCase);
        return _skillIds.TryGetValue(name, out int id) ? id : null;
    }

    public SystemInfo? SystemByName(string name)
    {
        _systemIds ??= Systems.Values.GroupBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Id, StringComparer.OrdinalIgnoreCase);
        return _systemIds.TryGetValue(name.Trim(), out int id) ? Systems[id] : null;
    }

    public string TypeName(int typeId) => Types.TryGetValue(typeId, out var t) ? t.Name : $"Type {typeId}";

    public string SystemName(int systemId) => Systems.TryGetValue(systemId, out var s) ? s.Name : $"System {systemId}";

    public string RegionName(int regionId) => Regions.TryGetValue(regionId, out var r) ? r : $"Region {regionId}";
}
