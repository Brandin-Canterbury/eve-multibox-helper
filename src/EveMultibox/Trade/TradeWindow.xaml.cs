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
        _app = app;
        _trade = trade;
        _market = new MarketService(trade, () => app.Config.Trade.HubStationIds);

        CharacterList.ItemsSource = trade.Characters;
        DestCharacterBox.ItemsSource = trade.Characters;
        RouteCharBox.ItemsSource = trade.Characters;
        SellCharBox.ItemsSource = trade.Characters;
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
            SelectDefaultCharacter();
            UpdateAges();
            RefreshRun();
            ageTimer.Start();
            await RestoreResultsAsync();
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
            if (_trade.CurrentRun is null) StartNewRun(sde);
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
        foreach (var box in new[] { DestCharacterBox, RouteCharBox, SellCharBox })
            if (box.SelectedItem is null) box.SelectedIndex = 0;
    }

    // ---- shared search setup ----

    private sealed record SearchSetup(CharacterState Character, SdeData Sde, IReadOnlyList<long> Stations, Func<int, int, int?> Jumps,
        Dictionary<int, double> Averages, List<EsiAsset> Assets, MarketSnapshot Market, CancellationToken Token);

    /// <summary>Everything both tabs need: fresh character info, the station set, orders, prices, assets.</summary>
    private async Task<SearchSetup?> PrepareAsync(ComboBox charBox, RadioButton hubs, TextBox radiusBox, CheckBox safeBox, TextBlock status)
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

        var averages = await _market.AveragePricesAsync(ct);
        status.Text = $"Reading {c.Name}'s assets…";
        var assets = await _market.AssetsAsync(c, ct);
        var snapshot = await _market.LoadOrdersAsync(stations, sde, progress, ct);
        await _trade.StationNamesAsync(stations);
        return new SearchSetup(c, sde, stations, _market.JumpCounter(sde, safe), averages, assets, snapshot, ct);
    }

    // ---- Trade Routes ----

    private async void PopulateRoutes_Click(object sender, RoutedEventArgs e)
    {
        // Old results stay visible until new ones arrive, so a failed refresh doesn't wipe them.
        PopulateRoutesButton.IsEnabled = false;
        try
        {
            var s = await PrepareAsync(RouteCharBox, RouteHubsRadio, RouteRadiusBox, RouteSafeBox, RouteStatus);
            if (s is null) return;

            var trader = MarketService.Trader(s.Character, s.Assets, s.Sde);
            RouteStatus.Text = $"Comparing {s.Market.OrderCount:N0} orders across {s.Stations.Count} stations…";
            var options = new RouteOptions { MaxResults = 1500 };
            var candidates = await Task.Run(() => RouteFinder.Find(s.Market, s.Stations, s.Sde, trader, s.Jumps, s.Averages, options), s.Token);

            // Only keep what the destination actually sells: units are cut to ~3 days of its trade volume.
            var routes = await _market.WithDemandAsync(candidates, s.Sde, DaysToSell, options.MinProfit, want: 100,
                new Progress<string>(m => RouteStatus.Text = m), s.Token);

            var rows = routes.Select(r => new RouteRow(r.Route, s.Sde, _trade.CachedStationName) { Daily = r.Daily }).ToList();
            RoutesGrid.ItemsSource = rows;
            RouteStatus.Text = rows.Count == 0
                ? "No profitable routes with real demand right now for your hold and wallet. Try another mode or a bigger ship."
                : $"{rows.Count} routes (units capped at ~{DaysToSell} days of sales at the destination). " +
                  $"Free hold: {trader.FreeCargo:N0} m³" +
                  (trader.FreeOreHold > 0 ? $", mining hold {trader.FreeOreHold:N0} m³" : "") +
                  $" (EVE's cargo data can be up to an hour old). Wallet {Isk.Short(trader.Wallet)}.";
            _trade.LastRoutes = new SearchResults(ModeOf(RouteHubsRadio), RadiusOf(RouteRadiusBox), s.Character.Id,
                s.Market.FetchedAt, RouteStatus.Text,
                routes.Select(r => new SavedRoute(r.Route, r.Daily)).ToList(), null,
                _trade.StationNamesFor(routes.SelectMany(r => new[] { r.Route.SourceStation, r.Route.DestStation })),
                trader.FreeCargo, trader.FreeOreHold, trader.Wallet);
            StartNewRun(s.Sde, cleared: _trade.CurrentRun?.Items.Count > 0);
            UpdateAges();
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

    private void RoutesGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        SelectRouteButton.IsEnabled = RoutesGrid.SelectedItem is RouteRow;
        AddToRunButton.IsEnabled = RoutesGrid.SelectedItem is RouteRow && _trade.CurrentRun is not null;
    }

    // ---- Trade run ----

    private sealed record RunItemView(RouteCandidate Route, string Name, string Detail, string ProfitText);

    /// <summary>A fresh, empty run sized to the hold and wallet from the latest route search.</summary>
    private void StartNewRun(SdeData sde, bool cleared = false)
    {
        if (_trade.LastRoutes is not { } last) return;
        _trade.CurrentRun = new TradeRun(last.FreeCargo, last.FreeOreHold, last.Wallet,
            type => sde.Types.TryGetValue(type, out var t) && t.IsOre);
        RefreshRun(cleared ? "New market data, so the run was cleared. Add routes again." : null);
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
        if (_trade.CurrentRun is not { } run || RoutesGrid.ItemsSource is not IEnumerable<RouteRow> rows) return;
        int added = run.AutoFill(rows.Select(r => r.Route));
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
        if (_trade.CurrentRun is not { Items.Count: > 0 } run || RouteCharBox.SelectedItem is not CharacterState c || _trade.Sde.Data is not { } sde) return;
        _app.ShowActionPlan(Widget.TradePlans.ForRun(run, c, sde, _trade.CachedStationName));
        RunMessage.Text = $"Run started in the action widget for {c.Name}.";
    }

    private void SelectRoute_Click(object sender, RoutedEventArgs e)
    {
        if (RoutesGrid.SelectedItem is not RouteRow row || RouteCharBox.SelectedItem is not CharacterState c || _trade.Sde.Data is not { } sde) return;
        _app.ShowActionPlan(Widget.TradePlans.ForRoute(row, c, sde));
        RouteStatus.Text = $"Route started in the action widget for {c.Name}.";
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
        _app.ShowActionPlan(Widget.TradePlans.ForSale(row, c, sde));
        SellStatus.Text = $"Selling {row.Item} in the action widget for {c.Name}.";
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
