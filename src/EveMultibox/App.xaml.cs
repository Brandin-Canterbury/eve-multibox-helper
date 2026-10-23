using System.Windows;
using EveMultibox.Alerts;
using EveMultibox.Clients;
using EveMultibox.Config;
using EveMultibox.Interop;
using EveMultibox.Logs;
using EveMultibox.Overlay;
using EveMultibox.Shell;
using EveMultibox.Switcher;
using EveMultibox.Esi;
using EveMultibox.StaticData;
using EveMultibox.Toolbox;
using EveMultibox.Trade;

namespace EveMultibox;

public partial class App : Application
{
    private ConfigStore _store = null!;
    private ClientTracker _tracker = null!;
    private OverlayManager _overlays = null!;
    private ThumbnailStrip _strip = null!;
    private ToolboxWindow _toolbox = null!;
    private HotkeyManager _hotkeys = null!;
    private LogWatcher _logs = null!;
    private MenuButtonWindow _menuButton = null!;
    private GeneralSettingsWindow? _generalSettings;
    private TradeService _trade = null!;
    private TradeWindow? _tradeWindow;
    private readonly AlertEngine _alertEngine = new();

    public AppConfig Config { get; private set; } = null!;
    public AlertCenter Alerts { get; } = new();
    public string ConfigPath => _store.Path;

    /// <summary>Set by <see cref="Quit"/>, so windows that normally just hide on close let the app exit.</summary>
    public bool IsQuitting { get; private set; }

    public string LogsRoot => string.IsNullOrWhiteSpace(Config.EveLogsFolder) ? LogFiles.DefaultLogsRoot : Config.EveLogsFolder!;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Safety net: log unexpected errors and keep running instead of vanishing.
        DispatcherUnhandledException += (_, args) =>
        {
            CrashLog.Write(args.Exception, "UI");
            args.Handled = true;
            ShowErrorOnce(args.Exception);
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            CrashLog.Write(args.Exception, "background task");
            args.SetObserved();
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex) CrashLog.Write(ex, "fatal");
        };

        // Started by the updater: let the previous copy finish exiting first, then tidy up after it.
        Updater.WaitForPreviousInstance(e.Args);
        if (Updater.IsReleaseBuild) Updater.CleanUpOldFiles();

        // --config <path> uses a different settings file (handy for testing against Notepad windows).
        int configArg = Array.IndexOf(e.Args, "--config");
        string configPath = configArg >= 0 && configArg + 1 < e.Args.Length ? e.Args[configArg + 1] : ConfigStore.DefaultPath;
        _store = new ConfigStore(configPath);
        bool firstRun = !System.IO.File.Exists(configPath);
        Config = _store.Load();

        _tracker = new ClientTracker(Config.Detection);
        _overlays = new OverlayManager(_tracker, Config);
        _strip = new ThumbnailStrip(_tracker, Config, Alerts);
        _toolbox = new ToolboxWindow(_tracker, Config, new KeySender(new Win32InputBackend()));
        _hotkeys = new HotkeyManager();

        _logs = new LogWatcher(LogsRoot);
        _logs.LineAdded += OnLogLine;
        // Switching to a client counts as having seen its alert.
        _tracker.ActiveClientChanged += () => Alerts.Acknowledge(_tracker.ActiveClient?.CharacterName);

        // The ☰ button is the on-screen home; the tray icon is always there, even when everything is auto-hidden.
        _menuButton = new MenuButtonWindow(this);
        _tray = new TrayIcon(this);
        _tracker.ClientsChanged += UpdateVisibility;
        _strip.SettingsClosed += UpdateVisibility;

        var esi = new EsiClient(cacheDir: EsiClient.DefaultCacheDir);
        _trade = new TradeService(esi, new EsiAuth(new TokenStore(TokenStore.DefaultDir), () => Config.Trade), new SdeStore(SdeStore.DefaultDir));

        _tracker.Start();
        Apply();
        if (firstRun) OpenMultiboxerSettings();
        _ = _trade.StartAsync();
        // --open-trade opens the Trade window at start-up (handy right after restarting the helper).
        if (e.Args.Contains("--open-trade")) ShowTrade();

        if (e.Args.Contains("--updated")) _tray.Notify($"Updated to version {Updater.CurrentVersion}.");
        StartUpdateChecks();
    }

    // ---- updates ----

    private static readonly TimeSpan FirstUpdateCheck = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan UpdateCheckEvery = TimeSpan.FromHours(6);
    private System.Windows.Threading.DispatcherTimer? _updateTimer;
    private Shell.UpdateWindow? _updateWindow;

    /// <summary>The newer release found by the last check, if any (shown in the ☰ menu and tray).</summary>
    public UpdateInfo? PendingUpdate { get; private set; }

    private void StartUpdateChecks()
    {
        if (!Updater.IsReleaseBuild) return; // Development copies never replace themselves.
        _updateTimer = new System.Windows.Threading.DispatcherTimer { Interval = FirstUpdateCheck };
        _updateTimer.Tick += async (_, _) =>
        {
            _updateTimer.Interval = UpdateCheckEvery;
            if (Config.Updates.AutoCheck) await CheckForUpdatesAsync(manual: false);
        };
        _updateTimer.Start();
    }

    /// <summary>Asks GitHub for a newer release. Manual checks always report back; automatic ones stay quiet unless there's an update.</summary>
    public async Task CheckForUpdatesAsync(bool manual)
    {
        if (!Updater.IsReleaseBuild)
        {
            if (manual)
                MessageBox.Show($"This is a development build ({Updater.CurrentVersion}), so it doesn't update itself.\n\nReleases: {Updater.ReleasesPage}",
                    "EVE Multibox Helper", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            var update = await new Updater().CheckAsync(Updater.CurrentVersion);
            PendingUpdate = update;
            if (update is null)
            {
                if (manual) MessageBox.Show($"You're on the latest version ({Updater.CurrentVersion}).", "EVE Multibox Helper",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            if (!manual && update.Version.ToString() == Config.Updates.SkippedVersion) return;

            _tray?.Notify($"Version {update.Version} is available. Click to update.", ShowUpdateWindow);
            if (manual) ShowUpdateWindow();
        }
        catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            if (manual) MessageBox.Show("Couldn't check for updates: " + ex.Message, "EVE Multibox Helper",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    public void ShowUpdateWindow()
    {
        if (PendingUpdate is not { } update) return;
        if (_updateWindow is not null)
        {
            _updateWindow.Activate();
            return;
        }
        _updateWindow = new Shell.UpdateWindow(this, update);
        _updateWindow.Closed += (_, _) => _updateWindow = null;
        _updateWindow.Show();
        _updateWindow.Activate();
    }

    private TrayIcon? _tray;
    private bool _hadClients;

    /// <summary>
    /// Decides which on-screen pieces show. With auto-hide on and no EVE client running, the ☰ button,
    /// clients strip and toolbox hide (the tray icon stays). The strip also stays while its settings are open.
    /// </summary>
    public void UpdateVisibility()
    {
        int clients = _tracker.Clients.Count;
        bool show = clients > 0 || !Config.AutoHideWithoutClients;

        if (show) _menuButton.Show(); else _menuButton.Hide();

        if ((show && Config.ShowMultiboxer) || _strip.SettingsOpen)
        {
            if (!_strip.IsVisible)
            {
                _strip.Show();
                _strip.Rebuild();
            }
        }
        else
        {
            _strip.Hide();
        }

        if (show && Config.ShowToolbox) _toolbox.Show(); else _toolbox.Hide();
        _strip.SetToolboxIndicator(Config.ShowToolbox);

        _tray?.SetStatus(clients switch
        {
            0 => "EVE Multibox Helper · no EVE clients",
            1 => "EVE Multibox Helper · 1 client",
            _ => $"EVE Multibox Helper · {clients} clients",
        });
        // The first time a client appears, grab the real EVE icon for the tray.
        if (clients > 0 && !_hadClients) _tray?.RefreshIcon();
        _hadClients = clients > 0;
    }

    /// <summary>Opens the multiboxer settings inside the clients strip, showing the strip if needed.</summary>
    public void OpenMultiboxerSettings()
    {
        if (!_strip.IsVisible) _strip.Show();
        _strip.OpenSettings();
    }

    private DateTime _lastErrorShown;

    /// <summary>Tells the user something went wrong, at most once a minute so a repeating error can't spam dialogs.</summary>
    private void ShowErrorOnce(Exception ex)
    {
        if (DateTime.UtcNow - _lastErrorShown < TimeSpan.FromMinutes(1)) return;
        _lastErrorShown = DateTime.UtcNow;
        MessageBox.Show($"Something went wrong, but the helper is still running.\n\n{ex.Message}\n\nDetails were saved to:\n{CrashLog.PathName}",
            "EVE Multibox Helper", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private Widget.ActionWidgetWindow? _widget;

    /// <summary>Shows the action widget with a new plan, replacing any plan in progress.</summary>
    public void ShowActionPlan(Widget.ActionPlan plan)
    {
        _widget?.Close();
        _widget = new Widget.ActionWidgetWindow(this, plan, _trade, _tracker);
        _widget.Closed += (_, _) => _widget = null;
        _widget.Show();
    }

    public void ShowTrade()
    {
        if (_tradeWindow is not null)
        {
            _tradeWindow.Activate();
            return;
        }
        _tradeWindow = new TradeWindow(this, _trade);
        _tradeWindow.Closed += (_, _) => _tradeWindow = null;
        _tradeWindow.Show();
        _tradeWindow.Activate();
    }

    /// <summary>Shows or hides the clients strip. Borders, name tags and the toolbox are unaffected.</summary>
    public void SetMultiboxerVisible(bool visible)
    {
        Config.ShowMultiboxer = visible;
        if (!visible) _strip.CloseSettings();
        UpdateVisibility();
        SaveQuietly();
    }

    public void ToggleToolbox()
    {
        Config.ShowToolbox = !Config.ShowToolbox;
        UpdateVisibility();
        _strip.SyncSettings();
        SaveQuietly();
    }

    public void ShowGeneralSettings()
    {
        if (_generalSettings is not null)
        {
            _generalSettings.Activate();
            return;
        }
        _generalSettings = new GeneralSettingsWindow(this);
        _generalSettings.PlaceAbove(_menuButton);
        _generalSettings.Closed += (_, _) => _generalSettings = null;
        _generalSettings.Show();
        _generalSettings.Activate();
    }

    public void ResetMenuButton()
    {
        _menuButton.ResetPosition();
        SaveQuietly();
    }

    public void Quit()
    {
        IsQuitting = true;
        Shutdown();
    }

    /// <summary>Saves without bothering the user if it fails; the next save or exit tries again.</summary>
    public void SaveQuietly()
    {
        try
        {
            _store.Save(Config);
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
        }
    }

    private void OnLogLine(LogEvent e)
    {
        _trade?.OnLogLine(e);
        if (!Config.Alerts.Enabled) return;
        var alert = _alertEngine.Evaluate(Config.Alerts.Rules, e);
        if (alert is not null) Alerts.Raise(alert, _tracker.ActiveClient?.CharacterName);
    }

    /// <summary>Focus only: brings the client with the newest unseen alert to the front.</summary>
    private void JumpToAlert()
    {
        var latest = Alerts.Latest;
        if (latest is null) return;
        var client = _tracker.FindByCharacter(latest.Character);
        if (client is not null) Win32.FocusWindow(client.Hwnd);
        else Alerts.Acknowledge(latest.Character); // That character isn't running any more.
    }

    /// <summary>Applies the current settings everywhere and saves them. Returns any problems to show the user.</summary>
    public List<string> ApplyAndSave()
    {
        var problems = Apply();
        _store.Save(Config);
        return problems;
    }

    private List<string> Apply()
    {
        var problems = new List<string>();

        _tracker.ApplyDetection(Config.Detection);
        _overlays.SetEnabled(Config.ShowOverlays);

        UpdateVisibility();
        _strip.Rebuild();
        _toolbox.Refresh();

        _overlays.Restyle();
        _strip.Restyle();

        // The logs feed both alerts and trade's quick location hints.
        bool wantLogs = Config.Alerts.Enabled || !string.IsNullOrWhiteSpace(Config.Trade.ClientId);
        if (wantLogs)
        {
            _logs.Start(LogsRoot);
            if (!System.IO.Directory.Exists(LogsRoot)) problems.Add($"EVE logs folder not found: {LogsRoot}");
        }
        else
        {
            _logs.Stop();
        }
        if (!Config.Alerts.Enabled) Alerts.Clear();
        _tradeWindow?.RefreshSetupState();
        int badRules = Config.Alerts.Rules.Count(r => r.Enabled && !r.IsValid);
        if (badRules > 0) problems.Add($"{badRules} alert rule(s) have an invalid pattern and won't fire.");

        _hotkeys.UnregisterAll();
        foreach (var profile in Config.Clients.Where(p => !string.IsNullOrWhiteSpace(p.FocusHotkey)))
        {
            string name = profile.CharacterName;
            // Focus only: this never sends any input to the game.
            RegisterHotkey(profile.FocusHotkey!, $"for {name}", () =>
            {
                var client = _tracker.FindByCharacter(name);
                if (client is not null) Win32.FocusWindow(client.Hwnd);
            }, problems);
        }
        if (Config.Alerts.Enabled && !string.IsNullOrWhiteSpace(Config.Alerts.JumpHotkey))
            RegisterHotkey(Config.Alerts.JumpHotkey!, "jump to alert", JumpToAlert, problems);

        return problems;
    }

    private void RegisterHotkey(string text, string purpose, Action action, List<string> problems)
    {
        if (!KeyChord.TryParse(text, out var chord, out var error))
        {
            problems.Add($"Hotkey {purpose}: {error}");
            return;
        }
        if (!_hotkeys.Register(chord!, action)) problems.Add($"{chord} ({purpose}) is already used by another program.");
    }

    protected override void OnExit(ExitEventArgs e)
    {
        IsQuitting = true;
        try
        {
            _store?.Save(Config);
        }
        catch (Exception)
        {
            // Never block exit on a failed save.
        }
        _trade?.Stop();
        _logs?.Dispose();
        _tray?.Dispose();
        _hotkeys?.Dispose();
        _overlays?.Dispose();
        _tracker?.Dispose();
        base.OnExit(e);
    }
}
