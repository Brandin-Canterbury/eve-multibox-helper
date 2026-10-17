using System.IO;
using System.Text;
using EveMultibox.Config;
using EveMultibox.Logs;
using EveMultibox.Sync;

namespace EveMultibox.Tests;

public sealed class SettingsSyncTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "EveMultiboxTests", Guid.NewGuid().ToString("N"));
    private string Profile => Path.Combine(_root, "eve", "e_eve_tq_tranquility", "settings_Default");
    private string Backups => Path.Combine(_root, "backups");

    public SettingsSyncTests()
    {
        Directory.CreateDirectory(Profile);
        Write("core_char_111.dat", "MAIN LAYOUT");
        Write("core_char_222.dat", "alt one");
        Write("core_char_333.dat", "alt two");
        Write("core_user_9001.dat", "account a");
        Write("core_user_9002.dat", "account b");
        Write("core_char__.dat", "placeholder");
        Write("core_char_('char', None, 'dat').dat", "junk");
    }

    private void Write(string name, string content) => File.WriteAllText(Path.Combine(Profile, name), content);
    private string Read(string name) => File.ReadAllText(Path.Combine(Profile, name));
    private SettingsFile Get(long id) => SettingsSync.ListFiles(Profile).Single(f => f.Id == id);

    [Fact]
    public void FindsProfilesAndRealFilesOnly()
    {
        Directory.CreateDirectory(Path.Combine(_root, "eve", "a_other_server", "settings_Custom"));
        var profiles = SettingsSync.FindProfileFolders(Path.Combine(_root, "eve"));
        Assert.Equal(Profile, profiles[0]); // Tranquility first.
        Assert.Equal(2, profiles.Count);

        var files = SettingsSync.ListFiles(Profile);
        Assert.Equal(new long[] { 111, 222, 333 }, files.Where(f => f.Kind == SettingsKind.Character).Select(f => f.Id).Order());
        Assert.Equal(new long[] { 9001, 9002 }, files.Where(f => f.Kind == SettingsKind.Account).Select(f => f.Id).Order());
    }

    [Fact]
    public void CopyBacksUpTargetsThenOverwrites()
    {
        var now = new DateTime(2026, 10, 17, 12, 30, 0);
        var result = SettingsSync.Copy(Get(111), new[] { Get(222), Get(333), Get(111) }, Backups, now);

        Assert.Equal(2, result.Copied); // The source itself is skipped.
        Assert.Equal("MAIN LAYOUT", Read("core_char_222.dat"));
        Assert.Equal("MAIN LAYOUT", Read("core_char_333.dat"));
        Assert.Equal("account a", Read("core_user_9001.dat"));

        Assert.Equal(Path.Combine(Backups, "settings_Default", "20261017_123000"), result.BackupFolder);
        Assert.Equal("alt one", File.ReadAllText(Path.Combine(result.BackupFolder, "core_char_222.dat")));
        Assert.Equal("alt two", File.ReadAllText(Path.Combine(result.BackupFolder, "core_char_333.dat")));
        Assert.False(File.Exists(Path.Combine(result.BackupFolder, "core_char_111.dat")));
    }

    [Fact]
    public void RefusesToMixCharacterAndAccountFiles()
    {
        Assert.Throws<InvalidOperationException>(() =>
            SettingsSync.Copy(Get(111), new[] { Get(9001) }, Backups, DateTime.Now));
        Assert.Equal("account a", Read("core_user_9001.dat"));
    }

    [Fact]
    public void RestoreLatestBackupUndoesCopy()
    {
        SettingsSync.Copy(Get(111), new[] { Get(222) }, Backups, new DateTime(2026, 1, 1, 10, 0, 0));
        Write("core_char_222.dat", "changed again");
        SettingsSync.Copy(Get(111), new[] { Get(222) }, Backups, new DateTime(2026, 1, 1, 11, 0, 0));

        string latest = SettingsSync.LatestBackup(Backups, Profile)!;
        Assert.EndsWith("20260101_110000", latest);
        Assert.Equal(1, SettingsSync.Restore(latest, Profile));
        Assert.Equal("changed again", Read("core_char_222.dat"));
    }

    [Fact]
    public void NoBackupsYieldsNull() => Assert.Null(SettingsSync.LatestBackup(Backups, Profile));

    [Fact]
    public void NameIndexComesFromLogFiles()
    {
        string logs = Path.Combine(_root, "logs");
        Directory.CreateDirectory(Path.Combine(logs, "Gamelogs"));
        Directory.CreateDirectory(Path.Combine(logs, "Chatlogs"));
        File.WriteAllText(Path.Combine(logs, "Gamelogs", "20261017_150207_222.txt"), "x");  // ID too short to trust
        File.WriteAllText(Path.Combine(logs, "Gamelogs", "20261017_150207_1621441463.txt"),
            "------\n  Gamelog\n  Listener: The Pheonix\n  Session Started: 2026.10.17 15:02:07\n");
        File.WriteAllText(Path.Combine(logs, "Chatlogs", "Local_20261017_150207_95000001.txt"),
            "\r\n  Channel Name:    Local\r\n  Listener:        Miner One\r\n", Encoding.Unicode);
        File.WriteAllText(Path.Combine(logs, "Gamelogs", "20261017_152914.txt"), "  Listener: Nobody\n");

        var map = CharacterNameIndex.Build(logs);
        Assert.Equal("The Pheonix", map[1621441463]);
        Assert.Equal("Miner One", map[95000001]);
        Assert.Equal(2, map.Count);
    }

    [Fact]
    public void AlertRulesSurviveConfigRoundTrip()
    {
        var store = new ConfigStore(Path.Combine(_root, "config.json"));
        var config = AppConfig.CreateDefault();
        config.Alerts.Rules[0].Enabled = false;
        config.Alerts.Rules.Add(new EveMultibox.Alerts.AlertRule { Name = "Intel", Source = LogSource.Chat, Channel = "Intel", Pattern = "Jita" });
        store.Save(config);

        Assert.Contains("\"Chat\"", File.ReadAllText(store.Path)); // Enums are stored by name.
        var loaded = store.Load();
        Assert.Equal(config.Alerts.Rules.Count, loaded.Alerts.Rules.Count);
        Assert.False(loaded.Alerts.Rules[0].Enabled);
        var intel = loaded.Alerts.Rules.Last();
        Assert.Equal((LogSource.Chat, "Intel", "Jita"), (intel.Source, intel.Channel, intel.Pattern));
    }

    [Fact]
    public void OldConfigWithoutAlertsGetsDefaultRules()
    {
        string path = Path.Combine(_root, "old.json");
        File.WriteAllText(path, "{ \"favorites\": [] }");
        var loaded = new ConfigStore(path).Load();
        Assert.Equal(AlertRule_DefaultCount, loaded.Alerts.Rules.Count);
        Assert.True(loaded.Alerts.Enabled);
    }

    private static int AlertRule_DefaultCount => EveMultibox.Alerts.AlertRule.Defaults().Count();

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
