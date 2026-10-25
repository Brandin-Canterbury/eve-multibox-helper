using System.Collections.ObjectModel;
using System.Windows.Threading;
using EveMultibox.Config;
using EveMultibox.Esi;
using EveMultibox.Logs;
using EveMultibox.StaticData;

namespace EveMultibox.Trade;

/// <summary>
/// The trade module's hub: signed-in characters and their live location, ship, wallet and
/// skills. Polls ESI (served from cache until ESI says the data changed) and uses the chat
/// logs as an early hint when a character jumps to a new system.
/// </summary>
public sealed class TradeService
{
    private const string LocalChangedPrefix = "Channel changed to Local : ";
    private static readonly TimeSpan PollEvery = TimeSpan.FromSeconds(15);

    private readonly EsiClient _esi;
    private readonly Dispatcher _ui;
    private readonly DispatcherTimer _poll;
    private readonly Dictionary<long, string> _stationNames = new();
    private bool _refreshing;

    public TradeService(EsiClient esi, EsiAuth auth, SdeStore sde)
    {
        _esi = esi;
        Auth = auth;
        Sde = sde;
        _ui = Dispatcher.CurrentDispatcher;
        _poll = new DispatcherTimer { Interval = PollEvery };
        _poll.Tick += async (_, _) => await RefreshAllAsync();
        // Sign-in finishes on a background thread, but the character list is bound to the UI,
        // so changes to it must happen on the UI thread.
        Auth.CharactersChanged += () =>
        {
            if (_ui.CheckAccess()) SyncCharacters();
            else _ui.BeginInvoke(SyncCharacters);
        };
    }

    public EsiAuth Auth { get; }
    public SdeStore Sde { get; }
    public EsiClient Esi => _esi;

    /// <summary>Each character's trade run being planned in the Trade Routes tab (kept while the helper runs).</summary>
    public TradeRuns Runs { get; } = new();

    /// <summary>The run for the character picked in the Trade Routes tab.</summary>
    public TradeRun? CurrentRun => Runs.Current;

    private readonly ResultsStore _results = new(ResultsStore.DefaultPath);
    private bool _resultsLoaded;
    private SearchResults? _lastRoutes;
    private SearchResults? _lastSells;

    /// <summary>Last Trade Routes results; saved to disk so they survive restarts and crashes.</summary>
    public SearchResults? LastRoutes
    {
        get { LoadResults(); return _lastRoutes; }
        set { LoadResults(); _lastRoutes = value; _results.Save(_lastRoutes, _lastSells); }
    }

    /// <summary>Last Sell my items results; saved to disk so they survive restarts and crashes.</summary>
    public SearchResults? LastSells
    {
        get { LoadResults(); return _lastSells; }
        set { LoadResults(); _lastSells = value; _results.Save(_lastRoutes, _lastSells); }
    }

    private void LoadResults()
    {
        if (_resultsLoaded) return;
        _resultsLoaded = true;
        (_lastRoutes, _lastSells) = _results.Load();
    }

    /// <summary>Adds station names saved with earlier results, so restored rows show names without asking ESI again.</summary>
    public void RememberStationNames(IReadOnlyDictionary<long, string> names)
    {
        foreach (var (id, name) in names) _stationNames.TryAdd(id, name);
    }

    /// <summary>The station names a set of results needs, for saving alongside them.</summary>
    public Dictionary<long, string> StationNamesFor(IEnumerable<long> ids) =>
        ids.Distinct().Where(_stationNames.ContainsKey).ToDictionary(id => id, id => _stationNames[id]);
    public ObservableCollection<CharacterState> Characters { get; } = new();

    /// <summary>Progress and problems worth showing in the trade window's status line.</summary>
    public event Action<string>? Status;

    public async Task StartAsync()
    {
        SyncCharacters();
        if (Characters.Count > 0) await EnsureStaticDataAsync();
        await RefreshAllAsync();
        _poll.Start();
    }

    public void Stop() => _poll.Stop();

    public async Task<SdeData?> EnsureStaticDataAsync()
    {
        try
        {
            return await Sde.EnsureAsync(new Progress<string>(m => Status?.Invoke(m)));
        }
        catch (Exception ex)
        {
            Status?.Invoke("Couldn't load EVE static data: " + ex.Message);
            return null;
        }
    }

    public async Task SignInAsync()
    {
        Status?.Invoke("Opening EVE sign-in in your browser…");
        var character = await Auth.SignInAsync();
        Status?.Invoke($"Signed in as {character.Name}.");
        await EnsureStaticDataAsync();
        await RefreshAllAsync();
    }

    public void SignOut(CharacterState character) => Auth.SignOut(character.Id);

    /// <summary>Sets the in-game autopilot destination (or adds a waypoint) through ESI. This never moves the ship.</summary>
    public async Task SetDestinationAsync(CharacterState character, long destinationId, bool addAsWaypoint)
    {
        string token = await Auth.GetAccessTokenAsync(character.Id);
        string clear = addAsWaypoint ? "false" : "true";
        await _esi.PostAsync($"ui/autopilot/waypoint?add_to_beginning=false&clear_other_waypoints={clear}&destination_id={destinationId}", token);
    }

    private sealed record EsiName(long Id, string Name, string Category);

    /// <summary>Names for many stations at once (ESI /universe/names takes up to 1000 IDs per call).</summary>
    public async Task<IReadOnlyDictionary<long, string>> StationNamesAsync(IEnumerable<long> stationIds)
    {
        var missing = stationIds.Distinct().Where(id => !_stationNames.ContainsKey(id)).ToList();
        foreach (var chunk in missing.Chunk(1000))
        {
            try
            {
                var names = await _esi.PostAsync<long[], List<EsiName>>("universe/names", chunk);
                foreach (var n in names) _stationNames[n.Id] = n.Name;
            }
            catch (EsiException)
            {
                // One unknown ID fails the whole batch; fall back to one at a time.
                foreach (long id in chunk) await StationNameAsync(id);
            }
        }
        return _stationNames;
    }

    public string CachedStationName(long stationId) =>
        _stationNames.TryGetValue(stationId, out var n) ? n : $"Station {stationId}";

    public async Task<string> StationNameAsync(long stationId)
    {
        if (_stationNames.TryGetValue(stationId, out var name)) return name;
        try
        {
            var station = await _esi.GetAsync<EsiStation>($"universe/stations/{stationId}");
            return _stationNames[stationId] = station.Name;
        }
        catch (EsiException)
        {
            return $"Station {stationId}";
        }
    }

    /// <summary>Feeds chat log lines in: a Local channel change means that character just jumped.</summary>
    public void OnLogLine(LogEvent e)
    {
        if (e.Source != LogSource.Chat || e.Line.Speaker != "EVE System" || !e.Line.Text.StartsWith(LocalChangedPrefix)) return;
        var character = Characters.FirstOrDefault(c => string.Equals(c.Name, e.Listener, StringComparison.OrdinalIgnoreCase));
        if (character is null) return;

        string systemName = e.Line.Text[LocalChangedPrefix.Length..].Trim();
        var system = Sde.Data?.SystemByName(systemName);
        character.SystemId = system?.Id ?? character.SystemId;
        character.StationId = null;
        character.LocationText = system is not null ? $"{FormatSystem(system)} · in space" : systemName;
        // ESI's location cache lags a few seconds behind; ask again shortly.
        _ = RefreshLaterAsync(character, TimeSpan.FromSeconds(8));
    }

    private async Task RefreshLaterAsync(CharacterState character, TimeSpan delay)
    {
        await Task.Delay(delay);
        await RefreshAsync(character);
    }

    private void SyncCharacters()
    {
        var signedIn = Auth.Characters;
        foreach (var gone in Characters.Where(c => signedIn.All(s => s.CharacterId != c.Id)).ToList()) Characters.Remove(gone);
        foreach (var s in signedIn.Where(s => Characters.All(c => c.Id != s.CharacterId)))
            Characters.Add(new CharacterState(s.CharacterId, s.Name));
    }

    public async Task RefreshAllAsync()
    {
        if (_refreshing) return;
        _refreshing = true;
        try
        {
            foreach (var c in Characters.ToList()) await RefreshAsync(c);
        }
        finally
        {
            _refreshing = false;
        }
    }

    /// <summary>Re-reads one character now (ESI's cache still applies, so this is cheap to call often).</summary>
    public Task RefreshCharacterAsync(CharacterState c) => RefreshAsync(c);

    private async Task RefreshAsync(CharacterState c)
    {
        try
        {
            string token = await Auth.GetAccessTokenAsync(c.Id);
            var sde = Sde.Data;

            var online = await _esi.GetAsync<EsiOnline>($"characters/{c.Id}/online", token);
            c.Online = online.Online;

            var location = await _esi.GetAsync<EsiLocation>($"characters/{c.Id}/location", token);
            c.SystemId = location.SolarSystemId;
            c.StationId = location.StationId;
            c.StructureId = location.StructureId;
            string where = sde?.Systems.TryGetValue(location.SolarSystemId, out var sys) == true ? FormatSystem(sys!) : $"System {location.SolarSystemId}";
            c.LocationText = location.StationId is long st ? $"{where} · {await StationNameAsync(st)}"
                : location.StructureId is not null ? $"{where} · docked in a structure"
                : $"{where} · in space";

            var ship = await _esi.GetAsync<EsiShip>($"characters/{c.Id}/ship", token);
            c.ShipTypeId = ship.ShipTypeId;
            c.ShipItemId = ship.ShipItemId;
            c.ShipName = ship.ShipName;
            var shipType = sde?.Types.GetValueOrDefault(ship.ShipTypeId);
            c.CargoCapacity = shipType?.Capacity ?? 0;
            c.OreHoldCapacity = shipType?.OreHold ?? 0;
            c.ShipText = $"{ship.ShipName} ({shipType?.Name ?? "ship"}) · {c.CargoCapacity:N0} m³ cargo" +
                         (c.OreHoldCapacity > 0 ? $" · {c.OreHoldCapacity:N0} m³ mining hold" : "");

            c.Wallet = await _esi.GetAsync<double>($"characters/{c.Id}/wallet", token);

            if (sde is not null)
            {
                var skills = await _esi.GetAsync<EsiSkills>($"characters/{c.Id}/skills", token);
                var standings = await _esi.GetAsync<List<EsiStanding>>($"characters/{c.Id}/standings", token);
                c.Skills = SkillProfile.From(
                    skills.Skills.ToDictionary(s => s.SkillId, s => s.ActiveSkillLevel), sde,
                    standings.GroupBy(s => s.FromId).ToDictionary(g => g.Key, g => g.First().Standing));
                c.SkillsText = DescribeSkills(c.Skills, location.StationId is long here ? sde.Stations.GetValueOrDefault(here) : null);
            }
            c.Error = null;
        }
        catch (SignInRequiredException ex)
        {
            c.Error = ex.Message;
        }
        catch (Exception ex) when (ex is EsiException or System.Net.Http.HttpRequestException or TaskCanceledException)
        {
            c.Error = "Couldn't reach ESI: " + ex.Message;
        }
    }

    internal static string FormatSystem(SystemInfo s) => $"{s.Name} ({s.DisplaySecurity:0.0})";

    internal static string DescribeSkills(SkillProfile p, StationInfo? station)
    {
        string broker = station is not null ? $"broker fee here {p.BrokerFeeAt(station):P2}" : $"base broker fee {Fees.BrokerFee(p.BrokerRelations, 0, 0):P2}";
        return $"Sales tax {p.SalesTax:P2} · {broker} · {p.OrderSlots} order slots · " +
               $"remote selling: {SkillProfile.RangeText(p.RemoteSellJumps)}";
    }
}
