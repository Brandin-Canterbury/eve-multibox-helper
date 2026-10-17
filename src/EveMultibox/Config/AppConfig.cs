using System.Collections.ObjectModel;
using System.Text.Json.Serialization;
using EveMultibox.Alerts;
using EveMultibox.Clients;
using EveMultibox.Toolbox;

namespace EveMultibox.Config;

public sealed class AppConfig
{
    public DetectionSettings Detection { get; set; } = new();
    public ObservableCollection<ClientProfile> Clients { get; set; } = new();
    public ObservableCollection<FavoriteCommand> Favorites { get; set; } = new();
    public AlertSettings Alerts { get; set; } = new();

    public bool ShowOverlays { get; set; } = true;
    public bool ShowThumbnails { get; set; } = true;
    public bool ShowToolbox { get; set; } = true;
    public int ThumbnailWidth { get; set; } = 240;

    public WindowSpot ToolboxPosition { get; set; } = new() { X = 40, Y = 200 };
    public WindowSpot StripPosition { get; set; } = new() { X = 40, Y = 40 };

    /// <summary>Distinct, high-contrast colors handed out to newly seen characters.</summary>
    public static readonly string[] Palette =
    {
        "#FF4D4D", "#4DA6FF", "#5CD65C", "#FFB84D", "#C774FF", "#4DE0E0", "#FF73C7", "#E6E64D",
    };

    public ClientProfile? FindProfile(string? characterName) =>
        characterName is null
            ? null
            : Clients.FirstOrDefault(c => string.Equals(c.CharacterName, characterName, StringComparison.OrdinalIgnoreCase));

    /// <summary>Returns the profile for a character, creating one with the next palette color if it's new.</summary>
    public ClientProfile GetOrCreateProfile(string characterName)
    {
        var existing = FindProfile(characterName);
        if (existing is not null) return existing;

        var used = Clients.Select(c => c.Color).ToHashSet(StringComparer.OrdinalIgnoreCase);
        string color = Palette.FirstOrDefault(p => !used.Contains(p)) ?? Palette[Clients.Count % Palette.Length];
        var profile = new ClientProfile { CharacterName = characterName, Label = characterName, Color = color };
        Clients.Add(profile);
        return profile;
    }

    /// <summary>Favorites shown when the given character is the toolbox target: global ones plus its own.</summary>
    public IEnumerable<FavoriteCommand> FavoritesFor(string? characterName) =>
        Favorites.Where(f => string.IsNullOrWhiteSpace(f.Character) ||
                             (characterName is not null &&
                              string.Equals(f.Character.Trim(), characterName, StringComparison.OrdinalIgnoreCase)));

    public static AppConfig CreateDefault()
    {
        var config = new AppConfig();
        // These match EVE's default shortcuts. Players often rebind, so the settings window reminds the
        // user to check them against Esc > Shortcuts in game.
        config.Favorites.Add(new FavoriteCommand { Name = "High slot 1", Keys = "F1" });
        config.Favorites.Add(new FavoriteCommand { Name = "High slot 2", Keys = "F2" });
        config.Favorites.Add(new FavoriteCommand { Name = "High slot 3", Keys = "F3" });
        config.Favorites.Add(new FavoriteCommand { Name = "Mid slot 1", Keys = "Alt+F1" });
        config.Favorites.Add(new FavoriteCommand { Name = "Stop ship", Keys = "Ctrl+Space" });
        return config;
    }
}

public sealed class DetectionSettings
{
    /// <summary>Process names (without .exe) that host EVE clients.</summary>
    public List<string> ProcessNames { get; set; } = new() { "exefile" };

    /// <summary>Regex applied to window titles; the "name" group is the character name.</summary>
    public string TitleRegex { get; set; } = TitleParser.DefaultPattern;

    /// <summary>Also track clients sitting at the login / character select screen (title "EVE").</summary>
    public bool IncludeLoginScreens { get; set; } = true;
}

public sealed class ClientProfile : ObservableObject
{
    private string _characterName = "";
    private string _label = "";
    private string _color = "#FFFFFF";
    private string? _focusHotkey;

    public string CharacterName { get => _characterName; set => Set(ref _characterName, value); }

    /// <summary>Short role label shown on overlays and thumbnails, e.g. "MINER 2".</summary>
    public string Label { get => _label; set => Set(ref _label, value); }

    /// <summary>#RRGGBB color used for this client's border, label and toolbox header.</summary>
    public string Color { get => _color; set => Set(ref _color, value); }

    /// <summary>Optional global hotkey (e.g. "Ctrl+Alt+1") that only brings this client to the front.</summary>
    public string? FocusHotkey { get => _focusHotkey; set => Set(ref _focusHotkey, value); }

    [JsonIgnore]
    public string DisplayLabel => string.IsNullOrWhiteSpace(Label) ? CharacterName : Label;
}

public sealed class AlertSettings
{
    public bool Enabled { get; set; } = true;

    /// <summary>EVE's logs folder. Blank means Documents\EVE\logs.</summary>
    public string? LogsFolder { get; set; }

    /// <summary>Brings the client with the newest unseen alert to the front. Focus only.</summary>
    public string? JumpHotkey { get; set; } = "Ctrl+Alt+A";

    public ObservableCollection<AlertRule> Rules { get; set; } = new(AlertRule.Defaults());
}

public sealed class WindowSpot
{
    public double X { get; set; }
    public double Y { get; set; }
}
