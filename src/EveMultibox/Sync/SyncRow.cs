using EveMultibox.Config;

namespace EveMultibox.Sync;

/// <summary>A settings file as shown in the UI sync tab, with its "overwrite this one" checkbox.</summary>
public sealed class SyncRow : ObservableObject
{
    private bool _selected;

    public SyncRow(SettingsFile file, string displayName)
    {
        File = file;
        DisplayName = displayName;
    }

    public SettingsFile File { get; }
    public string DisplayName { get; }
    public string KindText => File.Kind == SettingsKind.Character ? "Character" : "Account";
    public long Id => File.Id;
    public DateTime LastWrite => File.LastWrite;

    public bool Selected { get => _selected; set => Set(ref _selected, value); }

    public override string ToString() => $"{DisplayName} · {File.LastWrite:g}";
}
