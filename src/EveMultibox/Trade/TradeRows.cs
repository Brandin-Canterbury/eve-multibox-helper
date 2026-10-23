using EveMultibox.Config;
using EveMultibox.StaticData;

namespace EveMultibox.Trade;

public static class Isk
{
    /// <summary>Compact ISK: 950, 12.5K, 3.42M, 1.20B.</summary>
    public static string Short(double isk) => Math.Abs(isk) switch
    {
        >= 1e9 => $"{isk / 1e9:0.00}B",
        >= 1e6 => $"{isk / 1e6:0.00}M",
        >= 1e4 => $"{isk / 1e3:0.0}K",
        _ => $"{isk:N2}",
    };

    public static string Full(double isk) => $"{isk:N2} ISK";

    /// <summary>Signed, for P&L lines: "+1,990,000.00 ISK" / "−1,000,000.00 ISK".</summary>
    public static string Signed(double isk) => (isk < 0 ? "−" : "+") + $"{Math.Abs(isk):N2} ISK";
}

/// <summary>How a value should read: gain (green), cost or loss (red), caution (yellow), a note, or a heading.</summary>
public enum Tone
{
    Normal,
    Dim,
    Positive,
    Negative,
    Warning,
    Header,
    Spacer,
}

/// <summary>One label/value line in a P&L tooltip.</summary>
public sealed record PnlLine(string Label, string Value, Tone Tone = Tone.Normal)
{
    public static readonly PnlLine Spacer = new("", "", Tone.Spacer);
    public static PnlLine Heading(string text) => new(text, "", Tone.Header);
    public static PnlLine Note(string text, Tone tone = Tone.Dim) => new("", text, tone);
}

/// <summary>One item in the P&L grid.</summary>
public sealed record PnlRowView(PnlRow Row, string Item)
{
    public string SalesText => Row.SoldValue > 0 ? Isk.Short(Row.SoldValue) : "";
    public string CostText => Row.CostOfSold > 0 ? Isk.Short(Row.CostOfSold) : "";
    public string ProfitText => Row.SoldQty == 0 ? "" : (Row.Profit >= 0 ? "+" : "") + Isk.Short(Row.Profit);
    public Tone ProfitTone => Row.Profit >= 0 ? Tone.Positive : Tone.Negative;
    public string UnknownText => Row.UnknownCostQty > 0 ? $"{Row.UnknownCostQty:N0}" : "";
}

/// <summary>Narrows hub-to-hub routes to one direction, e.g. only Amarr → Jita.</summary>
public static class RouteFilter
{
    /// <param name="from">Buy station to keep, or null for any.</param>
    /// <param name="to">Sell station to keep, or null for any.</param>
    public static bool Matches(RouteCandidate route, long? from, long? to) =>
        (from is null || route.SourceStation == from) && (to is null || route.DestStation == to);
}

/// <summary>One trade route row in the Trade Routes grid.</summary>
public sealed class RouteRow : ObservableObject
{
    private double? _daily;

    public RouteRow(RouteCandidate r, SdeData sde, Func<long, string> stationName)
    {
        Route = r;
        var src = sde.Stations[r.SourceStation];
        var dst = sde.Stations[r.DestStation];
        BuyAt = SystemLabel(sde, src.SystemId);
        SellAt = SystemLabel(sde, dst.SystemId);
        BuyStationName = stationName(r.SourceStation);
        SellStationName = stationName(r.DestStation);
        DestRegionId = sde.Systems[dst.SystemId].RegionId;
        BuildLines();
    }

    public RouteCandidate Route { get; }
    public string Item => Route.ItemName;
    public string BuyAt { get; }
    public string SellAt { get; }
    public string BuyStationName { get; }
    public string SellStationName { get; }
    public int DestRegionId { get; }
    public int Jumps => Route.TotalJumps;
    public string JumpsText => $"{Route.TotalJumps}  ({Route.JumpsToSource} + {Route.JumpsBetween})";
    public long Units => Route.Units;
    public double SeedCost => Route.SeedCost;
    public string SeedCostText => Isk.Short(Route.SeedCost);
    public double Profit => Route.Profit;
    public string ProfitText => (Route.Profit >= 0 ? "+" : "") + Isk.Short(Route.Profit);
    public Tone ProfitTone => Route.Profit >= 0 ? Tone.Positive : Tone.Negative;
    public double ProfitPerJump => Route.ProfitPerJump;
    public string ProfitPerJumpText => Isk.Short(Route.ProfitPerJump);
    public double Margin => Route.Margin;
    public string MarginText => $"{Route.Margin:P0}";

    /// <summary>Above this per-unit price the trade loses money after tax and broker fee.</summary>
    public double BreakEven => Route.Units > 0 ? (Route.Revenue - Route.SalesTax - Route.BrokerFee) / Route.Units : 0;

    /// <summary>Average units sold per day at the destination region.</summary>
    public double? Daily
    {
        get => _daily;
        set
        {
            if (!Set(ref _daily, value)) return;
            OnPropertyChanged(nameof(DailyVolume));
            OnPropertyChanged(nameof(DemandTone));
            BuildLines();
            OnPropertyChanged(nameof(PnlLines));
        }
    }

    public string DailyVolume => Daily is double d ? $"{d:N0}" : "…";

    /// <summary>Yellow when it would take more than a day of the destination's sales to clear the load.</summary>
    public Tone DemandTone => Daily is double d && Route.Units > d ? Tone.Warning : Tone.Normal;

    public IReadOnlyList<PnlLine> PnlLines { get; private set; } = Array.Empty<PnlLine>();

    private void BuildLines()
    {
        var r = Route;
        var lines = new List<PnlLine>
        {
            PnlLine.Heading($"{r.ItemName} × {r.Units:N0}"),
            new("Cargo", $"{r.CargoUsed:N1} m³  ({r.UnitVolume:N3} m³ each)", Tone.Dim),
            new("Buy at", BuyStationName),
            new("Sell at", SellStationName),
            PnlLine.Spacer,
            new("Buy cost", Isk.Signed(-r.SeedCost), Tone.Negative),
            new("", $"avg {r.AvgBuyPrice:N2} each", Tone.Dim),
            new("Sell revenue", Isk.Signed(r.Revenue), Tone.Positive),
            new("", $"listed at {r.SellPrice:N2} (lowest seller − 1)", Tone.Dim),
            new("Sales tax", Isk.Signed(-r.SalesTax), Tone.Negative),
            new("Broker fee", Isk.Signed(-r.BrokerFee), Tone.Negative),
            new("Profit", Isk.Signed(r.Profit), r.Profit >= 0 ? Tone.Positive : Tone.Negative),
            new("Margin", $"{r.Margin:P1} on cost", r.Profit >= 0 ? Tone.Positive : Tone.Negative),
            new("Break-even", $"pay at most {BreakEven:N2} each", Tone.Warning),
            PnlLine.Spacer,
            new("Jumps", $"{r.JumpsToSource} to buy, then {r.JumpsBetween} to sell"),
        };
        if (r.InstantSellPrice is double instant)
            lines.Add(new("Instant sell there", $"{instant:N2} each", instant >= BreakEven ? Tone.Normal : Tone.Warning));
        if (Daily is double d)
            lines.Add(new("Sold per day there", $"{d:N0}", r.Units > d ? Tone.Warning : Tone.Normal));
        if (r.LimitedByCargo) lines.Add(PnlLine.Note("Limited by your free hold space.", Tone.Warning));
        if (r.LimitedByWallet) lines.Add(PnlLine.Note("Limited by your wallet.", Tone.Warning));
        PnlLines = lines;
    }

    internal static string SystemLabel(SdeData sde, int systemId) =>
        sde.Systems.TryGetValue(systemId, out var s) ? $"{s.Name} ({s.DisplaySecurity:0.0})" : $"System {systemId}";
}

/// <summary>One owned item in the Sell my items grid, with its best way to sell.</summary>
public sealed class SellRow
{
    public SellRow(SellOption? now, SellOption? list, SdeData sde, Func<long, string> stationName)
    {
        Best = (now, list) switch
        {
            ({ } n, { } l) => n.Net >= l.Net ? n : l,
            ({ } n, null) => n,
            (null, { } l) => l,
            _ => throw new ArgumentException("Need at least one option."),
        };
        Alternative = ReferenceEquals(Best, now) ? list : now;

        var stack = Best.Stack;
        Item = Best.ItemName;
        Quantity = stack.Quantity;
        Where = stack.InCurrentShip ? "In your ship"
            : stack.StationId is long st ? stationName(st)
            : stack.SystemId is int sys ? RouteRow.SystemLabel(sde, sys)
            : "Unknown";
        SellAt = RouteRow.SystemLabel(sde, sde.Stations[Best.Station].SystemId);
        SellStationName = stationName(Best.Station);

        var lines = new List<PnlLine>
        {
            PnlLine.Heading($"{Item} × {Best.Units:N0}"),
            new("Now at", Where),
            new("Best", $"{Best.HowText}"),
            new("", SellStationName, Tone.Dim),
            PnlLine.Spacer,
            new("Gross", Isk.Signed(Best.Gross), Tone.Positive),
            new("", $"{Best.UnitPrice:N2} each", Tone.Dim),
            new("Sales tax", Isk.Signed(-Best.SalesTax), Tone.Negative),
        };
        if (!Best.Instant) lines.Add(new("Broker fee", Isk.Signed(-Best.BrokerFee), Tone.Negative));
        lines.Add(new("Net", Isk.Signed(Best.Net), Best.Net >= 0 ? Tone.Positive : Tone.Negative));
        lines.Add(new("Jumps", $"{Best.Jumps} from where it is"));
        if (Alternative is { } alt)
        {
            lines.Add(PnlLine.Spacer);
            lines.Add(new("Other option", alt.HowText, Tone.Dim));
            lines.Add(new("", $"{stationName(alt.Station)} · net {Isk.Short(alt.Net)} · {alt.Jumps} jumps", Tone.Dim));
        }
        if (!Best.Instant) lines.Add(PnlLine.Note("A listing sells only as fast as buyers come, and can be undercut.", Tone.Warning));
        if (Best.Units < Quantity) lines.Add(PnlLine.Note($"Only {Best.Units:N0} of {Quantity:N0} fit the buy orders there.", Tone.Warning));
        PnlLines = lines;
    }

    public SellOption Best { get; }
    public SellOption? Alternative { get; }
    public string Item { get; }
    public long Quantity { get; }
    public string Where { get; }
    public string How => Best.HowText;
    public Tone HowTone => Best.Instant ? Tone.Normal : Tone.Warning;
    public string SellAt { get; }
    public string SellStationName { get; }
    public int Jumps => Best.Jumps;
    public double Net => Best.Net;
    public string NetText => (Best.Net >= 0 ? "+" : "") + Isk.Short(Best.Net);
    public Tone NetTone => Best.Net >= 0 ? Tone.Positive : Tone.Negative;
    public string UnitPriceText => $"{Best.UnitPrice:N2}";
    public IReadOnlyList<PnlLine> PnlLines { get; }
}
