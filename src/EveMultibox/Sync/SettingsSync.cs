using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;

namespace EveMultibox.Sync;

public enum SettingsKind
{
    Character,
    Account,
}

/// <summary>One EVE settings file: core_char_&lt;id&gt;.dat (per character) or core_user_&lt;id&gt;.dat (per account).</summary>
public sealed record SettingsFile(string Path, SettingsKind Kind, long Id, DateTime LastWrite)
{
    public string FileName => System.IO.Path.GetFileName(Path);
}

public sealed record SyncResult(int Copied, string BackupFolder);

/// <summary>
/// Copies EVE's UI settings files from one character (or account) to others, so every client gets
/// the same window layout and overview. Always backs up every file it is about to overwrite.
/// </summary>
public static class SettingsSync
{
    private static readonly Regex FileRegex = new(@"^core_(?<kind>char|user)_(?<id>\d+)\.dat$", RegexOptions.IgnoreCase);

    public static string DefaultEveRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CCP", "EVE");

    public static string DefaultBackupRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "EveMultibox", "settings-backups");

    /// <summary>All settings_* folders under the EVE root, live server (Tranquility) first.</summary>
    public static List<string> FindProfileFolders(string eveRoot)
    {
        if (!Directory.Exists(eveRoot)) return new();
        return Directory.EnumerateDirectories(eveRoot)
            .SelectMany(server => Directory.EnumerateDirectories(server, "settings_*"))
            .OrderByDescending(p => p.Contains("tranquility", StringComparison.OrdinalIgnoreCase))
            .ThenBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Lists real character/account settings files. Placeholder files such as core_char__.dat are skipped.</summary>
    public static List<SettingsFile> ListFiles(string profileFolder)
    {
        if (!Directory.Exists(profileFolder)) return new();
        var result = new List<SettingsFile>();
        foreach (var path in Directory.EnumerateFiles(profileFolder, "core_*.dat"))
        {
            var m = FileRegex.Match(Path.GetFileName(path));
            if (!m.Success || !long.TryParse(m.Groups["id"].Value, out long id)) continue;
            var kind = m.Groups["kind"].Value.Equals("char", StringComparison.OrdinalIgnoreCase) ? SettingsKind.Character : SettingsKind.Account;
            result.Add(new SettingsFile(path, kind, id, File.GetLastWriteTime(path)));
        }
        return result.OrderBy(f => f.Kind).ThenByDescending(f => f.LastWrite).ToList();
    }

    /// <summary>
    /// EVE writes its settings when a client closes, so copying while clients run would be undone.
    /// </summary>
    public static bool AnyEveClientRunning() => Process.GetProcessesByName("exefile").Length > 0;

    /// <summary>Copies <paramref name="source"/> over each target of the same kind, after backing up all targets.</summary>
    public static SyncResult Copy(SettingsFile source, IEnumerable<SettingsFile> targets, string backupRoot, DateTime now)
    {
        var list = targets.Where(t => !string.Equals(t.Path, source.Path, StringComparison.OrdinalIgnoreCase)).ToList();
        if (list.Any(t => t.Kind != source.Kind))
            throw new InvalidOperationException("Character settings can only be copied to characters, and account settings to accounts.");
        if (!File.Exists(source.Path)) throw new FileNotFoundException("Source settings file is gone.", source.Path);

        string profileName = Path.GetFileName(Path.GetDirectoryName(source.Path)!);
        string backupFolder = Path.Combine(backupRoot, profileName, now.ToString("yyyyMMdd_HHmmss"));
        Directory.CreateDirectory(backupFolder);

        // Back up everything first. If any backup fails, nothing has been overwritten yet.
        foreach (var t in list) File.Copy(t.Path, Path.Combine(backupFolder, t.FileName), overwrite: true);
        File.WriteAllText(Path.Combine(backupFolder, "restore-to.txt"), Path.GetDirectoryName(source.Path));

        foreach (var t in list) File.Copy(source.Path, t.Path, overwrite: true);
        return new SyncResult(list.Count, backupFolder);
    }

    /// <summary>Newest backup folder for a profile, or null.</summary>
    public static string? LatestBackup(string backupRoot, string profileFolder)
    {
        string dir = Path.Combine(backupRoot, Path.GetFileName(profileFolder));
        if (!Directory.Exists(dir)) return null;
        return Directory.EnumerateDirectories(dir).OrderByDescending(d => Path.GetFileName(d), StringComparer.Ordinal).FirstOrDefault();
    }

    /// <summary>Puts every .dat file from a backup folder back into the profile folder.</summary>
    public static int Restore(string backupFolder, string profileFolder)
    {
        int n = 0;
        foreach (var file in Directory.EnumerateFiles(backupFolder, "core_*.dat"))
        {
            File.Copy(file, Path.Combine(profileFolder, Path.GetFileName(file)), overwrite: true);
            n++;
        }
        return n;
    }
}
