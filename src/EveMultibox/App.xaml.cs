using System.Windows;
using EveMultibox.Alerts;
using EveMultibox.Clients;
using EveMultibox.Config;
using EveMultibox.Interop;
using EveMultibox.Logs;
using EveMultibox.Overlay;
using EveMultibox.Switcher;
using EveMultibox.Toolbox;

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
    private readonly AlertEngine _alertEngine = new();

    public AppConfig Config { get; private set; } = null!;
    public AlertCenter Alerts { get; } = new();

    public string LogsRoot => string.IsNullOrWhiteSpace(Config.Alerts.LogsFolder) ? LogFiles.DefaultLogsRoot : Config.Alerts.LogsFolder!;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // --config <path> uses a different settings file (handy for testing against Notepad windows).
        int configArg = Array.IndexOf(e.Args, "--config");
        string configPath = configArg >= 0 && configArg + 1 < e.Args.Length ? e.Args[configArg + 1] : ConfigStore.DefaultPath;
        _store = new ConfigStore(configPath);
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

        var main = new MainWindow(this);
        MainWindow = main;
        main.Show();

        _tracker.Start();
        Apply();
    }

    private void OnLogLine(LogEvent e)
    {
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

        _strip.Rebuild();
        if (Config.ShowThumbnails) _strip.Show(); else _strip.Hide();

        _toolbox.Refresh();
        if (Config.ShowToolbox) _toolbox.Show(); else _toolbox.Hide();

        _overlays.Restyle();
        _strip.Restyle();

        if (Config.Alerts.Enabled)
        {
            _logs.Start(LogsRoot);
            if (!System.IO.Directory.Exists(LogsRoot)) problems.Add($"EVE logs folder not found: {LogsRoot}");
        }
        else
        {
            _logs.Stop();
            Alerts.Clear();
        }
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
        try
        {
            _store?.Save(Config);
        }
        catch (Exception)
        {
            // Never block exit on a failed save.
        }
        _logs?.Dispose();
        _hotkeys?.Dispose();
        _overlays?.Dispose();
        _tracker?.Dispose();
        base.OnExit(e);
    }
}
