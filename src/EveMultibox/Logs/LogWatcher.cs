using System.IO;
using System.Windows.Threading;

namespace EveMultibox.Logs;

public sealed record LogEvent(LogSource Source, string? Listener, string? Channel, LogLine Line);

/// <summary>
/// Polls the EVE Gamelogs and Chatlogs folders and reports new lines. Files that already exist at
/// start-up are followed from their end, so old history never raises alerts.
/// </summary>
public sealed class LogWatcher : IDisposable
{
    private static readonly TimeSpan ActiveWindow = TimeSpan.FromHours(12);

    private readonly DispatcherTimer _timer;
    private readonly Dictionary<string, LogTailer> _tailers = new(StringComparer.OrdinalIgnoreCase);
    private string _root;
    private bool _primed;
    private int _tick;

    public LogWatcher(string logsRoot)
    {
        _root = logsRoot;
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(500) };
        _timer.Tick += (_, _) => Poll();
    }

    public event Action<LogEvent>? LineAdded;

    public bool IsRunning => _timer.IsEnabled;

    public void Start(string logsRoot)
    {
        if (!string.Equals(logsRoot, _root, StringComparison.OrdinalIgnoreCase))
        {
            _tailers.Clear();
            _primed = false;
            _root = logsRoot;
        }
        if (!_primed) Discover();
        _timer.Start();
    }

    public void Stop() => _timer.Stop();

    private void Poll()
    {
        // New log files appear when a character logs in or joins a channel; look for them every 2 s.
        if (++_tick % 4 == 0) Discover();
        foreach (var tailer in _tailers.Values.ToList())
        {
            foreach (var line in tailer.ReadNew())
                LineAdded?.Invoke(new LogEvent(tailer.Source, tailer.Header.Listener, tailer.Header.ChannelName, line));
        }
    }

    private void Discover()
    {
        var cutoff = DateTime.UtcNow - ActiveWindow;
        foreach (var (sub, source) in new[] { ("Gamelogs", LogSource.Game), ("Chatlogs", LogSource.Chat) })
        {
            string dir = Path.Combine(_root, sub);
            if (!Directory.Exists(dir)) continue;
            foreach (var file in new DirectoryInfo(dir).EnumerateFiles("*.txt"))
            {
                // Windows can lag on updating write times for files EVE keeps open, so also use creation time.
                var touched = file.LastWriteTimeUtc > file.CreationTimeUtc ? file.LastWriteTimeUtc : file.CreationTimeUtc;
                if (_tailers.ContainsKey(file.FullName) || touched < cutoff) continue;
                // On the first pass, skip existing content. Files that appear later are read from the start.
                _tailers[file.FullName] = new LogTailer(file.FullName, source, startAtEnd: !_primed);
            }
        }

        foreach (var stale in _tailers.Keys.Where(p => !File.Exists(p)).ToList()) _tailers.Remove(stale);
        _primed = true;
    }

    public void Dispose() => _timer.Stop();
}
