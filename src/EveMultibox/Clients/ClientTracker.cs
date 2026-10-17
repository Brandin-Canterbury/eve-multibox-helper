using System.Diagnostics;
using System.Windows.Threading;
using EveMultibox.Config;
using EveMultibox.Interop;

namespace EveMultibox.Clients;

/// <summary>
/// Finds EVE client windows and keeps track of which one the user is working in. Polling is cheap
/// here (one EnumWindows per second, a foreground check every 100 ms) and avoids hook edge cases.
/// </summary>
public sealed class ClientTracker : IDisposable
{
    private const int TickMs = 100;
    private const int ScanEveryTicks = 10;

    private readonly DispatcherTimer _timer;
    private readonly Dictionary<uint, string> _processNames = new();
    private List<EveClient> _clients = new();
    private DetectionSettings _detection;
    private TitleParser _parser;
    private HashSet<string> _processFilter;
    private int _tick;

    public ClientTracker(DetectionSettings detection)
    {
        _detection = detection;
        _parser = new TitleParser(detection.TitleRegex, detection.IncludeLoginScreens);
        _processFilter = ToFilter(detection);
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(TickMs) };
        _timer.Tick += (_, _) => OnTick();
    }

    public IReadOnlyList<EveClient> Clients => _clients;

    /// <summary>The EVE client that most recently had focus. The toolbox only ever sends to this one.</summary>
    public EveClient? ActiveClient { get; private set; }

    /// <summary>Clients appeared, disappeared, or a title changed (e.g. login → character).</summary>
    public event Action? ClientsChanged;

    public event Action? ActiveClientChanged;

    /// <summary>Raised every tick so overlays can follow window moves.</summary>
    public event Action? Tick;

    public void Start()
    {
        Scan();
        UpdateActive();
        _timer.Start();
    }

    public void ApplyDetection(DetectionSettings detection)
    {
        _detection = detection;
        _parser = new TitleParser(detection.TitleRegex, detection.IncludeLoginScreens);
        _processFilter = ToFilter(detection);
        Scan();
        UpdateActive();
    }

    public EveClient? FindByHwnd(IntPtr hwnd) => _clients.FirstOrDefault(c => c.Hwnd == hwnd);

    public EveClient? FindByCharacter(string name) =>
        _clients.FirstOrDefault(c => string.Equals(c.CharacterName, name, StringComparison.OrdinalIgnoreCase));

    private static HashSet<string> ToFilter(DetectionSettings d) =>
        d.ProcessNames.Select(p => p.Trim().Replace(".exe", "", StringComparison.OrdinalIgnoreCase))
            .Where(p => p.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private void OnTick()
    {
        if (++_tick % ScanEveryTicks == 0) Scan();
        UpdateActive();
        Tick?.Invoke();
    }

    private void UpdateActive()
    {
        var fg = FindByHwnd(Win32.GetForegroundWindow());
        var next = fg ?? (ActiveClient is not null && _clients.Contains(ActiveClient) ? ActiveClient : null);
        if (!ReferenceEquals(next, ActiveClient))
        {
            ActiveClient = next;
            ActiveClientChanged?.Invoke();
        }
    }

    private void Scan()
    {
        var found = new List<(IntPtr Hwnd, uint Pid, string Title, ParsedTitle Parsed)>();
        var seenPids = new HashSet<uint>();
        Win32.EnumWindows((hwnd, _) =>
        {
            if (!Win32.IsWindowVisible(hwnd)) return true;
            Win32.GetWindowThreadProcessId(hwnd, out uint pid);
            seenPids.Add(pid);
            if (!_processFilter.Contains(ProcessName(pid))) return true;

            string title = Win32.GetWindowTitle(hwnd);
            var parsed = _parser.Parse(title);
            if (parsed.IsClient) found.Add((hwnd, pid, title, parsed));
            return true;
        }, IntPtr.Zero);

        bool changed = found.Count != _clients.Count;
        var next = new List<EveClient>(found.Count);
        foreach (var f in found)
        {
            var client = _clients.FirstOrDefault(c => c.Hwnd == f.Hwnd) ?? new EveClient(f.Hwnd, f.Pid);
            if (!_clients.Contains(client) || client.Title != f.Title) changed = true;
            client.Title = f.Title;
            client.CharacterName = f.Parsed.CharacterName;
            next.Add(client);
        }

        // Stable order: named characters alphabetically, then login screens.
        next.Sort((a, b) => (a.CharacterName is null, b.CharacterName is null) switch
        {
            (false, true) => -1,
            (true, false) => 1,
            _ => string.Compare(a.CharacterName, b.CharacterName, StringComparison.OrdinalIgnoreCase) is var c && c != 0
                ? c
                : a.ProcessId.CompareTo(b.ProcessId),
        });

        _clients = next;
        // Forget processes that have no windows any more, so a recycled PID gets looked up again.
        foreach (var stale in _processNames.Keys.Where(p => !seenPids.Contains(p)).ToList())
            _processNames.Remove(stale);

        if (changed) ClientsChanged?.Invoke();
    }

    private string ProcessName(uint pid)
    {
        if (_processNames.TryGetValue(pid, out var name)) return name;
        try
        {
            using var p = Process.GetProcessById((int)pid);
            name = p.ProcessName;
        }
        catch (ArgumentException)
        {
            name = "";
        }
        catch (InvalidOperationException)
        {
            name = "";
        }
        _processNames[pid] = name;
        return name;
    }

    public void Dispose() => _timer.Stop();
}
