using System.Windows;
using System.Windows.Input;
using EveMultibox.Config;
using EveMultibox.Trade;

namespace EveMultibox.Shell;

/// <summary>Choices for the order and wallet notifications. Opened from the ☰ menu and the tray.</summary>
public partial class NotificationsWindow : Window
{
    public sealed class CharacterChoice
    {
        public long Id { get; init; }
        public string Name { get; init; } = "";
        public bool Watched { get; set; }
        public string NextCheck { get; init; } = "";
    }

    private readonly App _app;
    private readonly NotificationService _service;
    private readonly List<CharacterChoice> _characters;

    public NotificationsWindow(App app, TradeService trade, NotificationService service)
    {
        InitializeComponent();
        WindowLook.Apply(this);
        _app = app;
        _service = service;

        var n = app.Config.Notifications;
        EnabledBox.IsChecked = n.Enabled;
        FillsBox.IsChecked = n.OrderFills;
        EndedBox.IsChecked = n.OrderEnded;
        WalletBox.IsChecked = n.WalletChanges;
        MinWalletBox.Text = $"{n.MinWalletChange:0}";
        BalanceBox.IsChecked = n.AppendBalance;
        ShowTemplates(n.SoldTemplate, n.BoughtTemplate, n.EndedTemplate, n.WalletTemplate);
        TokensText.Text = "Use these in the messages: " + string.Join(" ", NotificationText.Tokens.Select(t => "{" + t + "}"));

        _characters = trade.Characters.Select(c => new CharacterChoice
        {
            Id = c.Id,
            Name = c.Name,
            Watched = !n.MutedCharacters.Contains(c.Id),
            NextCheck = service.NextCheckFor(c.Id) is { } next ? $"next check {next.ToLocalTime():t}" : "",
        }).ToList();
        CharacterList.ItemsSource = _characters;
        if (_characters.Count == 0) NoCharactersText.Visibility = Visibility.Visible;

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

    private void ShowTemplates(string sold, string bought, string ended, string wallet)
    {
        SoldBox.Text = sold;
        BoughtBox.Text = bought;
        EndedBox2.Text = ended;
        WalletTemplateBox.Text = wallet;
    }

    /// <summary>The settings as they are on screen, or null with the problem shown.</summary>
    private NotificationSettings? ReadForm()
    {
        if (!IskAmount.TryParse(MinWalletBox.Text, out double minWallet) || minWallet < 0)
        {
            StatusText.Text = "Wallet change must be an ISK amount, e.g. 1000000 or 1M.";
            return null;
        }
        return new NotificationSettings
        {
            Enabled = EnabledBox.IsChecked == true,
            OrderFills = FillsBox.IsChecked == true,
            OrderEnded = EndedBox.IsChecked == true,
            WalletChanges = WalletBox.IsChecked == true,
            MinWalletChange = minWallet,
            AppendBalance = BalanceBox.IsChecked == true,
            // Keep characters that aren't signed in right now muted as they were.
            MutedCharacters = _app.Config.Notifications.MutedCharacters.Where(id => _characters.All(c => c.Id != id))
                .Concat(_characters.Where(c => !c.Watched).Select(c => c.Id)).ToList(),
            SoldTemplate = OrDefault(SoldBox.Text, NotificationSettings.DefaultSold),
            BoughtTemplate = OrDefault(BoughtBox.Text, NotificationSettings.DefaultBought),
            EndedTemplate = OrDefault(EndedBox2.Text, NotificationSettings.DefaultEnded),
            WalletTemplate = OrDefault(WalletTemplateBox.Text, NotificationSettings.DefaultWallet),
        };
    }

    private static string OrDefault(string text, string fallback) => string.IsNullOrWhiteSpace(text) ? fallback : text.Trim();

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (ReadForm() is not { } settings) return;
        _app.Config.Notifications = settings;
        var problems = _app.ApplyAndSave();
        StatusText.Text = problems.Count == 0 ? $"Saved at {DateTime.Now:t}." : string.Join(" ", problems);
    }

    private void Test_Click(object sender, RoutedEventArgs e)
    {
        if (ReadForm() is not { } settings) return;
        _service.SendTest(settings);
        StatusText.Text = "Test sent. It appears in the bottom-right corner.";
    }

    private void ResetTemplates_Click(object sender, RoutedEventArgs e) =>
        ShowTemplates(NotificationSettings.DefaultSold, NotificationSettings.DefaultBought,
            NotificationSettings.DefaultEnded, NotificationSettings.DefaultWallet);

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => DragMove();
}

/// <summary>Reads ISK amounts as people type them: 1500000, 1,500,000, 1.5M, 250k, 2b.</summary>
public static class IskAmount
{
    public static bool TryParse(string? text, out double isk)
    {
        isk = 0;
        string t = (text ?? "").Trim().Replace(",", "").Replace(" ", "");
        if (t.Length == 0) return false;
        double factor = char.ToLowerInvariant(t[^1]) switch { 'k' => 1e3, 'm' => 1e6, 'b' => 1e9, _ => 1 };
        if (factor != 1) t = t[..^1];
        if (!double.TryParse(t, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double v)) return false;
        isk = v * factor;
        return true;
    }
}
