using EveMultibox.Clients;

namespace EveMultibox.Tests;

public class TitleParserTests
{
    private readonly TitleParser _parser = new(TitleParser.DefaultPattern, includeLoginScreens: true);

    [Theory]
    [InlineData("EVE - Jita Trader", "Jita Trader")]
    [InlineData("EVE - X", "X")]
    [InlineData("  EVE - Spaced Name  ", "Spaced Name")]
    public void ParsesCharacterNames(string title, string expected)
    {
        var parsed = _parser.Parse(title);
        Assert.True(parsed.IsClient);
        Assert.Equal(expected, parsed.CharacterName);
    }

    [Fact]
    public void LoginScreenIsAClientWithoutName()
    {
        var parsed = _parser.Parse("EVE");
        Assert.True(parsed.IsLoginScreen);
    }

    [Fact]
    public void LoginScreenIgnoredWhenDisabled()
    {
        var parser = new TitleParser(TitleParser.DefaultPattern, includeLoginScreens: false);
        Assert.False(parser.Parse("EVE").IsClient);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("EVE Launcher")]
    [InlineData("EVE - ")]
    [InlineData("Untitled - Notepad")]
    public void RejectsOtherWindows(string? title) => Assert.False(_parser.Parse(title).IsClient);

    [Fact]
    public void CustomPatternForNotepadTesting()
    {
        var parser = new TitleParser(@"^(?<name>.+) - Notepad$", includeLoginScreens: false);
        Assert.Equal("miner1.txt", parser.Parse("miner1.txt - Notepad").CharacterName);
    }

    [Theory]
    [InlineData(@"^EVE - (?<name>.+)$", true)]
    [InlineData(@"^EVE - (.+)$", false)]
    [InlineData(@"^EVE - (?<name>.+$", false)]
    public void ValidatesPatterns(string pattern, bool valid) =>
        Assert.Equal(valid, TitleParser.IsValidPattern(pattern, out _));
}
