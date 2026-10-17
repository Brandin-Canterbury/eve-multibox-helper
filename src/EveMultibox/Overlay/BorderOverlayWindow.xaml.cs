using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using EveMultibox.Interop;
using EveMultibox.Util;

namespace EveMultibox.Overlay;

/// <summary>
/// A click-through colored frame and name label drawn over one EVE client. It never takes focus
/// or mouse input; every click goes straight through to the game.
/// </summary>
public partial class BorderOverlayWindow : Window
{
    private IntPtr _hwnd;

    public BorderOverlayWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            Win32.AddExStyle(_hwnd, Win32.WS_EX_TRANSPARENT | Win32.WS_EX_LAYERED | Win32.WS_EX_TOOLWINDOW | Win32.WS_EX_NOACTIVATE);
        };
    }

    public void SetAppearance(string label, Color color, bool active)
    {
        var brush = Visuals.Brush(color);
        Frame.BorderBrush = brush;
        Frame.BorderThickness = new Thickness(active ? 5 : 2);
        Frame.Opacity = active ? 1.0 : 0.7;
        LabelPill.Background = brush;
        LabelPill.Opacity = active ? 0.95 : 0.75;
        LabelText.Foreground = Visuals.TextOn(color);
        LabelText.Text = label;
    }

    /// <summary>
    /// Sits the overlay on the client's game area, directly above the client in z-order. It is not
    /// "always on top", so other apps you drag over EVE still cover it normally.
    /// </summary>
    public void Track(IntPtr target)
    {
        if (_hwnd == IntPtr.Zero) return;

        if (!Win32.IsWindow(target) || Win32.IsIconic(target) || !Win32.TryGetClientScreenRect(target, out var r) || r.Width <= 0)
        {
            if (IsVisible) Hide();
            return;
        }
        if (!IsVisible) Show();

        IntPtr above = Win32.GetWindow(target, Win32.GW_HWNDPREV);
        // Skip past always-on-top windows (like our toolbox): a normal window can't sit among them.
        while (above != IntPtr.Zero && above != _hwnd &&
               (Win32.GetWindowLongPtr(above, Win32.GWL_EXSTYLE).ToInt64() & Win32.WS_EX_TOPMOST) != 0)
        {
            above = Win32.GetWindow(above, Win32.GW_HWNDPREV);
        }

        uint flags = Win32.SWP_NOACTIVATE | Win32.SWP_NOOWNERZORDER;
        IntPtr insertAfter = above == IntPtr.Zero ? Win32.HWND_TOP : above;
        if (above == _hwnd)
        {
            const uint SWP_NOZORDER = 0x0004;
            flags |= SWP_NOZORDER;
        }
        Win32.SetWindowPos(_hwnd, insertAfter, r.Left, r.Top, r.Width, r.Height, flags);
    }
}
