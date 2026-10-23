using System.IO;
using System.Text.Json;

namespace EveMultibox.Trade;

// ESI shapes (snake_case JSON maps onto these via EsiClient.Json).

/// <summary>An open market order (or, from order history, a finished one with <see cref="State"/>).</summary>
public sealed record CharacterOrder(
    long OrderId, int TypeId, int RegionId, long LocationId, string Range, bool? IsBuyOrder,
    double Price, long VolumeTotal, long VolumeRemain, DateTime Issued, int Duration,
    double? Escrow = null, int? MinVolume = null, string? State = null)
{
    public bool IsBuy => IsBuyOrder == true;
    public double RemainingValue => Price * VolumeRemain;
    public DateTime ExpiresAt => Issued.AddDays(Duration);
}

public sealed record WalletTransaction(
    long TransactionId, DateTime Date, int TypeId, long LocationId, double UnitPrice, long Quantity,
    long ClientId, bool IsBuy, bool IsPersonal, long JournalRefId)
{
    public double Total => UnitPrice * Quantity;
}

public sealed record JournalEntry(long Id, DateTime Date, string RefType, double? Amount, double? Balance, string? Description);

/// <summary>
/// A character's trading history kept on disk. ESI only returns recent transactions (about 30
/// days), so new ones are merged in on every sync and the history grows past ESI's window.
/// </summary>
public sealed class Ledger
{
    public const string SalesTaxRef = "transaction_tax";
    public const string BrokerFeeRef = "brokers_fee";

    public List<WalletTransaction> Transactions { get; set; } = new();

    /// <summary>Market fee entries only (sales tax and broker fees).</summary>
    public List<JournalEntry> Fees { get; set; } = new();

    public DateTimeOffset? LastSync { get; set; }

    /// <summary>Adds anything not seen before. Returns how many new transactions there were.</summary>
    public int Merge(IEnumerable<WalletTransaction> transactions, IEnumerable<JournalEntry> journal)
    {
        var known = Transactions.Select(t => t.TransactionId).ToHashSet();
        var fresh = transactions.Where(t => t.IsPersonal && known.Add(t.TransactionId)).ToList();
        Transactions.AddRange(fresh);
        Transactions.Sort((a, b) => a.Date != b.Date ? a.Date.CompareTo(b.Date) : a.TransactionId.CompareTo(b.TransactionId));

        var knownFees = Fees.Select(f => f.Id).ToHashSet();
        Fees.AddRange(journal.Where(j => (j.RefType == SalesTaxRef || j.RefType == BrokerFeeRef) && knownFees.Add(j.Id)));
        Fees.Sort((a, b) => a.Date.CompareTo(b.Date));
        return fresh.Count;
    }
}

public sealed class LedgerStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };
    private readonly string _dir;

    public LedgerStore(string dir) => _dir = dir;

    public static string DefaultDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EveMultibox", "ledger");

    private string FileFor(long characterId) => Path.Combine(_dir, $"{characterId}.json");

    public Ledger Load(long characterId)
    {
        try
        {
            string file = FileFor(characterId);
            return File.Exists(file) ? JsonSerializer.Deserialize<Ledger>(File.ReadAllText(file), Json) ?? new Ledger() : new Ledger();
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            return new Ledger();
        }
    }

    public void Save(long characterId, Ledger ledger)
    {
        Directory.CreateDirectory(_dir);
        string file = FileFor(characterId);
        File.WriteAllText(file + ".tmp", JsonSerializer.Serialize(ledger, Json));
        File.Move(file + ".tmp", file, overwrite: true);
    }
}

/// <summary>Profit and loss for one item over a period.</summary>
public sealed record PnlRow(
    int TypeId, long BoughtQty, double BoughtValue, long SoldQty, double SoldValue,
    double CostOfSold, double Profit, long UnknownCostQty)
{
    public double AvgBuy => BoughtQty > 0 ? BoughtValue / BoughtQty : 0;
    public double AvgSell => SoldQty > 0 ? SoldValue / SoldQty : 0;
}

public sealed record PnlReport(
    IReadOnlyList<PnlRow> Rows, double Sales, double Purchases, double SalesTax, double BrokerFees,
    double TradingProfit, long UnknownCostSales)
{
    /// <summary>Profit on sales with a known cost, minus the period's sales tax and broker fees.</summary>
    public double NetProfit => TradingProfit - SalesTax - BrokerFees;
}

/// <summary>
/// Works out realised profit from a ledger. Each item's cost is the average price paid for the
/// units still held (buys raise it, sales use it up). Sales of units bought before the recorded
/// history have no known cost: they're counted in sales but left out of profit, and flagged.
/// </summary>
public static class PnlCalculator
{
    public static PnlReport Compute(Ledger ledger, DateTime fromUtc, DateTime toUtc)
    {
        var held = new Dictionary<int, (long Qty, double Cost)>();
        var rows = new Dictionary<int, PnlRow>();

        // Walk the whole history so costs carry over from before the period.
        foreach (var t in ledger.Transactions)
        {
            held.TryGetValue(t.TypeId, out var h);
            bool inPeriod = t.Date >= fromUtc && t.Date < toUtc;
            var row = rows.GetValueOrDefault(t.TypeId) ?? new PnlRow(t.TypeId, 0, 0, 0, 0, 0, 0, 0);

            if (t.IsBuy)
            {
                held[t.TypeId] = (h.Qty + t.Quantity, h.Cost + t.Total);
                if (inPeriod) rows[t.TypeId] = row with { BoughtQty = row.BoughtQty + t.Quantity, BoughtValue = row.BoughtValue + t.Total };
                continue;
            }

            long matched = Math.Min(t.Quantity, h.Qty);
            double avg = h.Qty > 0 ? h.Cost / h.Qty : 0;
            double cost = matched * avg;
            held[t.TypeId] = (h.Qty - matched, h.Cost - cost);

            if (inPeriod)
            {
                rows[t.TypeId] = row with
                {
                    SoldQty = row.SoldQty + t.Quantity,
                    SoldValue = row.SoldValue + t.Total,
                    CostOfSold = row.CostOfSold + cost,
                    Profit = row.Profit + matched * t.UnitPrice - cost,
                    UnknownCostQty = row.UnknownCostQty + (t.Quantity - matched),
                };
            }
        }

        var fees = ledger.Fees.Where(f => f.Date >= fromUtc && f.Date < toUtc).ToList();
        double tax = -fees.Where(f => f.RefType == Ledger.SalesTaxRef).Sum(f => f.Amount ?? 0);
        double broker = -fees.Where(f => f.RefType == Ledger.BrokerFeeRef).Sum(f => f.Amount ?? 0);

        var list = rows.Values.OrderByDescending(r => r.Profit).ToList();
        return new PnlReport(list, list.Sum(r => r.SoldValue), list.Sum(r => r.BoughtValue), tax, broker,
            list.Sum(r => r.Profit), list.Sum(r => r.UnknownCostQty));
    }
}
