using System;
using System.Collections.Generic;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MicSwitcher.Core;
using Wpf.Ui.Controls;

namespace MicSwitcher.App;

/// <summary>Icons shown for the mics, including the user's own choices.</summary>
public static class MicAppearance
{
    /// <summary>Built-in icons the user can pick from (key = SymbolRegular name).</summary>
    public static readonly IReadOnlyList<SymbolRegular> Presets = new[]
    {
        SymbolRegular.Mic24, SymbolRegular.Headphones24, SymbolRegular.Laptop24, SymbolRegular.Speaker224,
        SymbolRegular.Desktop24, SymbolRegular.Phone24, SymbolRegular.Video24, SymbolRegular.Person24,
        SymbolRegular.Star24, SymbolRegular.Heart24,
    };

    private static readonly string IconFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MicSwitcher", "icons");

    public static SymbolRegular SymbolOf(MicSlot mic) =>
        Enum.TryParse(mic.IconKey, out SymbolRegular symbol) && Array.IndexOf(Enum.GetValues<SymbolRegular>(), symbol) >= 0
            ? symbol
            : mic.Fallback ? SymbolRegular.Laptop24 : SymbolRegular.Mic24;

    /// <summary>The user's own picture for a mic, or null when none is set or it can no longer be read.</summary>
    public static ImageSource? ImageOf(MicSlot mic)
    {
        string? path = mic.IconPath;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return null;
        }

        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad; // do not keep the file locked
            image.DecodePixelWidth = 64;
            image.UriSource = new Uri(path, UriKind.Absolute);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Copies a picture the user selected into the app's own folder (small PNG), so it keeps working even if the
    /// original file is moved or deleted. Returns the new path, or null with an error message.
    /// </summary>
    public static string? Import(string slotId, string sourcePath, out string? error)
    {
        error = null;
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = 64;
            image.UriSource = new Uri(sourcePath, UriKind.Absolute);
            image.EndInit();

            Directory.CreateDirectory(IconFolder);
            string target = Path.Combine(IconFolder, $"{slotId}-{DateTime.UtcNow:yyyyMMddHHmmss}.png");
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(image));
            using (FileStream stream = File.Create(target))
            {
                encoder.Save(stream);
            }

            return target;
        }
        catch (Exception ex)
        {
            error = Loc.F("Customize_BadPicture", ex.Message);
            return null;
        }
    }
}
