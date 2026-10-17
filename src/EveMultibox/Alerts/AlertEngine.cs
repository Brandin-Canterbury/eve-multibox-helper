using EveMultibox.Logs;

namespace EveMultibox.Alerts;

public sealed record Alert(AlertRule Rule, string Character, string Text, DateTime At);

/// <summary>Matches log events against rules and applies per-rule, per-character cooldowns.</summary>
public sealed class AlertEngine
{
    private readonly Func<DateTime> _clock;
    private readonly Dictionary<(AlertRule, string), DateTime> _last = new();

    public AlertEngine(Func<DateTime>? clock = null) => _clock = clock ?? (() => DateTime.UtcNow);

    /// <summary>The first matching rule's alert, or null. Lines from logs without a known character are ignored.</summary>
    public Alert? Evaluate(IEnumerable<AlertRule> rules, LogEvent e)
    {
        if (string.IsNullOrWhiteSpace(e.Listener)) return null;
        var now = _clock();

        foreach (var rule in rules)
        {
            if (!rule.Matches(e)) continue;

            var key = (rule, e.Listener.ToLowerInvariant());
            if (_last.TryGetValue(key, out var last) && now - last < TimeSpan.FromSeconds(rule.CooldownSeconds)) return null;
            _last[key] = now;

            string text = e.Source == LogSource.Chat && e.Line.Speaker is not null
                ? $"{e.Channel}: {e.Line.Speaker} > {e.Line.Text}"
                : e.Line.Text;
            return new Alert(rule, e.Listener, text, now);
        }
        return null;
    }
}
