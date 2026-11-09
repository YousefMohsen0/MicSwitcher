using System;
using System.Collections.Generic;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MicSwitcher.Core;
using Wpf.Ui.Controls;

namespace MicSwitcher.App;

/// <summary>Names and icons shown for the three mics, including the user's own choices.</summary>
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

    public static string DefaultName(MicRole role) => role switch
    {
        MicRole.Pro => "Pro mic",
        MicRole.Headset => "Headset mic",
        _ => "Laptop mic",
    };

    public static SymbolRegular DefaultIcon(MicRole role) => role switch
    {
        MicRole.Pro => SymbolRegular.Mic24,
        MicRole.Headset => SymbolRegular.Headphones24,
        _ => SymbolRegular.Laptop24,
    };

    public static string NameOf(AppSettings settings, MicRole role)
    {
        string? label = settings.Get(role).Label;
        return string.IsNullOrWhiteSpace(label) ? DefaultName(role) : label.Trim();
    }

    public static SymbolRegular SymbolOf(AppSettings settings, MicRole role) =>
        Enum.TryParse(settings.Get(role).IconKey, out SymbolRegular symbol) && Array.IndexOf(Enum.GetValues<SymbolRegular>(), symbol) >= 0
            ? symbol
            : DefaultIcon(role);

    /// <summary>The user's own picture for a mic, or null when none is set or it can no longer be read.</summary>
    public static ImageSource? ImageOf(AppSettings settings, MicRole role)
    {
        string? path = settings.Get(role).IconPath;
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
    public static string? Import(MicRole role, string sourcePath, out string? error)
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
            string target = Path.Combine(IconFolder, $"{role}-{DateTime.UtcNow:yyyyMMddHHmmss}.png");
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
            error = "That file could not be used as a picture: " + ex.Message;
            return null;
        }
    }
}
