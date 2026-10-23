using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using EveMultibox.Config;
using EveMultibox.Interop;

namespace EveMultibox.Shell;

/// <summary>
/// The ☰ button: the app's main menu. It's locked in place so it can't be nudged by accident;
/// hold Alt and drag to move it. Like the other overlays, it never takes focus from EVE.
/// Its saved position is in real screen pixels, because WPF's own units get unreliable when
/// monitors use different scaling.
/// </summary>
public partial class MenuButtonWindow : Window
{
    private const double EdgeMarginDips = 16;

    private readonly App _app;
    private IntPtr _hwnd;
    private bool _dragged;
    private DateTime _menuClosedAt;

    public MenuButtonWindow(App app)
    {
        InitializeComponent();
        WindowLook.Apply(this);
        _app = app;

        // Start off-screen; the real spot is set in pixels once the window exists.
        Left = -10000;
        Top = -10000;

        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            Win32.AddExStyle(_hwnd, Win32.WS_EX_NOACTIVATE | Win32.WS_EX_TOOLWINDOW);
            HwndSource.FromHwnd(_hwnd).AddHook(Win32.AllowTinyWindowHook);
        };
        // WPF finishes its own sizing and positioning after Loaded, which would undo a move made
        // there, so place the button once the first frame is on screen.
        ContentRendered += (_, _) => PlaceAt(_app.Config.MenuButtonPosition);
    }

    /// <summary>Bottom-right corner of a work area, inset by a margin. All values in pixels.</summary>
    internal static (int X, int Y) DefaultPosition(RECT workArea, int width, int height, int margin) =>
        (workArea.Right - width - margin, workArea.Bottom - height - margin);

    /// <summary>True if the button's center would be on some monitor (they may have changed since it was saved).</summary>
    internal static bool IsOnScreen(WindowSpot spot, int width, int height, RECT virtualScreen)
    {
        double cx = spot.X + width / 2.0, cy = spot.Y + height / 2.0;
        return cx >= virtualScreen.Left && cx < virtualScreen.Right && cy >= virtualScreen.Top && cy < virtualScreen.Bottom;
    }

    private void PlaceAt(WindowSpot? spot)
    {
        if (_hwnd == IntPtr.Zero) return;
        Win32.GetWindowRect(_hwnd, out var r);
        int w = r.Width, h = r.Height;

        int x, y;
        if (spot is not null && IsOnScreen(spot, w, h, Win32.VirtualScreen()))
        {
            (x, y) = ((int)spot.X, (int)spot.Y);
        }
        else
        {
            int margin = (int)Math.Round(EdgeMarginDips * VisualTreeHelper.GetDpi(this).DpiScaleX);
            (x, y) = DefaultPosition(Win32.PrimaryWorkArea(), w, h, margin);
        }

        const uint SWP_NOZORDER = 0x0004;
        Win32.SetWindowPos(_hwnd, IntPtr.Zero, x, y, 0, 0, Win32.SWP_NOSIZE | SWP_NOZORDER | Win32.SWP_NOACTIVATE);
    }

    public void ResetPosition()
    {
        _app.Config.MenuButtonPosition = null;
        PlaceAt(null);
    }

    private void Face_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragged = false;
        if (!Win32.IsAltDown()) return;

        // Alt + drag moves the button. Without Alt it stays put.
        Menu.IsOpen = false;
        DragMove();
        _dragged = true;
        Win32.GetWindowRect(_hwnd, out var r);
        _app.Config.MenuButtonPosition = new WindowSpot { X = r.Left, Y = r.Top };
        _app.SaveQuietly();
        e.Handled = true;
    }

    private void Face_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_dragged) return;
        // Clicking the button while the menu is open closes it on mouse-down; don't reopen it on mouse-up.
        if (DateTime.UtcNow - _menuClosedAt < TimeSpan.FromMilliseconds(250)) return;

        MultiboxerCheck.Text = _app.Config.ShowMultiboxer ? "✓" : "";
        Menu.IsOpen = true;
    }

    private void Menu_Closed(object? sender, EventArgs e) => _menuClosedAt = DateTime.UtcNow;

    private void Face_MouseEnter(object sender, MouseEventArgs e) => Face.Background = (Brush)FindResource("ControlHover");

    private void Face_MouseLeave(object sender, MouseEventArgs e) => Face.Background = (Brush)FindResource("Control");

    private void Multiboxer_Click(object sender, RoutedEventArgs e)
    {
        Menu.IsOpen = false;
        _app.SetMultiboxerVisible(!_app.Config.ShowMultiboxer);
    }

    private void Trade_Click(object sender, RoutedEventArgs e)
    {
        Menu.IsOpen = false;
        _app.ShowTrade();
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        Menu.IsOpen = false;
        _app.ShowGeneralSettings();
    }

    private void Quit_Click(object sender, RoutedEventArgs e)
    {
        Menu.IsOpen = false;
        _app.Quit();
    }
}
