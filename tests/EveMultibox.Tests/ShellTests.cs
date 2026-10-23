using System.IO;
using EveMultibox.Config;
using EveMultibox.Interop;
using EveMultibox.Shell;

namespace EveMultibox.Tests;

public sealed class ShellTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "EveMultiboxTests", Guid.NewGuid().ToString("N"));

    public ShellTests() => Directory.CreateDirectory(_dir);

    [Fact]
    public void MenuButtonDefaultsToBottomRightOfWorkArea()
    {
        // Your main monitor: 2752x1152 px with a 48 px taskbar, 125% scaling (50 px button, 20 px margin).
        var (x, y) = MenuButtonWindow.DefaultPosition(new RECT(0, 0, 2752, 1104), 50, 50, 20);
        Assert.Equal((2682, 1034), (x, y));
    }

    [Theory]
    [InlineData(100, 100, true)]
    [InlineData(-1400, -1900, true)]  // On a monitor above-left of the main one.
    [InlineData(5000, 100, false)]    // Monitor no longer connected.
    [InlineData(-1470, 500, false)]   // Mostly off the left edge.
    public void SavedPositionMustStillBeOnScreen(double x, double y, bool expected)
    {
        var virtualScreen = new RECT(-1440, -2003, 3440, 1440);
        Assert.Equal(expected, MenuButtonWindow.IsOnScreen(new WindowSpot { X = x, Y = y }, 50, 50, virtualScreen));
    }

    [Fact]
    public void NewConfigShowsMultiboxerAndUsesDefaultButtonSpot()
    {
        var config = AppConfig.CreateDefault();
        Assert.True(config.ShowMultiboxer);
        Assert.Null(config.MenuButtonPosition);
        Assert.Null(config.EveLogsFolder);
    }

    [Fact]
    public void OldLogsFolderSettingMovesToGeneralSettings()
    {
        string path = Path.Combine(_dir, "config.json");
        File.WriteAllText(path, """{ "alerts": { "enabled": true, "logsFolder": "D:\\EVE\\logs" } }""");

        var store = new ConfigStore(path);
        var config = store.Load();
        Assert.Equal(@"D:\EVE\logs", config.EveLogsFolder);

        store.Save(config);
        string json = File.ReadAllText(path);
        Assert.Contains("\"eveLogsFolder\"", json);
        Assert.DoesNotContain("\"logsFolder\"", json); // The old key is dropped once migrated.
    }

    [Fact]
    public void MenuButtonPositionRoundTrips()
    {
        var store = new ConfigStore(Path.Combine(_dir, "c.json"));
        var config = AppConfig.CreateDefault();
        config.MenuButtonPosition = new WindowSpot { X = 1800, Y = 950 };
        config.ShowMultiboxer = false;
        store.Save(config);

        var loaded = store.Load();
        Assert.Equal((1800d, 950d), (loaded.MenuButtonPosition!.X, loaded.MenuButtonPosition.Y));
        Assert.False(loaded.ShowMultiboxer);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }
}
