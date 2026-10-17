using EveMultibox.Clients;
using EveMultibox.Config;
using EveMultibox.Util;

namespace EveMultibox.Overlay;

/// <summary>Keeps one border overlay per EVE client in sync with the tracker.</summary>
public sealed class OverlayManager : IDisposable
{
    private readonly ClientTracker _tracker;
    private readonly AppConfig _config;
    private readonly Dictionary<IntPtr, BorderOverlayWindow> _overlays = new();
    private bool _enabled;

    public OverlayManager(ClientTracker tracker, AppConfig config)
    {
        _tracker = tracker;
        _config = config;
        _tracker.ClientsChanged += Sync;
        _tracker.ActiveClientChanged += Restyle;
        _tracker.Tick += Follow;
    }

    public void SetEnabled(bool enabled)
    {
        _enabled = enabled;
        Sync();
    }

    /// <summary>Call after labels or colors change in settings.</summary>
    public void Restyle()
    {
        foreach (var client in _tracker.Clients)
        {
            if (!_overlays.TryGetValue(client.Hwnd, out var overlay)) continue;
            var (label, color, _) = Visuals.For(_config, client);
            overlay.SetAppearance(label, color, ReferenceEquals(client, _tracker.ActiveClient));
        }
    }

    private void Sync()
    {
        var wanted = _enabled ? _tracker.Clients.Select(c => c.Hwnd).ToHashSet() : new HashSet<IntPtr>();
        foreach (var hwnd in _overlays.Keys.Where(h => !wanted.Contains(h)).ToList())
        {
            _overlays[hwnd].Close();
            _overlays.Remove(hwnd);
        }
        foreach (var hwnd in wanted.Where(h => !_overlays.ContainsKey(h)))
        {
            var overlay = new BorderOverlayWindow();
            overlay.Show();
            _overlays[hwnd] = overlay;
        }
        Restyle();
        Follow();
    }

    private void Follow()
    {
        foreach (var (hwnd, overlay) in _overlays) overlay.Track(hwnd);
    }

    public void Dispose()
    {
        foreach (var o in _overlays.Values) o.Close();
        _overlays.Clear();
    }
}
