namespace EveMultibox.Clients;

/// <summary>One running EVE client window.</summary>
public sealed class EveClient
{
    public EveClient(IntPtr hwnd, uint processId)
    {
        Hwnd = hwnd;
        ProcessId = processId;
    }

    public IntPtr Hwnd { get; }
    public uint ProcessId { get; }
    public string Title { get; internal set; } = "";

    /// <summary>Null while the client is at the login or character select screen.</summary>
    public string? CharacterName { get; internal set; }

    public bool IsLoginScreen => CharacterName is null;

    public override string ToString() => CharacterName ?? $"Login screen (pid {ProcessId})";
}
