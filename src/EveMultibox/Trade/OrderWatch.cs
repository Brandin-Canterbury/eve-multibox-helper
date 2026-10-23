using System.Text.RegularExpressions;

namespace EveMultibox.Trade;

public enum OrderEventKind
{
    /// <summary>Some of a sell order sold.</summary>
    Sold,

    /// <summary>Some of a buy order was filled.</summary>
    Bought,

    /// <summary>An order is gone: fully sold/filled, expired or cancelled.</summary>
    Ended,

    WalletChanged,
}

public sealed record OrderEvent(OrderEventKind Kind, CharacterOrder? Order, long Quantity, double Value, string? State = null, double? WalletChange = null);

/// <summary>Spots what changed between two looks at a character's orders and wallet.</summary>
public static class OrderWatch
{
    /// <param name="history">Recently finished orders (from ESI's order history), to tell sold-out from expired or cancelled.</param>
    public static List<OrderEvent> Diff(IReadOnlyList<CharacterOrder> before, IReadOnlyList<CharacterOrder> now,
        IReadOnlyList<CharacterOrder>? history = null)
    {
        var events = new List<OrderEvent>();
        var current = now.ToDictionary(o => o.OrderId);
        var finished = (history ?? Array.Empty<CharacterOrder>()).GroupBy(o => o.OrderId).ToDictionary(g => g.Key, g => g.First());

        foreach (var old in before)
        {
            if (current.TryGetValue(old.OrderId, out var still))
            {
                long moved = old.VolumeRemain - still.VolumeRemain;
                if (moved > 0) events.Add(new OrderEvent(old.IsBuy ? OrderEventKind.Bought : OrderEventKind.Sold, still, moved, moved * still.Price));
                continue;
            }

            // Gone. Whatever was left either traded (volume_remain 0 in history) or didn't (expired/cancelled).
            if (finished.TryGetValue(old.OrderId, out var done))
            {
                long moved = old.VolumeRemain - done.VolumeRemain;
                if (moved > 0) events.Add(new OrderEvent(old.IsBuy ? OrderEventKind.Bought : OrderEventKind.Sold, done, moved, moved * done.Price));
                string state = done.VolumeRemain == 0 ? (done.IsBuy ? "was completely filled" : "sold out")
                    : done.State == "cancelled" ? "was cancelled" : "expired";
                events.Add(new OrderEvent(OrderEventKind.Ended, done, 0, 0, state));
            }
            else
            {
                events.Add(new OrderEvent(OrderEventKind.Ended, old, 0, 0, "finished (sold out, expired or cancelled)"));
            }
        }
        return events;
    }

    public static OrderEvent? WalletDiff(double? before, double? now, double minChange) =>
        before is double b && now is double n && Math.Abs(n - b) >= Math.Max(0.01, minChange)
            ? new OrderEvent(OrderEventKind.WalletChanged, null, 0, n, WalletChange: n - b)
            : null;
}

/// <summary>
/// Notification text with fill-in tokens: {character} {item} {qty} {remaining} {price} {total}
/// {station} {side} {state} {change} {balance}.
/// </summary>
public static class NotificationText
{
    public static readonly string[] Tokens = { "character", "item", "qty", "remaining", "price", "total", "station", "side", "state", "change", "balance" };

    public static string Fill(string template, IReadOnlyDictionary<string, string> values) =>
        Regex.Replace(template, @"\{(\w+)\}", m => values.TryGetValue(m.Groups[1].Value, out var v) ? v : m.Value);

    public static Dictionary<string, string> ValuesFor(OrderEvent e, string character, Func<int, string> itemName,
        Func<long, string> stationName, double? balance)
    {
        var values = new Dictionary<string, string>
        {
            ["character"] = character,
            ["balance"] = balance is double b ? Isk.Short(b) + " ISK" : "?",
        };
        if (e.Order is { } o)
        {
            values["item"] = itemName(o.TypeId);
            values["qty"] = $"{e.Quantity:N0}";
            values["remaining"] = $"{o.VolumeRemain:N0}";
            values["price"] = $"{o.Price:N2}";
            values["total"] = Isk.Short(e.Value) + " ISK";
            values["station"] = stationName(o.LocationId);
            values["side"] = o.IsBuy ? "buy" : "sell";
            values["state"] = e.State ?? "";
        }
        if (e.WalletChange is double change) values["change"] = (change >= 0 ? "+" : "−") + Isk.Short(Math.Abs(change)) + " ISK";
        return values;
    }
}
