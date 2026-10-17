using System.Diagnostics;
using System.IO;
using System.Windows;
using EveMultibox.Alerts;
using EveMultibox.Clients;
using EveMultibox.Config;
using EveMultibox.Logs;
using EveMultibox.Sync;
using EveMultibox.Toolbox;

namespace EveMultibox;

/// <summary>Settings: client labels and colors, favorites, alerts, UI sync, display toggles and detection.</summary>
public partial class MainWindow : Window
{
    private const string NoAccountCopy = "(don't copy account settings)";

    private readonly App _app;
    private Dictionary<long, string>? _names;
    private List<SyncRow> _syncRows = new();

    private AppConfig Config => _app.Config;

    public MainWindow(App app)
    {
        InitializeComponent();
        _app = app;
        ClientsGrid.ItemsSource = Config.Clients;
        FavoritesGrid.ItemsSource = Config.Favorites;
        RulesGrid.ItemsSource = Config.Alerts.Rules;
        RuleSourceColumn.ItemsSource = Enum.GetValues<LogSource>();
        LoadFields();
        LoadProfiles();
    }

    private void LoadFields()
    {
        ShowOverlaysBox.IsChecked = Config.ShowOverlays;
        ShowThumbnailsBox.IsChecked = Config.ShowThumbnails;
        ShowToolboxBox.IsChecked = Config.ShowToolbox;
        ThumbWidthSlider.Value = Config.ThumbnailWidth;
        ProcessNamesBox.Text = string.Join(", ", Config.Detection.ProcessNames);
        TitleRegexBox.Text = Config.Detection.TitleRegex;
        IncludeLoginBox.IsChecked = Config.Detection.IncludeLoginScreens;
        AlertsEnabledBox.IsChecked = Config.Alerts.Enabled;
        LogsFolderBox.Text = Config.Alerts.LogsFolder ?? "";
        JumpHotkeyBox.Text = Config.Alerts.JumpHotkey ?? "";
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        ClientsGrid.CommitEdit();
        FavoritesGrid.CommitEdit();
        RulesGrid.CommitEdit();

        if (!TitleParser.IsValidPattern(TitleRegexBox.Text, out var regexError))
        {
            StatusText.Text = "Title pattern: " + regexError;
            return;
        }

        Config.ShowOverlays = ShowOverlaysBox.IsChecked == true;
        Config.ShowThumbnails = ShowThumbnailsBox.IsChecked == true;
        Config.ShowToolbox = ShowToolboxBox.IsChecked == true;
        Config.ThumbnailWidth = (int)ThumbWidthSlider.Value;
        Config.Detection.ProcessNames = ProcessNamesBox.Text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToList();
        Config.Detection.TitleRegex = TitleRegexBox.Text;
        Config.Detection.IncludeLoginScreens = IncludeLoginBox.IsChecked == true;
        Config.Alerts.Enabled = AlertsEnabledBox.IsChecked == true;
        Config.Alerts.LogsFolder = string.IsNullOrWhiteSpace(LogsFolderBox.Text) ? null : LogsFolderBox.Text.Trim();
        Config.Alerts.JumpHotkey = string.IsNullOrWhiteSpace(JumpHotkeyBox.Text) ? null : JumpHotkeyBox.Text.Trim();

        var problems = _app.ApplyAndSave();

        int badFavorites = Config.Favorites.Count(f => !f.IsValid);
        if (badFavorites > 0) problems.Add($"{badFavorites} favorite(s) have keys I can't read and are disabled.");

        StatusText.Text = problems.Count == 0 ? $"Saved at {DateTime.Now:t}." : string.Join(" ", problems);
    }

    // ---- Favorites ----

    private void AddFavorite_Click(object sender, RoutedEventArgs e)
    {
        var fav = new FavoriteCommand { Name = "New button", Keys = "F1" };
        Config.Favorites.Add(fav);
        FavoritesGrid.SelectedItem = fav;
        FavoritesGrid.ScrollIntoView(fav);
    }

    private void RemoveFavorite_Click(object sender, RoutedEventArgs e)
    {
        if (FavoritesGrid.SelectedItem is FavoriteCommand fav) Config.Favorites.Remove(fav);
    }

    private void MoveUp_Click(object sender, RoutedEventArgs e) => MoveSelected(-1);

    private void MoveDown_Click(object sender, RoutedEventArgs e) => MoveSelected(1);

    private void MoveSelected(int delta)
    {
        if (FavoritesGrid.SelectedItem is not FavoriteCommand fav) return;
        FavoritesGrid.CommitEdit();
        int i = Config.Favorites.IndexOf(fav), j = i + delta;
        if (j < 0 || j >= Config.Favorites.Count) return;
        Config.Favorites.Move(i, j);
        FavoritesGrid.SelectedItem = fav;
    }

    // ---- Alerts ----

    private void AddRule_Click(object sender, RoutedEventArgs e)
    {
        var rule = new AlertRule { Name = "New rule", Source = LogSource.Game, LineType = "notify", Pattern = "" };
        Config.Alerts.Rules.Add(rule);
        RulesGrid.SelectedItem = rule;
        RulesGrid.ScrollIntoView(rule);
    }

    private void RemoveRule_Click(object sender, RoutedEventArgs e)
    {
        if (RulesGrid.SelectedItem is AlertRule rule) Config.Alerts.Rules.Remove(rule);
    }

    private void DefaultRules_Click(object sender, RoutedEventArgs e)
    {
        int added = 0;
        foreach (var rule in AlertRule.Defaults())
        {
            if (Config.Alerts.Rules.Any(r => string.Equals(r.Name, rule.Name, StringComparison.OrdinalIgnoreCase))) continue;
            Config.Alerts.Rules.Add(rule);
            added++;
        }
        StatusText.Text = added == 0 ? "All default rules are already in the list." : $"Added {added} default rule(s). Save to apply.";
    }

    // ---- UI sync ----

    private void LoadProfiles()
    {
        var folders = SettingsSync.FindProfileFolders(SettingsSync.DefaultEveRoot);
        ProfileBox.ItemsSource = folders.Select(f => new ProfileItem(f)).ToList();
        if (folders.Count > 0) ProfileBox.SelectedIndex = 0;
        else SyncGrid.ItemsSource = null;
    }

    private sealed record ProfileItem(string Path)
    {
        public override string ToString() =>
            $"{System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(Path))}\\{System.IO.Path.GetFileName(Path)}";
    }

    private void ProfileBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e) => RefreshSyncRows();

    private void RefreshSync_Click(object sender, RoutedEventArgs e)
    {
        _names = null;
        LoadProfiles();
    }

    private void RefreshSyncRows()
    {
        if (ProfileBox.SelectedItem is not ProfileItem profile) return;
        _names ??= CharacterNameIndex.Build(_app.LogsRoot);

        _syncRows = SettingsSync.ListFiles(profile.Path)
            .Select(f => new SyncRow(f, f.Kind == SettingsKind.Character
                ? _names.TryGetValue(f.Id, out var n) ? n : "Unknown character"
                : "Account"))
            .ToList();
        SyncGrid.ItemsSource = _syncRows;

        var chars = _syncRows.Where(r => r.File.Kind == SettingsKind.Character).ToList();
        CharSourceBox.ItemsSource = chars;
        CharSourceBox.SelectedItem = chars.FirstOrDefault(); // Newest first: usually the character you just set up.

        var accounts = new List<object> { NoAccountCopy };
        accounts.AddRange(_syncRows.Where(r => r.File.Kind == SettingsKind.Account));
        AcctSourceBox.ItemsSource = accounts;
        AcctSourceBox.SelectedIndex = 0;
    }

    private bool RefuseIfEveRunning()
    {
        if (!SettingsSync.AnyEveClientRunning()) return false;
        MessageBox.Show(this,
            "EVE is still running. Close every EVE client first. EVE saves its settings when it closes, which would undo the change.",
            "Close EVE first", MessageBoxButton.OK, MessageBoxImage.Warning);
        return true;
    }

    private void CopySettings_Click(object sender, RoutedEventArgs e)
    {
        SyncGrid.CommitEdit();
        var charSource = CharSourceBox.SelectedItem as SyncRow;
        var acctSource = AcctSourceBox.SelectedItem as SyncRow;

        var charTargets = charSource is null ? new List<SyncRow>()
            : _syncRows.Where(r => r.Selected && r.File.Kind == SettingsKind.Character && r != charSource).ToList();
        var acctTargets = acctSource is null ? new List<SyncRow>()
            : _syncRows.Where(r => r.Selected && r.File.Kind == SettingsKind.Account && r != acctSource).ToList();

        if (charTargets.Count + acctTargets.Count == 0)
        {
            StatusText.Text = "Tick at least one file to overwrite (other than the source).";
            return;
        }
        if (RefuseIfEveRunning()) return;

        string summary = string.Join("\n", charTargets.Concat(acctTargets).Select(r => $"  • {r.DisplayName} ({r.Id})"));
        string from = charSource is not null && charTargets.Count > 0 ? $"Characters get settings from {charSource.DisplayName}.\n" : "";
        if (acctSource is not null && acctTargets.Count > 0) from += $"Accounts get settings from account {acctSource.Id}.\n";
        if (MessageBox.Show(this, $"{from}\nThese files will be overwritten (backups are saved first):\n{summary}",
                "Copy UI settings", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
            return;

        try
        {
            var now = DateTime.Now;
            int copied = 0;
            string? backup = null;
            if (charSource is not null && charTargets.Count > 0)
            {
                var r = SettingsSync.Copy(charSource.File, charTargets.Select(t => t.File), SettingsSync.DefaultBackupRoot, now);
                copied += r.Copied;
                backup = r.BackupFolder;
            }
            if (acctSource is not null && acctTargets.Count > 0)
            {
                var r = SettingsSync.Copy(acctSource.File, acctTargets.Select(t => t.File), SettingsSync.DefaultBackupRoot, now);
                copied += r.Copied;
                backup = r.BackupFolder;
            }
            StatusText.Text = $"Copied settings to {copied} file(s). Backup: {backup}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            StatusText.Text = "Copy failed: " + ex.Message;
        }
        RefreshSyncRows();
    }

    private void RestoreBackup_Click(object sender, RoutedEventArgs e)
    {
        if (ProfileBox.SelectedItem is not ProfileItem profile) return;
        string? latest = SettingsSync.LatestBackup(SettingsSync.DefaultBackupRoot, profile.Path);
        if (latest is null)
        {
            StatusText.Text = "No backups yet for this settings folder.";
            return;
        }
        if (RefuseIfEveRunning()) return;

        var files = Directory.EnumerateFiles(latest, "core_*.dat").Select(Path.GetFileName);
        if (MessageBox.Show(this, $"Put these files back from the backup made {Path.GetFileName(latest)}?\n\n  {string.Join("\n  ", files)}",
                "Restore backup", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
            return;

        try
        {
            int n = SettingsSync.Restore(latest, profile.Path);
            StatusText.Text = $"Restored {n} file(s) from {latest}.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusText.Text = "Restore failed: " + ex.Message;
        }
        RefreshSyncRows();
    }

    private void OpenBackups_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(SettingsSync.DefaultBackupRoot);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{SettingsSync.DefaultBackupRoot}\"") { UseShellExecute = true });
    }
}
