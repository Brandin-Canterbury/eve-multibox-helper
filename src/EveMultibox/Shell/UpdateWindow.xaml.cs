using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Input;

namespace EveMultibox.Shell;

/// <summary>Offers a newer release: Update &amp; restart, Later, or Skip this version.</summary>
public partial class UpdateWindow : Window
{
    private readonly App _app;
    private readonly UpdateInfo _update;
    private readonly Updater _updater = new();
    private bool _busy;

    public UpdateWindow(App app, UpdateInfo update)
    {
        InitializeComponent();
        WindowLook.Apply(this);
        _app = app;
        _update = update;

        VersionText.Text = $"Version {update.Version} is available";
        CurrentText.Text = $"You have {Updater.CurrentVersion}. Download size {update.Size / 1048576.0:N0} MB.";
        NotesBox.Text = string.IsNullOrWhiteSpace(update.Notes) ? "No release notes." : update.Notes.Trim();
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && !_busy) Close();
        };
    }

    private async void Update_Click(object sender, RoutedEventArgs e)
    {
        _busy = true;
        UpdateButton.IsEnabled = LaterButton.IsEnabled = SkipButton.IsEnabled = CloseButton.IsEnabled = false;
        Progress.Visibility = Visibility.Visible;
        StatusText.Foreground = (System.Windows.Media.Brush)FindResource("TextDim");
        try
        {
            StatusText.Text = "Downloading…";
            string file = await _updater.DownloadAsync(_update, new Progress<double>(p =>
            {
                Progress.Value = p;
                StatusText.Text = $"Downloading… {p:P0}";
            }));

            StatusText.Text = "Verified. Installing and restarting…";
            Updater.InstallAndRelaunch(file);
            _app.Quit();
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or UnauthorizedAccessException
                                       or InvalidOperationException or TaskCanceledException or System.ComponentModel.Win32Exception)
        {
            _busy = false;
            Progress.Visibility = Visibility.Collapsed;
            StatusText.Foreground = (System.Windows.Media.Brush)FindResource("Negative");
            StatusText.Text = "The update didn't install: " + ex.Message + " The helper is unchanged.";
            UpdateButton.IsEnabled = LaterButton.IsEnabled = SkipButton.IsEnabled = CloseButton.IsEnabled = true;
            UpdateButton.Content = "Try again";
        }
    }

    private void Skip_Click(object sender, RoutedEventArgs e)
    {
        _app.Config.Updates.SkippedVersion = _update.Version.ToString();
        _app.SaveQuietly();
        Close();
    }

    private void Later_Click(object sender, RoutedEventArgs e)
    {
        if (!_busy) Close();
    }

    private void PageLink_Click(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo(_update.PageUrl) { UseShellExecute = true });

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => DragMove();
}
