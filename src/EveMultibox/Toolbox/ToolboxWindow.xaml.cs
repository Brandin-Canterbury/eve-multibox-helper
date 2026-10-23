using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using EveMultibox.Clients;
using EveMultibox.Config;
using EveMultibox.Interop;
using EveMultibox.Util;

namespace EveMultibox.Toolbox;

/// <summary>
/// Favorites buttons for common EVE shortcuts. The window never takes focus, so the EVE client you
/// clicked stays in the foreground, and each button press sends exactly one shortcut to it.
/// </summary>
public partial class ToolboxWindow : Window
{
    private static readonly SolidColorBrush NormalStatus = Visuals.Brush(Color.FromRgb(0x26, 0x26, 0x2C));
    private static readonly SolidColorBrush WarnStatus = Visuals.Brush(Color.FromRgb(0xB0, 0x30, 0x30));

    private readonly ClientTracker _tracker;
    private readonly AppConfig _config;
    private readonly KeySender _sender;

    public ToolboxWindow(ClientTracker tracker, AppConfig config, KeySender sender)
    {
        InitializeComponent();
        Shell.WindowLook.Apply(this);
        _tracker = tracker;
        _config = config;
        _sender = sender;
        Left = config.ToolboxPosition.X;
        Top = config.ToolboxPosition.Y;

        SourceInitialized += (_, _) =>
            Win32.AddExStyle(new WindowInteropHelper(this).Handle, Win32.WS_EX_NOACTIVATE | Win32.WS_EX_TOOLWINDOW);
        _tracker.ActiveClientChanged += Refresh;
        _tracker.ClientsChanged += Refresh;
        Refresh();
    }

    /// <summary>Rebuilds the header and buttons for the current target. Call after settings change.</summary>
    public void Refresh()
    {
        var target = _tracker.ActiveClient;
        var (label, color, _) = Visuals.For(_config, target);
        Header.Background = Visuals.Brush(color);
        var fg = Visuals.TextOn(color);
        HeaderCaption.Foreground = HeaderTarget.Foreground = HeaderCharacter.Foreground = fg;

        if (target is null)
        {
            HeaderTarget.Text = "No client yet";
            HeaderCharacter.Text = "Click into an EVE window first";
        }
        else
        {
            HeaderTarget.Text = label;
            HeaderCharacter.Text = target.CharacterName ?? "(not logged in)";
        }

        Buttons.Children.Clear();
        foreach (var fav in _config.FavoritesFor(target?.CharacterName))
        {
            bool valid = fav.TryGetChord(out var chord);
            var name = new TextBlock { Text = fav.Name, FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis };
            var keys = new TextBlock { Text = valid ? chord!.ToString() : "invalid keys", FontSize = 11, Opacity = 0.6, Margin = new Thickness(10, 0, 0, 0) };
            var row = new DockPanel();
            DockPanel.SetDock(keys, Dock.Right);
            row.Children.Add(keys);
            row.Children.Add(name);

            var button = new Button { Content = row, Style = (Style)Resources["FavButton"], IsEnabled = valid && target is not null };
            if (valid) button.Click += (_, _) => OnFavorite(fav, chord!);
            Buttons.Children.Add(button);
        }

        if (Buttons.Children.Count == 0)
            Buttons.Children.Add(new TextBlock { Text = "No favorites yet. Add some in Settings.", Foreground = Brushes.Gray, Margin = new Thickness(8) });
    }

    private void OnFavorite(FavoriteCommand fav, KeyChord chord)
    {
        var target = _tracker.ActiveClient;
        var result = _sender.TrySend(target?.Hwnd ?? IntPtr.Zero, fav.Id, chord);
        string label = Visuals.For(_config, target).Label;

        switch (result)
        {
            case SendResult.Sent:
                ShowStatus($"Sent {chord} ({fav.Name}) to {label}.", warn: false);
                break;
            case SendResult.CoolingDown:
                break; // Accidental double-click: ignore quietly.
            case SendResult.TargetNotForeground:
                ShowStatus($"Nothing sent. {label} isn't the active window. Click into it, then press the button again.", warn: true);
                break;
            case SendResult.NoTarget:
            case SendResult.TargetGone:
                ShowStatus("Nothing sent. Click into an EVE client first.", warn: true);
                break;
            case SendResult.SendFailed:
                ShowStatus("Windows blocked the keypress. If EVE runs as administrator, run this tool as administrator too.", warn: true);
                break;
        }
    }

    private void ShowStatus(string text, bool warn)
    {
        StatusText.Text = text;
        StatusText.Foreground = warn ? Brushes.White : Visuals.Brush(Color.FromRgb(0xAA, 0xAA, 0xAA));
        if (!warn)
        {
            StatusBar.Background = NormalStatus;
            return;
        }

        // Flash the status bar so a refused click is hard to miss.
        var flash = new ColorAnimation(WarnStatus.Color, NormalStatus.Color, TimeSpan.FromMilliseconds(1600))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn },
        };
        var brush = new SolidColorBrush(WarnStatus.Color);
        StatusBar.Background = brush;
        brush.BeginAnimation(SolidColorBrush.ColorProperty, flash);
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        DragMove();
        _config.ToolboxPosition = new WindowSpot { X = Left, Y = Top };
        ((App)Application.Current).SaveQuietly();
    }
}
