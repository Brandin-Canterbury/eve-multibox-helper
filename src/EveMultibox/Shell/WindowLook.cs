using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Shell;

namespace EveMultibox.Shell;

/// <summary>
/// Makes helper windows look the same everywhere: square corners (Windows 11 rounds them unless
/// asked not to), a dark frame wherever Windows still draws one, and for resizable borderless
/// windows no light strip of the standard window frame along the top.
/// </summary>
public static class WindowLook
{
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWCP_DONOTROUND = 1;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    /// <summary>Call from a window's constructor.</summary>
    /// <param name="resizable">Borderless windows that can be resized get a plain 6 px resize edge instead of the Windows frame.</param>
    public static void Apply(Window window, bool resizable = false)
    {
        if (resizable)
        {
            WindowChrome.SetWindowChrome(window, new WindowChrome
            {
                CaptionHeight = 0,
                ResizeBorderThickness = new Thickness(6),
                GlassFrameThickness = new Thickness(0),
                CornerRadius = new CornerRadius(0),
                UseAeroCaptionButtons = false,
            });
        }
        window.SourceInitialized += (_, _) => Square(new WindowInteropHelper(window).Handle);
    }

    /// <summary>Square corners and a dark frame. Older Windows versions simply ignore these settings.</summary>
    public static void Square(IntPtr hwnd)
    {
        int corners = DWMWCP_DONOTROUND;
        DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref corners, sizeof(int));
        int dark = 1;
        DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
    }
}
