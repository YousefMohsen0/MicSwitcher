using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Markup;

namespace MicSwitcher.App;

/// <summary>A language the app can be shown in.</summary>
public sealed record AppLanguage(string Code, string NativeName, string FlagResource, bool RightToLeft);

/// <summary>Looks up UI text for the current language. Text for a language that lacks a key falls back to English.</summary>
public static class Loc
{
    public static readonly IReadOnlyList<AppLanguage> Languages = new[]
    {
        new AppLanguage("en", "English", "Assets/flag-en.png", false),
        new AppLanguage("ar-EG", "العربية (مصر)", "Assets/flag-eg.png", true),
    };

    private static Dictionary<string, string> _current = Strings.English;

    public static AppLanguage Language { get; private set; } = Languages[0];

    public static FlowDirection Direction => Language.RightToLeft ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;

    /// <summary>Picks the language from a setting ("auto" follows Windows).</summary>
    public static void Apply(string? setting)
    {
        AppLanguage chosen = Languages[0];
        string code = string.IsNullOrWhiteSpace(setting) || setting == "auto"
            ? CultureInfo.InstalledUICulture.Name
            : setting;

        foreach (AppLanguage language in Languages)
        {
            if (string.Equals(language.Code, code, StringComparison.OrdinalIgnoreCase)
                || string.Equals(language.Code.Split('-')[0], code.Split('-')[0], StringComparison.OrdinalIgnoreCase))
            {
                chosen = language;
                break;
            }
        }

        Language = chosen;
        _current = chosen.Code == "ar-EG" ? Strings.Arabic : Strings.English;
    }

    public static string T(string key)
    {
        if (_current.TryGetValue(key, out string? text) || Strings.English.TryGetValue(key, out text))
        {
            return text;
        }

        return key;
    }

    public static string F(string key, params object?[] args) => string.Format(CultureInfo.CurrentCulture, T(key), args);
}

/// <summary>XAML: <c>Text="{local:T Key_Name}"</c>.</summary>
[MarkupExtensionReturnType(typeof(string))]
public sealed class TExtension : MarkupExtension
{
    public TExtension(string key) => Key = key;

    public string Key { get; set; }

    public override object ProvideValue(IServiceProvider serviceProvider) => Loc.T(Key);
}
