using System.IO;
using System.Text;

namespace EveMultibox.Logs;

/// <summary>Follows one log file, returning only lines that were completely written since the last read.</summary>
public sealed class LogTailer
{
    private Encoding? _encoding;
    private long _position;

    public LogTailer(string path, LogSource source, bool startAtEnd)
    {
        Path = path;
        Source = source;
        Header = LogHeader.Read(path) ?? new LogHeader(null, null);
        if (startAtEnd)
        {
            try
            {
                _position = new FileInfo(path).Length;
            }
            catch (IOException)
            {
                _position = 0;
            }
        }
    }

    public string Path { get; }
    public LogSource Source { get; }
    public LogHeader Header { get; private set; }

    public List<LogLine> ReadNew()
    {
        var lines = new List<LogLine>();
        try
        {
            using var fs = LogFiles.OpenShared(Path);
            if (fs.Length < _position) _position = 0; // File was truncated or replaced.
            if (fs.Length == _position) return lines;

            if (_encoding is null)
            {
                var head = new byte[4];
                int h = fs.Read(head, 0, head.Length);
                _encoding = LogFiles.DetectEncoding(head, h);
            }
            if (Header.Listener is null) Header = LogHeader.Read(Path) ?? Header;

            fs.Position = _position;
            var bytes = new byte[fs.Length - _position];
            int read = fs.Read(bytes, 0, bytes.Length);
            string text = _encoding.GetString(bytes, 0, read);

            // Only consume up to the last newline; a half-written line is read next time.
            int lastNewline = text.LastIndexOf('\n');
            if (lastNewline < 0) return lines;
            string complete = text[..(lastNewline + 1)];
            _position += _encoding.GetByteCount(complete);

            foreach (var raw in complete.Split('\n'))
            {
                var line = LogLine.Parse(raw, Source);
                if (line is not null) lines.Add(line);
            }
        }
        catch (IOException)
        {
            // EVE may briefly lock or rotate the file; try again on the next poll.
        }
        catch (UnauthorizedAccessException)
        {
        }
        return lines;
    }
}
