using System.IO;
using System.Text;
using EveMultibox.Logs;

namespace EveMultibox.Tests;

public sealed class LogTests : IDisposable
{
    // Real line shapes from EVE's logs.
    private const string IncomingHit = "[ 2026.10.08 03:55:56 ] (combat) <color=0xffcc0000><b>2</b> <color=0x77ffffff><font size=10>from</font> <b><color=0xffffffff>Corpii Follower</b><font size=10><color=0x77ffffff> - Penetrates";
    private const string OutgoingHit = "[ 2026.10.08 03:56:03 ] (combat) <color=0xff00ffff><b>60</b> <color=0x77ffffff><font size=10>to</font> <b><color=0xffffffff>Corpii Follower</b><font size=10><color=0x77ffffff> - Scourge Auto-Targeting Light Missile I - Hits";
    private const string ChatLine = "﻿[ 2026.10.17 15:02:11 ] EVE System > Channel changed to Local : Jita";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "EveMultiboxTests", Guid.NewGuid().ToString("N"));

    public LogTests() => Directory.CreateDirectory(_dir);

    [Fact]
    public void ParsesGameLineAndStripsMarkup()
    {
        var line = LogLine.Parse(IncomingHit, LogSource.Game)!;
        Assert.Equal("combat", line.Type);
        Assert.Equal("2 from Corpii Follower - Penetrates", line.Text);
        Assert.Equal(new DateTime(2026, 10, 8, 3, 55, 56, DateTimeKind.Utc), line.Time);

        Assert.Equal("60 to Corpii Follower - Scourge Auto-Targeting Light Missile I - Hits", LogLine.Parse(OutgoingHit, LogSource.Game)!.Text);
    }

    [Fact]
    public void ParsesChatLineWithLeadingBom()
    {
        var line = LogLine.Parse(ChatLine, LogSource.Chat)!;
        Assert.Equal("EVE System", line.Speaker);
        Assert.Equal("Channel changed to Local : Jita", line.Text);
        Assert.Null(line.Type);
    }

    [Theory]
    [InlineData("------------------------------------------------------------")]
    [InlineData("  Listener: The Pheonix")]
    [InlineData("")]
    public void IgnoresHeaderAndBlankLines(string raw) => Assert.Null(LogLine.Parse(raw, LogSource.Game));

    [Fact]
    public void DecodesHtmlEntities() =>
        Assert.Equal("a > b & c", LogLine.Clean("a &gt; b <b>&amp;</b> c"));

    [Fact]
    public void ParsesHeaders()
    {
        var chat = LogHeader.Parse("\n  Channel ID:      local\n  Channel Name:    Local\n  Listener:        The Pheonix\n");
        Assert.Equal(("The Pheonix", "Local"), (chat.Listener, chat.ChannelName));
        var game = LogHeader.Parse("  Gamelog\n  Listener: Miner One\n  Session Started: 2026.10.17 15:02:07\n");
        Assert.Equal("Miner One", game.Listener);
        Assert.Null(game.ChannelName);
    }

    [Fact]
    public void TailerReadsOnlyCompleteNewLinesFromUtf8GameLog()
    {
        string path = Path.Combine(_dir, "20261017_150207_123456789.txt");
        File.WriteAllText(path, GameHeader("Miner One") + IncomingHit + "\n", new UTF8Encoding(false));

        var tailer = new LogTailer(path, LogSource.Game, startAtEnd: true);
        Assert.Equal("Miner One", tailer.Header.Listener);
        Assert.Empty(tailer.ReadNew()); // Existing history is skipped.

        File.AppendAllText(path, OutgoingHit + "\n[ 2026.10.08 03:56:04 ] (notify) half writ");
        var lines = tailer.ReadNew();
        Assert.Single(lines);
        Assert.StartsWith("60 to", lines[0].Text);

        File.AppendAllText(path, "ten line\n");
        Assert.Equal("half written line", Assert.Single(tailer.ReadNew()).Text);
    }

    [Fact]
    public void TailerReadsUtf16ChatLogFromStart()
    {
        string path = Path.Combine(_dir, "Intel_20261017_150207_123456789.txt");
        string header = "\r\n\r\n        ---------------------------------------------------------------\r\n\r\n" +
                        "          Channel ID:      -12345\r\n          Channel Name:    Alliance Intel\r\n" +
                        "          Listener:        Scout Alt\r\n        ---------------------------------------------------------------\r\n\r\n";
        File.WriteAllText(path, header + ChatLine + "\r\n" + "﻿[ 2026.10.17 15:03:00 ] Someone > Хорошо hostile in system\r\n", Encoding.Unicode);

        var tailer = new LogTailer(path, LogSource.Chat, startAtEnd: false);
        Assert.Equal(("Scout Alt", "Alliance Intel"), (tailer.Header.Listener, tailer.Header.ChannelName));
        var lines = tailer.ReadNew();
        Assert.Equal(2, lines.Count);
        Assert.Equal("Хорошо hostile in system", lines[1].Text);

        using (var w = new StreamWriter(path, append: true, Encoding.Unicode))
            w.Write("﻿[ 2026.10.17 15:04:00 ] Other > clear\r\n");
        Assert.Equal("clear", Assert.Single(tailer.ReadNew()).Text);
    }

    [Fact]
    public void TailerRestartsAfterTruncation()
    {
        string path = Path.Combine(_dir, "g.txt");
        File.WriteAllText(path, GameHeader("A") + IncomingHit + "\n" + OutgoingHit + "\n");
        var tailer = new LogTailer(path, LogSource.Game, startAtEnd: true);
        File.WriteAllText(path, IncomingHit + "\n");
        Assert.Single(tailer.ReadNew());
    }

    private static string GameHeader(string listener) =>
        "------------------------------------------------------------\n  Gamelog\n" +
        $"  Listener: {listener}\n  Session Started: 2026.10.17 15:02:07\n------------------------------------------------------------\n";

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }
}
