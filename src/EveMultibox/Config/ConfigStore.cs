using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace EveMultibox.Config;

public sealed class ConfigStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    public string Path { get; }

    public ConfigStore(string path) => Path = path;

    public static string DefaultPath =>
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "EveMultibox", "config.json");

    /// <summary>Loads the config. A missing file gives defaults; an unreadable one is set aside as .bad.</summary>
    public AppConfig Load()
    {
        if (!File.Exists(Path)) return AppConfig.CreateDefault();
        try
        {
            return JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(Path), JsonOptions) ?? AppConfig.CreateDefault();
        }
        catch (JsonException)
        {
            File.Copy(Path, Path + ".bad", overwrite: true);
            return AppConfig.CreateDefault();
        }
    }

    public void Save(AppConfig config)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        // Write to a temp file first so a crash mid-write can't leave a half-written config.
        string tmp = Path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(config, JsonOptions));
        File.Move(tmp, Path, overwrite: true);
    }
}
