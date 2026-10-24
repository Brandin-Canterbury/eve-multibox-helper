using EveMultibox.Config;

namespace EveMultibox.Widget;

/// <summary>Where a character's action widget opens: its own saved spot, or the next free spot in a cascade.</summary>
public static class WidgetPlacement
{
    public const double Step = 36;

    /// <param name="open">Top-left corners of the widgets already on screen.</param>
    /// <param name="workRight">Right edge of the work area (for the default spot).</param>
    /// <param name="workTop">Top edge of the work area (for the default spot).</param>
    public static WindowSpot For(WidgetSettings settings, long characterId, IReadOnlyCollection<WindowSpot> open,
        double width, double workRight, double workTop)
    {
        if (settings.Positions.TryGetValue(characterId, out var own)) return new WindowSpot { X = own.X, Y = own.Y };

        var start = settings.Position ?? new WindowSpot { X = workRight - width - 80, Y = workTop + 120 };
        var spot = new WindowSpot { X = start.X, Y = start.Y };
        // Step down-left past any widget already there, so a new one never hides another.
        for (int i = 0; i < 20 && open.Any(o => Math.Abs(o.X - spot.X) < Step / 2 && Math.Abs(o.Y - spot.Y) < Step / 2); i++)
        {
            spot.X -= Step;
            spot.Y += Step;
        }
        return spot;
    }
}
