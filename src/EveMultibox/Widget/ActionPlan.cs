namespace EveMultibox.Widget;

/// <summary>Which EVE shortcut a widget button presses. The actual keys come from settings.</summary>
public enum GameKey
{
    JumpOrDock,
    Undock,
}

/// <summary>A value the user pastes into EVE, e.g. a quantity or price.</summary>
public sealed record CopyValue(string Label, string Value);

/// <summary>
/// One step in a guided plan. Every button is a single, deliberate action: set an autopilot
/// destination through ESI, press one EVE shortcut in the EVE window you're already in, or copy a
/// value. Nothing chains or repeats on its own.
/// </summary>
public sealed record ActionStep(string Title, string Instruction)
{
    /// <summary>Offer a "Set destination" button for this station or system.</summary>
    public long? DestinationId { get; init; }
    public string? DestinationLabel { get; init; }

    /// <summary>System to count remaining jumps to (shown live while travelling).</summary>
    public int? TargetSystemId { get; init; }

    /// <summary>One-key EVE actions offered on this step.</summary>
    public IReadOnlyList<GameKey> Keys { get; init; } = Array.Empty<GameKey>();

    public IReadOnlyList<CopyValue> Copies { get; init; } = Array.Empty<CopyValue>();

    /// <summary>Step completes on its own when this becomes true (e.g. docked at the station).</summary>
    public Func<PlanContext, bool>? CompleteWhen { get; init; }

    /// <summary>Text on the manual "I've done it" button.</summary>
    public string DoneLabel { get; init; } = "Done";
}

/// <summary>What a step's completion check can see about the character doing the plan.</summary>
public sealed record PlanContext(int? SystemId, long? StationId, long? StructureId)
{
    public bool InSpace => StationId is null && StructureId is null;
}

public sealed record ActionPlan(string Title, long CharacterId, string CharacterName, IReadOnlyList<ActionStep> Steps);
