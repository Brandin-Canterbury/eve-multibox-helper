using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text.Json;
using EveMultibox.Esi;

namespace EveMultibox.StaticData;

/// <summary>
/// Downloads CCP's official static data export (JSON Lines) once, keeps only what trade needs in a
/// small compressed cache, and re-checks for a new build weekly. Only the first run pays for the
/// ~100 MB download.
/// </summary>
public sealed class SdeStore
{
    public const string LatestUrl = "https://developers.eveonline.com/static-data/tranquility/latest.jsonl";
    public const string ZipUrlFormat = "https://developers.eveonline.com/static-data/tranquility/eve-online-static-data-{0}-jsonl.zip";

    private static readonly TimeSpan RecheckEvery = TimeSpan.FromDays(7);

    private const int GeneralMiningHoldAttribute = 1556;
    private const int AsteroidHoldAttribute = 3227;

    private readonly string _dir;
    private readonly SemaphoreSlim _lock = new(1);

    public SdeStore(string dir)
    {
        _dir = dir;
        Directory.CreateDirectory(_dir);
    }

    public static string DefaultDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EveMultibox", "sde");

    public SdeData? Data { get; private set; }

    // Bump the version when the cached fields change, so old caches are rebuilt.
    private string CacheFile => Path.Combine(_dir, "sde-trade-v2.json.gz");

    public async Task<SdeData> EnsureAsync(IProgress<string>? progress = null, CancellationToken ct = default)
    {
        if (Data is not null) return Data;
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (Data is not null) return Data;

            var cached = File.Exists(CacheFile) ? LoadCache(CacheFile) : null;
            if (cached is not null && DateTime.UtcNow - File.GetLastWriteTimeUtc(CacheFile) < RecheckEvery)
                return Data = cached;

            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(20) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd(EsiClient.UserAgent);

            int latest;
            try
            {
                progress?.Report("Checking for new static data…");
                latest = ParseBuildNumber(await http.GetStringAsync(LatestUrl, ct).ConfigureAwait(false));
            }
            catch (HttpRequestException) when (cached is not null)
            {
                return Data = cached; // Offline: the old data is fine.
            }

            if (cached is not null && cached.BuildNumber == latest)
            {
                File.SetLastWriteTimeUtc(CacheFile, DateTime.UtcNow);
                return Data = cached;
            }

            progress?.Report("Downloading EVE static data (about 100 MB, first time only)…");
            string zipPath = Path.Combine(_dir, $"download-{latest}.zip");
            try
            {
                using (var response = await http.GetAsync(string.Format(ZipUrlFormat, latest), HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
                {
                    response.EnsureSuccessStatusCode();
                    await using var file = File.Create(zipPath);
                    await response.Content.CopyToAsync(file, ct).ConfigureAwait(false);
                }

                progress?.Report("Preparing static data…");
                var data = await Task.Run(() =>
                {
                    using var zip = File.OpenRead(zipPath);
                    return Build(zip);
                }, ct).ConfigureAwait(false);
                SaveCache(CacheFile, data);
                return Data = data;
            }
            finally
            {
                if (File.Exists(zipPath)) File.Delete(zipPath);
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    internal static int ParseBuildNumber(string latestJsonl)
    {
        foreach (var line in latestJsonl.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            using var doc = JsonDocument.Parse(line);
            if (doc.RootElement.GetProperty("_key").GetString() == "sde") return doc.RootElement.GetProperty("buildNumber").GetInt32();
        }
        throw new InvalidDataException("latest.jsonl has no sde record.");
    }

    /// <summary>Reads the parts of the SDE zip that trade needs.</summary>
    internal static SdeData Build(Stream zipStream)
    {
        using var zip = new ZipArchive(zipStream, ZipArchiveMode.Read);

        int build = 0;
        foreach (var e in Lines(zip, "_sde.jsonl"))
            if (e.GetProperty("_key").GetString() == "sde") build = e.GetProperty("buildNumber").GetInt32();

        var groupCategory = new Dictionary<int, int>();
        foreach (var e in Lines(zip, "groups.jsonl"))
            groupCategory[e.GetProperty("_key").GetInt32()] = e.GetProperty("categoryID").GetInt32();

        var types = new Dictionary<int, TypeInfo>();
        foreach (var e in Lines(zip, "types.jsonl"))
        {
            if (!Bool(e, "published")) continue;
            int groupId = e.GetProperty("groupID").GetInt32();
            int category = groupCategory.GetValueOrDefault(groupId);
            int? marketGroup = e.TryGetProperty("marketGroupID", out var mg) ? mg.GetInt32() : null;
            // Things you can trade, plus skills (for skill lookups by name).
            if (marketGroup is null && category != TypeInfo.SkillCategory) continue;

            int id = e.GetProperty("_key").GetInt32();
            types[id] = new TypeInfo(id, English(e, "name"), Num(e, "volume"), Num(e, "packagedVolume"), Num(e, "capacity"),
                groupId, category, marketGroup);
        }

        // Mining hold size lives in each ship's dogma attributes (1556 general mining hold, 3227 asteroid hold).
        if (zip.GetEntry("typeDogma.jsonl") is not null)
        {
            foreach (var e in Lines(zip, "typeDogma.jsonl"))
            {
                int id = e.GetProperty("_key").GetInt32();
                if (!types.TryGetValue(id, out var ship) || ship.CategoryId != TypeInfo.ShipCategory) continue;
                if (!e.TryGetProperty("dogmaAttributes", out var attrs)) continue;
                double oreHold = 0;
                foreach (var a in attrs.EnumerateArray())
                {
                    int attr = a.GetProperty("attributeID").GetInt32();
                    if (attr is GeneralMiningHoldAttribute or AsteroidHoldAttribute) oreHold = Math.Max(oreHold, a.GetProperty("value").GetDouble());
                }
                if (oreHold > 0) types[id] = ship with { OreHold = oreHold };
            }
        }

        var regions = new Dictionary<int, string>();
        foreach (var e in Lines(zip, "mapRegions.jsonl"))
            regions[e.GetProperty("_key").GetInt32()] = English(e, "name");

        var neighbors = new Dictionary<int, HashSet<int>>();
        foreach (var e in Lines(zip, "mapStargates.jsonl"))
        {
            int from = e.GetProperty("solarSystemID").GetInt32();
            int to = e.GetProperty("destination").GetProperty("solarSystemID").GetInt32();
            if (!neighbors.TryGetValue(from, out var set)) neighbors[from] = set = new HashSet<int>();
            set.Add(to);
        }

        var systems = new Dictionary<int, SystemInfo>();
        foreach (var e in Lines(zip, "mapSolarSystems.jsonl"))
        {
            int id = e.GetProperty("_key").GetInt32();
            systems[id] = new SystemInfo(id, English(e, "name"), Num(e, "securityStatus"), e.GetProperty("regionID").GetInt32(),
                neighbors.TryGetValue(id, out var n) ? n.Order().ToArray() : Array.Empty<int>());
        }

        var corpFaction = new Dictionary<int, int>();
        foreach (var e in Lines(zip, "npcCorporations.jsonl"))
            if (e.TryGetProperty("factionID", out var f)) corpFaction[e.GetProperty("_key").GetInt32()] = f.GetInt32();

        var stations = new Dictionary<long, StationInfo>();
        foreach (var e in Lines(zip, "npcStations.jsonl"))
        {
            long id = e.GetProperty("_key").GetInt64();
            int owner = e.GetProperty("ownerID").GetInt32();
            stations[id] = new StationInfo(id, e.GetProperty("solarSystemID").GetInt32(), owner,
                corpFaction.TryGetValue(owner, out int faction) ? faction : null);
        }

        return new SdeData { BuildNumber = build, Types = types, Systems = systems, Stations = stations, Regions = regions };
    }

    private static IEnumerable<JsonElement> Lines(ZipArchive zip, string name)
    {
        var entry = zip.GetEntry(name) ?? throw new InvalidDataException($"Static data is missing {name}.");
        using var reader = new StreamReader(entry.Open());
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (line.Length == 0) continue;
            using var doc = JsonDocument.Parse(line);
            yield return doc.RootElement.Clone();
        }
    }

    private static string English(JsonElement e, string property) =>
        e.TryGetProperty(property, out var p)
            ? p.ValueKind == JsonValueKind.Object ? (p.TryGetProperty("en", out var en) ? en.GetString() ?? "" : "") : p.GetString() ?? ""
            : "";

    private static double Num(JsonElement e, string property) =>
        e.TryGetProperty(property, out var p) && p.ValueKind == JsonValueKind.Number ? p.GetDouble() : 0;

    private static bool Bool(JsonElement e, string property) =>
        e.TryGetProperty(property, out var p) && p.ValueKind == JsonValueKind.True;

    // ---- compact cache ----

    private sealed record CacheDoc(int Build, List<TypeInfo> Types, List<SystemInfo> Systems, List<StationInfo> Stations, Dictionary<int, string> Regions);

    internal static void SaveCache(string path, SdeData data)
    {
        using var file = File.Create(path + ".tmp");
        using (var gz = new GZipStream(file, CompressionLevel.Optimal))
        {
            JsonSerializer.Serialize(gz, new CacheDoc(data.BuildNumber, data.Types.Values.ToList(), data.Systems.Values.ToList(),
                data.Stations.Values.ToList(), data.Regions));
        }
        file.Close();
        File.Move(path + ".tmp", path, overwrite: true);
    }

    internal static SdeData? LoadCache(string path)
    {
        try
        {
            using var file = File.OpenRead(path);
            using var gz = new GZipStream(file, CompressionMode.Decompress);
            var doc = JsonSerializer.Deserialize<CacheDoc>(gz);
            if (doc is null) return null;
            return new SdeData
            {
                BuildNumber = doc.Build,
                Types = doc.Types.ToDictionary(t => t.Id),
                Systems = doc.Systems.ToDictionary(s => s.Id),
                Stations = doc.Stations.ToDictionary(s => s.Id),
                Regions = doc.Regions,
            };
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidDataException)
        {
            return null;
        }
    }
}
