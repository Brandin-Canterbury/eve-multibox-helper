using System.Reflection;
using System.Security;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using EveMultibox.Config;
using EveMultibox.Esi;
using EveMultibox.Logs;
using Microsoft.Win32;

namespace EveMultibox.Shell;

/// <summary>App-wide settings that aren't specific to the multiboxer. Opened from the ☰ menu.</summary>
public partial class GeneralSettingsWindow : Window
{
    private readonly App _app;

    public GeneralSettingsWindow(App app)
    {
        InitializeComponent();
        _app = app;

        LogsFolderBox.Text = app.Config.EveLogsFolder ?? "";
        StartWithWindowsBox.IsChecked = StartupRegistration.IsEnabled();
        AutoHideBox.IsChecked = app.Config.AutoHideWithoutClients;

        var trade = app.Config.Trade;
        ClientIdBox.Text = trade.ClientId ?? "";
        CallbackPortBox.Text = trade.CallbackPort.ToString();
        CallbackUrlText.Text = trade.CallbackUrl;
        CallbackPortBox.TextChanged += (_, _) =>
        {
            if (int.TryParse(CallbackPortBox.Text, out int port)) CallbackUrlText.Text = $"http://localhost:{port}/callback/";
        };
        JumpRadiusBox.Text = trade.JumpRadius.ToString();
        JumpKeyBox.Text = app.Config.Widget.JumpKey ?? "";
        UndockKeyBox.Text = app.Config.Widget.UndockKey ?? "";
        ScopesText.Text = $"{EsiAuth.Scopes.Length} scopes (read-only, plus setting waypoints)";

        var version = Assembly.GetExecutingAssembly().GetName().Version;
        AboutText.Text =
            $"EVE Multibox Helper {version?.ToString(3)}\n" +
            "It never broadcasts or multiplexes input: thumbnails and hotkeys only switch windows, " +
            "one toolbox click sends one key to the client already in front, and alerts only notify.\n" +
            $"Config: {app.ConfigPath}";

        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) Close();
        };
    }

    /// <summary>Opens just above the ☰ button, right-aligned with it, kept on screen.</summary>
    public void PlaceAbove(Window anchor)
    {
        WindowStartupLocation = WindowStartupLocation.Manual;
        Loaded += (_, _) =>
        {
            var work = SystemParameters.WorkArea;
            Left = Math.Max(work.Left, anchor.Left + anchor.ActualWidth - ActualWidth);
            Top = Math.Max(work.Top, anchor.Top - ActualHeight - 8);
        };
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        _app.Config.EveLogsFolder = string.IsNullOrWhiteSpace(LogsFolderBox.Text) ? null : LogsFolderBox.Text.Trim();
        _app.Config.AutoHideWithoutClients = AutoHideBox.IsChecked == true;

        var problems = new List<string>();
        var trade = _app.Config.Trade;
        trade.ClientId = string.IsNullOrWhiteSpace(ClientIdBox.Text) ? null : ClientIdBox.Text.Trim();
        if (int.TryParse(CallbackPortBox.Text, out int port) && port is > 1024 and < 65536) trade.CallbackPort = port;
        else problems.Add("Callback port must be a number between 1025 and 65535.");
        if (int.TryParse(JumpRadiusBox.Text, out int radius) && radius is >= 1 and <= 40) trade.JumpRadius = radius;
        else problems.Add("Route search radius must be 1–40 jumps.");

        foreach (var (box, apply, label) in new (TextBox, Action<string?>, string)[]
                 {
                     (JumpKeyBox, k => _app.Config.Widget.JumpKey = k, "Jump / Dock"),
                     (UndockKeyBox, k => _app.Config.Widget.UndockKey = k, "Undock"),
                 })
        {
            string text = box.Text.Trim();
            if (text.Length == 0) apply(null);
            else if (Toolbox.KeyChord.TryParse(text, out var chord, out var error)) apply(chord!.ToString());
            else problems.Add($"{label} key: {error}");
        }
        try
        {
            StartupRegistration.Set(StartWithWindowsBox.IsChecked == true);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException)
        {
            problems.Add("Couldn't change the Windows startup setting: " + ex.Message);
        }

        problems.AddRange(_app.ApplyAndSave());
        StatusText.Text = problems.Count == 0 ? $"Saved at {DateTime.Now:t}." : string.Join(" ", problems);
    }

    private void CopyCallback_Click(object sender, RoutedEventArgs e)
    {
        Clipboard.SetText(CallbackUrlText.Text);
        StatusText.Text = "Callback URL copied.";
    }

    private void CopyScopes_Click(object sender, RoutedEventArgs e)
    {
        Clipboard.SetText(string.Join(" ", EsiAuth.Scopes));
        StatusText.Text = "Scopes copied. Tick the same ones on your EVE app.";
    }

    private void OpenDevSite_Click(object sender, RoutedEventArgs e) =>
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("https://developers.eveonline.com/applications") { UseShellExecute = true });

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Choose your EVE logs folder",
            InitialDirectory = string.IsNullOrWhiteSpace(LogsFolderBox.Text) ? LogFiles.DefaultLogsRoot : LogsFolderBox.Text,
        };
        if (dialog.ShowDialog(this) == true) LogsFolderBox.Text = dialog.FolderName;
    }

    private void ResetMenuButton_Click(object sender, RoutedEventArgs e)
    {
        _app.ResetMenuButton();
        StatusText.Text = "Menu button moved back to the bottom-right.";
    }

    private void Quit_Click(object sender, RoutedEventArgs e) => _app.Quit();

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => DragMove();
}
