using EveMultibox.Esi;
using EveMultibox.StaticData;

namespace EveMultibox.Trade;

/// <summary>A character's market orders, how they compare with the competition, and their wallet history.</summary>
public sealed class OrdersService
{
    private readonly TradeService _trade;
    private readonly LedgerStore _ledgers;

    public OrdersService(TradeService trade, LedgerStore ledgers)
    {
        _trade = trade;
        _ledgers = ledgers;
    }

    private EsiClient Esi => _trade.Esi;

    private static string OrdersPath(long id) => $"characters/{id}/orders";

    /// <summary>Open orders, plus when ESI will next refresh them (about every 20 minutes).</summary>
    public async Task<(List<CharacterOrder> Orders, DateTimeOffset NextRefresh)> OrdersAsync(CharacterState c, CancellationToken ct = default)
    {
        string token = await _trade.Auth.GetAccessTokenAsync(c.Id, ct);
        var orders = await Esi.GetAsync<List<CharacterOrder>>(OrdersPath(c.Id), token, ct);
        var next = Esi.ExpiresOf(OrdersPath(c.Id), token) ?? DateTimeOffset.UtcNow.AddMinutes(20);
        return (orders, next);
    }

    /// <summary>Recently finished orders (the newest page), to tell sold-out from expired or cancelled.</summary>
    public async Task<List<CharacterOrder>> RecentHistoryAsync(CharacterState c, CancellationToken ct = default)
    {
        string token = await _trade.Auth.GetAccessTokenAsync(c.Id, ct);
        return await Esi.GetAsync<List<CharacterOrder>>($"characters/{c.Id}/orders/history?page=1", token, ct);
    }

    public async Task<double> WalletAsync(CharacterState c, CancellationToken ct = default)
    {
        string token = await _trade.Auth.GetAccessTokenAsync(c.Id, ct);
        return await Esi.GetAsync<double>($"characters/{c.Id}/wallet", token, ct);
    }

    /// <summary>
    /// The best competing price at the order's station: the lowest other sell order for a sell
    /// order, or the highest other buy order for a buy order. Null if nobody else is there.
    /// </summary>
    public async Task<double?> BestCompetitorAsync(CharacterOrder o, CancellationToken ct = default)
    {
        string side = o.IsBuy ? "buy" : "sell";
        var orders = await Esi.GetAllPagesAsync<MarketOrder>($"markets/{o.RegionId}/orders?order_type={side}&type_id={o.TypeId}", ct: ct);
        var others = orders.Where(m => m.LocationId == o.LocationId && m.OrderId != o.OrderId).Select(m => m.Price).ToList();
        if (others.Count == 0) return null;
        return o.IsBuy ? others.Max() : others.Min();
    }

    /// <summary>Fetches recent transactions and market fees, adds new ones to the saved history, and returns it.</summary>
    public async Task<Ledger> SyncLedgerAsync(CharacterState c, CancellationToken ct = default)
    {
        string token = await _trade.Auth.GetAccessTokenAsync(c.Id, ct);
        var transactions = await Esi.GetAsync<List<WalletTransaction>>($"characters/{c.Id}/wallet/transactions", token, ct);
        var journal = await Esi.GetAllPagesAsync<JournalEntry>($"characters/{c.Id}/wallet/journal", token, ct);

        var ledger = _ledgers.Load(c.Id);
        ledger.Merge(transactions, journal);
        ledger.LastSync = DateTimeOffset.UtcNow;
        _ledgers.Save(c.Id, ledger);
        return ledger;
    }

    public Ledger SavedLedger(long characterId) => _ledgers.Load(characterId);
}

/// <summary>How an order stands against the competition at its station.</summary>
public static class OrderStanding
{
    /// <returns>Text such as "Lowest", "Undercut by 1.00", and whether it needs attention.</returns>
    public static (string Text, bool NeedsAttention) Describe(CharacterOrder o, double? best)
    {
        if (best is not double b) return (o.IsBuy ? "Only buyer" : "Only seller", false);
        if (o.IsBuy)
            return b > o.Price ? ($"Outbid by {b - o.Price:N2}", true) : (b == o.Price ? "Tied highest" : "Highest", false);
        // A tie still sells (or buys) in turn, so it isn't flagged; only being beaten is.
        return b < o.Price ? ($"Undercut by {o.Price - b:N2}", true) : (b == o.Price ? "Tied lowest" : "Lowest", false);
    }
}

/// <summary>One open order in the My Orders grid.</summary>
public sealed class OrderRow : Config.ObservableObject
{
    private string _standing = "…";
    private Tone _standingTone = Tone.Dim;

    public OrderRow(CharacterOrder o, string characterName, SdeData sde, Func<long, string> stationName)
    {
        Order = o;
        Character = characterName;
        Item = sde.TypeName(o.TypeId);
        Station = stationName(o.LocationId);
    }

    public CharacterOrder Order { get; }
    public string Character { get; }
    public string Side => Order.IsBuy ? "Buy" : "Sell";
    public Tone SideTone => Order.IsBuy ? Tone.Warning : Tone.Positive;
    public string Item { get; }
    public string Station { get; }
    public double Price => Order.Price;
    public string PriceText => $"{Order.Price:N2}";
    public long Remaining => Order.VolumeRemain;
    public string QuantityText => $"{Order.VolumeRemain:N0} / {Order.VolumeTotal:N0}";
    public double Value => Order.RemainingValue;
    public string ValueText => Isk.Short(Order.RemainingValue);
    public string EscrowText => Order.IsBuy && Order.Escrow is double e ? Isk.Short(e) : "";
    public DateTime Expires => Order.ExpiresAt;

    public string ExpiresText
    {
        get
        {
            var left = Order.ExpiresAt - DateTime.UtcNow;
            return left.TotalSeconds <= 0 ? "expired" : left.TotalDays >= 1 ? $"{(int)left.TotalDays}d {left.Hours}h" : $"{left.Hours}h {left.Minutes}m";
        }
    }

    /// <summary>"Lowest", "Undercut by 1.00"… filled in once competitor prices are checked.</summary>
    public string Standing { get => _standing; set => Set(ref _standing, value); }
    public Tone StandingTone { get => _standingTone; set => Set(ref _standingTone, value); }
}
