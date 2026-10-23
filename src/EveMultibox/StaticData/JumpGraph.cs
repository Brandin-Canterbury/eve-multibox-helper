namespace EveMultibox.StaticData;

/// <summary>Jump distances over the stargate network, computed locally (no ESI calls).</summary>
public sealed class JumpGraph
{
    private readonly IReadOnlyDictionary<int, SystemInfo> _systems;

    public JumpGraph(IReadOnlyDictionary<int, SystemInfo> systems) => _systems = systems;

    /// <summary>
    /// Every system within <paramref name="maxJumps"/> of <paramref name="origin"/>, with its distance.
    /// With <paramref name="minSecurity"/>, routes never pass through systems below it (the origin itself is always allowed).
    /// </summary>
    public Dictionary<int, int> Within(int origin, int maxJumps, double? minSecurity = null)
    {
        var dist = new Dictionary<int, int> { [origin] = 0 };
        if (!_systems.ContainsKey(origin)) return dist;

        var queue = new Queue<int>();
        queue.Enqueue(origin);
        while (queue.Count > 0)
        {
            int current = queue.Dequeue();
            int d = dist[current];
            if (d >= maxJumps) continue;

            foreach (int next in _systems[current].Neighbors)
            {
                if (dist.ContainsKey(next) || !_systems.TryGetValue(next, out var sys)) continue;
                if (minSecurity is double min && sys.DisplaySecurity < min) continue;
                dist[next] = d + 1;
                queue.Enqueue(next);
            }
        }
        return dist;
    }

    /// <summary>Jumps on the shortest route, or null if there's none (e.g. wormhole space or blocked by the security filter).</summary>
    public int? Jumps(int from, int to, double? minSecurity = null)
    {
        if (from == to) return 0;
        var dist = Within(from, int.MaxValue, minSecurity);
        return dist.TryGetValue(to, out int d) ? d : null;
    }
}
