using System.Windows.Threading;
using EveMultibox.Config;
using EveMultibox.Esi;
using EveMultibox.Shell;

namespace EveMultibox.Trade;

/// <summary>
/// Watches each signed-in character's market orders and wallet and raises a standard Windows
/// notification when something changes (an order sold, filled, ended; the wallet moved).
/// Each character is checked just after ESI refreshes its order list (about every 20 minutes),
/// so every check sees new data. The first look after start-up only records a baseline.
/// </summary>
public sealed class NotificationService
{
    private sealed class Watch
    {
        public List<CharacterOrder>? Orders;
        public double? Wallet;
        public DateTimeOffset NextCheck = DateTimeOffset.MinValue;
    }

    /// <summary>A little after ESI's refresh time, so the new data is definitely there.</summary>
    private static readonly TimeSpan AfterRefresh = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan RetryAfterError = TimeSpan.FromMinutes(5);

    private readonly App _app;
    private readonly TradeService _trade;
    private readonly OrdersService _orders;
    private readonly Action<string, string> _notify;
    private readonly Dictionary<long, Watch> _watch = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(30) };
    private bool _busy;

    public NotificationService(App app, TradeService trade, OrdersService orders, Action<string, string> notify)
    {
        _app = app;
        _trade = trade;
        _orders = orders;
        _notify = notify;
        _timer.Tick += async (_, _) => await TickAsync();
    }

    private NotificationSettings Settings => _app.Config.Notifications;

    public void Start() => _timer.Start();

    public void Stop() => _timer.Stop();

    /// <summary>When the next check for a character is due (for the settings window).</summary>
    public DateTimeOffset? NextCheckFor(long characterId) =>
        _watch.TryGetValue(characterId, out var w) && w.NextCheck > DateTimeOffset.MinValue ? w.NextCheck : null;

    private async Task TickAsync()
    {
        if (_busy || !Settings.Enabled) return;
        _busy = true;
        try
        {
            foreach (var c in _trade.Characters.ToList())
            {
                if (Settings.MutedCharacters.Contains(c.Id)) continue;
                if (!_watch.TryGetValue(c.Id, out var w)) _watch[c.Id] = w = new Watch();
                if (DateTimeOffset.UtcNow < w.NextCheck) continue;
                await CheckAsync(c, w);
            }
        }
        finally
        {
            _busy = false;
        }
    }

    private async Task CheckAsync(CharacterState c, Watch w)
    {
        try
        {
            var (orders, nextRefresh) = await _orders.OrdersAsync(c);
            double wallet = await _orders.WalletAsync(c);
            w.NextCheck = nextRefresh + AfterRefresh;

            if (w.Orders is not null)
            {
                // Finished orders only show up in history; fetch it only when something disappeared.
                bool somethingEnded = w.Orders.Any(o => orders.All(n => n.OrderId != o.OrderId));
                var history = somethingEnded ? await _orders.RecentHistoryAsync(c) : null;

                var events = OrderWatch.Diff(w.Orders, orders, history);
                if (OrderWatch.WalletDiff(w.Wallet, wallet, Settings.MinWalletChange) is { } walletEvent) events.Add(walletEvent);

                var sde = _trade.Sde.Data;
                var lines = Compose(events, Settings, e => NotificationText.ValuesFor(e, c.Name,
                    t => sde?.TypeName(t) ?? $"item {t}", _trade.CachedStationName, wallet));
                if (lines.Count > 0)
                {
                    if (Settings.AppendBalance && !events.Any(e => e.Kind == OrderEventKind.WalletChanged && Settings.WalletChanges))
                        lines.Add($"Wallet: {Isk.Short(wallet)} ISK");
                    _notify($"Phoenix's Eve Helper · {c.Name}", string.Join("\n", lines));
                }
            }

            w.Orders = orders;
            w.Wallet = wallet;

            // Keep the P&L history growing in the background.
            await _orders.SyncLedgerAsync(c);
        }
        catch (Exception ex) when (ex is EsiException or SignInRequiredException or System.Net.Http.HttpRequestException or TaskCanceledException)
        {
            w.NextCheck = DateTimeOffset.UtcNow + RetryAfterError;
            CrashLog.Write(ex, $"order check for {c.Name}");
        }
    }

    /// <summary>Turns events into notification lines, following the user's choices and templates.</summary>
    public static List<string> Compose(IEnumerable<OrderEvent> events, NotificationSettings settings,
        Func<OrderEvent, IReadOnlyDictionary<string, string>> values)
    {
        var lines = new List<string>();
        foreach (var e in events)
        {
            string? template = e.Kind switch
            {
                OrderEventKind.Sold when settings.OrderFills => settings.SoldTemplate,
                OrderEventKind.Bought when settings.OrderFills => settings.BoughtTemplate,
                OrderEventKind.Ended when settings.OrderEnded => settings.EndedTemplate,
                OrderEventKind.WalletChanged when settings.WalletChanges => settings.WalletTemplate,
                _ => null,
            };
            if (!string.IsNullOrWhiteSpace(template)) lines.Add(NotificationText.Fill(template, values(e)));
        }
        return lines;
    }

    /// <summary>Shows a sample notification using the current templates (from the settings window).</summary>
    public void SendTest(NotificationSettings settings)
    {
        var order = new CharacterOrder(1, 2629, 10000002, 60003760, "station", false, 199, 10_000, 6_000, DateTime.UtcNow, 90);
        var events = new List<OrderEvent>
        {
            new(OrderEventKind.Sold, order, 4_000, 4_000 * 199.0),
            new(OrderEventKind.WalletChanged, null, 0, 1_440_796_000, WalletChange: 796_000),
        };
        var lines = Compose(events, settings, e => NotificationText.ValuesFor(e, "The Pheonix",
            _ => "Inferno Light Missile", _ => "Jita IV - Moon 4 - Caldari Navy Assembly Plant", 1_440_796_000));
        if (settings.AppendBalance && !settings.WalletChanges) lines.Add("Wallet: 1.44B ISK");
        _notify("Phoenix's Eve Helper · test", lines.Count > 0 ? string.Join("\n", lines) : "(Nothing is switched on to notify about.)");
    }
}
