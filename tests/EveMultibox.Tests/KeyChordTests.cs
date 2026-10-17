using System.Windows.Input;
using EveMultibox.Toolbox;

namespace EveMultibox.Tests;

public class KeyChordTests
{
    [Theory]
    [InlineData("F1", Key.F1, false, false, false)]
    [InlineData("Alt+F1", Key.F1, false, true, false)]
    [InlineData("ctrl + space", Key.Space, true, false, false)]
    [InlineData("Ctrl+Alt+Shift+C", Key.C, true, true, true)]
    [InlineData("Ctrl+Alt+1", Key.D1, true, true, false)]
    [InlineData("Num5", Key.NumPad5, false, false, false)]
    [InlineData("Esc", Key.Escape, false, false, false)]
    public void Parses(string text, Key key, bool ctrl, bool alt, bool shift)
    {
        Assert.True(KeyChord.TryParse(text, out var chord, out var error), error);
        Assert.Equal(new KeyChord(key, ctrl, alt, shift), chord);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Ctrl")]
    [InlineData("Ctrl+")]
    [InlineData("Win+D")]
    [InlineData("F1+F2")]
    [InlineData("NotAKey")]
    [InlineData("123")]
    [InlineData("LWin")]
    public void RejectsInvalidOrMultiKeyInput(string text) => Assert.False(KeyChord.TryParse(text, out _, out _));

    [Theory]
    [InlineData("alt+f1", "Alt+F1")]
    [InlineData("shift+ctrl+1", "Ctrl+Shift+1")]
    [InlineData("PgDn", "PageDown")]
    public void RoundTripsToCanonicalText(string input, string expected)
    {
        var chord = KeyChord.Parse(input);
        Assert.Equal(expected, chord.ToString());
        Assert.Equal(chord, KeyChord.Parse(chord.ToString()));
    }
}
