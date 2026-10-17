using System.Windows.Input;

namespace EveMultibox.Toolbox;

/// <summary>
/// A single key with optional modifiers, e.g. "Ctrl+Space" or "F1". There is deliberately no way to
/// express a sequence: one chord is one EVE shortcut, which keeps every button at one action.
/// </summary>
public sealed record KeyChord(Key Key, bool Ctrl = false, bool Alt = false, bool Shift = false)
{
    private static readonly Dictionary<string, Key> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Esc"] = Key.Escape,
        ["Enter"] = Key.Return,
        ["PgUp"] = Key.PageUp,
        ["PgDn"] = Key.PageDown,
        ["Del"] = Key.Delete,
        ["Ins"] = Key.Insert,
        ["Plus"] = Key.OemPlus,
        ["Minus"] = Key.OemMinus,
        ["-"] = Key.OemMinus,
        ["="] = Key.OemPlus,
        [","] = Key.OemComma,
        ["."] = Key.OemPeriod,
        ["/"] = Key.OemQuestion,
        [";"] = Key.OemSemicolon,
        ["'"] = Key.OemQuotes,
        ["["] = Key.OemOpenBrackets,
        ["]"] = Key.OemCloseBrackets,
        ["\\"] = Key.OemPipe,
        ["`"] = Key.OemTilde,
    };

    private static readonly HashSet<Key> Forbidden = new()
    {
        Key.None, Key.LeftCtrl, Key.RightCtrl, Key.LeftAlt, Key.RightAlt, Key.LeftShift, Key.RightShift,
        Key.LWin, Key.RWin, Key.System,
    };

    public static bool TryParse(string? text, out KeyChord? chord, out string? error)
    {
        chord = null;
        error = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            error = "No key given.";
            return false;
        }

        // "Ctrl++" style isn't supported; use "Ctrl+Plus".
        var parts = text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            error = "No key given.";
            return false;
        }

        bool ctrl = false, alt = false, shift = false;
        for (int i = 0; i < parts.Length - 1; i++)
        {
            switch (parts[i].ToLowerInvariant())
            {
                case "ctrl" or "control": ctrl = true; break;
                case "alt": alt = true; break;
                case "shift": shift = true; break;
                default:
                    error = $"\"{parts[i]}\" isn't a modifier. Use Ctrl, Alt or Shift, then one key.";
                    return false;
            }
        }

        if (!TryParseKey(parts[^1], out var key))
        {
            error = $"\"{parts[^1]}\" isn't a key name I recognise.";
            return false;
        }

        chord = new KeyChord(key, ctrl, alt, shift);
        return true;
    }

    public static KeyChord Parse(string text) =>
        TryParse(text, out var chord, out var error) ? chord! : throw new FormatException(error);

    private static bool TryParseKey(string token, out Key key)
    {
        if (Aliases.TryGetValue(token, out key)) return true;
        if (token.Length == 1 && char.IsDigit(token[0]))
        {
            key = Key.D0 + (token[0] - '0');
            return true;
        }
        if (token.StartsWith("Num", StringComparison.OrdinalIgnoreCase) && token.Length == 4 && char.IsDigit(token[3]))
        {
            key = Key.NumPad0 + (token[3] - '0');
            return true;
        }
        if (Enum.TryParse(token, ignoreCase: true, out key) && !int.TryParse(token, out _) && !Forbidden.Contains(key))
            return true;
        key = Key.None;
        return false;
    }

    public int VirtualKey => KeyInterop.VirtualKeyFromKey(Key);

    public override string ToString()
    {
        var parts = new List<string>(4);
        if (Ctrl) parts.Add("Ctrl");
        if (Alt) parts.Add("Alt");
        if (Shift) parts.Add("Shift");
        parts.Add(KeyName(Key));
        return string.Join("+", parts);
    }

    private static string KeyName(Key key) => key switch
    {
        >= Key.D0 and <= Key.D9 => ((char)('0' + (key - Key.D0))).ToString(),
        >= Key.NumPad0 and <= Key.NumPad9 => "Num" + (key - Key.NumPad0),
        Key.Return => "Enter",
        Key.Escape => "Esc",
        Key.Next => "PageDown",
        Key.Prior => "PageUp",
        _ => key.ToString(),
    };
}
