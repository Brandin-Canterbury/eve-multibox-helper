using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows.Threading;
using EveMultibox.Alerts;
using EveMultibox.Clients;
using EveMultibox.Interop;
using EveMultibox.Shell;
using EveMultibox.Trade;

namespace EveMultibox.Edge;

/// <summary>Where the API listens and the key it wants; Xeneon Edge Hub reads the same file.</summary>
public sealed class EdgeApiSettings
{
    public int Port { get; set; } = 31416;
    public string Key { get; set; } = "";

    public static string DefaultPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "EveMultibox", "edge-api.json");

    /// <summary>Loads the settings, creating them (with a fresh random key) the first time.</summary>
    public static EdgeApiSettings LoadOrCreate(string path)
    {
        EdgeApiSettings? settings = null;
        try
        {
            if (File.Exists(path)) settings = JsonSerializer.Deserialize<EdgeApiSettings>(File.ReadAllText(path), EdgeApiServer.Json);
        }
        catch (Exception ex) when (ex is JsonException or IOException) { }
        settings ??= new EdgeApiSettings();
        if (string.IsNullOrWhiteSpace(settings.Key))
        {
            settings.Key = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(settings, EdgeApiServer.Json));
        }
        return settings;
    }
}

/// <summary>
/// A small HTTP API on localhost for Xeneon Edge Hub's EVE module:
/// <list type="bullet">
/// <item>GET /state: clients, characters, alerts, orders and profit (<see cref="EdgeState"/>);</item>
/// <item>POST /focus?character=Name: bring that client to the front (focus only, like the thumbnails);</item>
/// <item>POST /ack?character=Name: mark that character's alert as seen;</item>
/// <item>POST /open?what=trade|toolbox|notifications|settings: open one of the helper's windows.</item>
/// </list>
/// It only listens on localhost, and every request must carry the key from edge-api.json in an
/// X-Edge-Key header. Web pages can't read that file, and can't send custom headers to it without
/// a CORS preflight this server never answers, so only programs on this PC can use it.
/// </summary>
public sealed class EdgeApiServer : IDisposable
{
    internal static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = false,
    };

    private static readonly TimeSpan OrdersEvery = TimeSpan.FromMinutes(5);
    private const int CompetitorChecksPerCharacter = 25;

    private readonly App _app;
    private readonly ClientTracker _tracker;
    private readonly TradeService _trade;
    private readonly OrdersService _orders;
    private readonly EdgeApiSettings _settings;
    private readonly CancellationTokenSource _stop = new();
    private HttpListener? _listener;
    private IReadOnlyList<EdgeOrder> _orderSnapshot = [];
    private IReadOnlyList<EdgePnl> _profitSnapshot = [];
    private DateTimeOffset? _ordersUpdated;

    /// <summary>Why it isn't listening (the port is taken, say), or null.</summary>
    public string? Problem { get; private set; }

    public EdgeApiServer(App app, ClientTracker tracker, TradeService trade, OrdersService orders, EdgeApiSettings settings)
    {
        _app = app;
        _tracker = tracker;
        _trade = trade;
        _orders = orders;
        _settings = settings;
    }

    public void Start()
    {
        try
        {
            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://localhost:{_settings.Port}/");
            _listener.Start();
        }
        catch (HttpListenerException ex)
        {
            Problem = $"The Edge API couldn't listen on port {_settings.Port}: {ex.Message}";
            CrashLog.Write(ex, "Edge API");
            _listener = null;
            return;
        }
        _ = ListenAsync(_listener, _stop.Token);
        _ = RefreshOrdersLoopAsync(_stop.Token);
    }

    private async Task ListenAsync(HttpListener listener, CancellationToken stop)
    {
        while (!stop.IsCancellationRequested && listener.IsListening)
        {
            HttpListenerContext context;
            try { context = await listener.GetContextAsync().ConfigureAwait(false); }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException) { return; }
            _ = HandleAsync(context);
        }
    }

    private async Task HandleAsync(HttpListenerContext context)
    {
        var response = context.Response;
        try
        {
            if (!IsAuthorised(context.Request.Headers["X-Edge-Key"]))
            {
                response.StatusCode = 401;
                return;
            }
            string path = context.Request.Url?.AbsolutePath.TrimEnd('/') ?? "";
            string method = context.Request.HttpMethod;
            string? character = context.Request.QueryString["character"];
            switch (method, path)
            {
                case ("GET", "/state"):
                    var state = await OnUi(BuildState).ConfigureAwait(false);
                    await WriteJson(response, state).ConfigureAwait(false);
                    return;
                case ("POST", "/focus"):
                    response.StatusCode = await OnUi(() => Focus(character)) ? 204 : 404;
                    return;
                case ("POST", "/ack"):
                    await OnUi(() => { _app.Alerts.Acknowledge(character); return true; }).ConfigureAwait(false);
                    response.StatusCode = 204;
                    return;
                case ("POST", "/open"):
                    response.StatusCode = await OnUi(() => Open(context.Request.QueryString["what"])) ? 204 : 404;
                    return;
                default:
                    response.StatusCode = 404;
                    return;
            }
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex, "Edge API request");
            response.StatusCode = 500;
        }
        finally
        {
            try { response.Close(); } catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException) { }
        }
    }

    /// <summary>Compares in constant time, so the key can't be guessed a character at a time.</summary>
    internal bool IsAuthorised(string? key) =>
        key is not null && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(key), Encoding.UTF8.GetBytes(_settings.Key));

    private Task<T> OnUi<T>(Func<T> work) => _app.Dispatcher.InvokeAsync(work, DispatcherPriority.Normal).Task;

    private EdgeState BuildState() => EdgeStateBuilder.Build(
        _tracker.Clients,
        _tracker.ActiveClient,
        _trade.Characters,
        _app.Alerts.Pending,
        _trade.Sde.Data,
        _trade.CachedStationName,
        _orderSnapshot,
        _profitSnapshot,
        _ordersUpdated,
        _trade.DestinationOf,
        _app.Config.Trade.SaferRoutes);

    private bool Focus(string? character)
    {
        if (string.IsNullOrWhiteSpace(character) || _tracker.FindByCharacter(character) is not { } client) return false;
        return Win32.FocusWindow(client.Hwnd);
    }

    private bool Open(string? what)
    {
        switch (what)
        {
            case "trade": _app.ShowTrade(); return true;
            case "toolbox": _app.ToggleToolbox(); return true;
            case "notifications": _app.ShowNotificationsWindow(); return true;
            case "settings": _app.ShowGeneralSettings(); return true;
            default: return false;
        }
    }

    private static async Task WriteJson(HttpListenerResponse response, object value)
    {
        byte[] body = JsonSerializer.SerializeToUtf8Bytes(value, Json);
        response.ContentType = "application/json";
        response.ContentLength64 = body.Length;
        await response.OutputStream.WriteAsync(body).ConfigureAwait(false);
    }

    // ---- Orders and profit, refreshed in the background ----

    private async Task RefreshOrdersLoopAsync(CancellationToken stop)
    {
        // Give sign-in and the first character refresh a moment.
        try { await Task.Delay(TimeSpan.FromSeconds(20), stop).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }
        while (!stop.IsCancellationRequested)
        {
            try { await RefreshOrdersAsync(stop).ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OperationCanceledException) { CrashLog.Write(ex, "Edge API orders"); }
            try { await Task.Delay(OrdersEvery, stop).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task RefreshOrdersAsync(CancellationToken stop)
    {
        var characters = await OnUi(() => _trade.Characters.Where(c => c.Error is null).ToList()).ConfigureAwait(false);
        var sde = _trade.Sde.Data;
        var orders = new List<EdgeOrder>();
        var profit = new List<EdgePnl>();
        foreach (var c in characters)
        {
            try
            {
                var (open, _) = await _orders.OrdersAsync(c, stop).ConfigureAwait(false);
                // Checking the competition costs a market request per order (cached by ESI); cap it.
                foreach (var (o, index) in open.OrderByDescending(o => o.RemainingValue).Select((o, i) => (o, i)))
                {
                    double? best = index < CompetitorChecksPerCharacter ? await _orders.BestCompetitorAsync(o, stop).ConfigureAwait(false) : null;
                    var (standing, attention) = index < CompetitorChecksPerCharacter ? OrderStanding.Describe(o, best) : ("—", false);
                    orders.Add(new EdgeOrder(
                        c.Id, c.Name, o.OrderId, o.TypeId, sde?.TypeName(o.TypeId) ?? $"Type {o.TypeId}",
                        o.IsBuy, o.Price, o.VolumeRemain, o.VolumeTotal,
                        await _trade.StationNameAsync(o.LocationId).ConfigureAwait(false),
                        o.Escrow, o.ExpiresAt, standing, attention));
                }

                var ledger = await _orders.SyncLedgerAsync(c, stop).ConfigureAwait(false);
                var now = DateTime.UtcNow;
                var report = PnlCalculator.Compute(ledger, now.AddDays(-7), now);
                profit.Add(new EdgePnl(c.Id, c.Name, 7, report.Sales, report.Purchases, report.NetProfit,
                    report.TradingProfit, report.SalesTax, report.BrokerFees));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // One character's ESI trouble shouldn't hide the others' orders.
                CrashLog.Write(ex, $"Edge API orders for {c.Name}");
            }
        }
        _orderSnapshot = orders;
        _profitSnapshot = profit;
        _ordersUpdated = DateTimeOffset.UtcNow;
    }

    public void Dispose()
    {
        _stop.Cancel();
        try { _listener?.Close(); } catch (ObjectDisposedException) { }
    }
}
