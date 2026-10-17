using System.Text.Json.Serialization;
using EveMultibox.Config;

namespace EveMultibox.Toolbox;

/// <summary>A toolbox button: a friendly name for one EVE shortcut.</summary>
public sealed class FavoriteCommand : ObservableObject
{
    private string _name = "";
    private string _keys = "";
    private string? _character;

    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Name { get => _name; set => Set(ref _name, value); }

    /// <summary>The shortcut as text, e.g. "Alt+F1". Must match the binding in EVE (Esc > Shortcuts).</summary>
    public string Keys
    {
        get => _keys;
        set
        {
            if (Set(ref _keys, value)) OnPropertyChanged(nameof(IsValid));
        }
    }

    /// <summary>Blank shows the button for every character; otherwise only when that character is the target.</summary>
    public string? Character { get => _character; set => Set(ref _character, value); }

    [JsonIgnore]
    public bool IsValid => KeyChord.TryParse(Keys, out _, out _);

    public bool TryGetChord(out KeyChord? chord) => KeyChord.TryParse(Keys, out chord, out _);
}
