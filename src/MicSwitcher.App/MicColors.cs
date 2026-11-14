using System;
using System.Windows.Media;
using MicSwitcher.Core;

namespace MicSwitcher.App;

/// <summary>One colour per mic position, shared by the tray icon, the timeline and the status dot.</summary>
public static class MicColors
{
    public static readonly (byte R, byte G, byte B)[] Rgb =
    {
        (46, 160, 67), (219, 143, 0), (207, 34, 46), (47, 129, 247), (163, 113, 247),
        (0, 168, 168), (219, 97, 162), (139, 148, 158), (188, 140, 80), (120, 190, 32),
    };

    public static (byte R, byte G, byte B) At(int index) => Rgb[((index % Rgb.Length) + Rgb.Length) % Rgb.Length];

    /// <summary>The colour of a mic: its own choice, or the palette colour for its position.</summary>
    public static (byte R, byte G, byte B) For(MicSlot? slot, int index)
    {
        string? hex = slot?.Color;
        if (hex is { Length: 7 } && hex[0] == '#'
            && byte.TryParse(hex.AsSpan(1, 2), System.Globalization.NumberStyles.HexNumber, null, out byte r)
            && byte.TryParse(hex.AsSpan(3, 2), System.Globalization.NumberStyles.HexNumber, null, out byte g)
            && byte.TryParse(hex.AsSpan(5, 2), System.Globalization.NumberStyles.HexNumber, null, out byte b))
        {
            return (r, g, b);
        }

        return At(index);
    }

    public static string ToHex((byte R, byte G, byte B) c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    public static Brush BrushFor(MicSlot? slot, int index)
    {
        (byte r, byte g, byte b) = For(slot, index);
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    public static Brush BrushAt(int index)
    {
        (byte r, byte g, byte b) = At(index);
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}
