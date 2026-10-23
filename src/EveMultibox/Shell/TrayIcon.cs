using System.Diagnostics;
using System.IO;
using Forms = System.Windows.Forms;
using Drawing = System.Drawing;

namespace EveMultibox.Shell;

/// <summary>
/// The helper's system tray icon, using the EVE client's own icon. It's always there, even when
/// everything else is auto-hidden because no EVE client is running.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private readonly Forms.NotifyIcon _icon;
    private readonly Forms.ToolStripMenuItem _multiboxer;

    public TrayIcon(App app)
    {
        var menu = new Forms.ContextMenuStrip();
        _multiboxer = new Forms.ToolStripMenuItem("Multiboxer (clients window)") { CheckOnClick = false };
        _multiboxer.Click += (_, _) => app.SetMultiboxerVisible(!app.Config.ShowMultiboxer);
        menu.Items.Add(_multiboxer);
        menu.Items.Add("Multiboxer settings", null, (_, _) => app.OpenMultiboxerSettings());
        menu.Items.Add("Trade", null, (_, _) => app.ShowTrade());
        menu.Items.Add("Notifications", null, (_, _) => app.ShowNotificationsWindow());
        menu.Items.Add("Settings", null, (_, _) => app.ShowGeneralSettings());
        var updates = new Forms.ToolStripMenuItem("Check for updates");
        updates.Click += async (_, _) =>
        {
            if (app.PendingUpdate is not null) app.ShowUpdateWindow();
            else await app.CheckForUpdatesAsync(manual: true);
        };
        menu.Items.Add(updates);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Quit", null, (_, _) => app.Quit());
        menu.Opening += (_, _) =>
        {
            _multiboxer.Checked = app.Config.ShowMultiboxer;
            _multiboxer.Enabled = app.MultiboxerAvailable;
            _multiboxer.ToolTipText = app.MultiboxerAvailable ? "" : "Needs two or more EVE clients running.";
            updates.Text = app.PendingUpdate is { } u ? $"Update to {u.Version}…" : "Check for updates";
        };

        _icon = new Forms.NotifyIcon
        {
            Icon = LoadEveIcon() ?? Drawing.SystemIcons.Application,
            Text = "Phoenix's Eve Helper",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.DoubleClick += (_, _) => app.ShowTrade();
        _icon.BalloonTipClicked += (_, _) => _balloonClick?.Invoke();
    }

    private Action? _balloonClick;

    /// <summary>A standard Windows notification from the tray icon; clicking it runs <paramref name="onClick"/>.</summary>
    public void Notify(string text, Action? onClick = null, string? title = null)
    {
        _balloonClick = onClick;
        // Windows limits notification text; keep it readable rather than cut mid-word by the system.
        if (text.Length > 250) text = text[..247] + "…";
        _icon.ShowBalloonTip(10_000, title ?? "Phoenix's Eve Helper", text, Forms.ToolTipIcon.Info);
    }

    /// <summary>Tooltip text, e.g. "Phoenix's Eve Helper · 2 clients". Windows caps it at 63 characters.</summary>
    public void SetStatus(string text) => _icon.Text = text.Length > 63 ? text[..63] : text;

    private static string CachedIconPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EveMultibox", "eve.ico");

    /// <summary>
    /// Takes the icon from a running EVE client (exefile.exe) and keeps a copy, so it's still
    /// available next time when EVE isn't running.
    /// </summary>
    public static Drawing.Icon? LoadEveIcon()
    {
        try
        {
            foreach (var p in Process.GetProcessesByName("exefile"))
            {
                using (p)
                {
                    string? exe = p.MainModule?.FileName;
                    if (exe is null || Drawing.Icon.ExtractAssociatedIcon(exe) is not { } icon) continue;
                    Directory.CreateDirectory(Path.GetDirectoryName(CachedIconPath)!);
                    using (var file = File.Create(CachedIconPath)) icon.Save(file);
                    return icon;
                }
            }
            if (File.Exists(CachedIconPath)) return new Drawing.Icon(CachedIconPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException or ArgumentException)
        {
            // No icon is no problem: the default application icon is used instead.
        }
        return null;
    }

    /// <summary>Retry grabbing the EVE icon (e.g. once a client has started).</summary>
    public void RefreshIcon()
    {
        if (LoadEveIcon() is { } icon) _icon.Icon = icon;
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
