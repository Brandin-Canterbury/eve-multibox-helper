using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using EveMultibox.Alerts;
using EveMultibox.Clients;
using EveMultibox.Config;
using EveMultibox.Interop;
using EveMultibox.Util;

namespace EveMultibox.Switcher;

/// <summary>
/// Live previews of every EVE client. Clicking one brings that client to the front. It only
/// changes focus and never sends anything to the game.
/// </summary>
public partial class ThumbnailStrip : Window
{
    private static readonly SolidColorBrush InactiveEdge = Visuals.Brush(Color.FromRgb(0x3A, 0x3A, 0x42));
    private static readonly SolidColorBrush HeaderNormal = Visuals.Brush(Color.FromRgb(0xBB, 0xBB, 0xC4));
    private static readonly SolidColorBrush HeaderAlert = Visuals.Brush(Color.FromRgb(0xFF, 0xC8, 0x4D));
    private const string HeaderHint = "EVE clients · click one to switch · drag here to move";

    private sealed class Cell
    {
        public required EveClient Client { get; init; }
        public required Border Outer { get; init; }
        public required Border ThumbArea { get; init; }
        public required Border LabelBar { get; init; }
        public required TextBlock Label { get; init; }
        public IntPtr Thumb { get; set; }
    }

    private readonly ClientTracker _tracker;
    private readonly AppConfig _config;
    private readonly AlertCenter _alerts;
    private readonly List<Cell> _cells = new();
    private IntPtr _hwnd;

    public ThumbnailStrip(ClientTracker tracker, AppConfig config, AlertCenter alerts)
    {
        InitializeComponent();
        _tracker = tracker;
        _config = config;
        _alerts = alerts;
        _alerts.Changed += Restyle;
        Left = config.StripPosition.X;
        Top = config.StripPosition.Y;

        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            Win32.AddExStyle(_hwnd, Win32.WS_EX_NOACTIVATE | Win32.WS_EX_TOOLWINDOW);
            Rebuild();
        };
        _tracker.ClientsChanged += Rebuild;
        _tracker.ActiveClientChanged += Restyle;
        _tracker.Tick += UpdateThumbnails;
        Closed += (_, _) => ClearCells();
    }

    public void Rebuild()
    {
        if (_hwnd == IntPtr.Zero) return;
        ClearCells();

        int width = Math.Clamp(_config.ThumbnailWidth, 120, 640);
        foreach (var client in _tracker.Clients)
        {
            var thumbArea = new Border { Width = width, Height = width * 9 / 16.0, Background = Visuals.Brush(Color.FromRgb(0x0B, 0x0B, 0x0D)) };
            var label = new TextBlock { FontWeight = FontWeights.Bold, FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis };
            var labelBar = new Border { Padding = new Thickness(6, 2, 6, 3), Child = label };
            var stack = new StackPanel();
            stack.Children.Add(thumbArea);
            stack.Children.Add(labelBar);
            var outer = new Border { Margin = new Thickness(3), BorderThickness = new Thickness(3), Child = stack, Cursor = Cursors.Hand };

            var cell = new Cell { Client = client, Outer = outer, ThumbArea = thumbArea, LabelBar = labelBar, Label = label };
            outer.MouseLeftButtonUp += (_, _) => Win32.FocusWindow(cell.Client.Hwnd);

            if (Dwm.DwmRegisterThumbnail(_hwnd, client.Hwnd, out var thumb) == 0) cell.Thumb = thumb;

            _cells.Add(cell);
            Cells.Children.Add(outer);
        }

        EmptyText.Visibility = _cells.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        Restyle();
        // Thumbnail rectangles depend on the new layout, so place them once layout has run.
        Dispatcher.BeginInvoke(UpdateThumbnails, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    public void Restyle()
    {
        foreach (var cell in _cells)
        {
            var (labelText, color, _) = Visuals.For(_config, cell.Client);
            bool active = ReferenceEquals(cell.Client, _tracker.ActiveClient);
            var alert = _alerts.For(cell.Client.CharacterName);

            cell.Label.Text = (alert is not null ? "⚠ " : active ? "▶ " : "") + labelText;
            cell.Outer.BorderBrush = active ? Visuals.Brush(color) : InactiveEdge;
            cell.Outer.ToolTip = alert is not null ? $"{alert.Rule.Name}: {alert.Text}" : cell.Client.Title;

            if (alert is not null)
            {
                // Pulse the label between the client's color and white until you switch to it.
                var pulse = new SolidColorBrush(color);
                pulse.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(color, Colors.White, TimeSpan.FromMilliseconds(450))
                {
                    AutoReverse = true,
                    RepeatBehavior = RepeatBehavior.Forever,
                });
                cell.LabelBar.Background = pulse;
                cell.Label.Foreground = Brushes.Black;
            }
            else
            {
                cell.LabelBar.Background = Visuals.Brush(color);
                cell.Label.Foreground = Visuals.TextOn(color);
            }
        }

        var latest = _alerts.Latest;
        if (latest is null)
        {
            HeaderText.Text = HeaderHint;
            HeaderText.Foreground = HeaderNormal;
        }
        else
        {
            string who = _config.FindProfile(latest.Character)?.DisplayLabel ?? latest.Character;
            string jump = string.IsNullOrWhiteSpace(_config.Alerts.JumpHotkey) ? "" : $" · {_config.Alerts.JumpHotkey} to jump there";
            HeaderText.Text = $"⚠ {who}: {latest.Rule.Name}{jump}";
            HeaderText.Foreground = HeaderAlert;
        }
    }

    private void UpdateThumbnails()
    {
        if (_hwnd == IntPtr.Zero || !IsVisible) return;
        var source = PresentationSource.FromVisual(this);
        if (source?.CompositionTarget is null) return;
        var toDevice = source.CompositionTarget.TransformToDevice;

        foreach (var cell in _cells)
        {
            if (cell.Thumb == IntPtr.Zero || !cell.ThumbArea.IsLoaded) continue;

            var topLeft = toDevice.Transform(cell.ThumbArea.TransformToAncestor(this).Transform(new Point(0, 0)));
            var size = toDevice.Transform(new Point(cell.ThumbArea.ActualWidth, cell.ThumbArea.ActualHeight));
            var dest = FitAspect(cell.Thumb, (int)topLeft.X, (int)topLeft.Y, (int)size.X, (int)size.Y);

            var props = new DWM_THUMBNAIL_PROPERTIES
            {
                dwFlags = Dwm.DWM_TNP_RECTDESTINATION | Dwm.DWM_TNP_VISIBLE | Dwm.DWM_TNP_OPACITY | Dwm.DWM_TNP_SOURCECLIENTAREAONLY,
                rcDestination = dest,
                opacity = 255,
                fVisible = true,
                fSourceClientAreaOnly = true,
            };
            Dwm.DwmUpdateThumbnailProperties(cell.Thumb, ref props);
        }
    }

    // Letterbox the preview so ultrawide or 4:3 clients aren't stretched.
    private static RECT FitAspect(IntPtr thumb, int x, int y, int w, int h)
    {
        if (Dwm.DwmQueryThumbnailSourceSize(thumb, out var src) != 0 || src.cx <= 0 || src.cy <= 0)
            return new RECT(x, y, x + w, y + h);

        double scale = Math.Min((double)w / src.cx, (double)h / src.cy);
        int fw = (int)(src.cx * scale), fh = (int)(src.cy * scale);
        int fx = x + (w - fw) / 2, fy = y + (h - fh) / 2;
        return new RECT(fx, fy, fx + fw, fy + fh);
    }

    private void ClearCells()
    {
        foreach (var cell in _cells)
            if (cell.Thumb != IntPtr.Zero) Dwm.DwmUnregisterThumbnail(cell.Thumb);
        _cells.Clear();
        Cells.Children.Clear();
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        DragMove();
        _config.StripPosition = new WindowSpot { X = Left, Y = Top };
    }
}
