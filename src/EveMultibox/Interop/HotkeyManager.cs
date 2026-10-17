using System.Windows.Interop;
using EveMultibox.Toolbox;

namespace EveMultibox.Interop;

/// <summary>System-wide hotkeys, received on a hidden message-only window.</summary>
public sealed class HotkeyManager : IDisposable
{
    private readonly HwndSource _source;
    private readonly Dictionary<int, Action> _actions = new();
    private int _nextId = 1;

    public HotkeyManager()
    {
        _source = new HwndSource(new HwndSourceParameters("EveMultiboxHotkeys") { ParentWindow = Win32.HWND_MESSAGE });
        _source.AddHook(WndProc);
    }

    /// <summary>Registers a hotkey. Returns false if the combination is already taken by another program.</summary>
    public bool Register(KeyChord chord, Action action)
    {
        uint mods = Win32.MOD_NOREPEAT;
        if (chord.Ctrl) mods |= Win32.MOD_CONTROL;
        if (chord.Alt) mods |= Win32.MOD_ALT;
        if (chord.Shift) mods |= Win32.MOD_SHIFT;

        int id = _nextId++;
        if (!Win32.RegisterHotKey(_source.Handle, id, mods, (uint)chord.VirtualKey)) return false;
        _actions[id] = action;
        return true;
    }

    public void UnregisterAll()
    {
        foreach (int id in _actions.Keys) Win32.UnregisterHotKey(_source.Handle, id);
        _actions.Clear();
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == Win32.WM_HOTKEY && _actions.TryGetValue(wParam.ToInt32(), out var action))
        {
            action();
            handled = true;
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        UnregisterAll();
        _source.Dispose();
    }
}
