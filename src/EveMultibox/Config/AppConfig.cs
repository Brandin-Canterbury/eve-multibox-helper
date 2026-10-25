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
    public TradeSettings Trade { get; set; } = new();
    public WidgetSettings Widget { get; set; } = new();
    public UpdateSettings Updates { get; set; } = new();
    public NotificationSettings Notifications { get; set; } = new();

    /// <summary>EVE's logs folder, used by alerts and character names. Blank means Documents\EVE\logs.</summary>
    public string? EveLogsFolder { get; set; }

    /// <summary>
    /// The clients strip (thumbnails, ⚙ and ★). Off by default; it also only shows with
    /// <see cref="MinClientsForMultiboxer"/> or more clients. Hiding it leaves borders and name tags alone.
    /// </summary>
    public bool ShowMultiboxer { get; set; }

    /// <summary>The clients strip is for multiboxing, so with fewer clients than this it stays hidden.</summary>
    public const int MinClientsForMultiboxer = 2;

    /// <summary>Settings format version, for one-time upgrades of older config files.</summary>
    public int ConfigVersion { get; set; }

    public const int CurrentConfigVersion = 2;

    /// <summary>Hide the ☰ button, clients strip and toolbox while no EVE client is running (the tray icon stays).</summary>
    public bool AutoHideWithoutClients { get; set; } = true;

    public bool ShowOverlays { get; set; } = true;
    public bool ShowThumbnails { get; set; } = true;
    public bool ShowToolbox { get; set; } = true;
    public int ThumbnailWidth { get; set; } = 240;

    public WindowSpot ToolboxPosition { get; set; } = new() { X = 40, Y = 200 };
    public WindowSpot StripPosition { get; set; } = new() { X = 40, Y = 40 };

    /// <summary>Where the ☰ menu button sits. Null means the default, the bottom-right of the main screen.</summary>
    public WindowSpot? MenuButtonPosition { get; set; }

    /// <summary>Brings older config files up to date after loading.</summary>
    public void Upgrade()
    {
        // The logs folder used to be an alerts setting; it's general now because more features read the logs.
#pragma warning disable CS0618
        if (EveLogsFolder is null && !string.IsNullOrWhiteSpace(Alerts.LogsFolder)) EveLogsFolder = Alerts.LogsFolder;
        Alerts.LogsFolder = null;
#pragma warning restore CS0618

        // Version 2: the clients strip now starts off (it was on by default before).
        if (ConfigVersion < 2) ShowMultiboxer = false;
        ConfigVersion = CurrentConfigVersion;
    }

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
        var config = new AppConfig { ConfigVersion = CurrentConfigVersion };
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

    /// <summary>Read from older config files only; see <see cref="AppConfig.EveLogsFolder"/>.</summary>
    [Obsolete("Moved to AppConfig.EveLogsFolder.")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LogsFolder { get; set; }

    /// <summary>Brings the client with the newest unseen alert to the front. Focus only.</summary>
    public string? JumpHotkey { get; set; } = "Ctrl+Alt+A";

    public ObservableCollection<AlertRule> Rules { get; set; } = new(AlertRule.Defaults());
}

public sealed class TradeSettings
{
    /// <summary>Client ID of the user's own app from developers.eveonline.com (PKCE, so no secret).</summary>
    public string? ClientId { get; set; }

    /// <summary>Must match the callback registered for the app: http://localhost:{port}/callback/</summary>
    public int CallbackPort { get; set; } = 51515;

    /// <summary>Default radius for the "within N jumps" route search.</summary>
    public int JumpRadius { get; set; } = 8;

    /// <summary>
    /// Count jumps the way EVE's "prefer safer" autopilot flies: around low-sec where there's a
    /// high-sec way, even if it's longer. Off counts the shortest route, low-sec and all.
    /// </summary>
    public bool SaferRoutes { get; set; } = true;

    /// <summary>Trade hub stations: Jita 4-4, Amarr VIII, Dodixie IX-20, Rens VI-8, Hek VIII-12.</summary>
    public List<long> HubStationIds { get; set; } = new() { 60003760, 60008494, 60011866, 60004588, 60005686 };

    public WindowSpot? WindowPosition { get; set; }

    [JsonIgnore]
    public string CallbackUrl => $"http://localhost:{CallbackPort}/callback/";
}

/// <summary>The action widget's EVE shortcuts. They must match Esc → Shortcuts in game.</summary>
public sealed class WidgetSettings
{
    /// <summary>EVE's "Dock / Jump / Activate gate": warps to 0 on the selected gate or station, then jumps or docks.</summary>
    public string? JumpKey { get; set; } = "D";

    /// <summary>EVE's undock shortcut (no default binding is assumed; set the one you use).</summary>
    public string? UndockKey { get; set; }

    /// <summary>Where the first widget opens when a character has no spot of its own yet.</summary>
    public WindowSpot? Position { get; set; }

    /// <summary>Each character's widget remembers its own spot (by character ID).</summary>
    public Dictionary<long, WindowSpot> Positions { get; set; } = new();
}

/// <summary>
/// Windows notifications about market orders and the wallet. Checks follow ESI's own refresh of a
/// character's orders (about every 20 minutes). Templates use tokens like {item} and {total}.
/// </summary>
public sealed class NotificationSettings
{
    public bool Enabled { get; set; } = true;

    /// <summary>Some of a sell order sold, or some of a buy order was filled.</summary>
    public bool OrderFills { get; set; } = true;

    /// <summary>An order finished: sold out, filled, expired or cancelled.</summary>
    public bool OrderEnded { get; set; } = true;

    public bool WalletChanges { get; set; }

    /// <summary>Smallest wallet change worth a notification, in ISK.</summary>
    public double MinWalletChange { get; set; } = 1_000_000;

    /// <summary>Add the character's new wallet balance at the end of each notification.</summary>
    public bool AppendBalance { get; set; } = true;

    /// <summary>Characters to leave out (by character ID).</summary>
    public List<long> MutedCharacters { get; set; } = new();

    public string SoldTemplate { get; set; } = DefaultSold;
    public string BoughtTemplate { get; set; } = DefaultBought;
    public string EndedTemplate { get; set; } = DefaultEnded;
    public string WalletTemplate { get; set; } = DefaultWallet;

    public const string DefaultSold = "{character} sold {qty} × {item} for {total} · {remaining} left";
    public const string DefaultBought = "{character} bought {qty} × {item} for {total} · {remaining} still wanted";
    public const string DefaultEnded = "{character}'s {side} order for {item} {state}";
    public const string DefaultWallet = "{character}'s wallet {change} → {balance}";
}

public sealed class UpdateSettings
{
    /// <summary>Check GitHub for a newer release shortly after start-up and every few hours.</summary>
    public bool AutoCheck { get; set; } = true;

    /// <summary>A version the user chose to skip; it won't be offered again automatically.</summary>
    public string? SkippedVersion { get; set; }
}

public sealed class WindowSpot
{
    public double X { get; set; }
    public double Y { get; set; }
}
