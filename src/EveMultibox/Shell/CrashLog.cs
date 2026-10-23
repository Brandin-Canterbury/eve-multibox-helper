using System.IO;

namespace EveMultibox.Shell;

/// <summary>Writes unexpected errors to %LocalAppData%\EveMultibox\errors.log so they can be diagnosed.</summary>
public static class CrashLog
{
    private const long MaxBytes = 1_000_000;
    private static readonly object Gate = new();

    public static string PathName =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EveMultibox", "errors.log");

    public static void Write(Exception ex, string where)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(PathName)!);
                // Keep the log small: start over once it passes 1 MB.
                if (File.Exists(PathName) && new FileInfo(PathName).Length > MaxBytes) File.Delete(PathName);
                File.AppendAllText(PathName, $"==== {DateTime.Now:yyyy-MM-dd HH:mm:ss} ({where})\n{ex}\n\n");
            }
        }
        catch (IOException)
        {
            // Never let logging itself take the app down.
        }
    }
}
