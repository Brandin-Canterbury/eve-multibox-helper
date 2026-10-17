using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using EveMultibox.Clients;
using EveMultibox.Config;

namespace EveMultibox.Util;

public static class Visuals
{
    public static readonly Color LoginColor = Color.FromRgb(0x90, 0x90, 0x90);

    public static Color ParseColor(string? hex, Color fallback)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(hex) && ColorConverter.ConvertFromString(hex.Trim()) is Color c) return c;
        }
        catch (FormatException)
        {
        }
        return fallback;
    }

    public static SolidColorBrush Brush(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    /// <summary>Black or white, whichever reads better on the given background.</summary>
    public static SolidColorBrush TextOn(Color bg) =>
        (0.299 * bg.R + 0.587 * bg.G + 0.114 * bg.B) > 150 ? Brushes.Black : Brushes.White;

    /// <summary>Label and color for a client. Creates a profile the first time a character is seen.</summary>
    public static (string Label, Color Color, ClientProfile? Profile) For(AppConfig config, EveClient? client)
    {
        if (client is null) return ("No client", LoginColor, null);
        if (client.CharacterName is null) return ("Login screen", LoginColor, null);
        var profile = config.GetOrCreateProfile(client.CharacterName);
        return (profile.DisplayLabel, ParseColor(profile.Color, LoginColor), profile);
    }
}

/// <summary>Binds a "#RRGGBB" string to a brush (used by the color swatches in settings).</summary>
public sealed class HexToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        Visuals.Brush(Visuals.ParseColor(value as string, Colors.Transparent));

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
