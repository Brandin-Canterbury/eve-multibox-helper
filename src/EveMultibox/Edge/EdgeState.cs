using EveMultibox.Alerts;
using EveMultibox.Clients;
using EveMultibox.StaticData;
using EveMultibox.Trade;

namespace EveMultibox.Edge;

/// <summary>
/// Everything the helper knows, as plain data for Xeneon Edge Hub's EVE module: the clients and
/// which one is in front, each signed-in character, pending alerts, and market orders and profit.
/// </summary>
public sealed record EdgeState(
    int Version,
    string? ActiveCharacter,
    IReadOnlyList<EdgeClient> Clients,
    IReadOnlyList<EdgeCharacter> Characters,
    IReadOnlyList<EdgeAlert> Alerts,
    IReadOnlyList<EdgeOrder> Orders,
    IReadOnlyList<EdgePnl> Profit,
    DateTimeOffset? OrdersUpdated);

public sealed record EdgeClient(string? Character, bool Active, EdgeAlert? Alert);

public sealed record EdgeCharacter(
    long Id,
    string Name,
    bool? Online,
    bool HasClient,
    bool Active,
    int? SystemId,
    string? SystemName,
    double? Security,
    string? Region,
    bool Docked,
    string Location,
    int? ShipTypeId,
    string? ShipName,
    string? ShipType,
    double CargoCapacity,
    double OreHoldCapacity,
    double? Wallet,
    string? Error,
    EdgeDestination? Destination = null);

/// <summary>Where a character is headed (set through the helper), and how far it is on the route the autopilot will fly.</summary>
public sealed record EdgeDestination(string Name, string? SystemName, double? Security, int? Jumps, int? LowSecSystems, bool Arrived, bool Safer);

public sealed record EdgeAlert(string Character, string Rule, string Text, DateTime At);

public sealed record EdgeOrder(
    long CharacterId,
    string Character,
    long OrderId,
    int TypeId,
    string Item,
    bool IsBuy,
    double Price,
    long Remaining,
    long Total,
    string Station,
    double? Escrow,
    DateTime Expires,
    string Standing,
    bool NeedsAttention);

/// <summary>A character's trading over the last week, from the saved wallet history.</summary>
public sealed record EdgePnl(long CharacterId, string Character, int Days, double Sales, double Purchases, double NetProfit,
    double TradingProfit = 0, double SalesTax = 0, double BrokerFees = 0);

public static class EdgeStateBuilder
{
    public const int Version = 1;

    public static EdgeState Build(
        IReadOnlyList<EveClient> clients,
        EveClient? active,
        IEnumerable<CharacterState> characters,
        IEnumerable<Alert> alerts,
        SdeData? sde,
        Func<long, string> stationName,
        IReadOnlyList<EdgeOrder> orders,
        IReadOnlyList<EdgePnl> profit,
        DateTimeOffset? ordersUpdated,
        Func<long, long?>? destinationOf = null,
        bool saferRoutes = true)
    {
        var graph = sde is not null ? new JumpGraph(sde.Systems) : null;
        var pending = alerts.OrderByDescending(a => a.At).Select(ToEdge).ToList();
        var withClient = clients.Where(c => c.CharacterName is not null)
            .Select(c => c.CharacterName!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var edgeClients = clients.Select(c => new EdgeClient(
            c.CharacterName,
            ReferenceEquals(c, active),
            pending.FirstOrDefault(a => string.Equals(a.Character, c.CharacterName, StringComparison.OrdinalIgnoreCase)))).ToList();

        var edgeCharacters = characters.Select(c =>
        {
            var system = c.SystemId is int id ? sde?.Systems.GetValueOrDefault(id) : null;
            var region = system is not null ? sde?.Regions.GetValueOrDefault(system.RegionId) : null;
            bool docked = c.StationId is not null || c.StructureId is not null;
            string location = c.StationId is long st ? stationName(st)
                : c.StructureId is not null ? "Docked in a structure"
                : c.SystemId is not null ? "In space"
                : "Locating…";
            return new EdgeCharacter(
                c.Id, c.Name, c.Online,
                HasClient: withClient.Contains(c.Name),
                Active: string.Equals(active?.CharacterName, c.Name, StringComparison.OrdinalIgnoreCase),
                c.SystemId, system?.Name, system?.DisplaySecurity, region,
                docked, location,
                c.ShipTypeId, c.ShipName,
                c.ShipTypeId is int t ? sde?.TypeName(t) : null,
                c.CargoCapacity, c.OreHoldCapacity, c.Wallet, c.Error,
                Destination(c, destinationOf?.Invoke(c.Id), sde, graph, stationName, saferRoutes));
        }).ToList();

        return new EdgeState(Version, active?.CharacterName, edgeClients, edgeCharacters, pending, orders, profit, ordersUpdated);
    }

    private static EdgeAlert ToEdge(Alert a) => new(a.Character, a.Rule.Name, a.Text, a.At);

    /// <summary>The destination as a name, a system and the jumps left on the route the autopilot will fly.</summary>
    internal static EdgeDestination? Destination(CharacterState c, long? destinationId, SdeData? sde, JumpGraph? graph,
        Func<long, string> stationName, bool saferRoutes)
    {
        if (destinationId is not long id) return null;
        int? systemId = null;
        string name;
        if (sde?.Stations.TryGetValue(id, out var station) == true)
        {
            systemId = station!.SystemId;
            name = stationName(id);
        }
        else if (id <= int.MaxValue && sde?.Systems.ContainsKey((int)id) == true)
        {
            systemId = (int)id;
            name = sde.SystemName((int)id);
        }
        else
        {
            name = stationName(id); // a structure, or something the static data doesn't know
        }

        var system = systemId is int sid ? sde?.Systems.GetValueOrDefault(sid) : null;
        int? jumps = null, low = null;
        if (graph is not null && c.SystemId is int here && systemId is int there)
        {
            if (saferRoutes && graph.Safer(here, there) is { } route) (jumps, low) = (route.Jumps, route.LowSecSystems);
            else if (!saferRoutes) jumps = graph.Jumps(here, there);
        }
        bool arrived = c.StationId == id || (c.StationId is null && c.StructureId is null && c.SystemId == systemId && systemId == id);
        return new EdgeDestination(name, system?.Name, system?.DisplaySecurity, jumps, low, arrived, saferRoutes);
    }
}
