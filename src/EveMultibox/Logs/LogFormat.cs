using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace EveMultibox.Logs;

public enum LogSource
{
    Game,
    Chat,
}

/// <summary>The header block at the top of every EVE log file.</summary>
public sealed record LogHeader(string? Listener, string? ChannelName)
{
    private static readonly Regex Field = new(@"^\s*(?<key>Listener|Channel Name):\s*(?<value>.+?)\s*$", RegexOptions.Multiline);

    public static LogHeader Parse(string text)
    {
        string? listener = null, channel = null;
        foreach (Match m in Field.Matches(text))
        {
            if (m.Groups["key"].Value == "Listener") listener ??= m.Groups["value"].Value;
            else channel ??= m.Groups["value"].Value;
        }
        return new LogHeader(listener, channel);
    }

    /// <summary>Reads just the header of a log file, even while EVE has it open.</summary>
    public static LogHeader? Read(string path)
    {
        try
        {
            using var fs = LogFiles.OpenShared(path);
            var buffer = new byte[2048];
            int n = fs.Read(buffer, 0, buffer.Length);
            return Parse(LogFiles.DetectEncoding(buffer, n).GetString(buffer, 0, n));
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }
}

/// <summary>One parsed log line. For game logs <see cref="Type"/> is e.g. "combat"; for chat it is null and <see cref="Speaker"/> is set.</summary>
public sealed record LogLine(DateTime Time, string? Type, string? Speaker, string Text)
{
    private static readonly Regex Stamp = new(@"^\uFEFF?\[ (?<t>\d{4}\.\d\d\.\d\d \d\d:\d\d:\d\d) \] (?<rest>.*)$");
    private static readonly Regex GameRest = new(@"^\((?<type>\w+)\) (?<text>.*)$");
    private static readonly Regex ChatRest = new(@"^(?<speaker>.+?) > (?<text>.*)$");
    private static readonly Regex Tags = new(@"<[^>]*>");

    public static LogLine? Parse(string line, LogSource source)
    {
        var m = Stamp.Match(line.TrimEnd('\r'));
        if (!m.Success) return null;
        if (!DateTime.TryParseExact(m.Groups["t"].Value, "yyyy.MM.dd HH:mm:ss", null,
                System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal, out var time))
            return null;

        string rest = m.Groups["rest"].Value;
        if (source == LogSource.Game)
        {
            var g = GameRest.Match(rest);
            return g.Success
                ? new LogLine(time, g.Groups["type"].Value, null, Clean(g.Groups["text"].Value))
                : new LogLine(time, null, null, Clean(rest));
        }

        var c = ChatRest.Match(rest);
        return c.Success ? new LogLine(time, null, c.Groups["speaker"].Value.Trim(), Clean(c.Groups["text"].Value)) : null;
    }

    /// <summary>Removes EVE's markup (&lt;color&gt;, &lt;b&gt;, links) and collapses whitespace.</summary>
    public static string Clean(string text)
    {
        string s = WebUtility.HtmlDecode(Tags.Replace(text, " "));
        return Regex.Replace(s, @"\s+", " ").Trim();
    }
}

public static class LogFiles
{
    public static string DefaultLogsRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "EVE", "logs");

    public static FileStream OpenShared(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

    /// <summary>Chat logs are UTF-16 LE; game logs are UTF-8.</summary>
    public static Encoding DetectEncoding(byte[] head, int count)
    {
        if (count >= 2 && head[0] == 0xFF && head[1] == 0xFE) return Encoding.Unicode;
        if (count >= 2 && head[1] == 0) return Encoding.Unicode;
        return new UTF8Encoding(false);
    }
}
