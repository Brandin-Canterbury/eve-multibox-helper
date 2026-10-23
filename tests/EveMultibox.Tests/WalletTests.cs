using System.IO;
using EveMultibox.Config;
using EveMultibox.Trade;

namespace EveMultibox.Tests;

public sealed class WalletTests : IDisposable
{
    private const int Inferno = 2629, Trit = 34;
    private static readonly DateTime Day1 = new(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "EveMultiboxTests", Guid.NewGuid().ToString("N"));
    private long _id;

    private WalletTransaction Tx(int type, bool buy, long qty, double price, DateTime when, bool personal = true) =>
        new(++_id, when, type, 60003760, price, qty, 90000001, buy, personal, 1000 + _id);

    private JournalEntry Fee(string refType, double amount, DateTime when) => new(++_id, when, refType, amount, null, null);

    // ---- ledger ----

    [Fact]
    public void LedgerMergeSkipsDuplicatesAndCorpTransactions()
    {
        var ledger = new Ledger();
        var a = Tx(Inferno, true, 100, 10, Day1);
        Assert.Equal(1, ledger.Merge(new[] { a, Tx(Inferno, true, 5, 10, Day1, personal: false) }, Array.Empty<JournalEntry>()));
        Assert.Equal(0, ledger.Merge(new[] { a }, Array.Empty<JournalEntry>())); // Seen before.
        Assert.Single(ledger.Transactions);
    }

    [Fact]
    public void LedgerKeepsOnlyMarketFeesFromTheJournal()
    {
        var ledger = new Ledger();
        ledger.Merge(Array.Empty<WalletTransaction>(), new[]
        {
            Fee(Ledger.SalesTaxRef, -100, Day1), Fee(Ledger.BrokerFeeRef, -50, Day1), Fee("bounty_prizes", 1e6, Day1),
        });
        Assert.Equal(2, ledger.Fees.Count);
    }

    [Fact]
    public void LedgerStoreRoundTrips()
    {
        var store = new LedgerStore(_dir);
        var ledger = new Ledger();
        ledger.Merge(new[] { Tx(Inferno, true, 100, 10, Day1) }, new[] { Fee(Ledger.SalesTaxRef, -5, Day1) });
        store.Save(42, ledger);
        var loaded = store.Load(42);
        Assert.Single(loaded.Transactions);
        Assert.Single(loaded.Fees);
        Assert.Empty(store.Load(43).Transactions); // Unknown character: empty, not an error.
    }

    // ---- P&L ----

    [Fact]
    public void ProfitUsesAverageCost()
    {
        var ledger = new Ledger();
        ledger.Merge(new[]
        {
            Tx(Inferno, true, 100, 10, Day1),            // 100 @ 10
            Tx(Inferno, true, 100, 20, Day1.AddHours(1)), // 100 @ 20 → average 15
            Tx(Inferno, false, 150, 25, Day1.AddDays(1)), // sell 150 @ 25: cost 150 × 15 = 2,250
        }, new[] { Fee(Ledger.SalesTaxRef, -100, Day1.AddDays(1)), Fee(Ledger.BrokerFeeRef, -40, Day1) });

        var r = PnlCalculator.Compute(ledger, DateTime.MinValue, DateTime.MaxValue);
        var row = Assert.Single(r.Rows);
        Assert.Equal(3_750, row.SoldValue, 6);
        Assert.Equal(2_250, row.CostOfSold, 6);
        Assert.Equal(1_500, row.Profit, 6);
        Assert.Equal(1_500 - 100 - 40, r.NetProfit, 6);
        Assert.Equal(3_000, r.Purchases, 6);
    }

    [Fact]
    public void SalesOfItemsBoughtBeforeHistoryAreFlaggedNotGuessed()
    {
        var ledger = new Ledger();
        ledger.Merge(new[] { Tx(Trit, true, 10, 4, Day1), Tx(Trit, false, 50, 5, Day1.AddDays(1)) }, Array.Empty<JournalEntry>());
        var row = Assert.Single(PnlCalculator.Compute(ledger, DateTime.MinValue, DateTime.MaxValue).Rows);
        Assert.Equal(40, row.UnknownCostQty);
        Assert.Equal(10 * 5 - 10 * 4, row.Profit, 6); // Only the 10 units with a known cost.
        Assert.Equal(250, row.SoldValue, 6);           // Sales still count in full.
    }

    [Fact]
    public void PeriodOnlyCountsItsOwnTradesButKeepsEarlierCosts()
    {
        var ledger = new Ledger();
        ledger.Merge(new[] { Tx(Inferno, true, 100, 10, Day1), Tx(Inferno, false, 100, 30, Day1.AddDays(10)) },
            new[] { Fee(Ledger.SalesTaxRef, -99, Day1) });

        var lastWeek = PnlCalculator.Compute(ledger, Day1.AddDays(7), Day1.AddDays(14));
        var row = Assert.Single(lastWeek.Rows);
        Assert.Equal(0, row.BoughtQty);             // The buy was before the period…
        Assert.Equal(2_000, row.Profit, 6);         // …but its cost still applies.
        Assert.Equal(0, lastWeek.SalesTax, 6);      // The fee was outside the period.
    }

    // ---- order changes ----

    private static CharacterOrder Order(long id, bool buy, long remain, long total = 100, double price = 199, int type = Inferno, string? state = null) =>
        new(id, type, 10000002, 60003760, "station", buy, price, total, remain, new DateTime(2026, 10, 20), 90, State: state);

    [Fact]
    public void PartialSaleIsReported()
    {
        var events = OrderWatch.Diff(new[] { Order(1, false, 100) }, new[] { Order(1, false, 60) });
        var e = Assert.Single(events);
        Assert.Equal(OrderEventKind.Sold, e.Kind);
        Assert.Equal(40, e.Quantity);
        Assert.Equal(40 * 199, e.Value, 6);
    }

    [Fact]
    public void BuyFillsAreReportedAsBought() =>
        Assert.Equal(OrderEventKind.Bought, Assert.Single(OrderWatch.Diff(new[] { Order(1, true, 10) }, new[] { Order(1, true, 4) })).Kind);

    [Fact]
    public void FinishedOrdersUseHistoryToTellSoldOutFromExpired()
    {
        var before = new[] { Order(1, false, 30), Order(2, false, 50), Order(3, false, 10) };
        var history = new[] { Order(1, false, 0, state: "expired"), Order(2, false, 50, state: "expired"), Order(3, false, 10, state: "cancelled") };
        var events = OrderWatch.Diff(before, Array.Empty<CharacterOrder>(), history);

        Assert.Contains(events, e => e.Kind == OrderEventKind.Sold && e.Order!.OrderId == 1 && e.Quantity == 30);
        Assert.Equal("sold out", events.Single(e => e.Kind == OrderEventKind.Ended && e.Order!.OrderId == 1).State);
        Assert.Equal("expired", events.Single(e => e.Kind == OrderEventKind.Ended && e.Order!.OrderId == 2).State);
        Assert.Equal("was cancelled", events.Single(e => e.Kind == OrderEventKind.Ended && e.Order!.OrderId == 3).State);
    }

    [Fact]
    public void NothingChangedMeansNoEvents() =>
        Assert.Empty(OrderWatch.Diff(new[] { Order(1, false, 50) }, new[] { Order(1, false, 50), Order(2, true, 5) }));

    [Fact]
    public void WalletChangeRespectsTheMinimum()
    {
        Assert.Null(OrderWatch.WalletDiff(1_000_000, 1_400_000, minChange: 500_000));
        Assert.Equal(-600_000, OrderWatch.WalletDiff(1_000_000, 400_000, minChange: 500_000)!.WalletChange!.Value, 6);
        Assert.Null(OrderWatch.WalletDiff(null, 5, 0)); // First look: nothing to compare with.
    }

    // ---- notification text ----

    [Fact]
    public void TemplatesFillTokensAndLeaveUnknownOnesAlone()
    {
        var e = new OrderEvent(OrderEventKind.Sold, Order(1, false, 60), 40, 7_960);
        var values = NotificationText.ValuesFor(e, "The Pheonix", _ => "Inferno Light Missile", _ => "Jita IV-4", 1_440_000_000);
        string text = NotificationText.Fill(NotificationSettings.DefaultSold + " {bogus}", values);
        Assert.Equal("The Pheonix sold 40 × Inferno Light Missile for 7,960.00 ISK · 60 left {bogus}", text);
        Assert.Equal("1.44B ISK", values["balance"]);
    }

    [Fact]
    public void WalletTemplateShowsSignedChange()
    {
        var e = OrderWatch.WalletDiff(1_000_000_000, 1_250_000_000, 0)!;
        var values = NotificationText.ValuesFor(e, "Kayla", _ => "", _ => "", 1_250_000_000);
        Assert.Equal("Kayla's wallet +250.00M ISK → 1.25B ISK", NotificationText.Fill(NotificationSettings.DefaultWallet, values));
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }
}
