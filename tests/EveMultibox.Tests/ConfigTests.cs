using System.IO;
using EveMultibox.Config;
using EveMultibox.Toolbox;

namespace EveMultibox.Tests;

public sealed class ConfigTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "EveMultiboxTests", Guid.NewGuid().ToString("N"));
    private string ConfigPath => Path.Combine(_dir, "config.json");

    [Fact]
    public void MissingFileGivesDefaults()
    {
        var config = new ConfigStore(ConfigPath).Load();
        Assert.NotEmpty(config.Favorites);
        Assert.Equal(new[] { "exefile" }, config.Detection.ProcessNames);
    }

    [Fact]
    public void RoundTrips()
    {
        var store = new ConfigStore(ConfigPath);
        var config = AppConfig.CreateDefault();
        var p = config.GetOrCreateProfile("Miner One");
        p.Label = "MINER 1";
        p.FocusHotkey = "Ctrl+Alt+1";
        config.Favorites.Add(new FavoriteCommand { Name = "Orca boost", Keys = "Alt+F2", Character = "Hauler" });
        config.ThumbnailWidth = 300;
        store.Save(config);

        var loaded = store.Load();
        var lp = Assert.Single(loaded.Clients);
        Assert.Equal("MINER 1", lp.Label);
        Assert.Equal("Ctrl+Alt+1", lp.FocusHotkey);
        Assert.Equal(p.Color, lp.Color);
        Assert.Equal(300, loaded.ThumbnailWidth);
        var fav = loaded.Favorites.Last();
        Assert.Equal(("Orca boost", "Alt+F2", "Hauler"), (fav.Name, fav.Keys, fav.Character));
        Assert.Equal(config.Favorites.Last().Id, fav.Id);
    }

    [Fact]
    public void CorruptFileIsSetAsideAndDefaultsUsed()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(ConfigPath, "{ not json");
        var config = new ConfigStore(ConfigPath).Load();
        Assert.NotEmpty(config.Favorites);
        Assert.True(File.Exists(ConfigPath + ".bad"));
    }

    [Fact]
    public void NewCharactersGetDistinctColors()
    {
        var config = new AppConfig();
        var colors = Enumerable.Range(0, AppConfig.Palette.Length)
            .Select(i => config.GetOrCreateProfile($"Char {i}").Color)
            .ToList();
        Assert.Equal(colors.Count, colors.Distinct().Count());
        Assert.Same(config.GetOrCreateProfile("char 0"), config.GetOrCreateProfile("Char 0"));
    }

    [Fact]
    public void FavoritesForTargetIncludeGlobalAndOwn()
    {
        var config = new AppConfig();
        config.Favorites.Add(new FavoriteCommand { Name = "All" });
        config.Favorites.Add(new FavoriteCommand { Name = "Orca only", Character = "Orca Pilot" });
        config.Favorites.Add(new FavoriteCommand { Name = "Miner only", Character = "Miner" });

        Assert.Equal(new[] { "All", "Orca only" }, config.FavoritesFor("orca pilot").Select(f => f.Name));
        Assert.Equal(new[] { "All" }, config.FavoritesFor(null).Select(f => f.Name));
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }
}
