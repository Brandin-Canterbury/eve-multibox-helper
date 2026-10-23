using EveMultibox.StaticData;

namespace EveMultibox.Trade;

/// <summary>A character's trade-relevant skills and standings, and what they mean for fees and reach.</summary>
public sealed record SkillProfile(
    int Accounting = 0,
    int BrokerRelations = 0,
    int AdvancedBrokerRelations = 0,
    int Trade = 0,
    int Retail = 0,
    int Wholesale = 0,
    int Tycoon = 0,
    int Marketing = 0,
    int Procurement = 0,
    int Daytrading = 0,
    int Visibility = 0)
{
    public static readonly SkillProfile None = new();

    /// <summary>Standings by NPC corporation or faction ID.</summary>
    public IReadOnlyDictionary<int, double> Standings { get; init; } = new Dictionary<int, double>();

    public double SalesTax => Fees.SalesTax(Accounting);

    public double BrokerFeeAt(StationInfo station)
    {
        double corp = Standings.GetValueOrDefault(station.OwnerCorpId);
        double faction = station.FactionId is int f ? Standings.GetValueOrDefault(f) : 0;
        return Fees.BrokerFee(BrokerRelations, faction, corp);
    }

    /// <summary>Max open market orders: 5, plus 4/8/16/32 per level of Trade/Retail/Wholesale/Tycoon.</summary>
    public int OrderSlots => 5 + 4 * Trade + 8 * Retail + 16 * Wholesale + 32 * Tycoon;

    /// <summary>How far away you can create sell orders (Marketing). null = only in your current station.</summary>
    public int? RemoteSellJumps => RangeJumps(Marketing);

    /// <summary>How far away you can create buy orders (Procurement).</summary>
    public int? RemoteBuyJumps => RangeJumps(Procurement);

    /// <summary>How far away you can modify existing orders (Daytrading).</summary>
    public int? RemoteModifyJumps => RangeJumps(Daytrading);

    public const int RegionWide = int.MaxValue;

    /// <summary>Remote order skills: 1 = same system, 2 = 5 jumps, 3 = 10, 4 = 20, 5 = whole region.</summary>
    public static int? RangeJumps(int level) => level switch
    {
        <= 0 => null,
        1 => 0,
        2 => 5,
        3 => 10,
        4 => 20,
        _ => RegionWide,
    };

    public static string RangeText(int? jumps) => jumps switch
    {
        null => "station only",
        0 => "same system",
        RegionWide => "whole region",
        int j => $"{j} jumps",
    };

    /// <summary>Builds a profile from trained skill levels keyed by skill type ID, looking IDs up by name.</summary>
    public static SkillProfile From(IReadOnlyDictionary<int, int> levels, SdeData sde, IReadOnlyDictionary<int, double>? standings = null)
    {
        int L(string name) => sde.SkillId(name) is int id ? levels.GetValueOrDefault(id) : 0;
        return new SkillProfile(
            L("Accounting"), L("Broker Relations"), L("Advanced Broker Relations"),
            L("Trade"), L("Retail"), L("Wholesale"), L("Tycoon"),
            L("Marketing"), L("Procurement"), L("Daytrading"), L("Visibility"))
        {
            Standings = standings ?? new Dictionary<int, double>(),
        };
    }
}
