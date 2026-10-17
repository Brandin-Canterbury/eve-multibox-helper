using EveMultibox.Alerts;
using EveMultibox.Logs;

namespace EveMultibox.Tests;

public class AlertTests
{
    private static readonly DateTime T0 = new(2026, 10, 17, 12, 0, 0, DateTimeKind.Utc);

    private static LogEvent Game(string type, string text, string listener = "Miner One") =>
        new(LogSource.Game, listener, null, new LogLine(T0, type, null, text));

    private static LogEvent Chat(string channel, string speaker, string text, string listener = "Scout") =>
        new(LogSource.Chat, listener, channel, new LogLine(T0, null, speaker, text));

    private static AlertRule Default(string name) => AlertRule.Defaults().Single(r => r.Name == name);

    [Theory]
    [InlineData("2 from Corpii Follower - Penetrates", true)]
    [InlineData("Pithum Silencer misses you completely", true)]
    [InlineData("60 to Corpii Follower - Scourge Light Missile - Hits", false)]
    [InlineData("Your Scourge Light Missile misses Corpii Follower completely", false)]
    public void TakingDamageOnlyMatchesIncoming(string text, bool expected) =>
        Assert.Equal(expected, Default("Taking damage").Matches(Game("combat", text)));

    [Fact]
    public void LineTypeMustMatch() =>
        Assert.False(Default("Taking damage").Matches(Game("notify", "2 from Corpii Follower")));

    [Theory]
    [InlineData("Your cargo hold is full.", true)]
    [InlineData("Ore hold is full", true)]
    [InlineData("Cargo is too far away. Ship is on automatic approach to cargo.", false)]
    public void HoldFull(string text, bool expected) =>
        Assert.Equal(expected, Default("Hold full").Matches(Game("notify", text)));

    [Fact]
    public void WarpScramble() =>
        Assert.True(Default("Warp scrambled / disrupted").Matches(Game("combat", "Warp scramble attempt from Pithum Silencer to you!")));

    [Fact]
    public void ChatRuleFiltersChannelAndIgnoresOwnMessages()
    {
        var rule = new AlertRule { Name = "Intel", Source = LogSource.Chat, Channel = "intel", Pattern = "Jita|Perimeter" };
        Assert.True(rule.Matches(Chat("Alliance Intel", "Friend", "hostile in jita")));
        Assert.False(rule.Matches(Chat("Local", "Friend", "hostile in jita")));
        Assert.False(rule.Matches(Chat("Alliance Intel", "Scout", "jita clear")));
        Assert.False(rule.Matches(Game("notify", "Jita")));
    }

    [Fact]
    public void DisabledAndInvalidRulesNeverMatch()
    {
        Assert.False(new AlertRule { Enabled = false, Pattern = "." }.Matches(Game("notify", "x")));
        var bad = new AlertRule { Pattern = "(unclosed" };
        Assert.False(bad.IsValid);
        Assert.False(bad.Matches(Game("notify", "(unclosed")));
    }

    [Fact]
    public void CooldownIsPerRuleAndCharacter()
    {
        var now = T0;
        var engine = new AlertEngine(() => now);
        var rules = new[] { Default("Taking damage") }; // 60 s cooldown

        Assert.NotNull(engine.Evaluate(rules, Game("combat", "5 from Rat")));
        now += TimeSpan.FromSeconds(10);
        Assert.Null(engine.Evaluate(rules, Game("combat", "5 from Rat")));
        Assert.NotNull(engine.Evaluate(rules, Game("combat", "5 from Rat", listener: "Miner Two")));
        now += TimeSpan.FromSeconds(60);
        Assert.NotNull(engine.Evaluate(rules, Game("combat", "5 from Rat")));
    }

    [Fact]
    public void LogsWithoutKnownCharacterAreIgnored()
    {
        var engine = new AlertEngine(() => T0);
        Assert.Null(engine.Evaluate(new[] { new AlertRule { Pattern = "." } }, Game("notify", "x", listener: "")));
    }

    [Fact]
    public void CenterSkipsFlashForActiveClientAndClearsOnAcknowledge()
    {
        var center = new AlertCenter();
        var rule = new AlertRule { Name = "r", Sound = false, Pattern = "." };
        int changes = 0;
        center.Changed += () => changes++;

        center.Raise(new Alert(rule, "Miner One", "x", T0), activeCharacter: "miner one");
        Assert.Null(center.Latest);

        center.Raise(new Alert(rule, "Miner Two", "x", T0), activeCharacter: "Miner One");
        center.Raise(new Alert(rule, "Hauler", "y", T0.AddSeconds(1)), activeCharacter: "Miner One");
        Assert.Equal("Hauler", center.Latest!.Character);
        Assert.NotNull(center.For("miner two"));

        center.Acknowledge("Hauler");
        Assert.Equal("Miner Two", center.Latest!.Character);
        Assert.Equal(3, changes);
    }
}
