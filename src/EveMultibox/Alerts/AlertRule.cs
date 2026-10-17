using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using EveMultibox.Config;
using EveMultibox.Logs;

namespace EveMultibox.Alerts;

/// <summary>
/// "When a log line matches this, tell me." Rules only notify (sound and a flashing thumbnail);
/// nothing here ever sends input to the game.
/// </summary>
public sealed class AlertRule : ObservableObject
{
    private bool _enabled = true;
    private string _name = "";
    private LogSource _source = LogSource.Game;
    private string? _lineType;
    private string? _channel;
    private string _pattern = "";
    private int _cooldownSeconds = 30;
    private bool _sound = true;

    public bool Enabled { get => _enabled; set => Set(ref _enabled, value); }
    public string Name { get => _name; set => Set(ref _name, value); }

    /// <summary>Game log (combat, notify...) or chat log.</summary>
    public LogSource Source { get => _source; set => Set(ref _source, value); }

    /// <summary>Game logs only: the line type in brackets, e.g. "combat" or "notify". Blank matches any.</summary>
    public string? LineType { get => _lineType; set => Set(ref _lineType, value); }

    /// <summary>Chat only: channel name must contain this (e.g. "Intel"). Blank matches any channel.</summary>
    public string? Channel { get => _channel; set => Set(ref _channel, value); }

    /// <summary>Case-insensitive regex tested against the line text with EVE's markup removed.</summary>
    public string Pattern
    {
        get => _pattern;
        set
        {
            if (Set(ref _pattern, value))
            {
                _regex = null;
                OnPropertyChanged(nameof(IsValid));
            }
        }
    }

    /// <summary>Minimum gap between alerts from this rule for the same character, so a fight doesn't spam.</summary>
    public int CooldownSeconds { get => _cooldownSeconds; set => Set(ref _cooldownSeconds, Math.Max(0, value)); }

    public bool Sound { get => _sound; set => Set(ref _sound, value); }

    [JsonIgnore]
    public bool IsValid => GetRegex() is not null;

    private Regex? _regex;

    internal Regex? GetRegex()
    {
        if (_regex is not null) return _regex;
        if (string.IsNullOrWhiteSpace(Pattern)) return null;
        try
        {
            _regex = new Regex(Pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));
        }
        catch (ArgumentException)
        {
            _regex = null;
        }
        return _regex;
    }

    public bool Matches(LogEvent e)
    {
        if (!Enabled || e.Source != Source) return false;

        if (Source == LogSource.Game)
        {
            if (!string.IsNullOrWhiteSpace(LineType) && !string.Equals(LineType.Trim(), e.Line.Type, StringComparison.OrdinalIgnoreCase))
                return false;
        }
        else
        {
            if (!string.IsNullOrWhiteSpace(Channel) &&
                (e.Channel is null || !e.Channel.Contains(Channel.Trim(), StringComparison.OrdinalIgnoreCase)))
                return false;
            // Your own messages never alert you.
            if (e.Listener is not null && string.Equals(e.Line.Speaker, e.Listener, StringComparison.OrdinalIgnoreCase)) return false;
        }

        var regex = GetRegex();
        if (regex is null) return false;
        try
        {
            return regex.IsMatch(e.Line.Text);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }

    public static IEnumerable<AlertRule> Defaults() => new[]
    {
        new AlertRule
        {
            Name = "Taking damage", Source = LogSource.Game, LineType = "combat",
            Pattern = @"^\d+ from |misses you", CooldownSeconds = 60,
        },
        new AlertRule
        {
            Name = "Warp scrambled / disrupted", Source = LogSource.Game, LineType = "combat",
            Pattern = @"warp (scramble|disruption) attempt", CooldownSeconds = 15,
        },
        new AlertRule
        {
            Name = "Hold full", Source = LogSource.Game, LineType = "notify",
            Pattern = @"hold.*\bfull\b", CooldownSeconds = 60,
        },
        new AlertRule
        {
            Name = "Out of ammo", Source = LogSource.Game, LineType = "notify",
            Pattern = @"run out of charges", CooldownSeconds = 60,
        },
        new AlertRule
        {
            Name = "Intel channel mention", Source = LogSource.Chat, Channel = "Intel",
            Pattern = @"REPLACE-WITH-YOUR-SYSTEM", CooldownSeconds = 10, Enabled = false,
        },
    };
}
