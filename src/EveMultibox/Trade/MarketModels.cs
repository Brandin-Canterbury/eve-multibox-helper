namespace EveMultibox.Trade;

// ESI market and asset shapes (snake_case JSON maps onto these via EsiClient.Json).

public sealed record MarketOrder(
    long OrderId, int TypeId, long LocationId, int SystemId, bool IsBuyOrder,
    double Price, long VolumeRemain, int MinVolume, string Range);

/// <summary>CCP's universe-wide average price per type (one call covers everything).</summary>
public sealed record MarketPrice(int TypeId, double AveragePrice, double AdjustedPrice);

public sealed record MarketHistoryDay(string Date, double Average, double Highest, double Lowest, long Volume, long OrderCount);

public sealed record EsiAsset(
    long ItemId, int TypeId, long Quantity, long LocationId, string LocationType, string LocationFlag,
    bool IsSingleton, bool? IsBlueprintCopy);
