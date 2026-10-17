using System.Media;

namespace EveMultibox.Alerts;

/// <summary>
/// Keeps the latest unseen alert for each character. An alert counts as seen once you switch to
/// that character's client. Raising an alert plays a sound and nothing else; it never touches the game.
/// </summary>
public sealed class AlertCenter
{
    private readonly Dictionary<string, Alert> _pending = new(StringComparer.OrdinalIgnoreCase);

    public event Action? Changed;

    public IReadOnlyCollection<Alert> Pending => _pending.Values;

    public Alert? Latest => _pending.Values.OrderByDescending(a => a.At).FirstOrDefault();

    public Alert? For(string? character) =>
        character is not null && _pending.TryGetValue(character, out var a) ? a : null;

    /// <param name="activeCharacter">The character whose client is in front. You're already looking at it, so it gets the sound only.</param>
    public void Raise(Alert alert, string? activeCharacter)
    {
        if (alert.Rule.Sound) SystemSounds.Exclamation.Play();
        if (string.Equals(alert.Character, activeCharacter, StringComparison.OrdinalIgnoreCase)) return;

        _pending[alert.Character] = alert;
        Changed?.Invoke();
    }

    public void Acknowledge(string? character)
    {
        if (character is not null && _pending.Remove(character)) Changed?.Invoke();
    }

    public void Clear()
    {
        if (_pending.Count == 0) return;
        _pending.Clear();
        Changed?.Invoke();
    }
}
