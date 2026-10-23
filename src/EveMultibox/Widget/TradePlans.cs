using EveMultibox.StaticData;
using EveMultibox.Trade;

namespace EveMultibox.Widget;

/// <summary>Builds the step-by-step plans for running a trade route or selling an item.</summary>
public static class TradePlans
{
    public static ActionPlan ForRoute(RouteRow row, CharacterState c, SdeData sde)
    {
        var r = row.Route;
        var steps = new List<ActionStep>();
        bool docked = c.StationId is not null || c.StructureId is not null;

        if (c.StationId != r.SourceStation)
        {
            if (docked) steps.Add(Undock());
            steps.Add(Travel(r.SourceStation, row.BuyStationName, sde));
        }

        // Above this price a unit loses money once tax and broker fee come off the resale.
        double breakEven = (r.Revenue - r.SalesTax - r.BrokerFee) / r.Units;
        steps.Add(new ActionStep($"Buy {r.Units:N0} × {r.ItemName}",
            $"Open the market (Alt+R), search \"{r.ItemName}\" and buy {r.Units:N0}. " +
            $"The plan averages {r.AvgBuyPrice:N2} each; never pay more than {breakEven:N2} (break-even). " +
            "If the cheap stock has gone, buy less rather than overpay.")
        {
            Copies = new[] { new CopyValue("Copy quantity", r.Units.ToString()), new CopyValue("Copy item name", r.ItemName) },
            DoneLabel = "Bought",
        });

        steps.Add(Undock());
        steps.Add(Travel(r.DestStation, row.SellStationName, sde));
        steps.Add(SellStep(r.ItemName, r.Units, r.SellPrice, instant: false));
        return new ActionPlan($"Route: {r.ItemName}", c.Id, c.Name, steps);
    }

    /// <summary>A multi-item run: travel to the buy station, buy each item, travel to the sell station, list each item.</summary>
    public static ActionPlan ForRun(TradeRun run, CharacterState c, SdeData sde, Func<long, string> stationName)
    {
        if (run.SourceStation is not long src || run.DestStation is not long dst)
            throw new InvalidOperationException("The run has no items.");

        var steps = new List<ActionStep>();
        bool docked = c.StationId is not null || c.StructureId is not null;
        if (c.StationId != src)
        {
            if (docked) steps.Add(Undock());
            steps.Add(Travel(src, stationName(src), sde));
        }

        int n = run.Items.Count, i = 0;
        foreach (var item in run.Items)
        {
            i++;
            double breakEven = (item.Revenue - item.SalesTax - item.BrokerFee) / item.Units;
            steps.Add(new ActionStep($"Buy {i}/{n}: {item.Units:N0} × {item.ItemName}",
                $"In the market (Alt+R), search \"{item.ItemName}\" and buy {item.Units:N0}. " +
                $"Planned average {item.AvgBuyPrice:N2}; never pay more than {breakEven:N2} (break-even). " +
                $"Planned space {item.CargoUsed:N0} m³.")
            {
                Copies = new[] { new CopyValue("Copy quantity", item.Units.ToString()), new CopyValue("Copy item name", item.ItemName) },
                DoneLabel = "Bought",
            });
        }

        steps.Add(Undock());
        steps.Add(Travel(dst, stationName(dst), sde));

        i = 0;
        foreach (var item in run.Items)
        {
            i++;
            var sell = SellStep(item.ItemName, item.Units, item.SellPrice, instant: false);
            steps.Add(sell with { Title = $"Sell {i}/{n}: " + sell.Title });
        }

        return new ActionPlan($"Run: {n} items", c.Id, c.Name, steps);
    }

    public static ActionPlan ForSale(SellRow row, CharacterState c, SdeData sde)
    {
        var best = row.Best;
        var stack = best.Stack;
        var steps = new List<ActionStep>();
        bool docked = c.StationId is not null || c.StructureId is not null;
        long? at = c.StationId; // Where the character will be docked as the plan unfolds.

        // Fetch the goods first unless they're already aboard.
        if (!stack.InCurrentShip && stack.StationId is long pickup)
        {
            if (at != pickup)
            {
                if (docked) steps.Add(Undock());
                steps.Add(Travel(pickup, row.Where, sde));
            }
            steps.Add(new ActionStep($"Load {best.Units:N0} × {row.Item}",
                $"Open your item hangar and drag {best.Units:N0} {row.Item} into your ship's cargo. " +
                "If they don't fit, board a bigger ship from the ship hangar first (right-click → Board), then load them.")
            {
                Copies = new[] { new CopyValue("Copy quantity", best.Units.ToString()) },
                DoneLabel = "Loaded",
            });
            at = pickup;
            docked = true;
        }

        if (at != best.Station)
        {
            if (docked) steps.Add(Undock());
            steps.Add(Travel(best.Station, row.SellStationName, sde));
        }

        steps.Add(SellStep(row.Item, best.Units, best.UnitPrice, best.Instant));
        return new ActionPlan($"Sell: {row.Item}", c.Id, c.Name, steps);
    }

    private static ActionStep Undock() => new("Undock",
        "Undock from the station. Use the Undock button here (presses your EVE undock shortcut) or the station panel in EVE.")
    {
        Keys = new[] { GameKey.Undock },
        CompleteWhen = ctx => ctx.InSpace,
        DoneLabel = "I've undocked",
    };

    private static ActionStep Travel(long stationId, string stationName, SdeData sde)
    {
        int? system = sde.Stations.TryGetValue(stationId, out var st) ? st.SystemId : null;
        return new ActionStep($"Travel to {stationName}",
            "1. Set destination (once).\n" +
            "2. Each system: click the yellow gate in your overview if it isn't already selected, then press Jump.\n" +
            "3. In the last system, select the station and press Jump/Dock to warp to it and dock.")
        {
            DestinationId = stationId,
            DestinationLabel = stationName,
            TargetSystemId = system,
            Keys = new[] { GameKey.JumpOrDock },
            CompleteWhen = ctx => ctx.StationId == stationId,
            DoneLabel = "I've docked",
        };
    }

    private static ActionStep SellStep(string item, long units, double price, bool instant) =>
        instant
            ? new ActionStep($"Sell {units:N0} × {item}",
                $"Right-click {item} in your cargo → Sell. Sell to the highest buy order (about {price:N2} each).")
            {
                Copies = new[] { new CopyValue("Copy quantity", units.ToString()) },
                DoneLabel = "Sold",
            }
            : new ActionStep($"List {units:N0} × {item} at {price:N2}",
                $"Right-click {item} in your cargo → Sell, switch to \"Advanced\", set the price to {price:N2} " +
                $"(the cheapest seller minus 1 ISK) and the quantity to {units:N0}, then place the order.")
            {
                Copies = new[] { new CopyValue("Copy price", price.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)), new CopyValue("Copy quantity", units.ToString()) },
                DoneLabel = "Listed",
            };
}
