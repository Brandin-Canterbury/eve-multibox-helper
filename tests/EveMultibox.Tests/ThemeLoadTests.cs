using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace EveMultibox.Tests;

/// <summary>
/// Loads the shared dark theme for real, so a broken style or template fails here instead of
/// when the helper opens a window.
/// </summary>
public class ThemeLoadTests
{
    private static void RunSta(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                error = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error is not null) throw new Xunit.Sdk.XunitException("Theme failed to load: " + error);
    }

    /// <summary>Parses the theme's XAML source with WPF's own reader (no running app needed).</summary>
    private static ResourceDictionary LoadTheme()
    {
        // Found relative to this source file, so it works whatever folder the tests are built into.
        string repo = System.IO.Path.GetFullPath(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(ThisFile())!, "..", ".."));
        string path = System.IO.Path.Combine(repo, "src", "EveMultibox", "Settings", "DarkTheme.xaml");
        Assert.True(System.IO.File.Exists(path), $"Couldn't find {path}.");
        using var stream = System.IO.File.OpenRead(path);
        return (ResourceDictionary)System.Windows.Markup.XamlReader.Load(stream);
    }

    private static string ThisFile([System.Runtime.CompilerServices.CallerFilePath] string path = "") => path;

    [Fact]
    public void ThemeLoadsAndStylesTheControlsThatWereWhite() => RunSta(() =>
    {
        var theme = LoadTheme();
        foreach (var type in new[] { typeof(CheckBox), typeof(RadioButton), typeof(ScrollBar), typeof(Slider), typeof(Button), typeof(DataGrid), typeof(ToolTip), typeof(ProgressBar) })
        {
            var style = theme[type] as Style;
            Assert.NotNull(style);
            Assert.Equal(type, style!.TargetType);
        }
        Assert.IsType<Style>(theme["GridCheck"]);
        Assert.IsType<Style>(theme["GridCheckEdit"]);
    });

    [Fact]
    public void CheckBoxTemplateBuildsWithItsParts() => RunSta(() =>
    {
        var theme = LoadTheme();
        var box = new CheckBox { Style = (Style)theme[typeof(CheckBox)], Content = "Avoid low-sec", IsChecked = true };
        box.ApplyTemplate();
        Assert.NotNull(box.Template.FindName("Box", box));
        Assert.NotNull(box.Template.FindName("Tick", box));
    });

    [Fact]
    public void ScrollBarHasATrackInBothOrientations() => RunSta(() =>
    {
        var theme = LoadTheme();
        foreach (var orientation in new[] { Orientation.Vertical, Orientation.Horizontal })
        {
            var bar = new ScrollBar { Style = (Style)theme[typeof(ScrollBar)], Orientation = orientation };
            bar.ApplyTemplate();
            Assert.NotNull(bar.Track);
        }
    });
}
