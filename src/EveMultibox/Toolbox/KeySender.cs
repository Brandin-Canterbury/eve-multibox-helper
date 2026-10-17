using EveMultibox.Interop;

namespace EveMultibox.Toolbox;

public enum SendResult
{
    Sent,
    NoTarget,
    TargetGone,
    TargetNotForeground,
    CoolingDown,
    SendFailed,
}

/// <summary>The OS calls KeySender needs, split out so the safety rules can be unit tested.</summary>
public interface IInputBackend
{
    IntPtr GetForegroundWindow();
    bool IsWindow(IntPtr hwnd);
    bool SendChord(KeyChord chord);
}

/// <summary>
/// Sends toolbox keypresses while enforcing the rules that keep the tool within CCP's EULA:
/// one click sends one chord, only to the client that is already in the foreground, never to a
/// background window, with no repeats, sequences or timers. A short per-button cooldown swallows
/// accidental double-clicks.
/// </summary>
public sealed class KeySender
{
    public static readonly TimeSpan DefaultCooldown = TimeSpan.FromMilliseconds(300);

    private readonly IInputBackend _backend;
    private readonly Func<DateTime> _clock;
    private readonly TimeSpan _cooldown;
    private readonly Dictionary<string, DateTime> _lastSent = new();

    public KeySender(IInputBackend backend, Func<DateTime>? clock = null, TimeSpan? cooldown = null)
    {
        _backend = backend;
        _clock = clock ?? (() => DateTime.UtcNow);
        _cooldown = cooldown ?? DefaultCooldown;
    }

    public SendResult TrySend(IntPtr target, string buttonId, KeyChord chord)
    {
        if (target == IntPtr.Zero) return SendResult.NoTarget;
        if (!_backend.IsWindow(target)) return SendResult.TargetGone;

        // The key goes to whatever window has focus, so refuse unless that is the intended client.
        // We never move focus here: if the user clicked somewhere else, they need to click the client again.
        if (_backend.GetForegroundWindow() != target) return SendResult.TargetNotForeground;

        var now = _clock();
        if (_lastSent.TryGetValue(buttonId, out var last) && now - last < _cooldown) return SendResult.CoolingDown;

        if (!_backend.SendChord(chord)) return SendResult.SendFailed;
        _lastSent[buttonId] = now;
        return SendResult.Sent;
    }
}

/// <summary>Real keyboard input through SendInput. Sends the whole chord in one call so nothing can interleave.</summary>
public sealed class Win32InputBackend : IInputBackend
{
    private const int VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_MENU = 0x12;

    // Keys that need KEYEVENTF_EXTENDEDKEY when sent by scan code.
    private static readonly HashSet<int> ExtendedKeys = new()
    {
        0x21, 0x22, 0x23, 0x24, // PageUp, PageDown, End, Home
        0x25, 0x26, 0x27, 0x28, // arrows
        0x2D, 0x2E,             // Insert, Delete
        0x6F, 0x90,             // Numpad divide, NumLock
    };

    public IntPtr GetForegroundWindow() => Win32.GetForegroundWindow();

    public bool IsWindow(IntPtr hwnd) => Win32.IsWindow(hwnd);

    public bool SendChord(KeyChord chord)
    {
        var mods = new List<int>(3);
        if (chord.Ctrl) mods.Add(VK_CONTROL);
        if (chord.Alt) mods.Add(VK_MENU);
        if (chord.Shift) mods.Add(VK_SHIFT);

        var inputs = new List<INPUT>(8);
        foreach (int m in mods) inputs.Add(Key(m, up: false));
        inputs.Add(Key(chord.VirtualKey, up: false));
        inputs.Add(Key(chord.VirtualKey, up: true));
        for (int i = mods.Count - 1; i >= 0; i--) inputs.Add(Key(mods[i], up: true));

        var array = inputs.ToArray();
        return Win32.SendInput((uint)array.Length, array, System.Runtime.InteropServices.Marshal.SizeOf<INPUT>()) == array.Length;
    }

    // Games commonly read scan codes (raw input / DirectInput) rather than virtual keys, so send those.
    private static INPUT Key(int vk, bool up)
    {
        uint flags = Win32.KEYEVENTF_SCANCODE;
        if (up) flags |= Win32.KEYEVENTF_KEYUP;
        if (ExtendedKeys.Contains(vk)) flags |= Win32.KEYEVENTF_EXTENDEDKEY;

        return new INPUT
        {
            type = Win32.INPUT_KEYBOARD,
            U = new InputUnion
            {
                ki = new KEYBDINPUT
                {
                    wVk = 0,
                    wScan = (ushort)Win32.MapVirtualKey((uint)vk, Win32.MAPVK_VK_TO_VSC),
                    dwFlags = flags,
                },
            },
        };
    }
}
