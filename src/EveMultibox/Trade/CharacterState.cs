using EveMultibox.Config;

namespace EveMultibox.Trade;

/// <summary>Live info about one signed-in character, refreshed from ESI (and hinted by the chat logs).</summary>
public sealed class CharacterState : ObservableObject
{
    private bool? _online;
    private int? _systemId;
    private long? _stationId;
    private long? _structureId;
    private string _locationText = "Locating…";
    private int? _shipTypeId;
    private string _shipText = "";
    private double? _wallet;
    private SkillProfile _skills = SkillProfile.None;
    private string _skillsText = "";
    private string? _error;

    public CharacterState(long id, string name)
    {
        Id = id;
        Name = name;
    }

    public long Id { get; }
    public string Name { get; }

    public bool? Online { get => _online; set { if (Set(ref _online, value)) OnPropertyChanged(nameof(OnlineText)); } }
    public string OnlineText => Online switch { true => "● online", false => "○ offline", null => "" };

    public int? SystemId { get => _systemId; set => Set(ref _systemId, value); }
    public long? StationId { get => _stationId; set => Set(ref _stationId, value); }
    public long? StructureId { get => _structureId; set => Set(ref _structureId, value); }
    public string LocationText { get => _locationText; set => Set(ref _locationText, value); }

    public int? ShipTypeId { get => _shipTypeId; set => Set(ref _shipTypeId, value); }
    public string ShipText { get => _shipText; set => Set(ref _shipText, value); }

    /// <summary>Cargo hold of the current ship in m³ (0 if unknown).</summary>
    public double CargoCapacity { get; set; }

    /// <summary>Mining/ore hold of the current ship in m³ (0 if it has none).</summary>
    public double OreHoldCapacity { get; set; }

    /// <summary>The current ship's item ID, used to find what's already in its holds.</summary>
    public long? ShipItemId { get; set; }

    public double? Wallet { get => _wallet; set { if (Set(ref _wallet, value)) OnPropertyChanged(nameof(WalletText)); } }
    public string WalletText => Wallet is double w ? $"{w:N0} ISK" : "";

    public SkillProfile Skills { get => _skills; set => Set(ref _skills, value); }
    public string SkillsText { get => _skillsText; set => Set(ref _skillsText, value); }

    public string? Error { get => _error; set => Set(ref _error, value); }

    public override string ToString() => Name;
}
