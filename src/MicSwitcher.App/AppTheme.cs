using System;
using System.Windows;
using System.Windows.Media;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace MicSwitcher.App;

/// <summary>Light, dark and pure-black (OLED) themes on top of WPF-UI's light and dark.</summary>
public static class AppTheme
{
    public const string System = "System";
    public const string Light = "Light";
    public const string Dark = "Dark";
    public const string Black = "Black";

    public static readonly string[] All = { System, Light, Dark, Black };

    private static readonly string[] BlackOverrides =
    {
        "ApplicationBackgroundBrush",
        "SolidBackgroundFillColorBaseBrush",
        "SolidBackgroundFillColorSecondaryBrush",
        "SolidBackgroundFillColorTertiaryBrush",
        "LayerFillColorDefaultBrush",
        "CardBackgroundFillColorDefaultBrush",
        "CardBackgroundFillColorSecondaryBrush",
    };

    private static string _current = System;

    public static bool IsBlack => ResolveName(_current) == Black;

    public static bool IsLight => ResolveName(_current) == Light;

    public static string Normalize(string? theme) =>
        Array.Find(All, t => string.Equals(t, theme, StringComparison.OrdinalIgnoreCase)) ?? System;

    private static string ResolveName(string theme) =>
        theme == System ? (SystemIsLight() ? Light : Dark) : theme;

    public static bool SystemIsLight()
    {
        try
        {
            using Microsoft.Win32.RegistryKey? key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int value && value != 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Applies a theme to the whole app (and to <paramref name="window"/> when given).</summary>
    public static void Apply(string? theme, FluentWindow? window = null)
    {
        _current = Normalize(theme);
        string resolved = ResolveName(_current);
        bool black = resolved == Black;

        ApplicationThemeManager.Apply(
            resolved == Light ? ApplicationTheme.Light : ApplicationTheme.Dark,
            black ? WindowBackdropType.None : WindowBackdropType.Mica);

        ResourceDictionary resources = Application.Current.Resources;
        foreach (string key in BlackOverrides)
        {
            resources.Remove(key);
        }

        if (black)
        {
            resources["ApplicationBackgroundBrush"] = Frozen(0, 0, 0);
            resources["SolidBackgroundFillColorBaseBrush"] = Frozen(0, 0, 0);
            resources["SolidBackgroundFillColorSecondaryBrush"] = Frozen(0, 0, 0);
            resources["SolidBackgroundFillColorTertiaryBrush"] = Frozen(8, 8, 8);
            resources["LayerFillColorDefaultBrush"] = Frozen(8, 8, 8);
            resources["CardBackgroundFillColorDefaultBrush"] = Frozen(14, 14, 14);
            resources["CardBackgroundFillColorSecondaryBrush"] = Frozen(14, 14, 14);
        }

        if (window is not null)
        {
            window.WindowBackdropType = black ? WindowBackdropType.None : WindowBackdropType.Mica;
            window.Background = black ? Frozen(0, 0, 0) : null;

            if (_current == System)
            {
                SystemThemeWatcher.Watch(window);
            }
            else
            {
                SystemThemeWatcher.UnWatch(window);
            }
        }
    }

    private static Brush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}
