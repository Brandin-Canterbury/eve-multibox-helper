using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace EveMultibox.Trade;

/// <summary>
/// Keeps the last Trade Routes and Sell my items results in a JSON file, so they survive closing
/// the window, restarting the helper, or a crash.
/// </summary>
public sealed class ResultsStore
{
    private sealed record Saved(SearchResults? Routes, SearchResults? Sells);

    private static readonly JsonSerializerOptions Json = new()
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _path;

    public ResultsStore(string path) => _path = path;

    public static string DefaultPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EveMultibox", "trade-results.json");

    public (SearchResults? Routes, SearchResults? Sells) Load()
    {
        try
        {
            if (!File.Exists(_path)) return (null, null);
            var saved = JsonSerializer.Deserialize<Saved>(File.ReadAllText(_path), Json);
            return (saved?.Routes, saved?.Sells);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return (null, null); // A damaged file just means starting fresh.
        }
    }

    public void Save(SearchResults? routes, SearchResults? sells)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            // Write to a temp file first so a crash mid-write can't leave a half-written file.
            File.WriteAllText(_path + ".tmp", JsonSerializer.Serialize(new Saved(routes, sells), Json));
            File.Move(_path + ".tmp", _path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Saving results is a convenience; never fail a search over it.
        }
    }
}
