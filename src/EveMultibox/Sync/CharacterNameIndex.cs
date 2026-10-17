using System.IO;
using System.Text.RegularExpressions;
using EveMultibox.Logs;

namespace EveMultibox.Sync;

/// <summary>
/// Maps character IDs to names using EVE's own log files: their names end in _&lt;characterId&gt;.txt
/// and their header has a "Listener: Name" line. Works offline, with no web lookups.
/// </summary>
public static class CharacterNameIndex
{
    // <date>_<time>_<characterId>.txt, optionally prefixed by the channel name. Logs written before
    // login are just <date>_<time>.txt, which must not be mistaken for an ID.
    private static readonly Regex IdInName = new(@"(?:^|_)\d{8}_\d{6}_(?<id>\d+)\.txt$", RegexOptions.IgnoreCase);

    public static Dictionary<long, string> Build(string logsRoot)
    {
        var map = new Dictionary<long, string>();
        foreach (string sub in new[] { "Gamelogs", "Chatlogs" })
        {
            string dir = Path.Combine(logsRoot, sub);
            if (!Directory.Exists(dir)) continue;

            // Newest first, so a renamed character gets its current name.
            foreach (var file in new DirectoryInfo(dir).EnumerateFiles("*.txt").OrderByDescending(f => f.LastWriteTimeUtc))
            {
                var m = IdInName.Match(file.Name);
                if (!m.Success || !long.TryParse(m.Groups["id"].Value, out long id) || map.ContainsKey(id)) continue;
                var header = LogHeader.Read(file.FullName);
                if (!string.IsNullOrWhiteSpace(header?.Listener)) map[id] = header.Listener!;
            }
        }
        return map;
    }
}
