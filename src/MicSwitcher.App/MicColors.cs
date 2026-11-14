using System.Windows.Media;

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

    public static Brush BrushAt(int index)
    {
        (byte r, byte g, byte b) = At(index);
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}
