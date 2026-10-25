using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using EveMultibox.Config;
using EveMultibox.Esi;
using EveMultibox.StaticData;

namespace EveMultibox.Trade;

/// <summary>The trade module's window. Stage 1: signed-in characters and autopilot destinations.</summary>
public partial class TradeWindow : Window
{
    private readonly App _app;
    private readonly TradeService _trade;
    private readonly MarketService _market;
    private CancellationTokenSource? _search;

    /// <summary>Route sizes are capped at what the destination sells in this many days.</summary>
    private const double DaysToSell = 3;

    public TradeWindow(App app, TradeService trade)
    {
        InitializeComponent();
        Shell.WindowLook.Apply(this, resizable: true);
        _app = app;
        _trade = trade;
        _market = new MarketService(trade, () => app.Config.Trade.HubStationIds);

        CharacterList.ItemsSource = trade.Characters;
        DestCharacterBox.ItemsSource = trade.Characters;
        RouteCharBox.ItemsSource = trade.Characters;
        SellCharBox.ItemsSource = trade.Characters;
        OrdersCharBox.ItemsSource = trade.Characters;
        BuildPnlChoices();
        trade.Characters.CollectionChanged += (_, _) => BuildPnlChoices();
        RouteRadiusBox.Text = SellRadiusBox.Text = app.Config.Trade.JumpRadius.ToString();
        Closed += (_, _) => _search?.Cancel();
        trade.Characters.CollectionChanged += (_, _) => SelectDefaultCharacter();
        trade.Status += ShowStatus;
        Closed += (_, _) => trade.Status -= ShowStatus;
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) Close();
        };

        if (app.Config.Trade.WindowPosition is WindowSpot spot)
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = spot.X;
            Top = spot.Y;
        }
        else
        {
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        // Keep the "data is N min old" notes current while the window is open.
        var ageTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        ageTimer.Tick += (_, _) => UpdateAges();
        Closed += (_, _) => ageTimer.Stop();

        Loaded += async (_, _) =>
        {
            KeepOnScreen();
            RefreshSetupState();
            // Picking defaults and restoring saved results aren't character changes by the user.
            _restoring = true;
            try
            {
                SelectDefaultCharacter();
                UpdateAges();
                RefreshRun();
                ageTimer.Start();
                await RestoreResultsAsync();
            }
            finally
            {
                _restoring = false;
            }
            await BuildHubFiltersAsync();
            await BuildHubButtonsAsync();
        };
    }

    /// <summary>Puts back the last saved searches (from this session or an earlier one).</summary>
    private async Task RestoreResultsAsync()
    {
        var routes = _trade.LastRoutes;
        var sells = _trade.LastSells;
        if (routes is null && sells is null) return;

        // Rows need static data (systems, stations), which may still be loading at startup.
        if (await _trade.EnsureStaticDataAsync() is not { } sde) return;

        if (routes?.Routes is { } savedRoutes)
        {
            _trade.RememberStationNames(routes.StationNames);
            var rows = savedRoutes
                .Where(r => sde.Stations.ContainsKey(r.Route.SourceStation) && sde.Stations.ContainsKey(r.Route.DestStation))
                .Select(r => new RouteRow(r.Route, sde, _trade.CachedStationName) { Daily = r.Daily })
                .ToList();
            Restore(routes, rows, RoutesGrid, RouteStatus, RouteCharBox, RouteHubsRadio, RouteJumpsRadio, RouteRadiusBox);

            // Every route found last time, so a different From / To works straight away (older saves only have the shown ones).
            _candidates = routes.Candidates ?? savedRoutes.Select(r => r.Route).ToList();
            _shownFilter = (routes.FilterFrom, routes.FilterTo);
            RouteFilterCount.Text = $"{rows.Count} routes  ·  from {_candidates.Count:N0} candidates";
            if (_trade.CurrentRun is null) StartNewRun(sde, routes.CharacterId, routes.FreeCargo, routes.FreeOreHold, routes.Wallet);
        }
        RefreshRun();

        if (sells?.Sales is { } savedSales)
        {
            _trade.RememberStationNames(sells.StationNames);
            var rows = savedSales
                .Where(s => (s.Now ?? s.List) is { } best && sde.Stations.ContainsKey(best.Station)
                            && (s.Now is null || sde.Stations.ContainsKey(s.Now.Station))
                            && (s.List is null || sde.Stations.ContainsKey(s.List.Station)))
                .Select(s => new SellRow(s.Now, s.List, sde, _trade.CachedStationName))
                .ToList();
            Restore(sells, rows, SellGrid, SellStatus, SellCharBox, SellHubsRadio, SellJumpsRadio, SellRadiusBox);
        }
        UpdateAges();
    }

    private void Restore(SearchResults last, System.Collections.IEnumerable rows, DataGrid grid, TextBlock status, ComboBox charBox,
        RadioButton hubs, RadioButton jumps, TextBox radius)
    {
        // A newer search in this window wins over the saved one.
        if (grid.ItemsSource is not null) return;
        grid.ItemsSource = rows;
        status.Text = last.Status;
        // Selecting the saved character isn't a user change (the Loaded handler sets _restoring around this).
        if (_trade.Characters.FirstOrDefault(c => c.Id == last.CharacterId) is { } c) charBox.SelectedItem = c;
        hubs.IsChecked = last.Mode == SearchMode.HubToHub;
        jumps.IsChecked = last.Mode == SearchMode.WithinJumps;
        if (last.Mode == SearchMode.WithinJumps) radius.Text = last.Radius.ToString();
    }

    /// <summary>"Repopulate" once there's data, and how old hub-to-hub data is.</summary>
    private void UpdateAges()
    {
        Describe(_trade.LastRoutes, PopulateRoutesButton, RouteAgeText);
        Describe(_trade.LastSells, PopulateSellButton, SellAgeText);
    }

    private static void Describe(SearchResults? last, Button button, TextBlock age)
    {
        button.Content = last is null ? "Populate" : "Repopulate";
        age.Text = last is { Mode: SearchMode.HubToHub } ? $"Hub data {AgeText(DateTimeOffset.UtcNow - last.At)}" : "";
    }

    internal static string AgeText(TimeSpan age) => age.TotalMinutes switch
    {
        < 1 => "from just now",
        < 2 => "1 minute old",
        < 60 => $"{(int)age.TotalMinutes} minutes old",
        < 120 => "1 hour old",
        _ => $"{(int)age.TotalHours} hours old",
    };

    private static SearchMode ModeOf(RadioButton hubs) => hubs.IsChecked == true ? SearchMode.HubToHub : SearchMode.WithinJumps;

    private static int RadiusOf(TextBox box) => int.TryParse(box.Text, out int r) ? r : 0;

    /// <summary>
    /// Moves the window back inside its monitor's work area if a saved position (or a size change)
    /// left part of it off screen. Works in real pixels, like the ☰ button.
    /// </summary>
    private void KeepOnScreen()
    {
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (!Interop.Win32.GetWindowRect(hwnd, out var r)) return;
        var work = System.Windows.Forms.Screen.FromHandle(hwnd).WorkingArea;

        int x = Math.Max(work.Left, Math.Min(r.Left, work.Right - r.Width));
        int y = Math.Max(work.Top, Math.Min(r.Top, work.Bottom - r.Height));
        if (x == r.Left && y == r.Top) return;

        const uint SWP_NOZORDER = 0x0004;
        Interop.Win32.SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0, Interop.Win32.SWP_NOSIZE | SWP_NOZORDER | Interop.Win32.SWP_NOACTIVATE);
    }

    /// <summary>Call after trade settings change.</summary>
    public void RefreshSetupState()
    {
        bool configured = _trade.Auth.IsConfigured;
        SetupBanner.Visibility = configured ? Visibility.Collapsed : Visibility.Visible;
        SignInButton.IsEnabled = configured;
    }

    private void SelectDefaultCharacter()
    {
        if (_trade.Characters.Count == 0) return;
        foreach (var box in new[] { DestCharacterBox, RouteCharBox, SellCharBox, OrdersCharBox })
            if (box.SelectedItem is null) box.SelectedIndex = 0;
    }

    // ---- shared search setup ----

    private sealed record SearchSetup(CharacterState Character, SdeData Sde, IReadOnlyList<long> Stations, Func<int, int, int?> Jumps,
        Dictionary<int, double> Averages, List<EsiAsset> Assets, MarketSnapshot Market, CancellationToken Token);

    /// <summary>Market data from the last route search this session, reusable for another character.</summary>
    private MarketCache? _routeMarket;

    /// <summary>Everything both tabs need: fresh character info, the station set, orders, prices, assets.</summary>
    /// <param name="reuse">Market data to reuse instead of downloading again, if it covers the same stations.</param>
    private async Task<SearchSetup?> PrepareAsync(ComboBox charBox, RadioButton hubs, TextBox radiusBox, CheckBox safeBox, TextBlock status,
        MarketCache? reuse = null)
    {
        if (charBox.SelectedItem is not CharacterState c)
        {
            status.Text = "Sign in a character on the Characters tab first.";
            return null;
        }
        if (!int.TryParse(radiusBox.Text, out int radius) || radius is < 1 or > 40)
        {
            status.Text = "Jumps must be a number from 1 to 40.";
            return null;
        }

        _search?.Cancel();
        _search = new CancellationTokenSource();
        var ct = _search.Token;
        var progress = new Progress<string>(m => status.Text = m);

        var sde = await _trade.EnsureStaticDataAsync();
        if (sde is null)
        {
            status.Text = "EVE static data isn't available yet (see the status line below).";
            return null;
        }

        status.Text = $"Checking {c.Name}'s location, ship and wallet…";
        await _trade.RefreshCharacterAsync(c);
        if (c.SystemId is null)
        {
            status.Text = $"Couldn't find {c.Name}'s location. {c.Error}";
            return null;
        }

        bool safe = safeBox.IsChecked == true;
        var mode = hubs.IsChecked == true ? SearchMode.HubToHub : SearchMode.WithinJumps;
        var stations = _market.StationsFor(mode, c.SystemId, radius, safe, sde);
        if (stations.Count == 0)
        {
            status.Text = "No NPC stations in range. Try more jumps.";
            return null;
        }

        status.Text = $"Reading {c.Name}'s assets…";
        var assets = await _market.AssetsAsync(c, ct);

        // Same stations as the market data already loaded? Then only the character changed: reuse it.
        bool reusable = reuse?.Covers(mode, safe, stations) == true;
        var averages = reusable ? reuse!.Averages : await _market.AveragePricesAsync(ct);
        var snapshot = reusable ? reuse!.Market : await _market.LoadOrdersAsync(stations, sde, progress, ct);
        await _trade.StationNamesAsync(stations);
        return new SearchSetup(c, sde, stations, _market.JumpCounter(sde, safe, _app.Config.Trade.SaferRoutes), averages, assets, snapshot, ct);
    }

    // ---- Trade Routes ----

    private async void PopulateRoutes_Click(object sender, RoutedEventArgs e) => await PopulateRoutesAsync(reuseMarket: false);

    /// <param name="reuseMarket">Recompute with this session's market data (e.g. after switching character) instead of downloading it again.</param>
    private async Task PopulateRoutesAsync(bool reuseMarket)
    {
        // Old results stay visible until new ones arrive, so a failed refresh doesn't wipe them.
        PopulateRoutesButton.IsEnabled = false;
        RouteStatus.Foreground = (System.Windows.Media.Brush)FindResource("TextDim");
        try
        {
            var s = await PrepareAsync(RouteCharBox, RouteHubsRadio, RouteRadiusBox, RouteSafeBox, RouteStatus,
                reuseMarket ? _routeMarket : null);
            if (s is null) return;
            _routeMarket = new MarketCache(ModeOf(RouteHubsRadio), RouteSafeBox.IsChecked == true, s.Stations, s.Market, s.Averages);

            var trader = MarketService.Trader(s.Character, s.Assets, s.Sde);
            RouteStatus.Text = $"Comparing {s.Market.OrderCount:N0} orders across {s.Stations.Count} stations…";

            // Hub to hub: keep the best routes for every direction (20 hub pairs), so a direction with thinner
            // margins, e.g. anything into Jita, isn't crowded out by the most profitable one.
            bool hubMode = ModeOf(RouteHubsRadio) == SearchMode.HubToHub;
            var options = hubMode
                ? new RouteOptions { MaxPerPair = CandidatesPerDirection, MaxResults = int.MaxValue }
                : new RouteOptions { MaxResults = 1500 };
            var candidates = await Task.Run(() => RouteFinder.Find(s.Market, s.Stations, s.Sde, trader, s.Jumps, s.Averages, options), s.Token);

            _candidates = candidates;
            _shownFilter = null;
            _trade.LastRoutes = new SearchResults(ModeOf(RouteHubsRadio), RadiusOf(RouteRadiusBox), s.Character.Id,
                s.Market.FetchedAt, "", new List<SavedRoute>(), null,
                _trade.StationNamesFor(candidates.SelectMany(r => new[] { r.SourceStation, r.DestStation })),
                trader.FreeCargo, trader.FreeOreHold, trader.Wallet, candidates);
            if (reuseMarket && _trade.Runs.Switch(s.Character.Id))
            {
                // Same market data, so this character's run from earlier still holds.
                RefreshRun($"Back to {s.Character.Name}'s run.");
            }
            else
            {
                // New market data: every run's prices are stale. Plans already in a widget carry on.
                bool hadItems = !reuseMarket && _trade.Runs.AnyItems;
                if (!reuseMarket) _trade.Runs.Reset();
                StartNewRun(s.Sde, s.Character.Id, trader.FreeCargo, trader.FreeOreHold, trader.Wallet, cleared: hadItems);
            }
            UpdateAges();

            // Demand-check and show the routes for the current From / To choice.
            await ShowFilteredRoutesAsync(force: true);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is EsiException or SignInRequiredException or System.Net.Http.HttpRequestException or InvalidOperationException)
        {
            RouteStatus.Text = "Search failed: " + ex.Message;
        }
        finally
        {
            PopulateRoutesButton.IsEnabled = true;
        }
    }

    // ---- From / To hub filter (hub to hub only) ----

    private sealed record HubChoice(long? StationId, string Name)
    {
        public override string ToString() => Name;
    }

    /// <summary>Best routes kept per hub direction before the demand check (5 hubs = 20 directions).</summary>
    private const int CandidatesPerDirection = 300;

    /// <summary>The demand-checked routes shown per direction.</summary>
    private const int RoutesShown = 100;

    /// <summary>Every route the last search found (best per direction), demand-checked on demand for the chosen From / To.</summary>
    private List<RouteCandidate>? _candidates;

    /// <summary>The From / To the grid currently shows, so re-selecting the same pair doesn't recheck.</summary>
    private (long? From, long? To)? _shownFilter;

    private CancellationTokenSource? _verify;
    private bool _buildingFilters;

    private async Task BuildHubFiltersAsync()
    {
        var hubs = _app.Config.Trade.HubStationIds;
        await _trade.StationNamesAsync(hubs);
        var choices = new List<HubChoice> { new(null, "Any hub") };
        choices.AddRange(hubs.Select(h => new HubChoice(h, ShortStation(h))));

        // Start on the direction the saved results are for, without triggering a recheck.
        _buildingFilters = true;
        RouteFromBox.ItemsSource = choices;
        RouteToBox.ItemsSource = choices.ToList();
        var last = _trade.LastRoutes;
        RouteFromBox.SelectedIndex = Math.Max(0, choices.FindIndex(c => c.StationId == last?.FilterFrom));
        RouteToBox.SelectedIndex = Math.Max(0, choices.FindIndex(c => c.StationId == last?.FilterTo));
        _buildingFilters = false;
        UpdateFilterRow();
    }

    private async void RouteFilter_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_buildingFilters) await ShowFilteredRoutesAsync();
    }

    private void RouteMode_Changed(object sender, RoutedEventArgs e)
    {
        if (IsLoaded) UpdateFilterRow();
    }

    private (long? From, long? To) CurrentFilter() => RouteHubsRadio.IsChecked == true
        ? ((RouteFromBox.SelectedItem as HubChoice)?.StationId, (RouteToBox.SelectedItem as HubChoice)?.StationId)
        : (null, null);

    private string FilterLabel((long? From, long? To) f) =>
        $"{(f.From is long a ? ShortStation(a) : "any hub")} → {(f.To is long b ? ShortStation(b) : "any hub")}";

    /// <summary>
    /// Demand-checks the routes for the chosen From / To and shows the best <see cref="RoutesShown"/>.
    /// Uses the last search's routes, so changing direction needs no new market download.
    /// </summary>
    private async Task ShowFilteredRoutesAsync(bool force = false)
    {
        UpdateFilterRow();
        var filter = CurrentFilter();
        if (_candidates is null || _trade.LastRoutes is not { } last || (!force && _shownFilter == filter)) return;
        if (await _trade.EnsureStaticDataAsync() is not { } sde) return;

        _verify?.Cancel();
        var cts = _verify = new CancellationTokenSource();
        var matching = _candidates.Where(r => RouteFilter.Matches(r, filter.From, filter.To)).ToList();
        string who = _trade.Characters.FirstOrDefault(c => c.Id == last.CharacterId)?.Name ?? "you";
        RouteStatus.Foreground = (System.Windows.Media.Brush)FindResource("TextDim");

        try
        {
            // Only keep what the destination actually sells: units are cut to ~3 days of its trade volume.
            var routes = await _market.WithDemandAsync(matching, sde, DaysToSell, new RouteOptions().MinProfit, want: RoutesShown,
                new Progress<string>(m => { if (!cts.IsCancellationRequested) RouteStatus.Text = m; }), cts.Token);
            if (cts.IsCancellationRequested) return;

            var rows = routes.Select(r => new RouteRow(r.Route, sde, _trade.CachedStationName) { Daily = r.Daily }).ToList();
            RoutesGrid.ItemsSource = rows;
            _shownFilter = filter;
            RouteFilterCount.Text = $"{rows.Count} routes for {FilterLabel(filter)}  ·  from {matching.Count:N0} candidates";
            RouteStatus.Text = rows.Count == 0
                ? $"No profitable routes with real demand for {FilterLabel(filter)} right now with {who}'s hold and wallet."
                : $"Best {rows.Count} routes for {FilterLabel(filter)}, sized for {who} (units capped at ~{DaysToSell} days of sales at the destination). " +
                  $"Free hold: {last.FreeCargo:N0} m³" + (last.FreeOreHold > 0 ? $", mining hold {last.FreeOreHold:N0} m³" : "") +
                  $" (EVE's cargo data can be up to an hour old). Wallet {Isk.Short(last.Wallet)}.";

            _trade.LastRoutes = last with
            {
                Routes = routes.Select(r => new SavedRoute(r.Route, r.Daily)).ToList(),
                Status = RouteStatus.Text,
                FilterFrom = filter.From,
                FilterTo = filter.To,
            };
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is EsiException or System.Net.Http.HttpRequestException)
        {
            if (!cts.IsCancellationRequested) RouteStatus.Text = "Couldn't check demand: " + ex.Message;
        }
    }

    private void UpdateFilterRow()
    {
        bool hubMode = RouteHubsRadio.IsChecked == true;
        HubFilterRow.IsEnabled = hubMode;
        HubFilterRow.Opacity = hubMode ? 1 : 0.4;
    }

    /// <summary>Swaps From and To: after a Jita → Amarr run, one click shows Amarr → Jita.</summary>
    private void SwapHubs_Click(object sender, RoutedEventArgs e)
    {
        int from = RouteFromBox.SelectedIndex;
        RouteFromBox.SelectedIndex = RouteToBox.SelectedIndex;
        RouteToBox.SelectedIndex = from;
    }

    private void RoutesGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        SelectRouteButton.IsEnabled = RoutesGrid.SelectedItem is RouteRow;
        AddToRunButton.IsEnabled = RoutesGrid.SelectedItem is RouteRow && _trade.CurrentRun is not null;
    }

    // ---- My Orders ----

    private CancellationTokenSource? _ordersLoad;

    private async void OrdersCharBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IsLoaded && !_restoring) await LoadOrdersAsync();
    }

    private async void RefreshOrders_Click(object sender, RoutedEventArgs e) => await LoadOrdersAsync();

    private async Task LoadOrdersAsync()
    {
        if (OrdersCharBox.SelectedItem is not CharacterState c)
        {
            OrdersStatus.Text = "Sign in a character on the Characters tab first.";
            return;
        }
        _ordersLoad?.Cancel();
        var cts = _ordersLoad = new CancellationTokenSource();
        RefreshOrdersButton.IsEnabled = false;
        try
        {
            if (await _trade.EnsureStaticDataAsync() is not { } sde) return;
            OrdersStatus.Text = $"Loading {c.Name}'s orders…";
            var (orders, nextRefresh) = await _app.Orders.OrdersAsync(c, cts.Token);
            await _trade.StationNamesAsync(orders.Select(o => o.LocationId));
            if (cts.IsCancellationRequested) return;

            var rows = orders.OrderBy(o => o.IsBuy).ThenBy(o => sde.TypeName(o.TypeId))
                .Select(o => new OrderRow(o, c.Name, sde, _trade.CachedStationName)).ToList();
            OrdersGrid.ItemsSource = rows;

            var sells = orders.Where(o => !o.IsBuy).ToList();
            var buys = orders.Where(o => o.IsBuy).ToList();
            SellOrdersValue.Text = $"{Isk.Short(sells.Sum(o => o.RemainingValue))}  ({sells.Count})";
            BuyOrdersValue.Text = $"{Isk.Short(buys.Sum(o => o.RemainingValue))}  ({buys.Count})";
            OrderSlotsText.Text = c.Skills.OrderSlots > 0 ? $"{orders.Count} / {c.Skills.OrderSlots}" : $"{orders.Count}";
            OrdersAttention.Text = "…";
            OrdersAgeText.Text = $"EVE refreshes this list at {nextRefresh.ToLocalTime():t}";
            OrdersStatus.Text = rows.Count == 0 ? $"{c.Name} has no open orders." : "Checking each order against the competition at its station…";

            // Compare with competing orders, a few at a time.
            var gate = new SemaphoreSlim(4);
            await Task.WhenAll(rows.Select(async row =>
            {
                await gate.WaitAsync(cts.Token);
                try
                {
                    var best = await _app.Orders.BestCompetitorAsync(row.Order, cts.Token);
                    var (text, attention) = OrderStanding.Describe(row.Order, best);
                    row.Standing = text;
                    row.StandingTone = attention ? (text.StartsWith("Tied") ? Tone.Warning : Tone.Negative) : Tone.Positive;
                }
                catch (EsiException)
                {
                    row.Standing = "couldn't check";
                }
                finally
                {
                    gate.Release();
                }
            }));
            if (cts.IsCancellationRequested) return;

            int attentionCount = rows.Count(r => r.StandingTone == Tone.Negative);
            OrdersAttention.Text = attentionCount == 0 ? "None" : $"{attentionCount} undercut / outbid";
            OrdersAttention.Foreground = (System.Windows.Media.Brush)FindResource(attentionCount == 0 ? "Positive" : "Negative");
            OrdersStatus.Text = rows.Count == 0 ? OrdersStatus.Text
                : $"{rows.Count} open orders for {c.Name}. Standing compares each order with other orders at the same station.";
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is EsiException or SignInRequiredException or System.Net.Http.HttpRequestException)
        {
            OrdersStatus.Text = "Couldn't load orders: " + ex.Message;
        }
        finally
        {
            RefreshOrdersButton.IsEnabled = true;
        }
    }

    // ---- P&L ----

    private sealed record CharChoice(CharacterState? Character, string Name)
    {
        public override string ToString() => Name;
    }

    private sealed record PeriodChoice(string Name, Func<(DateTime From, DateTime To)> Range)
    {
        public override string ToString() => Name;
    }

    private bool _pnlSyncedOnce;

    private void BuildPnlChoices()
    {
        var current = (PnlCharBox.SelectedItem as CharChoice)?.Character?.Id;
        var choices = new List<CharChoice> { new(null, "All characters") };
        choices.AddRange(_trade.Characters.Select(c => new CharChoice(c, c.Name)));
        PnlCharBox.ItemsSource = choices;
        PnlCharBox.SelectedItem = choices.FirstOrDefault(c => c.Character?.Id == current) ?? choices[0];

        if (PnlPeriodBox.ItemsSource is null)
        {
            // EVE runs on UTC, so "today" starts at 00:00 EVE time.
            PnlPeriodBox.ItemsSource = new List<PeriodChoice>
            {
                new("Today (EVE time)", () => (DateTime.UtcNow.Date, DateTime.MaxValue)),
                new("Last 7 days", () => (DateTime.UtcNow.AddDays(-7), DateTime.MaxValue)),
                new("Last 30 days", () => (DateTime.UtcNow.AddDays(-30), DateTime.MaxValue)),
                new("All time", () => (DateTime.MinValue, DateTime.MaxValue)),
            };
            PnlPeriodBox.SelectedIndex = 1;
        }
    }

    private IReadOnlyList<CharacterState> PnlCharacters() =>
        PnlCharBox.SelectedItem is CharChoice { Character: { } one } ? new[] { one } : _trade.Characters.ToList();

    private async void PnlFilter_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;
        // The first time the report is shown, fetch the latest from EVE; after that, use the saved history.
        if (!_pnlSyncedOnce) await SyncPnlAsync();
        else await ShowPnlAsync();
    }

    private async void SyncPnl_Click(object sender, RoutedEventArgs e) => await SyncPnlAsync();

    private async Task SyncPnlAsync()
    {
        SyncPnlButton.IsEnabled = false;
        try
        {
            foreach (var c in PnlCharacters())
            {
                PnlStatus.Text = $"Syncing {c.Name}'s wallet…";
                await _app.Orders.SyncLedgerAsync(c);
            }
            _pnlSyncedOnce = true;
        }
        catch (Exception ex) when (ex is EsiException or SignInRequiredException or System.Net.Http.HttpRequestException)
        {
            PnlStatus.Text = "Couldn't sync: " + ex.Message + " Showing the saved history.";
        }
        finally
        {
            SyncPnlButton.IsEnabled = true;
        }
        await ShowPnlAsync();
    }

    /// <summary>Profit and loss from the saved history (no network), for one character or all.</summary>
    private async Task ShowPnlAsync()
    {
        if (await _trade.EnsureStaticDataAsync() is not { } sde || PnlPeriodBox.SelectedItem is not PeriodChoice period) return;
        var (from, to) = period.Range();

        var ledgers = PnlCharacters().Select(c => _app.Orders.SavedLedger(c.Id)).ToList();
        var reports = ledgers.Select(l => PnlCalculator.Compute(l, from, to)).ToList();

        // Combine characters item by item.
        var rows = reports.SelectMany(r => r.Rows).GroupBy(r => r.TypeId).Select(g => new PnlRow(g.Key,
                g.Sum(r => r.BoughtQty), g.Sum(r => r.BoughtValue), g.Sum(r => r.SoldQty), g.Sum(r => r.SoldValue),
                g.Sum(r => r.CostOfSold), g.Sum(r => r.Profit), g.Sum(r => r.UnknownCostQty)))
            .OrderByDescending(r => r.Profit)
            .Select(r => new PnlRowView(r, sde.TypeName(r.TypeId)))
            .ToList();
        PnlGrid.ItemsSource = rows;

        double trading = reports.Sum(r => r.TradingProfit), net = reports.Sum(r => r.NetProfit);
        PnlSales.Text = Isk.Short(reports.Sum(r => r.Sales));
        PnlPurchases.Text = Isk.Short(reports.Sum(r => r.Purchases));
        PnlTax.Text = Isk.Short(reports.Sum(r => r.SalesTax));
        PnlBroker.Text = Isk.Short(reports.Sum(r => r.BrokerFees));
        PnlTrading.Text = (trading >= 0 ? "+" : "") + Isk.Short(trading);
        PnlNet.Text = (net >= 0 ? "+" : "") + Isk.Short(net);
        PnlTrading.Foreground = (System.Windows.Media.Brush)FindResource(trading >= 0 ? "Positive" : "Negative");
        PnlNet.Foreground = (System.Windows.Media.Brush)FindResource(net >= 0 ? "Positive" : "Negative");

        var first = ledgers.SelectMany(l => l.Transactions).Select(t => (DateTime?)t.Date).Min();
        var synced = ledgers.Select(l => l.LastSync).Where(s => s is not null).Max();
        PnlSyncText.Text = (first is DateTime f ? $"History since {f:d MMM yyyy}" : "No saved history yet") +
                           (synced is DateTimeOffset s ? $" · synced {s.ToLocalTime():t}" : "");

        long unknown = reports.Sum(r => r.UnknownCostSales);
        PnlStatus.Text = unknown > 0
            ? $"{unknown:N0} units were sold without a known cost (bought before the saved history), so they count in sales but not in profit."
            : "Profit uses each item's average purchase cost. Net profit also takes off the period's sales tax and broker fees.";
    }

    // ---- Trade run ----

    private sealed record RunItemView(RouteCandidate Route, string Name, string Detail, string ProfitText);

    /// <summary>Whose hold and wallet the current run is sized for.</summary>
    private CharacterState? RunCharacter => _trade.Characters.FirstOrDefault(c => c.Id == _trade.Runs.CurrentFor);

    /// <summary>A fresh, empty run sized to one character's free hold and wallet before buying.</summary>
    private void StartNewRun(SdeData sde, long characterId, double cargo, double ore, double wallet, bool cleared = false)
    {
        _trade.Runs.Start(characterId, new TradeRun(cargo, ore, wallet, type => sde.Types.TryGetValue(type, out var t) && t.IsOre));
        RefreshRun(cleared ? "New market data, so the runs were cleared. Add routes again." : null);
    }

    private bool _restoring;

    /// <summary>
    /// Routes and the run are sized to one character's hold and wallet, so picking someone else
    /// re-sizes them: instantly from this session's market data, or the run alone until Repopulate.
    /// </summary>
    private async void RouteCharBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_restoring || !IsLoaded || RouteCharBox.SelectedItem is not CharacterState c || _trade.LastRoutes is not { } last) return;
        if (last.CharacterId == c.Id && _trade.Runs.CurrentFor == c.Id) return;

        if (_routeMarket is not null)
        {
            await PopulateRoutesAsync(reuseMarket: true);
            return;
        }

        // No market data loaded this session (e.g. results restored after a restart): re-size the run now,
        // and say the routes themselves need a Repopulate.
        try
        {
            if (await _trade.EnsureStaticDataAsync() is not { } sde) return;
            await _trade.RefreshCharacterAsync(c);
            if (_trade.Runs.Switch(c.Id))
            {
                RefreshRun($"Back to {c.Name}'s run.");
            }
            else
            {
                var trader = MarketService.Trader(c, await _market.AssetsAsync(c, CancellationToken.None), sde);
                StartNewRun(sde, c.Id, trader.FreeCargo, trader.FreeOreHold, trader.Wallet);
            }
            string owner = _trade.Characters.FirstOrDefault(x => x.Id == last.CharacterId)?.Name ?? "another character";
            RouteStatus.Foreground = (System.Windows.Media.Brush)FindResource("Warning");
            RouteStatus.Text = $"The run is now sized for {c.Name}, but these routes were sized for {owner}. " +
                               $"Press Repopulate to size them for {c.Name}.";
        }
        catch (Exception ex) when (ex is EsiException or SignInRequiredException or System.Net.Http.HttpRequestException or InvalidOperationException)
        {
            RouteStatus.Text = $"Couldn't read {c.Name}'s hold and wallet: {ex.Message}";
        }
    }

    private void RefreshRun(string? message = null)
    {
        var run = _trade.CurrentRun;
        if (run is null)
        {
            RunHeader.Text = "Populate routes first";
            RunCargoText.Text = RunCashText.Text = RunProfitText.Text = "";
            RunList.ItemsSource = null;
            StartRunButton.IsEnabled = AutoFillButton.IsEnabled = AddToRunButton.IsEnabled = false;
            return;
        }

        RunCaption.Text = RunCharacter is { } owner ? $"TRADE RUN · SIZED FOR {owner.Name.ToUpperInvariant()}" : "TRADE RUN";
        RunHeader.Text = run.SourceStation is long src && run.DestStation is long dst
            ? $"{ShortStation(src)} → {ShortStation(dst)}"
            : "Add routes to plan one trip";

        RunCargoText.Text = $"{run.CargoUsed:N0} / {run.CargoSpace:N0} m³";
        RunCargoBar.Value = run.CargoSpace > 0 ? run.CargoUsed / run.CargoSpace : 0;

        bool hasOre = run.OreSpace > 0;
        RunOreRow.Visibility = RunOreBar.Visibility = hasOre ? Visibility.Visible : Visibility.Collapsed;
        RunOreText.Text = $"{run.OreUsed:N0} / {run.OreSpace:N0} m³";
        RunOreBar.Value = hasOre ? run.OreUsed / run.OreSpace : 0;

        RunCashText.Text = $"{Isk.Short(run.CashUsed)} / {Isk.Short(run.Cash)}";
        RunCashBar.Value = run.Cash > 0 ? run.CashUsed / run.Cash : 0;
        RunProfitText.Text = run.Items.Count == 0 ? "—" : "+" + Isk.Short(run.Profit);

        RunList.ItemsSource = run.Items
            .Select(i => new RunItemView(i, i.ItemName,
                $"{i.Units:N0} units · {i.CargoUsed:N0} m³ · cost {Isk.Short(i.SeedCost)}", "+" + Isk.Short(i.Profit)))
            .ToList();

        StartRunButton.IsEnabled = run.Items.Count > 0;
        AutoFillButton.IsEnabled = true;
        AddToRunButton.IsEnabled = RoutesGrid.SelectedItem is RouteRow;
        if (message is not null) RunMessage.Text = message;
    }

    /// <summary>"Jita IV - Moon 4 - Caldari Navy Assembly Plant" → "Jita IV-4".</summary>
    private string ShortStation(long id)
    {
        string name = _trade.CachedStationName(id);
        var parts = name.Split(" - ");
        return parts.Length >= 2 && parts[1].StartsWith("Moon ") ? $"{parts[0]}-{parts[1][5..]}" : parts[0];
    }

    private void AddToRun_Click(object sender, RoutedEventArgs e)
    {
        if (RoutesGrid.SelectedItem is not RouteRow row || _trade.CurrentRun is not { } run) return;
        string? problem = run.TryAdd(row.Route);
        RefreshRun(problem ?? (run.Items[^1].Units < row.Units
            ? $"Added {run.Items[^1].Units:N0} of {row.Units:N0} {row.Item}: that's all that fits."
            : $"Added {row.Units:N0} {row.Item}."));
    }

    private void AutoFill_Click(object sender, RoutedEventArgs e)
    {
        if (_trade.CurrentRun is not { } run) return;
        // Only the routes the From/To filter is showing, so a return run is packed with return items.
        var visible = RoutesGrid.Items.OfType<RouteRow>().Select(r => r.Route).ToList();
        int added = run.AutoFill(visible);
        RefreshRun(added == 0 ? "Nothing more fits profitably on this run's route." : $"Added {added} item(s).");
    }

    private void ClearRun_Click(object sender, RoutedEventArgs e)
    {
        _trade.CurrentRun?.Clear();
        RefreshRun("Run cleared.");
    }

    private void RemoveRunItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: RouteCandidate item }) _trade.CurrentRun?.Remove(item);
        RefreshRun();
    }

    private void StartRun_Click(object sender, RoutedEventArgs e)
    {
        // The run goes to the character it was sized for.
        if (_trade.CurrentRun is not { Items.Count: > 0 } run || RunCharacter is not { } c || _trade.Sde.Data is not { } sde) return;
        if (_app.ShowActionPlan(Widget.TradePlans.ForRun(run, c, sde, _trade.CachedStationName)))
            RunMessage.Text = $"Run started in {c.Name}'s action widget. Pick another character to plan their run too.";
    }

    private void SelectRoute_Click(object sender, RoutedEventArgs e)
    {
        if (RoutesGrid.SelectedItem is not RouteRow row || RouteCharBox.SelectedItem is not CharacterState c || _trade.Sde.Data is not { } sde) return;
        if (_app.ShowActionPlan(Widget.TradePlans.ForRoute(row, c, sde)))
            RouteStatus.Text = $"Route started in {c.Name}'s action widget.";
    }

    // ---- Sell my items ----

    private async void PopulateSell_Click(object sender, RoutedEventArgs e)
    {
        PopulateSellButton.IsEnabled = false;
        try
        {
            var s = await PrepareAsync(SellCharBox, SellHubsRadio, SellRadiusBox, SellSafeBox, SellStatus);
            if (s is null) return;

            var c = s.Character;
            var stacks = SellFinder.ResolveStacks(s.Assets, c.ShipItemId, c.SystemId, c.StationId, s.Sde);
            await _trade.StationNamesAsync(stacks.Where(x => x.StationId is not null).Select(x => x.StationId!.Value));
            SellStatus.Text = $"Pricing {stacks.Count} item stacks…";
            var options = await Task.Run(() => SellFinder.Find(s.Market, s.Stations, s.Sde, c.Skills, stacks, s.Jumps, s.Averages), s.Token);

            var rows = options.Where(o => o.SellNow is not null || o.List is not null)
                .Select(o => new SellRow(o.SellNow, o.List, s.Sde, _trade.CachedStationName))
                .ToList();
            SellGrid.ItemsSource = rows;
            int unsellable = options.Count - rows.Count;
            SellStatus.Text = $"{rows.Count} items can be sold in range" +
                              (unsellable > 0 ? $"; {unsellable} have no buyers or sellers there" : "") +
                              ". Assets per EVE can be up to an hour old.";
            var sales = options.Where(o => o.SellNow is not null || o.List is not null)
                .Select(o => new SavedSale(o.SellNow, o.List)).ToList();
            _trade.LastSells = new SearchResults(ModeOf(SellHubsRadio), RadiusOf(SellRadiusBox), c.Id,
                s.Market.FetchedAt, SellStatus.Text, null, sales,
                _trade.StationNamesFor(sales.SelectMany(x => new[] { x.Now?.Station, x.List?.Station, x.Now?.Stack.StationId, x.List?.Stack.StationId })
                    .OfType<long>()));
            UpdateAges();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is EsiException or SignInRequiredException or System.Net.Http.HttpRequestException or InvalidOperationException)
        {
            SellStatus.Text = "Search failed: " + ex.Message;
        }
        finally
        {
            PopulateSellButton.IsEnabled = true;
        }
    }

    private void SellGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        SellItemButton.IsEnabled = SellGrid.SelectedItem is SellRow;

    private void SellItem_Click(object sender, RoutedEventArgs e)
    {
        if (SellGrid.SelectedItem is not SellRow row || SellCharBox.SelectedItem is not CharacterState c || _trade.Sde.Data is not { } sde) return;
        if (_app.ShowActionPlan(Widget.TradePlans.ForSale(row, c, sde)))
            SellStatus.Text = $"Selling {row.Item} in {c.Name}'s action widget.";
    }

    private async Task BuildHubButtonsAsync()
    {
        HubButtons.Children.Clear();
        HubButtons.Children.Add(new TextBlock { Text = "Hubs", Width = 60, VerticalAlignment = VerticalAlignment.Center });
        foreach (long stationId in _app.Config.Trade.HubStationIds)
        {
            string name = await _trade.StationNameAsync(stationId);
            string shortName = name.Split(' ')[0]; // "Jita IV - Moon 4 - Caldari Navy Assembly Plant" → "Jita"
            var button = new Button { Content = shortName, ToolTip = name, Margin = new Thickness(0, 0, 6, 0) };
            button.Click += async (_, _) => await SetDestinationAsync(stationId, name);
            HubButtons.Children.Add(button);
        }
    }

    private async Task SetDestinationAsync(long destinationId, string label)
    {
        if (DestCharacterBox.SelectedItem is not CharacterState character)
        {
            ShowStatus("Sign in a character first.");
            return;
        }
        bool waypoint = AsWaypointBox.IsChecked == true;
        try
        {
            await _trade.SetDestinationAsync(character, destinationId, waypoint);
            ShowStatus(waypoint ? $"Added {label} as a waypoint for {character.Name}." : $"Autopilot destination set to {label} for {character.Name}.");
        }
        catch (Exception ex) when (ex is EsiException or SignInRequiredException or System.Net.Http.HttpRequestException)
        {
            ShowStatus("Couldn't set the destination: " + ex.Message);
        }
    }

    private async void SetSystem_Click(object sender, RoutedEventArgs e) => await SetSystemFromBoxAsync();

    private async void SystemBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) await SetSystemFromBoxAsync();
    }

    private async Task SetSystemFromBoxAsync()
    {
        var sde = await _trade.EnsureStaticDataAsync();
        var system = sde?.SystemByName(SystemBox.Text);
        if (system is null)
        {
            ShowStatus($"No solar system called \"{SystemBox.Text.Trim()}\".");
            return;
        }
        await SetDestinationAsync(system.Id, system.Name);
    }

    private async void SignIn_Click(object sender, RoutedEventArgs e)
    {
        SignInButton.IsEnabled = false;
        try
        {
            await _trade.SignInAsync();
        }
        catch (Exception ex) when (ex is SignInRequiredException or EsiException or JwtValidationException or System.Net.Http.HttpRequestException)
        {
            ShowStatus("Sign-in failed: " + ex.Message);
        }
        finally
        {
            RefreshSetupState();
        }
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        ShowStatus("Refreshing…");
        await _trade.RefreshAllAsync();
        ShowStatus($"Updated {DateTime.Now:T}.");
    }

    private void SignOut_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: CharacterState c } &&
            MessageBox.Show(this, $"Sign out {c.Name}? You can sign in again any time.", "Sign out",
                MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK)
        {
            _trade.SignOut(c);
        }
    }

    private void OpenSettings_Click(object sender, RoutedEventArgs e) => _app.ShowGeneralSettings();

    private void ShowStatus(string message) => StatusText.Text = message;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        DragMove();
        _app.Config.Trade.WindowPosition = new WindowSpot { X = Left, Y = Top };
        _app.SaveQuietly();
    }
}
