namespace EveMultibox.Trade;

/// <summary>
/// Market orders indexed by station and item: sell orders cheapest first, buy orders highest first.
/// Only NPC stations are kept (structure markets come later).
/// </summary>
public sealed class MarketSnapshot
{
    private readonly Dictionary<(long Station, int Type), List<MarketOrder>> _sells = new();
    private readonly Dictionary<(long Station, int Type), List<MarketOrder>> _buys = new();
    private readonly Dictionary<long, HashSet<int>> _typesAt = new();

    public MarketSnapshot(IEnumerable<MarketOrder> orders, Func<long, bool> isKnownStation, DateTimeOffset fetchedAt)
    {
        FetchedAt = fetchedAt;
        foreach (var o in orders)
        {
            if (o.VolumeRemain <= 0 || o.Price <= 0 || !isKnownStation(o.LocationId)) continue;
            var key = (o.LocationId, o.TypeId);
            var map = o.IsBuyOrder ? _buys : _sells;
            if (!map.TryGetValue(key, out var list)) map[key] = list = new List<MarketOrder>();
            list.Add(o);
            if (!_typesAt.TryGetValue(o.LocationId, out var types)) _typesAt[o.LocationId] = types = new HashSet<int>();
            types.Add(o.TypeId);
        }
        foreach (var list in _sells.Values) list.Sort((a, b) => a.Price.CompareTo(b.Price));
        foreach (var list in _buys.Values) list.Sort((a, b) => b.Price.CompareTo(a.Price));
    }

    public DateTimeOffset FetchedAt { get; }

    public int OrderCount => _sells.Values.Sum(l => l.Count) + _buys.Values.Sum(l => l.Count);

    /// <summary>Sell orders at a station for an item, cheapest first.</summary>
    public IReadOnlyList<MarketOrder> Sells(long station, int type) =>
        _sells.TryGetValue((station, type), out var l) ? l : Array.Empty<MarketOrder>();

    /// <summary>Buy orders at a station for an item, highest first.</summary>
    public IReadOnlyList<MarketOrder> Buys(long station, int type) =>
        _buys.TryGetValue((station, type), out var l) ? l : Array.Empty<MarketOrder>();

    public double? LowestSell(long station, int type) => Sells(station, type) is { Count: > 0 } s ? s[0].Price : null;

    public double? HighestBuy(long station, int type) => Buys(station, type) is { Count: > 0 } b ? b[0].Price : null;

    public IReadOnlyCollection<int> TypesAt(long station) =>
        _typesAt.TryGetValue(station, out var t) ? t : (IReadOnlyCollection<int>)Array.Empty<int>();
}
